// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Per-field column accumulator for the shared columnar batch format. Stores all values for a
/// single field across all packets in a <see cref="ColumnarPacketBatch"/>, keyed by the owning
/// packet ID and the field's topology node ID (so repeated occurrences of the same field within
/// one packet remain distinguishable).
/// <para>
/// Only the value list matching <see cref="FieldType"/> is populated by <see cref="Add"/>; the
/// others stay empty. Values are taken from <see cref="FieldValueData"/> (and optional custom
/// strings) — the same Core types used by the protocol stack.
/// </para>
/// <para>
/// <b>Retention:</b> a bag keeps its rows until the batch is flushed, while the packet buffers behind the values may be
/// recycled. Scalars, strings and the fixed-size addresses are self-contained (inline or immutable), but a
/// <see cref="FieldType.Bytes"/> value can alias a caller's array, so it is copied on <see cref="Add"/>.
/// </para>
/// <para>
/// <b>Size estimate:</b> <see cref="EstimatedSizeBytes"/> is a deliberately cheap approximation used only to decide when
/// a block is full: 8 bytes for the two id columns plus the payload size (strings count 2 bytes per char plus 4 bytes
/// of overhead). It ignores list growth slack and object headers, so it is neither exact nor a limit.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; the owning <see cref="ColumnarPacketBatch"/> is single-threaded.
/// </para>
/// </summary>
internal sealed class FieldColumnBag
{
    #region Fields

    // Parallel row lists: row i of every populated list belongs together (packet id, node id, value, custom strings).
    // Only the value list of the bag's FieldType grows; the others stay empty. The 256 initial capacity of the id lists is
    // a sizing hint, not a limit.
    private readonly List<int> _PacketIds = new(256);
    private readonly List<int> _NodeIds = new(256);

    private readonly List<bool> _BoolValues = new();
    private readonly List<long> _I64Values = new();
    private readonly List<ulong> _U64Values = new();
    private readonly List<double> _F64Values = new();
    private readonly List<long> _TimestampValues = new();
    private readonly List<string?> _StringValues = new();
    /// <summary>
    /// Materialized byte payloads for <see cref="FieldType.Bytes"/> (owned copies) and, after first
    /// <see cref="BytesValues"/> access, for fixed-size address types deferred via
    /// <see cref="_FixedBytePayloads"/>.
    /// </summary>
    private readonly List<byte[]?> _BytesValues = new();
    /// <summary>
    /// Deferred fixed-size address payloads (MAC/IPv4/IPv6/EUI64/UUID) stored as
    /// <see cref="FieldValueData"/> until a sink requests <see cref="BytesValues"/>, avoiding a
    /// heap <c>byte[]</c> per value during batch accumulation.
    /// </summary>
    private readonly List<FieldValueData> _FixedBytePayloads = new();

    private readonly List<string?> _CustomRepresentations = new();
    private readonly List<string?> _CustomTexts = new();

    /// <summary>Running size estimate in bytes (see the class remarks); updated by <see cref="Add"/>, cleared by <see cref="Reset"/>.</summary>
    private long _EstimatedSizeAccumulator { get; set; }

    #endregion

    #region Constructor

    /// <summary>Creates a column bag for the given field.</summary>
    /// <param name="fieldIdValue">The field identifier this bag accumulates values for.</param>
    /// <param name="fieldType">The field's declared value type (from field metadata).</param>
    internal FieldColumnBag(int fieldIdValue, FieldType fieldType)
    {
        FieldIdValue = fieldIdValue;
        FieldType = fieldType;
    }

    #endregion

    #region Properties

    /// <summary>The field identifier this bag accumulates values for.</summary>
    internal int FieldIdValue { get; }

    /// <summary>The field's declared value type.</summary>
    internal FieldType FieldType { get; }

    /// <summary>Number of rows (values) accumulated.</summary>
    internal int RowCount => _PacketIds.Count;

    /// <summary>Rough estimate of the memory consumed by this bag's accumulated rows, in bytes.</summary>
    internal long EstimatedSizeBytes => _EstimatedSizeAccumulator;

    /// <summary>Packet identifiers, one per row (same range as <see cref="PacketId"/>).</summary>
    internal IReadOnlyList<int> PacketIds => _PacketIds;

    /// <summary>Topology node identifiers, one per row, scoped to <see cref="PacketIds"/>.</summary>
    internal IReadOnlyList<int> NodeIds => _NodeIds;

