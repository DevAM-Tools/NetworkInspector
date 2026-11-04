// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field payloads for same-as-previous detection in the PBF Standard
/// block builder. Compares <see cref="FieldValueData"/> (via
/// <see cref="FieldValueData.Equals(FieldValueData)"/>) and optional custom representation /
/// custom text strings — no string formatting of values.
/// Distinct from <see cref="PreviousFieldStore"/> (JSON Compact; typed <see cref="FieldValue"/>).
/// <para>
/// One snapshot is kept per field ID, holding the last serialized occurrence of that field (whichever packet or
/// tree position it came from). Container fields without a value overwrite the snapshot with an empty one, so a
/// value after a container never compares equal.
/// </para>
/// <para>
/// <b>Storage choice:</b> a dense array indexed by field ID (no hashing, no per-lookup allocation) when the ID
/// space is small, a dictionary when it is large. IDs outside a dense array go to an overflow dictionary so an
/// unexpected ID is still compared correctly instead of failing.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one block builder and so by one thread at a time: the caller's thread compares
/// and updates it while it fills the block, and in background mode the writer thread resets it after the hand-off (see
/// <see cref="IExportBatch"/>).
/// </para>
/// </summary>
internal sealed class PreviousFieldValueStore
{
    #region Configuration and state

    // Largest field count for which the dense array is used: 2048, a constant sizing choice, not a limit. Perf: the dense
    // array holds one Snapshot (a few tens of bytes) per field ID for the whole export, so 2048 entries cost on the order of
    // 100 KiB and give one array load per comparison; beyond that the dictionary holds only the IDs that occurred. The caller
    // passes the field count of its stack, so the dense path runs for every stack up to this size.
    // PreviousFieldStore uses the same value.
    private const int _DenseThreshold = 2048;

    /// <summary>
    /// Last occurrence of a field. The <see cref="FieldValueData"/> is kept by value. A string payload is an immutable object,
    /// so sharing it with the packet is safe. A bytes payload is not: it references the array the parser sliced, which may
    /// belong to a buffer the producer reuses once <c>OnPacket</c> has returned, and the snapshot lives until the block ends.
    /// For bytes the snapshot therefore points into <see cref="OwnedBytes"/>, a private copy that the next occurrence of the
    /// same field overwrites in place when it fits (no allocation in the steady state).
    /// </summary>
    /// <param name="Data">The payload of the occurrence.</param>
    /// <param name="CustomRepresentation">The custom representation, or null when absent.</param>
    /// <param name="CustomText">The custom text, or null when absent.</param>
    /// <param name="HasValue">Whether the occurrence carried a value (false for a container field).</param>
    /// <param name="OwnedBytes">The private copy a bytes payload points into, or null for every other payload type.</param>
    private readonly record struct Snapshot(
        FieldValueData Data,
        string? CustomRepresentation,
        string? CustomText,
        bool HasValue,
        byte[]? OwnedBytes)
    {
        /// <summary>Creates the snapshot of an occurrence that carried a value.</summary>
        /// <param name="data">The payload of the occurrence.</param>
        /// <param name="customRepresentation">The custom representation, or null when absent.</param>
        /// <param name="customText">The custom text, or null when absent.</param>
        /// <param name="ownedBytes">The private copy a bytes payload points into, or null.</param>
        internal Snapshot(FieldValueData data, string? customRepresentation, string? customText, byte[]? ownedBytes)
            : this(data, customRepresentation, customText, HasValue: true, ownedBytes)
        {
        }
    }

    /// <summary>Dense representation: the snapshot per field id; null with the dictionary representation.</summary>
    private readonly Snapshot[]? _DenseValues;

    /// <summary>Dictionary representation: the snapshot per field id; null with the dense representation.</summary>
    private readonly Dictionary<int, Snapshot>? _SparseValues;

    /// <summary>Overflow of the dense representation: snapshots of field ids outside the dense array; created on first use.</summary>
    private Dictionary<int, Snapshot>? _OverflowValues;

