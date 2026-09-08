// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Frozen prefix of one <see cref="ValueCacheSeries"/>: <see cref="Count"/> and monotonic flags are
/// loaded when the handle is created. Index access, range queries, and enumerators see only
/// <c>[0, Count)</c>. A concurrent writer may append more rows; those rows are not visible on this
/// handle. Follow the tail with a new <see cref="ValueCacheSeries.Handle"/> and
/// <see cref="EnumerateFrom"/> from the previous <see cref="Count"/> (watermark). Prefix slots are
/// grow-only and stay stable; this type does not copy column data.
/// <para>
/// <see cref="TryFindPacketIds"/> / <see cref="TryFindTimestamps"/> binary-search when that column
/// is non-decreasing (parse-time record and increasing <see cref="ValueCache.RecordPacket"/>,
/// including <see cref="ValueCaptureMode.AllOccurrences"/> duplicates). When the flag is false the
/// Try method returns false: matches are not a contiguous index range, so walk and filter.
/// </para>
/// <para>
/// SIMD: <see cref="EnumerateChunks()"/> / <see cref="EnumerateChunksFrom"/> yield contiguous SoA
/// spans (packet ids, timestamps) clipped to this snapshot. Full inner chunks are
/// <c>1 &lt;&lt; <see cref="ChunkShift"/></c> rows; the first span after a watermark and the last
/// span may be shorter. Spans are not promised to be 32-byte aligned — use unaligned vector loads.
/// <see cref="ValueCacheRow{T}"/> gather is the scalar scan; chunks are the vector scan.
/// </para>
/// </summary>
public readonly struct ValueCacheSeriesHandle
{
    #region Fields

    /// <summary>Live series. Reads are clipped to <see cref="Count"/>; columns are not copied.</summary>
    private readonly ValueCacheSeries _Series;

    #endregion

    #region Constructors

    internal ValueCacheSeriesHandle(ValueCacheSeries series, int observedCount, bool packetIdsMonotonic, bool timestampsMonotonic)
    {
        _Series = series;
        Count = observedCount;
        PacketIdsMonotonic = packetIdsMonotonic;
        TimestampsMonotonic = timestampsMonotonic;
    }

    #endregion

    #region Properties

    /// <summary>Published rows visible through this snapshot. Does not grow when the series appends.</summary>
    public int Count { get; }

    /// <summary>True when packet ids in this snapshot are non-decreasing (binary search is valid).</summary>
    public bool PacketIdsMonotonic { get; }

    /// <summary>True when timestamps in this snapshot are non-decreasing (binary search is valid).</summary>
    public bool TimestampsMonotonic { get; }

    /// <summary>Log₂ of rows per inner column chunk. Same as <see cref="ValueCacheSeries.ChunkShift"/>.</summary>
    public int ChunkShift => _Series.ChunkShift;

    #endregion

    #region Public API

    /// <summary>Packet id at <paramref name="index"/> in this snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacketId(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return _Series.PacketIdAt(index);
    }

    /// <summary>Timestamp (nanoseconds) at <paramref name="index"/> in this snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return _Series.TimestampAt(index);
    }

    /// <summary>Rows whose packet id equals <paramref name="packetId"/>.</summary>
    /// <returns>
    /// <see langword="true"/> when ids are non-decreasing (range may be empty).
    /// <see langword="false"/> when ids are not non-decreasing: walk and filter, there is no contiguous span.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindPacketId(int packetId, out ValueCacheSeriesRange range) =>
        TryFindPacketIds(packetId, packetId, out range);

    /// <summary>
    /// Rows whose packet id is in <c>[<paramref name="minInclusive"/>, <paramref name="maxInclusive"/>]</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when ids are non-decreasing; <paramref name="range"/> is the matching
    /// half-open index span (empty when nothing matches or the bounds are inverted).
    /// <see langword="false"/> when ids are not non-decreasing: matching rows need not be contiguous,
    /// so <paramref name="range"/> is empty and the caller must scan
    /// (<see cref="GetEnumerator"/> / <see cref="EnumerateFrom"/>) and filter on packet id.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindPacketIds(int minInclusive, int maxInclusive, out ValueCacheSeriesRange range)
    {
        if (!PacketIdsMonotonic)
        {
            range = default;
            return false;
        }

        range = _FindPacketIdsMonotonic(minInclusive, maxInclusive);
        return true;
    }

    /// <summary>Rows whose timestamp equals <paramref name="timestampNanos"/>.</summary>
    /// <returns>
    /// <see langword="true"/> when timestamps are non-decreasing (range may be empty).
    /// <see langword="false"/> when timestamps are not non-decreasing: walk and filter.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindTimestamp(long timestampNanos, out ValueCacheSeriesRange range) =>
        TryFindTimestamps(timestampNanos, timestampNanos, out range);

    /// <summary>
    /// Rows whose timestamp is in <c>[<paramref name="minInclusive"/>, <paramref name="maxInclusive"/>]</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when timestamps are non-decreasing; <paramref name="range"/> is the matching
    /// half-open index span (empty when nothing matches or the bounds are inverted).
    /// <see langword="false"/> when timestamps are not non-decreasing: matching rows need not be contiguous,
    /// so scan and filter on timestamp.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindTimestamps(long minInclusive, long maxInclusive, out ValueCacheSeriesRange range)
    {
        if (!TimestampsMonotonic)
        {
            range = default;
            return false;
        }

        range = _FindTimestampsMonotonic(minInclusive, maxInclusive);
        return true;
    }

    /// <summary>Walks this snapshot in row order from index 0. Stops at <see cref="Count"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() =>
        new Enumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <summary>
    /// Walks <c>[<paramref name="startIndex"/>, <see cref="Count"/>)</c>. Resume after a previous
    /// snapshot by passing that snapshot's <see cref="Count"/> as the watermark:
    /// <c>foreach (var row in series.Handle.EnumerateFrom(watermark))</c>.
    /// <paramref name="startIndex"/> equal to <see cref="Count"/> yields nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is not in <c>[0, Count]</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator EnumerateFrom(int startIndex)
    {
        if ((uint)startIndex > (uint)Count)
        {
            _ThrowIndexOutOfRange(startIndex);
        }

        return new Enumerator(_Series, startIndex, Count);
    }

    /// <summary>Walks <c>[<paramref name="range"/>.Start, <paramref name="range"/>.End)</c> inside this snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> is not contained in <c>[0, Count]</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator Enumerate(ValueCacheSeriesRange range)
    {
        if ((uint)range.End > (uint)Count)
        {
            _ThrowIndexOutOfRange(range.End);
        }

        return new Enumerator(_Series, range.Start, range.End);
    }

    /// <summary>
    /// Packet-id inner chunk at <paramref name="chunkIndex"/>, clipped to <see cref="Count"/>,
    /// starting at slot 0 of that chunk. Prefer <see cref="EnumerateChunksFrom"/> for a watermark slice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacketIdChunk(int chunkIndex, out ReadOnlySpan<int> span) =>
        _Series.TryGetPacketIdChunk(chunkIndex, Count, out span);

    /// <summary>
    /// Timestamp inner chunk at <paramref name="chunkIndex"/>, clipped to <see cref="Count"/>,
    /// starting at slot 0 of that chunk. Prefer <see cref="EnumerateChunksFrom"/> for a watermark slice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTimestampChunk(int chunkIndex, out ReadOnlySpan<long> span) =>
        _Series.TryGetTimestampChunk(chunkIndex, Count, out span);

    /// <summary>Walks SoA chunks from index 0 to <see cref="Count"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunks() =>
        new ChunkEnumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <summary>
    /// Walks SoA chunks over <c>[<paramref name="startIndex"/>, <see cref="Count"/>)</c>.
    /// The first span is sliced from the watermark slot so SIMD does not re-read prior rows in that chunk.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is not in <c>[0, Count]</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunksFrom(int startIndex)
    {
        if ((uint)startIndex > (uint)Count)
        {
            _ThrowIndexOutOfRange(startIndex);
        }

        return new ChunkEnumerator(_Series, startIndex, Count);
    }

    /// <summary>Walks SoA chunks covering <paramref name="range"/> inside this snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> is not contained in <c>[0, Count]</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunks(ValueCacheSeriesRange range)
    {
        if ((uint)range.End > (uint)Count)
        {
            _ThrowIndexOutOfRange(range.End);
        }

        return new ChunkEnumerator(_Series, range.Start, range.End);
    }

    #endregion

    #region Nested types

    /// <summary>
    /// One contiguous SoA slice of packet ids and timestamps. Lengths match.
    /// Suitable for SIMD on <see cref="PacketIds"/> / <see cref="Timestamps"/>.
    /// </summary>
    public readonly ref struct Chunk
    {
        #region Constructors

        internal Chunk(int startIndex, ReadOnlySpan<int> packetIds, ReadOnlySpan<long> timestamps)
        {
            StartIndex = startIndex;
            PacketIds = packetIds;
            Timestamps = timestamps;
        }

        #endregion

        #region Properties

        /// <summary>Series index of <c>PacketIds[0]</c> / <c>Timestamps[0]</c>.</summary>
        public int StartIndex { get; }

        /// <summary>Packet ids in this slice. Contiguous; may be shorter than a full inner chunk.</summary>
        public ReadOnlySpan<int> PacketIds { get; }

        /// <summary>Timestamps in this slice, aligned with <see cref="PacketIds"/>.</summary>
        public ReadOnlySpan<long> Timestamps { get; }

        /// <summary>Number of rows in this slice.</summary>
        public int Length => PacketIds.Length;

        #endregion
    }

    /// <summary>Ref enumerator over <see cref="Chunk"/> slices for SIMD / bulk scans.</summary>
    public ref struct ChunkEnumerator
    {
        #region Fields

        private readonly ValueCacheSeries _Series;
        private readonly int _EndExclusive;
        private readonly int _ChunkShift;
        private readonly int _ChunkMask;
        private int _NextIndex;
        private int _StartIndex;
        private ReadOnlySpan<int> _PacketIds;
        private ReadOnlySpan<long> _Timestamps;

        #endregion

        #region Constructors

        internal ChunkEnumerator(ValueCacheSeries series, int startIndex, int endExclusive)
        {
            _Series = series;
            _EndExclusive = endExclusive;
            _ChunkShift = series.ChunkShift;
            _ChunkMask = (1 << _ChunkShift) - 1;
            _NextIndex = startIndex;
            _StartIndex = 0;
            _PacketIds = [];
            _Timestamps = [];
        }

        #endregion

        #region Properties

        /// <summary>Current SoA slice.</summary>
        public readonly Chunk Current =>
            new Chunk(_StartIndex, _PacketIds, _Timestamps);

        #endregion

        #region Public API

        /// <summary>Returns this enumerator so <c>foreach</c> works on <see cref="EnumerateChunksFrom"/> / <see cref="EnumerateChunks(ValueCacheSeriesRange)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ChunkEnumerator GetEnumerator() =>
            this;

        /// <summary>Advances to the next SoA slice. The first slice after a watermark starts at that slot, not slot 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int next = _NextIndex;
            if (next >= _EndExclusive)
            {
                return false;
            }

            int chunkIndex = next >> _ChunkShift;
            if (!_Series.TryGetPacketIdChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<int> packetIds)
                || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<long> timestamps))
            {
                return false;
            }

            int slot = next & _ChunkMask;
            _PacketIds = packetIds[slot..];
            _Timestamps = timestamps[slot..];
            _StartIndex = next;
            int chunkStart = chunkIndex << _ChunkShift;
            _NextIndex = chunkStart + packetIds.Length;
            return true;
        }

        #endregion
    }

    /// <summary>Ref enumerator over packet-id and timestamp columns for one index span.</summary>
    public ref struct Enumerator
    {
        #region Fields

        private readonly ValueCacheSeries _Series;
        private readonly int _EndExclusive;
        private readonly int _ChunkShift;
        private readonly int _ChunkMask;
        private int _Index;
        private int _LoadedChunk;
        private ReadOnlySpan<int> _PacketIds;
        private ReadOnlySpan<long> _Timestamps;
        private int _PacketId;
        private long _TimestampNanos;

        #endregion

        #region Constructors

        internal Enumerator(ValueCacheSeries series, int startIndex, int endExclusive)
        {
            _Series = series;
            _EndExclusive = endExclusive;
            _ChunkShift = series.ChunkShift;
            _ChunkMask = (1 << _ChunkShift) - 1;
            _Index = startIndex - 1;
            _LoadedChunk = -1;
            _PacketIds = [];
            _Timestamps = [];
            _PacketId = 0;
            _TimestampNanos = 0;
        }

        #endregion

        #region Properties

        /// <summary>Current packet id and timestamp.</summary>
        public readonly (int PacketId, long TimestampNanos) Current =>
            (_PacketId, _TimestampNanos);

        /// <summary>Packet id of the current row.</summary>
        public readonly int PacketId => _PacketId;

        /// <summary>Timestamp (nanoseconds) of the current row.</summary>
        public readonly long TimestampNanos => _TimestampNanos;

        /// <summary>Index of the current row in the series snapshot.</summary>
        public readonly int Index => _Index;

        #endregion

        #region Public API

        /// <summary>Returns this enumerator so <c>foreach</c> works on <see cref="EnumerateFrom"/> / <see cref="Enumerate"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() =>
            this;

        /// <summary>Advances to the next published row in the span. Does not pick up later appends.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int next = _Index + 1;
            if (next >= _EndExclusive)
            {
                return false;
            }

            int chunkIndex = next >> _ChunkShift;
            if (chunkIndex != _LoadedChunk)
            {
                if (!_Series.TryGetPacketIdChunk(chunkIndex, _EndExclusive, out _PacketIds)
                    || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out _Timestamps))
                {
                    return false;
                }

                _LoadedChunk = chunkIndex;
            }

            int slot = next & _ChunkMask;
            _PacketId = _PacketIds[slot];
            _TimestampNanos = _Timestamps[slot];
            _Index = next;
            return true;
        }

        #endregion
    }

    #endregion

    #region Private helpers

    private ValueCacheSeriesRange _FindPacketIdsMonotonic(int minInclusive, int maxInclusive)
    {
        if (Count == 0 || minInclusive > maxInclusive)
        {
            return default;
        }

        int start = _LowerBoundPacketId(minInclusive);
        int end = _UpperBoundPacketId(maxInclusive);
        return new ValueCacheSeriesRange(start, end);
    }

    private ValueCacheSeriesRange _FindTimestampsMonotonic(long minInclusive, long maxInclusive)
    {
        if (Count == 0 || minInclusive > maxInclusive)
        {
            return default;
        }

        int start = _LowerBoundTimestamp(minInclusive);
        int end = _UpperBoundTimestamp(maxInclusive);
        return new ValueCacheSeriesRange(start, end);
    }

    private int _LowerBoundPacketId(int target)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_Series.PacketIdAt(mid) < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private int _UpperBoundPacketId(int target)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_Series.PacketIdAt(mid) <= target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private int _LowerBoundTimestamp(long target)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_Series.TimestampAt(mid) < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private int _UpperBoundTimestamp(long target)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_Series.TimestampAt(mid) <= target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published snapshot range.");

    #endregion
}
