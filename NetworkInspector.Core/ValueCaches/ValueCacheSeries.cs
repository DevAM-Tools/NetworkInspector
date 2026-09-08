// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Non-generic reader façade for one recorded series (payload, custom text, or custom representation).
/// Packet-id and timestamp columns live here so <see cref="Handle"/>, <see cref="GetPacketId"/>, and
/// <see cref="TryGetPacketIdChunk"/> are not virtual. The value column stays on
/// <see cref="ValueCacheSeries{T}"/>.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. The writer calls
/// <see cref="ValueCacheSeries{T}.Record"/> (or <see cref="ValueCache.RecordPacket"/> /
/// parse-time record). Concurrent readers load <see cref="Count"/> or open a <see cref="Handle"/>
/// and read indexes strictly less than that count. A row becomes visible as soon as its three
/// columns are appended and <see cref="Count"/> is published; readers may see a packet that is still
/// being parsed. There is no public growth callback on Core <see cref="ValueCache"/>; poll
/// <see cref="Count"/>. Contended <see cref="ValueCacheSeries{T}.Record"/> waits on a series-level
/// CAS gate so the three column appends stay one row.
/// </para>
/// </summary>
public abstract class ValueCacheSeries : IReadOnlyValueCacheSeries
{
    #region Fields

    private readonly ChunkedGrowOnlyStore<int> _PacketIds;
    private readonly ChunkedGrowOnlyStore<long> _Timestamps;
    private readonly ValueCaptureMode _CaptureMode;
    private volatile int _Count;
    private volatile int _PacketIdsNonDecreasing = 1;
    private volatile int _TimestampsNonDecreasing = 1;
    private int _LastPacketId = -1;
    private long _LastTimestampNanos;

    #endregion

    #region Constructors

    /// <summary>Creates empty packet-id and timestamp stores. <paramref name="chunkShift"/> must be in [4, 20].</summary>
    private protected ValueCacheSeries(
        FieldId fieldId,
        FieldType fieldType,
        ValueCaptureMode captureMode,
        int chunkShift)
    {
        FieldId = fieldId;
        FieldType = fieldType;
        ChunkShift = chunkShift;
        _CaptureMode = captureMode;
        _PacketIds = new(chunkShift);
        _Timestamps = new(chunkShift);
    }

    #endregion

    #region Properties

    /// <summary>Recorded field.</summary>
    public FieldId FieldId { get; }

    /// <summary>Log₂ of rows per inner column chunk. Same value passed into every store.</summary>
    public int ChunkShift { get; }

    /// <summary>
    /// Payload <see cref="Fields.FieldType"/> of the stack field, or <see cref="FieldType.String"/>
    /// for custom-text/representation series.
    /// </summary>
    public FieldType FieldType { get; }

    /// <summary>Capture mode used when recording occurrences.</summary>
    public ValueCaptureMode CaptureMode => _CaptureMode;

    /// <summary>
    /// Number of published rows. Volatile; readers must treat this as the exclusive upper bound
    /// for indexed reads. Do not use column-store <c>Count</c> values.
    /// </summary>
    public int Count => _Count;

    /// <summary>
    /// Frozen prefix: loads <see cref="Count"/> and monotonic flags once. Index, range, and
    /// <c>foreach</c> ignore rows published after this load. Take another handle to follow the tail.
    /// </summary>
    public ValueCacheSeriesHandle Handle =>
        new ValueCacheSeriesHandle(this, _Count, _PacketIdsNonDecreasing != 0, _TimestampsNonDecreasing != 0);

    /// <summary>True when published packet ids are still non-decreasing.</summary>
    public bool PacketIdsMonotonic => _PacketIdsNonDecreasing != 0;

    /// <summary>True when published timestamps are still non-decreasing.</summary>
    public bool TimestampsMonotonic => _TimestampsNonDecreasing != 0;

    /// <summary>Last appended packet id, or -1 when empty. Writer only (FirstOccurrence skip).</summary>
    private protected int LastPacketId => _LastPacketId;

    #endregion

    #region Public API

    /// <summary>Packet id at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacketId(int index)
    {
        if ((uint)index >= (uint)_Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return PacketIdAt(index);
    }

    /// <summary>Timestamp (nanoseconds) at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp(int index)
    {
        if ((uint)index >= (uint)_Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return TimestampAt(index);
    }

    /// <summary>
    /// Returns the published packet-id chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/> (the caller's previously loaded <see cref="Count"/>).
    /// Prefer <see cref="Handle"/> for scans.
    /// </summary>
    public bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _PacketIds.TryGetPublishedChunk(chunkIndex, _ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="Handle"/> for scans.
    /// </summary>
    public bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _Timestamps.TryGetPublishedChunk(chunkIndex, _ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Zero-allocation read-only view. Keep the compile-time type as
    /// <see cref="ReadOnlyValueCacheSeries"/>; assigning to <see cref="IReadOnlyValueCacheSeries"/> boxes.
    /// </summary>
    public ReadOnlyValueCacheSeries AsReadOnlyView() => new(this);

    /// <inheritdoc/>
    public ValueCacheSeriesHandle GetHandle() => Handle;

    #endregion

    #region Internal lifecycle

    /// <summary>Drops published rows so this series can be filled again. Writer only.</summary>
    internal abstract void ClearRows();

    #endregion

    #region Private protected helpers

    /// <summary>
    /// Appends packet-id and timestamp columns and updates monotonic flags. Does not publish
    /// <see cref="Count"/>. Writer only; caller holds the series append gate.
    /// </summary>
    private protected void AppendKeys(int packetId, long timestampNanos)
    {
        if (_Count != 0)
        {
            if (packetId < _LastPacketId)
            {
                _PacketIdsNonDecreasing = 0;
            }

            if (timestampNanos < _LastTimestampNanos)
            {
                _TimestampsNonDecreasing = 0;
            }
        }

        _PacketIds.Append(packetId);
        _Timestamps.Append(timestampNanos);
        _LastPacketId = packetId;
        _LastTimestampNanos = timestampNanos;
    }

    /// <summary>Publishes <see cref="Count"/> from the packet-id store after the value column is appended.</summary>
    private protected void PublishCount() =>
        _Count = _PacketIds.Count;

    /// <summary>Clears packet-id / timestamp stores, count, and monotonic flags. Writer only.</summary>
    private protected void ClearKeyColumns()
    {
        _PacketIds.Clear();
        _Timestamps.Clear();
        _Count = 0;
        _LastPacketId = -1;
        _LastTimestampNanos = 0;
        _PacketIdsNonDecreasing = 1;
        _TimestampsNonDecreasing = 1;
    }

    /// <summary>Packet id with no live-<see cref="Count"/> check. Caller proved <paramref name="index"/> is in a snapshot prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int PacketIdAt(int index) =>
        _PacketIds.Get(index);

    /// <summary>Timestamp with no live-<see cref="Count"/> check. Caller proved <paramref name="index"/> is in a snapshot prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long TimestampAt(int index) =>
        _Timestamps.Get(index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected int ClipObservedCount(int observedCount)
    {
        if (observedCount < 0)
        {
            return 0;
        }

        int published = _Count;
        if (observedCount > published)
        {
            return published;
        }

        return observedCount;
    }

    #endregion

    #region Private helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ClipObservedCount(int observedCount) =>
        ClipObservedCount(observedCount);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private protected static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");

    #endregion
}
