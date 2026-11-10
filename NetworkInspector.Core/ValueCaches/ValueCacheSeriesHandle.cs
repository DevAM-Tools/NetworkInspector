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
    #region Snapshot state

    /// <summary>
    /// Live series. Reads are clipped to <see cref="Count"/>; columns are not copied. The handle is a small readonly struct
    /// (series reference, count, two flags), so taking a snapshot allocates nothing. Rows below the frozen count are never
    /// moved or changed by the writer (grow-only chunks), which is why the unchecked reads below are safe.
    /// </summary>
    private readonly ValueCacheSeries _Series;

    /// <summary>
    /// Freezes a prefix of <paramref name="series"/>. The caller must have loaded <paramref name="observedCount"/> before the
    /// two flags (see the flag fields of <see cref="ValueCacheSeries"/>): the writer clears a flag before it publishes the
    /// row that breaks the order, so this order can only report a flag as too pessimistic, never too optimistic.
    /// </summary>
    /// <param name="series">The live series.</param>
    /// <param name="observedCount">Published row count at snapshot time; the exclusive upper bound of every read.</param>
    /// <param name="packetIdsMonotonic">Whether packet ids in the prefix are non-decreasing.</param>
    /// <param name="timestampsMonotonic">Whether timestamps in the prefix are non-decreasing.</param>
    internal ValueCacheSeriesHandle(ValueCacheSeries series, int observedCount, bool packetIdsMonotonic, bool timestampsMonotonic)
    {
        _Series = series;
        Count = observedCount;
        PacketIdsMonotonic = packetIdsMonotonic;
        TimestampsMonotonic = timestampsMonotonic;
    }

    /// <summary>Published rows visible through this snapshot. Does not grow when the series appends.</summary>
    public int Count { get; }

    /// <summary>True when packet ids in this snapshot are non-decreasing (binary search is valid).</summary>
    public bool PacketIdsMonotonic { get; }

    /// <summary>True when timestamps in this snapshot are non-decreasing (binary search is valid).</summary>
    public bool TimestampsMonotonic { get; }

    /// <summary>Log₂ of rows per inner column chunk. Same as <see cref="ValueCacheSeries.ChunkShift"/>.</summary>
    public int ChunkShift => _Series.ChunkShift;

    #endregion

    #region Row access

    /// <summary>Packet id at <paramref name="index"/> in this snapshot.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet id of that row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacketId(int index)
    {
        // Bounds are checked against the frozen Count (a plain readonly field) and the read then skips the series' own
        // volatile Count check: every index below the frozen Count was published before the handle was created. The
        // unsigned compare rejects negative and too-large indexes in one branch.
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return _Series.PacketIdAt(index);
    }

    /// <summary>Timestamp (nanoseconds) at <paramref name="index"/> in this snapshot.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet timestamp of that row in nanoseconds.</returns>
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

    #endregion

    #region Search by packet id and timestamp

    /// <summary>Rows whose packet id equals <paramref name="packetId"/>.</summary>
    /// <param name="packetId">The packet id to find.</param>
    /// <param name="range">The matching half-open row span; empty when nothing matches or the method returns <see langword="false"/>.</param>
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
    /// <param name="minInclusive">Smallest packet id to match.</param>
    /// <param name="maxInclusive">Largest packet id to match.</param>
    /// <param name="range">The matching half-open row span; empty when nothing matches or the method returns <see langword="false"/>.</param>
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
    /// <param name="timestampNanos">The timestamp to find, in nanoseconds.</param>
    /// <param name="range">The matching half-open row span; empty when nothing matches or the method returns <see langword="false"/>.</param>
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
    /// <param name="minInclusive">Smallest timestamp to match, in nanoseconds.</param>
    /// <param name="maxInclusive">Largest timestamp to match, in nanoseconds.</param>
    /// <param name="range">The matching half-open row span; empty when nothing matches or the method returns <see langword="false"/>.</param>
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

    #endregion

    #region Row and chunk walks

    /// <summary>Walks this snapshot in row order from index 0. Stops at <see cref="Count"/>.</summary>
    /// <returns>A ref enumerator over the rows <c>[0, Count)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() =>
        new Enumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <summary>
    /// Walks <c>[<paramref name="startIndex"/>, <see cref="Count"/>)</c>. Resume after a previous
    /// snapshot by passing that snapshot's <see cref="Count"/> as the watermark:
    /// <c>foreach (var row in series.Handle.EnumerateFrom(watermark))</c>.
    /// <paramref name="startIndex"/> equal to <see cref="Count"/> yields nothing.
    /// </summary>
    /// <param name="startIndex">First row to visit, in <c>[0, Count]</c>.</param>
    /// <returns>A ref enumerator over the rows <c>[startIndex, Count)</c>.</returns>
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
    /// <param name="range">The row span to visit, for example from <see cref="TryFindPacketIds"/>.</param>
    /// <returns>A ref enumerator over the rows of <paramref name="range"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> is not contained in <c>[0, Count]</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator Enumerate(ValueCacheSeriesRange range)
    {
        // Only End is checked: the range constructor already guarantees 0 <= Start <= End, so End <= Count bounds both ends.
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
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="span">The packet ids of the chunk below <see cref="Count"/>; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row of this snapshot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacketIdChunk(int chunkIndex, out ReadOnlySpan<int> span) =>
        _Series.TryGetPacketIdChunk(chunkIndex, Count, out span);

    /// <summary>
    /// Timestamp inner chunk at <paramref name="chunkIndex"/>, clipped to <see cref="Count"/>,
    /// starting at slot 0 of that chunk. Prefer <see cref="EnumerateChunksFrom"/> for a watermark slice.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="span">The timestamps of the chunk below <see cref="Count"/>; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row of this snapshot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTimestampChunk(int chunkIndex, out ReadOnlySpan<long> span) =>
        _Series.TryGetTimestampChunk(chunkIndex, Count, out span);

    /// <summary>Walks SoA chunks from index 0 to <see cref="Count"/>.</summary>
    /// <returns>A ref enumerator over the chunk slices covering <c>[0, Count)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunks() =>
        new ChunkEnumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <summary>
    /// Walks SoA chunks over <c>[<paramref name="startIndex"/>, <see cref="Count"/>)</c>.
    /// The first span is sliced from the watermark slot so SIMD does not re-read prior rows in that chunk.
    /// </summary>
    /// <param name="startIndex">First row to visit (the watermark), in <c>[0, Count]</c>.</param>
    /// <returns>A ref enumerator over the chunk slices covering <c>[startIndex, Count)</c>.</returns>
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
    /// <param name="range">The row span to visit, for example from <see cref="TryFindPacketIds"/>.</param>
    /// <returns>A ref enumerator over the chunk slices covering <paramref name="range"/>.</returns>
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

    #region Chunk and row enumerators

    /// <summary>
    /// One contiguous SoA slice of packet ids and timestamps. Lengths match.
    /// Suitable for SIMD on <see cref="PacketIds"/> / <see cref="Timestamps"/>.
    /// </summary>
    public readonly ref struct Chunk
    {
        /// <summary>Wraps the two column slices of one chunk.</summary>
        /// <param name="startIndex">Series index of the first row of the slices.</param>
        /// <param name="packetIds">Packet ids of the slice.</param>
        /// <param name="timestamps">Timestamps of the slice; the same length as <paramref name="packetIds"/>.</param>
        internal Chunk(int startIndex, ReadOnlySpan<int> packetIds, ReadOnlySpan<long> timestamps)
        {
            StartIndex = startIndex;
            PacketIds = packetIds;
            Timestamps = timestamps;
        }

        /// <summary>Series index of <c>PacketIds[0]</c> / <c>Timestamps[0]</c>.</summary>
        public int StartIndex { get; }

        /// <summary>Packet ids in this slice. Contiguous; may be shorter than a full inner chunk.</summary>
        public ReadOnlySpan<int> PacketIds { get; }

        /// <summary>Timestamps in this slice, aligned with <see cref="PacketIds"/>.</summary>
        public ReadOnlySpan<long> Timestamps { get; }

        /// <summary>Number of rows in this slice.</summary>
        public int Length => PacketIds.Length;
    }

    /// <summary>
    /// Ref enumerator over <see cref="Chunk"/> slices for SIMD / bulk scans. It is a <c>ref struct</c> because it holds
    /// spans straight into the series' inner chunk arrays: no copy and no allocation, at the price that it cannot cross an
    /// <c>await</c> or be stored in a field. Not thread-safe (one enumerator per thread); the underlying data is stable.
    /// </summary>
    public ref struct ChunkEnumerator
    {
        /// <summary>The series whose chunks are walked.</summary>
        private readonly ValueCacheSeries _Series;

        /// <summary>Exclusive end of the walk: the frozen count of the snapshot, or the end of the requested range.</summary>
        private readonly int _EndExclusive;

        /// <summary>Log₂ of rows per chunk, copied from the series.</summary>
        private readonly int _ChunkShift;

        /// <summary>Mask that maps a row index to its slot inside a chunk (<c>(1 &lt;&lt; _ChunkShift) - 1</c>).</summary>
        private readonly int _ChunkMask;

        /// <summary>Row index at which the next <see cref="MoveNext"/> starts.</summary>
        private int _NextIndex;

        /// <summary>Series index of the first row of the current slice.</summary>
        private int _StartIndex;

        /// <summary>Packet ids of the current slice.</summary>
        private ReadOnlySpan<int> _PacketIds;

        /// <summary>Timestamps of the current slice.</summary>
        private ReadOnlySpan<long> _Timestamps;

        /// <summary>Starts at row <paramref name="startIndex"/>; callers have already validated the bounds against the frozen count.</summary>
        /// <param name="series">The series to walk.</param>
        /// <param name="startIndex">First row to visit.</param>
        /// <param name="endExclusive">Exclusive end row; not above the frozen count of the snapshot.</param>
        internal ChunkEnumerator(ValueCacheSeries series, int startIndex, int endExclusive)
        {
            // The chunk size is a power of two (1 << ChunkShift), so row -> (chunk, slot) is a shift and a mask.
            _Series = series;
            _EndExclusive = endExclusive;
            _ChunkShift = series.ChunkShift;
            _ChunkMask = (1 << _ChunkShift) - 1;
            _NextIndex = startIndex;
            _StartIndex = 0;
            _PacketIds = [];
            _Timestamps = [];
        }

        /// <summary>Current SoA slice; valid after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
        public readonly Chunk Current =>
            new Chunk(_StartIndex, _PacketIds, _Timestamps);

        /// <summary>
        /// Returns this enumerator so <c>foreach</c> works on <see cref="EnumerateChunksFrom"/> /
        /// <see cref="EnumerateChunks(ValueCacheSeriesRange)"/>.
        /// </summary>
        /// <returns>A copy of this enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ChunkEnumerator GetEnumerator() =>
            this;

        /// <summary>Advances to the next SoA slice. The first slice after a watermark starts at that slot, not slot 0.</summary>
        /// <returns><see langword="false"/> at the end of the walk, or when a chunk cannot be fetched (the walk stops early).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int next = _NextIndex;
            if (next >= _EndExclusive)
            {
                return false;
            }

            // Fetch the chunk that holds 'next'. The snapshot end is passed as the clip bound, so the span never reaches
            // past the frozen prefix. A failed lookup (the chunk is not published, or the series was cleared underneath
            // this enumerator) ends the walk instead of throwing: the scan simply stops early.
            int chunkIndex = next >> _ChunkShift;
            if (!_Series.TryGetPacketIdChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<int> packetIds)
                || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<long> timestamps))
            {
                return false;
            }

            // Slice from the watermark slot (only non-zero for the first chunk), and continue at the first row after the
            // clipped chunk. The clipped length, not the full chunk size, is used so the last chunk ends at the snapshot end.
            int slot = next & _ChunkMask;
            _PacketIds = packetIds[slot..];
            _Timestamps = timestamps[slot..];
            _StartIndex = next;
            int chunkStart = chunkIndex << _ChunkShift;
            _NextIndex = chunkStart + packetIds.Length;
            return true;
        }
    }

    /// <summary>
    /// Ref enumerator over packet-id and timestamp columns for one index span. A <c>ref struct</c> that keeps the current
    /// chunk's spans, so per row it costs two span reads; the chunk lookup (and its volatile loads) happens once per chunk,
    /// not once per row. Cannot cross an <c>await</c>. Not thread-safe; the underlying data is stable.
    /// </summary>
    public ref struct Enumerator
    {
        /// <summary>The series whose rows are walked.</summary>
        private readonly ValueCacheSeries _Series;

        /// <summary>Exclusive end of the walk: the frozen count of the snapshot, or the end of the requested range.</summary>
        private readonly int _EndExclusive;

        /// <summary>Log₂ of rows per chunk, copied from the series.</summary>
        private readonly int _ChunkShift;

        /// <summary>Mask that maps a row index to its slot inside a chunk (<c>(1 &lt;&lt; _ChunkShift) - 1</c>).</summary>
        private readonly int _ChunkMask;

        /// <summary>Index of the last row returned; one before the start row until the first <see cref="MoveNext"/>.</summary>
        private int _Index;

        /// <summary>Index of the chunk whose spans are held in <see cref="_PacketIds"/>, or -1 when none is loaded.</summary>
        private int _LoadedChunk;

        /// <summary>Packet ids of the loaded chunk, clipped to the end of the walk.</summary>
        private ReadOnlySpan<int> _PacketIds;

        /// <summary>Timestamps of the loaded chunk, clipped to the end of the walk.</summary>
        private ReadOnlySpan<long> _Timestamps;

        /// <summary>Packet id of the current row.</summary>
        private int _PacketId;

        /// <summary>Timestamp (nanoseconds) of the current row.</summary>
        private long _TimestampNanos;

        /// <summary>Starts before row <paramref name="startIndex"/>; callers have already validated the bounds against the frozen count.</summary>
        /// <param name="series">The series to walk.</param>
        /// <param name="startIndex">First row to visit.</param>
        /// <param name="endExclusive">Exclusive end row; not above the frozen count of the snapshot.</param>
        internal Enumerator(ValueCacheSeries series, int startIndex, int endExclusive)
        {
            _Series = series;
            _EndExclusive = endExclusive;
            _ChunkShift = series.ChunkShift;
            _ChunkMask = (1 << _ChunkShift) - 1;

            // Index is the last row returned; the first MoveNext advances to startIndex. LoadedChunk -1 forces the first load.
            _Index = startIndex - 1;
            _LoadedChunk = -1;
            _PacketIds = [];
            _Timestamps = [];
            _PacketId = 0;
            _TimestampNanos = 0;
        }

        /// <summary>Current packet id and timestamp; valid after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
        public readonly (int PacketId, long TimestampNanos) Current =>
            (_PacketId, _TimestampNanos);

        /// <summary>Packet id of the current row.</summary>
        public readonly int PacketId => _PacketId;

        /// <summary>Timestamp (nanoseconds) of the current row.</summary>
        public readonly long TimestampNanos => _TimestampNanos;

        /// <summary>Index of the current row in the series snapshot.</summary>
        public readonly int Index => _Index;

        /// <summary>Returns this enumerator so <c>foreach</c> works on <see cref="EnumerateFrom"/> / <see cref="Enumerate"/>.</summary>
        /// <returns>A copy of this enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() =>
            this;

        /// <summary>Advances to the next published row in the span. Does not pick up later appends.</summary>
        /// <returns><see langword="false"/> at the end of the span, or when a chunk cannot be fetched (the walk stops early).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int next = _Index + 1;
            if (next >= _EndExclusive)
            {
                return false;
            }

            // Reload the column spans only when the walk crosses into another chunk. A failed lookup ends the walk early
            // (see ChunkEnumerator.MoveNext).
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

            // Read the row from the cached spans; the spans are clipped to the snapshot, so no further bounds work is needed.
            int slot = next & _ChunkMask;
            _PacketId = _PacketIds[slot];
            _TimestampNanos = _Timestamps[slot];
            _Index = next;
            return true;
        }
    }

    #endregion

    #region Binary search over the frozen prefix

    // The four bound searches below are classic binary searches over the frozen prefix [0, Count). They read through the
    // unchecked PacketIdAt / TimestampAt (the index is always below the frozen count) and rely on the column being
    // non-decreasing, which the callers establish through the monotonic flag; on an unsorted column the result would be
    // meaningless, which is why TryFind* refuses to search then. Lower bound = first row with value >= target; upper bound =
    // first row with value > target. Together they give the half-open span of all duplicates (AllOccurrences can repeat an
    // id). The midpoint lo + ((hi - lo) >> 1) cannot overflow for any int count.

    /// <summary>Half-open row span of packet ids in the closed interval; empty for an empty prefix or inverted bounds.</summary>
    /// <param name="minInclusive">Smallest packet id to match.</param>
    /// <param name="maxInclusive">Largest packet id to match.</param>
    /// <returns>The row span; the caller has established that packet ids are non-decreasing.</returns>
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

    /// <summary>Half-open row span of timestamps in the closed interval; empty for an empty prefix or inverted bounds.</summary>
    /// <param name="minInclusive">Smallest timestamp to match, in nanoseconds.</param>
    /// <param name="maxInclusive">Largest timestamp to match, in nanoseconds.</param>
    /// <returns>The row span; the caller has established that timestamps are non-decreasing.</returns>
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

    /// <summary>Index of the first row of the frozen prefix whose packet id is at least <paramref name="target"/>; <c>Count</c> when none is.</summary>
    /// <param name="target">The packet id to search for.</param>
    /// <returns>The lower-bound row index in <c>[0, Count]</c>.</returns>
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

    /// <summary>Index of the first row of the frozen prefix whose packet id is greater than <paramref name="target"/>; <c>Count</c> when none is.</summary>
    /// <param name="target">The packet id to search for.</param>
    /// <returns>The upper-bound row index in <c>[0, Count]</c>.</returns>
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

    /// <summary>Index of the first row of the frozen prefix whose timestamp is at least <paramref name="target"/>; <c>Count</c> when none is.</summary>
    /// <param name="target">The timestamp in nanoseconds to search for.</param>
    /// <returns>The lower-bound row index in <c>[0, Count]</c>.</returns>
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

    /// <summary>Index of the first row of the frozen prefix whose timestamp is greater than <paramref name="target"/>; <c>Count</c> when none is.</summary>
    /// <param name="target">The timestamp in nanoseconds to search for.</param>
    /// <returns>The upper-bound row index in <c>[0, Count]</c>.</returns>
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

    #endregion

    #region Out-of-range failure

    /// <summary>Kept out of line (NoInlining) so the inlined index getters and enumerator factories stay small.</summary>
    /// <param name="index">The rejected index, reported in the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published snapshot range.");

    #endregion
}