    /// <summary>Values for a <see cref="FieldType.Bool"/> bag.</summary>
    internal IReadOnlyList<bool> BoolValues => _BoolValues;

    /// <summary>Values for an <see cref="FieldType.I64"/> bag.</summary>
    internal IReadOnlyList<long> I64Values => _I64Values;

    /// <summary>Values for an <see cref="FieldType.U64"/> bag.</summary>
    internal IReadOnlyList<ulong> U64Values => _U64Values;

    /// <summary>Values for an <see cref="FieldType.F64"/> bag.</summary>
    internal IReadOnlyList<double> F64Values => _F64Values;

    /// <summary>Values (nanoseconds since Unix epoch) for a <see cref="FieldType.Timestamp"/> bag.</summary>
    internal IReadOnlyList<long> TimestampValues => _TimestampValues;

    /// <summary>Values for a <see cref="FieldType.String"/> bag.</summary>
    internal IReadOnlyList<string?> StringValues => _StringValues;

    /// <summary>
    /// Values for a <see cref="FieldType.Bytes"/> bag or one of the fixed-size address bags
    /// (<see cref="FieldType.MacAddress"/>, <see cref="FieldType.IPv4Address"/>,
    /// <see cref="FieldType.IPv6Address"/>, <see cref="FieldType.Eui64"/>, <see cref="FieldType.Uuid"/>).
    /// Fixed-size address types are materialized from deferred <see cref="FieldValueData"/> on first access.
    /// </summary>
    internal IReadOnlyList<byte[]?> BytesValues
    {
        get
        {
            _MaterializeFixedBytePayloads();
            return _BytesValues;
        }
    }

    /// <summary>Optional custom representation strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomRepresentations => _CustomRepresentations;

    /// <summary>Optional custom text strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomTexts => _CustomTexts;

    #endregion

    #region Public API

