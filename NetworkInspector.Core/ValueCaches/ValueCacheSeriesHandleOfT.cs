// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Frozen prefix of one <see cref="ValueCacheSeries{T}"/> including the value column.
/// Packet-id and timestamp APIs match <see cref="ValueCacheSeriesHandle"/>.
/// <see cref="Count"/> does not grow with later <see cref="ValueCacheSeries{T}.Record"/> calls;
/// take another <see cref="ValueCacheSeries{T}.Handle"/> and <see cref="EnumerateFrom"/> from the
/// previous count to follow the tail.
/// SIMD: <see cref="EnumerateChunks()"/> yields contiguous SoA value spans for unmanaged <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">Payload type of the series.</typeparam>
public readonly struct ValueCacheSeriesHandle<T>
{
    #region Snapshot state

    /// <summary>Live series. Reads are clipped to <see cref="Count"/>; the value column is not copied.</summary>
    private readonly ValueCacheSeries<T> _Series;

    /// <summary>
    /// Wraps the keys-only snapshot with the typed series, so that count, flags, and the key columns come from one frozen
    /// prefix (see <see cref="ValueCacheSeriesHandle"/>) and the value column is read against the same count.
    /// </summary>
    /// <param name="series">The live series.</param>
    /// <param name="keys">The snapshot taken from <paramref name="series"/>; its count bounds every read of this handle.</param>
    internal ValueCacheSeriesHandle(ValueCacheSeries<T> series, ValueCacheSeriesHandle keys)
    {
        _Series = series;
        Keys = keys;
    }

    /// <inheritdoc cref="ValueCacheSeriesHandle.Count"/>
    public int Count => Keys.Count;

    /// <inheritdoc cref="ValueCacheSeriesHandle.PacketIdsMonotonic"/>
    public bool PacketIdsMonotonic => Keys.PacketIdsMonotonic;

    /// <inheritdoc cref="ValueCacheSeriesHandle.TimestampsMonotonic"/>
    public bool TimestampsMonotonic => Keys.TimestampsMonotonic;

    /// <inheritdoc cref="ValueCacheSeriesHandle.ChunkShift"/>
    public int ChunkShift => Keys.ChunkShift;

    /// <summary>Packet-id / timestamp snapshot (no values), taken at the same moment as this handle.</summary>
    public ValueCacheSeriesHandle Keys { get; }

    #endregion

    #region Snapshot reads and walks

    /// <inheritdoc cref="ValueCacheSeriesHandle.GetPacketId"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacketId(int index) =>
        Keys.GetPacketId(index);

    /// <inheritdoc cref="ValueCacheSeriesHandle.GetTimestamp"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp(int index) =>
        Keys.GetTimestamp(index);

    /// <summary>Payload at <paramref name="index"/> in this snapshot.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The stored payload of that row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetValue(int index)
    {
        // Checked against the frozen count, then read without the series' volatile count check: the value slot below the
        // frozen count was written before that count was published.
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return _Series.ValueAt(index);
    }

    /// <summary>Gathered row at <paramref name="index"/>. Each of the three reads checks the frozen count again; use an enumerator for scans.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    public ValueCacheRow<T> this[int index] =>
        new ValueCacheRow<T>(GetPacketId(index), GetTimestamp(index), GetValue(index));

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryFindPacketId"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindPacketId(int packetId, out ValueCacheSeriesRange range) =>
        Keys.TryFindPacketId(packetId, out range);

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryFindPacketIds"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindPacketIds(int minInclusive, int maxInclusive, out ValueCacheSeriesRange range) =>
        Keys.TryFindPacketIds(minInclusive, maxInclusive, out range);

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryFindTimestamp"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindTimestamp(long timestampNanos, out ValueCacheSeriesRange range) =>
        Keys.TryFindTimestamp(timestampNanos, out range);

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryFindTimestamps"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFindTimestamps(long minInclusive, long maxInclusive, out ValueCacheSeriesRange range) =>
        Keys.TryFindTimestamps(minInclusive, maxInclusive, out range);

    /// <summary>Walks this snapshot in row order from index 0, including values. Stops at <see cref="Count"/>.</summary>
    /// <returns>A ref enumerator over the rows <c>[0, Count)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() =>
        new Enumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <inheritdoc cref="ValueCacheSeriesHandle.EnumerateFrom"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator EnumerateFrom(int startIndex)
    {
        if ((uint)startIndex > (uint)Count)
        {
            _ThrowIndexOutOfRange(startIndex);
        }

        return new Enumerator(_Series, startIndex, Count);
    }

    /// <inheritdoc cref="ValueCacheSeriesHandle.Enumerate"/>
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
    /// Value inner chunk at <paramref name="chunkIndex"/>, clipped to <see cref="Count"/>,
    /// starting at slot 0 of that chunk. Prefer <see cref="EnumerateChunksFrom"/> for a watermark slice.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="span">The values of the chunk below <see cref="Count"/>; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row of this snapshot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValueChunk(int chunkIndex, out ReadOnlySpan<T> span) =>
        _Series.TryGetValueChunk(chunkIndex, Count, out span);

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryGetPacketIdChunk"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacketIdChunk(int chunkIndex, out ReadOnlySpan<int> span) =>
        Keys.TryGetPacketIdChunk(chunkIndex, out span);

    /// <inheritdoc cref="ValueCacheSeriesHandle.TryGetTimestampChunk"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTimestampChunk(int chunkIndex, out ReadOnlySpan<long> span) =>
        Keys.TryGetTimestampChunk(chunkIndex, out span);

    /// <summary>Walks SoA chunks from index 0 to <see cref="Count"/>, including values.</summary>
    /// <returns>A ref enumerator over the chunk slices covering <c>[0, Count)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunks() =>
        new ChunkEnumerator(_Series, startIndex: 0, endExclusive: Count);

    /// <inheritdoc cref="ValueCacheSeriesHandle.EnumerateChunksFrom"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator EnumerateChunksFrom(int startIndex)
    {
        if ((uint)startIndex > (uint)Count)
        {
            _ThrowIndexOutOfRange(startIndex);
        }

        return new ChunkEnumerator(_Series, startIndex, Count);
    }

    /// <inheritdoc cref="ValueCacheSeriesHandle.EnumerateChunks(ValueCacheSeriesRange)"/>
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
    /// One contiguous SoA slice of packet ids, timestamps, and values. Lengths match.
    /// For unmanaged <typeparamref name="T"/>, <see cref="Values"/> is the SIMD payload span.
    /// </summary>
    public readonly ref struct Chunk
    {
        /// <summary>Wraps the three column slices of one chunk.</summary>
        /// <param name="startIndex">Series index of the first row of the slices.</param>
        /// <param name="packetIds">Packet ids of the slice.</param>
        /// <param name="timestamps">Timestamps of the slice; the same length as <paramref name="packetIds"/>.</param>
        /// <param name="values">Payloads of the slice; the same length as <paramref name="packetIds"/>.</param>
        internal Chunk(
            int startIndex,
            ReadOnlySpan<int> packetIds,
            ReadOnlySpan<long> timestamps,
            ReadOnlySpan<T> values)
        {
            StartIndex = startIndex;
            PacketIds = packetIds;
            Timestamps = timestamps;
            Values = values;
        }

        /// <summary>Series index of <c>Values[0]</c>.</summary>
        public int StartIndex { get; }

        /// <summary>Packet ids in this slice, aligned with <see cref="Values"/>.</summary>
        public ReadOnlySpan<int> PacketIds { get; }

        /// <summary>Timestamps in this slice, aligned with <see cref="Values"/>.</summary>
        public ReadOnlySpan<long> Timestamps { get; }

        /// <summary>Payloads in this slice. Contiguous; may be shorter than a full inner chunk.</summary>
        public ReadOnlySpan<T> Values { get; }

        /// <summary>Number of rows in this slice.</summary>
        public int Length => Values.Length;
    }

    /// <summary>
    /// Ref enumerator over <see cref="Chunk"/> slices for SIMD / bulk scans. A <c>ref struct</c> holding spans straight into
    /// the series' inner chunk arrays (no copy, no allocation); it cannot cross an <c>await</c> or be stored in a field.
    /// Not thread-safe (one enumerator per thread); the underlying data is stable.
    /// </summary>
    public ref struct ChunkEnumerator
    {
        /// <summary>The series whose chunks are walked.</summary>
        private readonly ValueCacheSeries<T> _Series;

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

        /// <summary>Values of the current slice.</summary>
        private ReadOnlySpan<T> _Values;

        /// <summary>Starts at row <paramref name="startIndex"/>; callers have already validated the bounds against the frozen count.</summary>
        /// <param name="series">The series to walk.</param>
        /// <param name="startIndex">First row to visit.</param>
        /// <param name="endExclusive">Exclusive end row; not above the frozen count of the snapshot.</param>
        internal ChunkEnumerator(ValueCacheSeries<T> series, int startIndex, int endExclusive)
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
            _Values = [];
        }

        /// <summary>Current SoA slice; valid after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
        public readonly Chunk Current =>
            new Chunk(_StartIndex, _PacketIds, _Timestamps, _Values);

        /// <summary>Returns this enumerator so <c>foreach</c> works on chunk walks.</summary>
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

            // Fetch the three columns of the chunk that holds 'next', each clipped to the snapshot end. A failed lookup ends
            // the walk instead of throwing (see ValueCacheSeriesHandle.ChunkEnumerator.MoveNext).
            int chunkIndex = next >> _ChunkShift;
            if (!_Series.TryGetPacketIdChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<int> packetIds)
                || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<long> timestamps)
                || !_Series.TryGetValueChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<T> values))
            {
                return false;
            }

            // Slice from the watermark slot (non-zero only for the first chunk); continue after the clipped chunk.
            int slot = next & _ChunkMask;
            _PacketIds = packetIds[slot..];
            _Timestamps = timestamps[slot..];
            _Values = values[slot..];
            _StartIndex = next;
            int chunkStart = chunkIndex << _ChunkShift;
            _NextIndex = chunkStart + packetIds.Length;
            return true;
        }
    }

    /// <summary>
    /// Ref enumerator over packet-id, timestamp, and value columns for one index span. A <c>ref struct</c> that keeps the
    /// current chunk's spans, so per row it costs three span reads; the chunk lookup happens once per chunk, not once per
    /// row. Cannot cross an <c>await</c>. Not thread-safe; the underlying data is stable.
    /// </summary>
    public ref struct Enumerator
    {
        /// <summary>The series whose rows are walked.</summary>
        private readonly ValueCacheSeries<T> _Series;

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

        /// <summary>Values of the loaded chunk, clipped to the end of the walk.</summary>
        private ReadOnlySpan<T> _Values;

        /// <summary>Packet id of the current row.</summary>
        private int _PacketId;

        /// <summary>Timestamp (nanoseconds) of the current row.</summary>
        private long _TimestampNanos;

        /// <summary>Payload of the current row.</summary>
        private T _Value;

        /// <summary>Starts before row <paramref name="startIndex"/>; callers have already validated the bounds against the frozen count.</summary>
        /// <param name="series">The series to walk.</param>
        /// <param name="startIndex">First row to visit.</param>
        /// <param name="endExclusive">Exclusive end row; not above the frozen count of the snapshot.</param>
        internal Enumerator(ValueCacheSeries<T> series, int startIndex, int endExclusive)
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
            _Values = [];
            _PacketId = 0;
            _TimestampNanos = 0;
            _Value = default!; // Invariant: Current reads _Value only after MoveNext stored a published value.
        }

        /// <summary>Current gathered row; valid after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
        public readonly ValueCacheRow<T> Current =>
            new ValueCacheRow<T>(_PacketId, _TimestampNanos, _Value);

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

            // Reload the column spans only when the walk crosses into another chunk; a failed lookup ends the walk early.
            int chunkIndex = next >> _ChunkShift;
            if (chunkIndex != _LoadedChunk)
            {
                if (!_Series.TryGetPacketIdChunk(chunkIndex, _EndExclusive, out _PacketIds)
                    || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out _Timestamps)
                    || !_Series.TryGetValueChunk(chunkIndex, _EndExclusive, out _Values))
                {
                    return false;
                }

                _LoadedChunk = chunkIndex;
            }

            int slot = next & _ChunkMask;
            _PacketId = _PacketIds[slot];
            _TimestampNanos = _Timestamps[slot];
            _Value = _Values[slot];
            _Index = next;
            return true;
        }
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
