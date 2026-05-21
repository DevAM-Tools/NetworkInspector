// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Persistent orchestrator that routes field values to their <see cref="ValueCacheSeries"/> during live parsing.
/// Lives inside <see cref="PacketIndex"/> for the lifetime of the session.
///
/// <para><b>Single-Writer:</b> All methods are called under Session._ParseLock.
/// Supports dynamic field add (no removal at runtime) while parsing is active.</para>
///
/// <para>Uses a dense <see cref="FieldId"/>-to-slot mapping for O(1) field lookup
/// and a bit-vector for first-value-wins deduplication within a single packet.</para>
///
/// <para><b>Slot strategy:</b> Slots are assigned sequentially via <c>_NextSlot++</c> and are
/// append-only — there is intentionally no <c>RemoveField</c> API. This guarantees that dedup
/// bit indices remain stable for a packet's lifetime and that lock-free readers never observe
/// a re-used slot. Slot count is bounded by total fields ever registered (typically a few
/// hundred at most), so memory overhead is negligible.</para>
/// </summary>
internal sealed class ValueCacheBuilder
{
    #region Fields

    // ── Field-to-Slot Mapping ────────────────────────────────
    // Dense array indexed by FieldId.Value. Value = slot index, or -1 if not tracked.
    private int[] _FieldSlots;

    // ── Per-Slot State ───────────────────────────────────────
    // One entry per tracked field. Slot index assigned sequentially via _NextSlot.
    private ValueCacheSeries?[] _Series;    // target series for each slot
    private int _NextSlot;                  // next available slot index

    // ── Per-Packet State ─────────────────────────────────────
    private ulong[] _DeduplicationBits;     // bit-vector: one bit per slot (first-value-wins)
    // Starts at -1 ("no active packet") so the < 0 guard in TryRecordValue catches calls
    // made before the very first BeginPacket.
    private int _CurrentPacketId = -1;
    private long _CurrentTimestamp;          // nanos

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a builder with capacity for the given number of field IDs.
    /// Starts with 0 tracked fields.
    /// </summary>
    internal ValueCacheBuilder(int fieldCapacity)
    {
        _FieldSlots = new int[fieldCapacity];
        Array.Fill(_FieldSlots, -1);
        _Series = new ValueCacheSeries?[16];
        _DeduplicationBits = new ulong[1];
    }

    #endregion

    #region Internal API

    // ── Dynamic Field Management (under ParseLock) ───────────

    /// <summary>
    /// Registers a <see cref="ValueCacheSeries"/> for live recording.
    /// The series must already be created and published in <see cref="ValueCacheManager"/>.
    /// This method only sets up the routing (slot mapping).
    /// </summary>
    internal void AddField(FieldId fieldId, ValueCacheSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        // Ensure slot array covers this FieldId
        if (fieldId.Value >= _FieldSlots.Length)
        {
            int oldLen = _FieldSlots.Length;
            int newLen = Math.Max(oldLen * 2, fieldId.Value + 1);
            // Array.Resize copies existing entries verbatim; only the freshly allocated
            // tail must be initialized to the "untracked" sentinel (-1). This avoids the
            // double-pass of allocating, full-Fill(-1), and then overwriting via Array.Copy.
            Array.Resize(ref _FieldSlots, newLen);
            Array.Fill(_FieldSlots, -1, oldLen, newLen - oldLen);
        }

        // Assign slot
        int slot = _NextSlot++;
        _FieldSlots[fieldId.Value] = slot;

        // Ensure series array covers the new slot
        if (slot >= _Series.Length)
        {
            int newLen = Math.Max(_Series.Length * 2, slot + 1);
            Array.Resize(ref _Series, newLen);
        }
        _Series[slot] = series;

        // Grow dedup bits if needed
        int requiredWords = (slot >> 6) + 1;
        if (requiredWords > _DeduplicationBits.Length)
        {
            Array.Resize(ref _DeduplicationBits, requiredWords);
        }
    }