    /// <summary>
    /// Appends one row from a Core <see cref="FieldValueData"/> payload plus optional custom strings.
    /// </summary>
    /// <param name="packetId">The owning packet's identifier.</param>
    /// <param name="nodeId">The field's topology node identifier within the packet.</param>
    /// <param name="data">Payload; <see cref="FieldValueData.Type"/> must match <see cref="FieldType"/>.</param>
    /// <param name="customRepresentation">Optional custom representation (already filtered by detail flags).</param>
    /// <param name="customText">Optional custom text (already filtered by detail flags).</param>
    /// <exception cref="InvalidOperationException">
    /// The value type of <paramref name="data"/> does not match <see cref="FieldType"/>, or the bag's type has no column.
    /// Nothing is added in that case.
    /// </exception>
    /// <remarks>
    /// <paramref name="data"/> is passed by <c>in</c> reference because <see cref="FieldValueData"/> is a
    /// 24-byte struct; this avoids a copy per value on the export path.
    /// </remarks>
    internal void Add(
        int packetId,
        int nodeId,
        in FieldValueData data,
        string? customRepresentation,
        string? customText)
    {
        // Extract typed payload before mutating row lists so a type mismatch leaves the bag unchanged.
        switch (FieldType)
        {
            case FieldType.Bool:
                if (!data.TryGetAsBool(out bool boolValue))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Bool but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _BoolValues.Add(boolValue);
                _EstimatedSizeAccumulator += 9; // ids (8) + bool (1)
                break;
            case FieldType.I64:
                if (!data.TryGetAsI64(out long i64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected I64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _I64Values.Add(i64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.U64:
                if (!data.TryGetAsU64(out ulong u64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected U64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _U64Values.Add(u64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.F64:
                if (!data.TryGetAsF64(out double f64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected F64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _F64Values.Add(f64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Timestamp:
                if (!data.TryGetAsTimestamp(out Timestamp timestamp))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Timestamp but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _TimestampValues.Add(timestamp.AsNanos);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.String:
                if (!data.TryGetAsString(out string stringValue))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected String but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _StringValues.Add(stringValue);
                _EstimatedSizeAccumulator += 8 + _EstimateStringBytes(stringValue);
                break;
            case FieldType.Bytes:
            {
                // Owned copy: the value may alias a recycled packet buffer, and the bag outlives the packet until flush.
                byte[]? bytes = FieldValueMaterializer.ToBytesArray(in data);
                if (bytes is null)
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Bytes but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _BytesValues.Add(bytes);
                _EstimatedSizeAccumulator += 8 + bytes.Length;
                break;
            }
            // Fixed-size address types (MAC, IPv4, IPv6, EUI-64, UUID): the type is validated here, but the value is kept
            // as the inline FieldValueData and turned into a byte[] only when a sink reads BytesValues. This avoids one
            // heap array per value while accumulating; the struct holds the address inline (no reference to a packet
            // buffer), so retaining it after the packet is recycled is safe. Estimates are 8 (ids) + address size.
            case FieldType.MacAddress:
                if (!data.TryGetAsMacAddress(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected MacAddress but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 14;
                break;
            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected IPv4Address but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 12;
                break;
            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected IPv6Address but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Eui64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Uuid but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            default:
                throw new InvalidOperationException(
                    $"Field {FieldIdValue} has unsupported column type {FieldType}.");
        }

        // The custom string columns always get one entry per row (null when absent) so every column has the same row
        // count and row i lines up across all lists.
        _CustomRepresentations.Add(customRepresentation);
        _CustomTexts.Add(customText);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customRepresentation);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customText);
    }

    /// <summary>
    /// Removes the rows after the first <paramref name="rowCount"/> rows and restores the size estimate. Used by
    /// <see cref="ColumnarPacketBatch"/> to undo the rows of a packet that could not be added completely, so a rejected
    /// packet leaves no partial rows behind.
    /// </summary>
    /// <param name="rowCount">Number of rows to keep; must be between 0 and <see cref="RowCount"/>.</param>
    /// <param name="estimatedSizeBytes">The value of <see cref="EstimatedSizeBytes"/> when the bag held exactly those rows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is negative or larger than <see cref="RowCount"/>.</exception>
    internal void TruncateTo(int rowCount, long estimatedSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, RowCount);

        // Every parallel list is cut back to the kept row count; a list that never received a row is shorter and stays as
        // it is. The deferred fixed-size payloads are the pending tail of the address rows (rows already turned into
        // byte arrays came earlier and are never removed here), so they keep rowCount minus the materialized rows.
        _TrimTo(_PacketIds, rowCount);
        _TrimTo(_NodeIds, rowCount);
        _TrimTo(_BoolValues, rowCount);
        _TrimTo(_I64Values, rowCount);
        _TrimTo(_U64Values, rowCount);
        _TrimTo(_F64Values, rowCount);
        _TrimTo(_TimestampValues, rowCount);
        _TrimTo(_StringValues, rowCount);
        _TrimTo(_BytesValues, rowCount);
        _TrimTo(_FixedBytePayloads, Math.Max(0, rowCount - _BytesValues.Count));
        _TrimTo(_CustomRepresentations, rowCount);
        _TrimTo(_CustomTexts, rowCount);
        _EstimatedSizeAccumulator = estimatedSizeBytes;
    }

    /// <summary>Resets all accumulated rows for reuse in the next block.</summary>
    internal void Reset()
    {
        _PacketIds.Clear();
        _NodeIds.Clear();
        _BoolValues.Clear();
        _I64Values.Clear();
        _U64Values.Clear();
        _F64Values.Clear();
        _TimestampValues.Clear();
        _StringValues.Clear();
        _BytesValues.Clear();
        _FixedBytePayloads.Clear();
        _CustomRepresentations.Clear();
        _CustomTexts.Clear();
        _EstimatedSizeAccumulator = 0;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Converts the deferred fixed-size address payloads into <c>byte[]</c> rows and appends them to
    /// <see cref="_BytesValues"/>. Idempotent: the deferred list is emptied, so a second call does nothing. A bag holds
    /// either deferred payloads or Bytes rows (never both), because its <see cref="FieldType"/> is fixed, so the append
    /// order matches the row order.
    /// </summary>
    private void _MaterializeFixedBytePayloads()
    {
        if (_FixedBytePayloads.Count == 0)
        {
            return;
        }

        for (int i = 0; i < _FixedBytePayloads.Count; i++)
        {
            FieldValueData payload = _FixedBytePayloads[i];
            _BytesValues.Add(FieldValueMaterializer.ToBytesArray(in payload));
        }

        _FixedBytePayloads.Clear();
    }

    /// <summary>Shortens <paramref name="list"/> to at most <paramref name="count"/> elements.</summary>
    private static void _TrimTo<T>(List<T> list, int count)
    {
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }
    }

    private static long _EstimateStringBytes(string? value) =>
        value is null
            ? 0
            : (value.Length * 2L) + 4;

    #endregion
}
