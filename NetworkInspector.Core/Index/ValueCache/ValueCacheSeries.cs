// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Append-capable columnar time-series for a single field.
///
/// <para><b>Thread-safety model (Single-Writer / Multi-Reader):</b></para>
/// <list type="bullet">
///   <item>Writer (under ParseLock): calls <see cref="TryAppend"/>, which writes at <c>_Count</c> and
///   then publishes via <see cref="Volatile.Write{T}(ref T, T)"/> on <c>_Count</c>.</item>
///   <item>Reader (lock-free): calls <see cref="Volatile.Read{T}(ref readonly T)"/> on <c>_Count</c> to get a snapshot,
///   then reads arrays sliced to that count. Arrays may be newer (grown) but never smaller than the captured count.</item>
/// </list>
///
/// <para><b>Memory ordering guarantee:</b></para>
/// <list type="bullet">
///   <item>Writer: all data writes happen BEFORE <c>Volatile.Write(_Count)</c> (release fence).</item>
///   <item>Reader: <c>Volatile.Read(_Count)</c> (acquire fence) ensures subsequent array reads see all data up to that count.</item>
/// </list>
///
/// <para><b>Growth safety:</b></para>
/// <para>When arrays need to grow, new larger arrays are allocated, old data is copied,
/// and references are replaced. Old readers holding the old array reference continue
/// to work — old data is still valid and the GC keeps the old array alive.</para>
/// </summary>
public sealed partial class ValueCacheSeries
{
    #region Fields
    // ── Identity (immutable after creation) ──────────────────
    private readonly FieldId _FieldId;
    private readonly FieldType _OriginalFieldType;
    private readonly ValueCacheStorageMode _StorageMode;

    // ── Parallel Arrays (writer replaces on grow) ────────────
    private long[] _Timestamps;         // nanos since epoch, strictly ascending
    private int[] _PacketIds;           // PacketId.Value per entry
    private ValueCacheData _Data;       // typed value array(s) — mutable for grow

    // ── Publication Point ────────────────────────────────────
    // _Count and _CompletenessRaw are accessed ONLY via Volatile.Read / Volatile.Write.
    // C# does not support 'volatile readonly', so all reads use:
    //   Volatile.Read(ref Unsafe.AsRef(in _Count))
    // Unsafe.AsRef produces a writable alias of the field, satisfying the ref-parameter
    // requirement of Volatile.Read. Do not change these field types without updating every
    // Volatile.Read/Write site — the cast would still compile but be silently wrong.
    private int _Count;                 // accessed via Volatile.Read/Write ONLY
    private int _Capacity;              // current array capacity (writer only)

    // ── Writer State (writer-only, under ParseLock) ──────────
    private long _LastTimestamp;         // for monotonic enforcement
    private int _CompletenessRaw;        // ValueCacheCompleteness as int for Volatile.Read/Write
    private double _BaseTimestamp;       // CompactFloat+Timestamp: first timestamp as seconds
    private bool _BaseTimestampSet;      // CompactFloat+Timestamp: base has been set

    #endregion

    #region Constructors

    /// <summary>Private constructor — use <see cref="CreateLive"/> factory.</summary>
    private ValueCacheSeries(
        FieldId fieldId,
        FieldType originalFieldType,
        ValueCacheStorageMode storageMode)
    {
        _FieldId = fieldId;
        _OriginalFieldType = originalFieldType;
        _StorageMode = storageMode;
        _Timestamps = [];
        _PacketIds = [];
        _Data = CreateEmptyData(originalFieldType, storageMode);
    }

    /// <summary>
    /// Creates a series from pre-built arrays. Used by retroactive field builders.
    /// The series is immediately fully populated (Count = timestamps.Length).
    /// </summary>
    internal ValueCacheSeries(
        FieldId fieldId,
        FieldType originalFieldType,
        ValueCacheStorageMode storageMode,
        long[] timestamps,
        int[] packetIds,
        ValueCacheData data,
        ValueCacheCompleteness completeness,
        double baseTimestamp = 0.0)
        : this(fieldId, originalFieldType, storageMode,
            timestamps, packetIds, data, completeness,
            count: timestamps.Length, baseTimestamp)
    {
    }