    /// <summary>Number of fields currently tracked (active slots with non-null series).</summary>
    internal int FieldCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _NextSlot; i++)
            {
                if (_Series[i] is not null)
                {
                    count++;
                }
            }
            return count;
        }
    }

    // ── Per-Packet Lifecycle (under ParseLock) ───────────────

    /// <summary>Called at start of each packet's parse pass.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginPacket(int packetId, long timestampNanos)
    {
        _CurrentPacketId = packetId;
        _CurrentTimestamp = timestampNanos;

        // Clear dedup bits for the new packet. Typical sessions track < 128 fields
        // (≤ 2 ulong words). Manual clearing avoids Array.Clear overhead for the
        // common case. For larger sessions (> 128 fields), Array.Clear is used.
        if (_DeduplicationBits.Length <= 2)
        {
            _DeduplicationBits[0] = 0;
            if (_DeduplicationBits.Length == 2)
            {
                _DeduplicationBits[1] = 0;
            }
        }
        else
        {
            Array.Clear(_DeduplicationBits);
        }
    }

    /// <summary>
    /// Records a field value during parsing. Fast-path: slot lookup, dedup check, series append.
    /// Called from <see cref="Protocols.ParseContext.TryRecordValue"/> via MutField.Append.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void TryRecordValue(FieldId fieldId, in FieldValueData value)
    {
        // Guard: no active packet means EndPacket has been called (or BeginPacket was never
        // called). Appending with _CurrentPacketId = -1 would silently record PacketId -1 in
        // the series, corrupting the packet-to-value association. Return early instead.
        if (_CurrentPacketId < 0)
        {
            return;
        }

        // O(1) slot lookup: _FieldSlots is indexed by FieldId.Value.
        // Unsigned comparison handles negative fieldId.Value as out-of-bounds.
        if ((uint)fieldId.Value >= (uint)_FieldSlots.Length)
        {
            return;
        }

        int slot = _FieldSlots[fieldId.Value];
        if (slot < 0)
        {
            return; // Field not tracked — either never registered or already removed
        }

        // First-value-wins deduplication: each slot maps to one bit in a ulong[] vector.
        // word = slot / 64 (which ulong element), bit = slot % 64 (which bit within it).
        // If the bit is already set, this field was recorded earlier in this packet.
        int word = slot >> 6;
        ulong bit = 1UL << (slot & 63);
        ref ulong dedupWord = ref _DeduplicationBits[word];
        if ((dedupWord & bit) != 0)
        {
            // Already recorded for this packet — series tracks completeness flag
            _Series[slot]?.MarkDuplicateDrop();
            return;
        }
        dedupWord |= bit;

        // Delegate to the series — it handles type-specific extraction + append
        _Series[slot]?.TryAppend(_CurrentTimestamp, _CurrentPacketId, value);
    }

    /// <summary>Called at end of each packet's parse pass.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndPacket() =>
        _CurrentPacketId = -1;

    #endregion

    #region Static Helpers

    // ── Compatibility Validation (static) ────────────────────

    /// <summary>Returns <see langword="true"/> if the field type can be cached.</summary>
    internal static bool IsFieldTypeCacheable(FieldType fieldType) => fieldType switch
    {
        FieldType.U64 or FieldType.I64 or FieldType.F64 or FieldType.Timestamp
            or FieldType.Bool or FieldType.IPv4Address or FieldType.MacAddress
            or FieldType.Eui64 or FieldType.IPv6Address or FieldType.Uuid => true,
        _ => false,
    };

    /// <summary>Returns <see langword="true"/> if the storage mode is compatible with the field type.</summary>
    internal static bool IsStorageModeCompatible(FieldType fieldType, ValueCacheStorageMode mode)
    {
        if (mode == ValueCacheStorageMode.Native)
        {
            return true; // Native is always compatible with cacheable types
        }

        return (fieldType, mode) switch
        {
            // CompactFloat: eligible for U64, I64, F64, Timestamp
            (FieldType.U64, ValueCacheStorageMode.CompactFloat) => true,
            (FieldType.I64, ValueCacheStorageMode.CompactFloat) => true,
            (FieldType.F64, ValueCacheStorageMode.CompactFloat) => true,
            (FieldType.Timestamp, ValueCacheStorageMode.CompactFloat) => true,

            // CompactInt (signed): eligible for I64 only
            (FieldType.I64, ValueCacheStorageMode.CompactInt8) => true,
            (FieldType.I64, ValueCacheStorageMode.CompactInt16) => true,
            (FieldType.I64, ValueCacheStorageMode.CompactInt32) => true,

            // CompactUInt (unsigned): eligible for U64 only
            (FieldType.U64, ValueCacheStorageMode.CompactUInt8) => true,
            (FieldType.U64, ValueCacheStorageMode.CompactUInt16) => true,
            (FieldType.U64, ValueCacheStorageMode.CompactUInt32) => true,

            _ => false,
        };
    }

    #endregion
}
