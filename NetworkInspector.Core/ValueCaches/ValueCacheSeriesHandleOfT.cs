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
    #region Fields

    /// <summary>Live series. Reads are clipped to <see cref="Count"/>; the value column is not copied.</summary>
    private readonly ValueCacheSeries<T> _Series;

    #endregion

    #region Constructors

    internal ValueCacheSeriesHandle(ValueCacheSeries<T> series, ValueCacheSeriesHandle keys)
    {
        _Series = series;
        Keys = keys;
    }

    #endregion

    #region Properties

    /// <inheritdoc cref="ValueCacheSeriesHandle.Count"/>
    public int Count => Keys.Count;

    /// <inheritdoc cref="ValueCacheSeriesHandle.PacketIdsMonotonic"/>
    public bool PacketIdsMonotonic => Keys.PacketIdsMonotonic;

    /// <inheritdoc cref="ValueCacheSeriesHandle.TimestampsMonotonic"/>
    public bool TimestampsMonotonic => Keys.TimestampsMonotonic;

    /// <inheritdoc cref="ValueCacheSeriesHandle.ChunkShift"/>
    public int ChunkShift => Keys.ChunkShift;

    /// <summary>Packet-id / timestamp snapshot (no values).</summary>
    public ValueCacheSeriesHandle Keys { get; }

    #endregion

    #region Public API

    /// <inheritdoc cref="ValueCacheSeriesHandle.GetPacketId"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacketId(int index) =>
        Keys.GetPacketId(index);

    /// <inheritdoc cref="ValueCacheSeriesHandle.GetTimestamp"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp(int index) =>
        Keys.GetTimestamp(index);

    /// <summary>Payload at <paramref name="index"/> in this snapshot.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetValue(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return _Series.ValueAt(index);
    }

    /// <summary>Gathered row at <paramref name="index"/>.</summary>
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

    #region Nested types

    /// <summary>
    /// One contiguous SoA slice of packet ids, timestamps, and values. Lengths match.
    /// For unmanaged <typeparamref name="T"/>, <see cref="Values"/> is the SIMD payload span.
    /// </summary>
    public readonly ref struct Chunk
    {
        #region Constructors

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

        #endregion

        #region Properties

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

        #endregion
    }

    /// <summary>Ref enumerator over <see cref="Chunk"/> slices for SIMD / bulk scans.</summary>
    public ref struct ChunkEnumerator
    {
        #region Fields

        private readonly ValueCacheSeries<T> _Series;
        private readonly int _EndExclusive;
        private readonly int _ChunkShift;
        private readonly int _ChunkMask;
        private int _NextIndex;
        private int _StartIndex;
        private ReadOnlySpan<int> _PacketIds;
        private ReadOnlySpan<long> _Timestamps;
        private ReadOnlySpan<T> _Values;

        #endregion

        #region Constructors

        internal ChunkEnumerator(ValueCacheSeries<T> series, int startIndex, int endExclusive)
        {
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

        #endregion

        #region Properties

        /// <summary>Current SoA slice.</summary>
        public readonly Chunk Current =>
            new Chunk(_StartIndex, _PacketIds, _Timestamps, _Values);

        #endregion

        #region Public API

        /// <summary>Returns this enumerator so <c>foreach</c> works on chunk walks.</summary>
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
                || !_Series.TryGetTimestampChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<long> timestamps)
                || !_Series.TryGetValueChunk(chunkIndex, _EndExclusive, out ReadOnlySpan<T> values))
            {
                return false;
            }

            int slot = next & _ChunkMask;
            _PacketIds = packetIds[slot..];
            _Timestamps = timestamps[slot..];
            _Values = values[slot..];
            _StartIndex = next;
            int chunkStart = chunkIndex << _ChunkShift;
            _NextIndex = chunkStart + packetIds.Length;
            return true;
        }

        #endregion
    }

    /// <summary>Ref enumerator over packet-id, timestamp, and value columns for one index span.</summary>
    public ref struct Enumerator
    {
        #region Fields

        private readonly ValueCacheSeries<T> _Series;
        private readonly int _EndExclusive;
        private readonly int _ChunkShift;
        private readonly int _ChunkMask;
        private int _Index;
        private int _LoadedChunk;
        private ReadOnlySpan<int> _PacketIds;
        private ReadOnlySpan<long> _Timestamps;
        private ReadOnlySpan<T> _Values;
        private int _PacketId;
        private long _TimestampNanos;
        private T _Value;

        #endregion

        #region Constructors

        internal Enumerator(ValueCacheSeries<T> series, int startIndex, int endExclusive)
        {
            _Series = series;
            _EndExclusive = endExclusive;
            _ChunkShift = series.ChunkShift;
            _ChunkMask = (1 << _ChunkShift) - 1;
            _Index = startIndex - 1;
            _LoadedChunk = -1;
            _PacketIds = [];
            _Timestamps = [];
            _Values = [];
            _PacketId = 0;
            _TimestampNanos = 0;
            _Value = default!;
        }

        #endregion

        #region Properties

        /// <summary>Current gathered row.</summary>
        public readonly ValueCacheRow<T> Current =>
            new ValueCacheRow<T>(_PacketId, _TimestampNanos, _Value);

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

        #endregion
    }

    #endregion

    #region Private helpers

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published snapshot range.");

    #endregion
}