    /// <summary>
    /// Creates a series from pre-built (possibly over-allocated) arrays. The arrays are taken
    /// over by reference — no defensive copy. Used by builders that finish their write phase
    /// with arrays whose <see cref="Count"/> is less than their capacity, allowing the builder
    /// to hand the arrays over directly without a trim-to-size copy.
    /// </summary>
    /// <remarks>
    /// The caller must guarantee that <paramref name="timestamps"/>, <paramref name="packetIds"/>
    /// and any value array inside <paramref name="data"/> all have <c>Length &gt;= count</c> and
    /// the same <c>Length</c> (capacity). For Bool storage the bit-packed byte array's length
    /// must satisfy <c>Length * 8 &gt;= count</c>.
    /// </remarks>
    internal ValueCacheSeries(
        FieldId fieldId,
        FieldType originalFieldType,
        ValueCacheStorageMode storageMode,
        long[] timestamps,
        int[] packetIds,
        ValueCacheData data,
        ValueCacheCompleteness completeness,
        int count,
        double baseTimestamp = 0.0)
    {
        if ((uint)count > (uint)timestamps.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"Count {count} exceeds timestamps capacity {timestamps.Length}.");
        }
        if (packetIds.Length != timestamps.Length)
        {
            throw new ArgumentException(
                "packetIds and timestamps must have identical capacity.", nameof(packetIds));
        }