    /// <summary>Creates a store optimized for the given field count.</summary>
    /// <param name="fieldCount">
    /// Number of field IDs expected (IDs <c>0</c> to <c>fieldCount - 1</c>); must not be negative. Selects the dense
    /// or the dictionary representation.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldCount"/> is negative.</exception>
    internal PreviousFieldValueStore(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldCount);
        if (fieldCount <= _DenseThreshold)
        {
            _DenseValues = new Snapshot[fieldCount];
        }
        else
        {
            _SparseValues = new(256);
        }
    }

    #endregion

    #region Comparison and update

    /// <summary>
    /// Computes same-as-previous flags and updates the stored snapshot.
    /// Pass <paramref name="hasValue"/> = <see langword="false"/> for container fields with no payload.
    /// </summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="hasValue">Whether the field carries a value payload.</param>
    /// <param name="data">The field's payload (ignored when <paramref name="hasValue"/> is false).</param>
    /// <param name="customRepresentation">Custom representation text, or <see langword="null"/> when absent.</param>
    /// <param name="customText">Custom text, or <see langword="null"/> when absent.</param>
    /// <returns>
    /// A combination of <see cref="SameFlags.FieldSameValue"/>, <see cref="SameFlags.FieldSameCustomRepresentation"/>
    /// and <see cref="SameFlags.FieldSameCustomText"/>; 0 when nothing repeats or when there is no previous value.
    /// </returns>
    internal uint CompareAndUpdate(
        int fieldIdValue,
        bool hasValue,
        in FieldValueData data,
        string? customRepresentation,
        string? customText)
    {
        // Read the previous snapshot, compare, and only then store the current one: the caller serializes every
        // occurrence exactly once, in order, so "previous" always means the occurrence written just before. The
        // comparison comes first because storing a bytes value overwrites the private copy the previous snapshot points to.
        Snapshot previous = _GetPrevious(fieldIdValue);
        uint flags = 0;

        // Nothing to compare against (first occurrence, or a container on either side): all parts are written in full.
        if (previous.HasValue && hasValue)
        {
            // Each part is flagged independently, so a field whose value repeats but whose text changed still saves the
            // value. A null custom text is never flagged (absent is simply not written), and the strings use ordinal
            // comparison because the flag promises byte-identical text, not culture-equivalent text.
            if (previous.Data.Equals(data))
            {
                flags |= SameFlags.FieldSameValue;
            }

            if (customRepresentation is not null
                && string.Equals(previous.CustomRepresentation, customRepresentation, StringComparison.Ordinal))
            {
                flags |= SameFlags.FieldSameCustomRepresentation;
            }

            if (customText is not null
                && string.Equals(previous.CustomText, customText, StringComparison.Ordinal))
            {
                flags |= SameFlags.FieldSameCustomText;
            }
        }

        Snapshot current = hasValue
            ? _Capture(previous, in data, customRepresentation, customText)
            : default;
        _SetPrevious(fieldIdValue, current);
        return flags;
    }

    /// <summary>
    /// Builds the snapshot to store for the current occurrence. A bytes payload is copied into the private array of the
    /// previous snapshot of the same field (reused when it is large enough), so the stored value no longer depends on the
    /// buffer the packet came from; every other payload type is self-contained and stored as it is.
    /// </summary>
    /// <param name="previous">The previous snapshot of the same field; its private byte array is reused.</param>
    /// <param name="data">The payload of the current occurrence.</param>
    /// <param name="customRepresentation">Custom representation text, or <see langword="null"/> when absent.</param>
    /// <param name="customText">Custom text, or <see langword="null"/> when absent.</param>
    /// <returns>The snapshot to store for the current occurrence.</returns>
    private static Snapshot _Capture(
        Snapshot previous, in FieldValueData data, string? customRepresentation, string? customText)
    {
        if (data.Type != FieldType.Bytes || !data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            return new Snapshot(data, customRepresentation, customText, ownedBytes: null);
        }

        byte[]? owned = previous.OwnedBytes;
        if (owned is null || owned.Length < bytes.Length)
        {
            // First bytes value of this field, or a longer one: allocate once, then reuse for the rest of the block.
            owned = new byte[bytes.Length];
        }

        bytes.Span.CopyTo(owned);
        return new Snapshot(
            FieldValueData.NewBytes(new ReadOnlyMemory<byte>(owned, 0, bytes.Length)),
            customRepresentation,
            customText,
            owned);
    }

    /// <summary>Returns the stored snapshot for a field ID, or an empty one (<c>HasValue</c> false) when none exists.</summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <returns>The snapshot of the last occurrence, or the default snapshot when the field was not seen.</returns>
    private Snapshot _GetPrevious(int fieldIdValue)
    {
        if (_DenseValues is not null)
        {
            if (fieldIdValue >= 0 && fieldIdValue < _DenseValues.Length)
            {
                return _DenseValues[fieldIdValue];
            }

            if (_OverflowValues is not null && _OverflowValues.TryGetValue(fieldIdValue, out Snapshot overflow))
            {
                return overflow;
            }

            return default;
        }

        return _SparseValues!.TryGetValue(fieldIdValue, out Snapshot sparse)
            ? sparse
            : default;
    }

    /// <summary>
    /// Stores the snapshot for a field ID. IDs outside the dense array (including negative ones) go to the overflow
    /// dictionary, which is created lazily because it is normally never needed.
    /// </summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="current">The snapshot of the occurrence just written; the default snapshot for a container field.</param>
    private void _SetPrevious(int fieldIdValue, Snapshot current)
    {
        if (_DenseValues is not null)
        {
            if (fieldIdValue >= 0 && fieldIdValue < _DenseValues.Length)
            {
                _DenseValues[fieldIdValue] = current;
                return;
            }

            _OverflowValues ??= new Dictionary<int, Snapshot>(16);
            _OverflowValues[fieldIdValue] = current;
            return;
        }

        _SparseValues![fieldIdValue] = current;
    }

    #endregion

    #region Reset

    /// <summary>Resets all stored previous values (for block boundary resets).</summary>
    internal void Reset()
    {
        // Also drops the references held by the snapshots, so payloads of the finished block can be collected.
        if (_DenseValues is not null)
        {
            Array.Clear(_DenseValues);
            _OverflowValues?.Clear();
        }
        else
        {
            _SparseValues!.Clear();
        }
    }

    #endregion
}