        _FieldId = fieldId;
        _OriginalFieldType = originalFieldType;
        _StorageMode = storageMode;
        _Timestamps = timestamps;
        _PacketIds = packetIds;
        _Data = data;
        _CompletenessRaw = (int)completeness;
        _BaseTimestamp = baseTimestamp;
        _Count = count;
        _Capacity = timestamps.Length;
    }

    // ── Factory ──────────────────────────────────────────────

    /// <summary>
    /// Creates an empty series ready for live append operations.
    /// Called under ParseLock when a new field cache is registered.
    /// Starts with capacity 0 — first <see cref="TryAppend"/> triggers initial allocation.
    /// </summary>
    internal static ValueCacheSeries CreateLive(
        FieldId fieldId, FieldType fieldType, ValueCacheStorageMode mode) =>
        new(fieldId, fieldType, mode);

    #endregion

    #region Properties

    // ── Reader Properties (thread-safe, lock-free) ───────────

    /// <summary>The field this series caches.</summary>
    public FieldId FieldId => _FieldId;

    /// <summary>The original field type (before any compact conversion).</summary>
    public FieldType OriginalFieldType => _OriginalFieldType;

    /// <summary>The storage mode used for values.</summary>
    public ValueCacheStorageMode StorageMode => _StorageMode;

    /// <summary>
    /// Number of published entries. Thread-safe via <see cref="Volatile.Read{T}(ref readonly T)"/>.
    /// Readers should capture this value once and use it for all subsequent span accesses
    /// to guarantee a consistent snapshot.
    /// </summary>
    public int Count => Volatile.Read(ref Unsafe.AsRef(in _Count));

    /// <summary>
    /// Captures the current published entry count for use with the count-based span accessors.
    /// Identical to <see cref="Count"/>, but named explicitly to signal snapshot intent.
    /// <example><code>
    /// int snapshot = series.SnapshotCount();
    /// ReadOnlySpan&lt;long&gt; ts = series.SliceTimestamps(snapshot);
    /// ReadOnlySpan&lt;ulong&gt; vals = series.SliceU64(snapshot);
    /// // ts and vals are guaranteed the same length
    /// </code></example>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SnapshotCount() => Volatile.Read(ref Unsafe.AsRef(in _Count));

    /// <summary>Completeness flags — indicates if any entries were dropped or clamped.
    /// Thread-safe: writer updates under ParseLock, reader sees at least the state
    /// published before the last <see cref="Count"/> snapshot (piggybacking on the
    /// Volatile.Write/Read in TryAppend/Count).</summary>
    public ValueCacheCompleteness Completeness =>
        (ValueCacheCompleteness)Volatile.Read(ref Unsafe.AsRef(in _CompletenessRaw));

    /// <summary>
    /// Estimated memory usage in bytes. Dynamically computed from current capacity.
    /// </summary>
    public long MemoryUsage
    {
        get
        {
            int capacity = _Capacity;
            // Timestamps (long[]) + PacketIds (int[]) + value arrays
            long baseSize = (capacity * sizeof(long)) + (capacity * sizeof(int));
            return baseSize + _Data.EstimateMemoryUsage();
        }
    }

    /// <summary>
    /// Base timestamp for CompactFloat + Timestamp mode (seconds since epoch, double precision).
    /// Zero for all other modes.
    /// </summary>
    public double BaseTimestamp => _BaseTimestamp;

    #endregion

    #region Span Accessors

    // ── Time / PacketId Access ───────────────────────────────

    /// <summary>Nanosecond timestamps, strictly ascending. Sliced to <see cref="Count"/>.</summary>
    public ReadOnlySpan<long> TimestampSpan
    {
        get
        {
            int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
            return _Timestamps.AsSpan(0, count);
        }
    }

    /// <summary>Packet IDs corresponding to each entry. Sliced to <see cref="Count"/>.</summary>
    public ReadOnlySpan<int> PacketIdSpan
    {
        get
        {
            int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
            return _PacketIds.AsSpan(0, count);
        }
    }

    // ── Typed Span Accessors (SIMD-ready) ────────────────────
    // Each accessor reads Volatile.Read(ref _Count) internally and
    // returns a span sliced to that count.

    /// <summary>Native U64, MacAddress, or Eui64 values.</summary>
    public ReadOnlySpan<ulong> AsU64Span() => SliceSpan<ulong>();

    /// <summary>Native I64 values.</summary>
    public ReadOnlySpan<long> AsI64Span() => SliceSpan<long>();

    /// <summary>Native F64 values.</summary>
    public ReadOnlySpan<double> AsF64Span() => SliceSpan<double>();

    /// <summary>Native Timestamp values (nanoseconds since epoch).</summary>
    public ReadOnlySpan<long> AsTimestampNanosSpan() => SliceSpan<long>();

    /// <summary>Native IPv4 addresses as uint.</summary>
    public ReadOnlySpan<uint> AsIPv4Span() => SliceSpan<uint>();

    /// <summary>CompactFloat values (for all eligible types; for Timestamp = deltas in seconds).</summary>
    public ReadOnlySpan<float> AsFloatSpan() => SliceSpan<float>();

    /// <summary>CompactInt8 values (signed, I64 only).</summary>
    public ReadOnlySpan<sbyte> AsCompactInt8Span() => SliceSpan<sbyte>();

    /// <summary>CompactInt16 values (signed, I64 only).</summary>
    public ReadOnlySpan<short> AsCompactInt16Span() => SliceSpan<short>();

    /// <summary>CompactInt32 values (signed, I64 only).</summary>
    public ReadOnlySpan<int> AsCompactInt32Span() => SliceSpan<int>();

    /// <summary>CompactUInt8 values (unsigned, U64 only).</summary>
    public ReadOnlySpan<byte> AsCompactUInt8Span() => SliceSpan<byte>();

    /// <summary>CompactUInt16 values (unsigned, U64 only).</summary>
    public ReadOnlySpan<ushort> AsCompactUInt16Span() => SliceSpan<ushort>();

    /// <summary>CompactUInt32 values (unsigned, U64 only).</summary>
    public ReadOnlySpan<uint> AsCompactUInt32Span() => SliceSpan<uint>();

    /// <summary>Bool values as dense bit-packed byte array (entry-index-based). Bit at position [i] is set if value is true.</summary>
    public ReadOnlySpan<byte> AsBoolBits()
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        int neededBytes = (count + 7) >> 3;
        return _Data.AsBoolBits().AsSpan(0, neededBytes);
    }

    /// <summary>Gets the low ulong span for 128-bit types (IPv6, UUID).</summary>
    public ReadOnlySpan<ulong> DualLowSpan()
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        return _Data.AsDualUlong().Low.AsSpan(0, count);
    }

    /// <summary>Gets the high ulong span for 128-bit types (IPv6, UUID).</summary>
    public ReadOnlySpan<ulong> DualHighSpan()
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        return _Data.AsDualUlong().High.AsSpan(0, count);
    }

    // ── Snapshot-Based Span Accessors ────────────────────────
    // Use these when reading multiple spans from the same series to guarantee
    // they are all sliced to the same count. Call SnapshotCount() once, then
    // pass the result to each accessor.
    //
    //   int snapshot = series.SnapshotCount();
    //   ReadOnlySpan<long> ts = series.SliceTimestamps(snapshot);
    //   ReadOnlySpan<ulong> vals = series.SliceU64(snapshot);

    /// <summary>Timestamps sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<long> SliceTimestamps(int snapshotCount) =>
        _Timestamps.AsSpan(0, snapshotCount);

    /// <summary>Packet IDs sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> SlicePacketIds(int snapshotCount) =>
        _PacketIds.AsSpan(0, snapshotCount);

    /// <summary>Native U64/MacAddress/Eui64 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ulong> SliceU64(int snapshotCount) =>
        _Data.AsArray<ulong>().AsSpan(0, snapshotCount);

    /// <summary>Native I64 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<long> SliceI64(int snapshotCount) =>
        _Data.AsArray<long>().AsSpan(0, snapshotCount);

    /// <summary>Native F64 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<double> SliceF64(int snapshotCount) =>
        _Data.AsArray<double>().AsSpan(0, snapshotCount);

    /// <summary>Native Timestamp values (nanos) sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<long> SliceTimestampNanos(int snapshotCount) =>
        _Data.AsArray<long>().AsSpan(0, snapshotCount);

    /// <summary>Native IPv4 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<uint> SliceIPv4(int snapshotCount) =>
        _Data.AsArray<uint>().AsSpan(0, snapshotCount);

    /// <summary>CompactFloat values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> SliceFloat(int snapshotCount) =>
        _Data.AsArray<float>().AsSpan(0, snapshotCount);

    /// <summary>CompactInt8 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<sbyte> SliceCompactInt8(int snapshotCount) =>
        _Data.AsArray<sbyte>().AsSpan(0, snapshotCount);

    /// <summary>CompactInt16 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<short> SliceCompactInt16(int snapshotCount) =>
        _Data.AsArray<short>().AsSpan(0, snapshotCount);

    /// <summary>CompactInt32 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> SliceCompactInt32(int snapshotCount) =>
        _Data.AsArray<int>().AsSpan(0, snapshotCount);

    /// <summary>CompactUInt8 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> SliceCompactUInt8(int snapshotCount) =>
        _Data.AsArray<byte>().AsSpan(0, snapshotCount);

    /// <summary>CompactUInt16 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ushort> SliceCompactUInt16(int snapshotCount) =>
        _Data.AsArray<ushort>().AsSpan(0, snapshotCount);

    /// <summary>CompactUInt32 values sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<uint> SliceCompactUInt32(int snapshotCount) =>
        _Data.AsArray<uint>().AsSpan(0, snapshotCount);

    /// <summary>Bool bit-packed bytes sliced to cover <paramref name="snapshotCount"/> entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> SliceBoolBits(int snapshotCount) =>
        _Data.AsBoolBits().AsSpan(0, (snapshotCount + 7) >> 3);

    /// <summary>Low ulong span for 128-bit types sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ulong> SliceDualLow(int snapshotCount) =>
        _Data.AsDualUlong().Low.AsSpan(0, snapshotCount);

    /// <summary>High ulong span for 128-bit types sliced to <paramref name="snapshotCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ulong> SliceDualHigh(int snapshotCount) =>
        _Data.AsDualUlong().High.AsSpan(0, snapshotCount);

    #endregion

    #region Time-Indexed Access

    // ── Time-Indexed Access ──────────────────────────────────

    /// <summary>
    /// Binary search for entries within [<paramref name="from"/>, <paramref name="to"/>] (inclusive, nanos).
    /// Returns (startIndex, count). O(log n). Uses a <see cref="Count"/> snapshot internally.
    /// </summary>
    public (int StartIndex, int Count) GetTimestampRange(Timestamp from, Timestamp to)
    {
        long fromNanos = from.AsNanos;
        long toNanos = to.AsNanos;
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        ReadOnlySpan<long> ts = _Timestamps.AsSpan(0, count);

        if (count == 0 || fromNanos > toNanos)
        {
            return (0, 0);
        }

        // Find first index >= fromNanos
        int lo = LowerBound(ts, fromNanos);
        if (lo >= count)
        {
            return (0, 0);
        }

        // Find first index > toNanos
        int hi = UpperBound(ts, toNanos, lo);
        return (lo, hi - lo);
    }

    /// <summary>
    /// Returns the index of the first entry with timestamp >= <paramref name="nanos"/>.
    /// Returns <see cref="Count"/> if all entries are before <paramref name="nanos"/>.
    /// </summary>
    public int BinarySearchTimestamp(long nanos)
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        return LowerBound(_Timestamps.AsSpan(0, count), nanos);
    }

    /// <summary>
    /// Reconstructs a <see cref="FieldValueData"/> at the given entry index.
    /// Useful for single-value queries, NOT for bulk processing (use typed spans instead).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative or >= <see cref="Count"/>.
    /// </exception>
    public FieldValueData GetValueAt(int index)
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        return (_OriginalFieldType, _StorageMode) switch
        {
            // ── Native modes ─────────────────────────────────
            (FieldType.U64, ValueCacheStorageMode.Native) => FieldValueData.NewU64(_Data.AsSpan<ulong>()[index]),
            (FieldType.I64, ValueCacheStorageMode.Native) => FieldValueData.NewI64(_Data.AsSpan<long>()[index]),
            (FieldType.F64, ValueCacheStorageMode.Native) => FieldValueData.NewF64(_Data.AsSpan<double>()[index]),
            (FieldType.Timestamp, ValueCacheStorageMode.Native) => FieldValueData.NewTimestamp(new Timestamp(_Data.AsSpan<long>()[index])),
            (FieldType.Bool, ValueCacheStorageMode.Native) => FieldValueData.NewBool((_Data.AsBoolBits()[index >> 3] & (1 << (index & 7))) != 0),
            (FieldType.IPv4Address, ValueCacheStorageMode.Native) => FieldValueData.NewIPv4(new IPv4Address(_Data.AsSpan<uint>()[index])),
            (FieldType.MacAddress, ValueCacheStorageMode.Native) => FieldValueData.NewMacAddress(new MacAddress(_Data.AsSpan<ulong>()[index])),
            (FieldType.Eui64, ValueCacheStorageMode.Native) => FieldValueData.NewEui64(new Eui64(_Data.AsSpan<ulong>()[index])),
            (FieldType.IPv6Address, ValueCacheStorageMode.Native) => ReconstructIPv6(index),
            (FieldType.Uuid, ValueCacheStorageMode.Native) => ReconstructUuid(index),

            // ── CompactFloat ─────────────────────────────────
            (FieldType.U64, ValueCacheStorageMode.CompactFloat) => FieldValueData.NewU64((ulong)_Data.AsSpan<float>()[index]),
            (FieldType.I64, ValueCacheStorageMode.CompactFloat) => FieldValueData.NewI64((long)_Data.AsSpan<float>()[index]),
            (FieldType.F64, ValueCacheStorageMode.CompactFloat) => FieldValueData.NewF64(_Data.AsSpan<float>()[index]),
            (FieldType.Timestamp, ValueCacheStorageMode.CompactFloat) => ReconstructTimestampFromCompactFloat(index),

            // ── CompactInt (signed, I64 only) ────────────────
            (FieldType.I64, ValueCacheStorageMode.CompactInt8) => FieldValueData.NewI64(_Data.AsSpan<sbyte>()[index]),
            (FieldType.I64, ValueCacheStorageMode.CompactInt16) => FieldValueData.NewI64(_Data.AsSpan<short>()[index]),
            (FieldType.I64, ValueCacheStorageMode.CompactInt32) => FieldValueData.NewI64(_Data.AsSpan<int>()[index]),

            // ── CompactUInt (unsigned, U64 only) ─────────────
            (FieldType.U64, ValueCacheStorageMode.CompactUInt8) => FieldValueData.NewU64(_Data.AsSpan<byte>()[index]),
            (FieldType.U64, ValueCacheStorageMode.CompactUInt16) => FieldValueData.NewU64(_Data.AsSpan<ushort>()[index]),
            (FieldType.U64, ValueCacheStorageMode.CompactUInt32) => FieldValueData.NewU64(_Data.AsSpan<uint>()[index]),

            _ => FieldValueData.None,
        };
    }

    #endregion
}
