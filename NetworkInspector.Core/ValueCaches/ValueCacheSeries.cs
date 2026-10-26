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

    // Structure-of-arrays columns. Each store publishes its own count as soon as its append finishes, which is before the
    // value column (on the derived type) has the row. That is why readers must never use the store counts and why
    // _Count below is the only visibility boundary.
    private readonly ChunkedGrowOnlyStore<int> _PacketIds;
    private readonly ChunkedGrowOnlyStore<long> _Timestamps;
    private readonly ValueCaptureMode _CaptureMode;

    // Published row count: written last by the single writer (volatile write = release, so every column slot below
    // this count is visible to a reader that loaded the count). Readers use it as the exclusive upper bound.
    private volatile int _Count;

    // 1 while every published packet id (timestamp) is >= its predecessor, 0 once one decreased; never goes back to 1
    // until ClearKeyColumns. Consumers use the flag to binary-search the column instead of scanning it. The writer
    // clears a flag before it publishes the offending row, so a flag read after Count can only be too pessimistic
    // (0 although the loaded prefix is still sorted), never too optimistic. Hence Handle must load Count first.
    private volatile int _PacketIdsNonDecreasing = 1;
    private volatile int _TimestampsNonDecreasing = 1;

    // Writer-only state (guarded by the series append gate of the derived type); readers never touch these.
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
    /// The count is loaded before the flags (argument evaluation order); that order is what keeps a flag from being
    /// reported as sorted for a prefix that is not (see the flag fields).
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
        _PacketIds.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="Handle"/> for scans.
    /// </summary>
    public bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _Timestamps.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

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
        // Monotonic flags: compare against the previous row only. The published count (not the store count) tells whether
        // a previous row exists. A flag is cleared here, before the new row is published, see the flag fields.
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

        // AppendOwned skips the store's own writer gate: the derived series serializes all appends and clears with its gate.
        _PacketIds.AppendOwned(packetId);
        _Timestamps.AppendOwned(timestampNanos);
        _LastPacketId = packetId;
        _LastTimestampNanos = timestampNanos;
    }

    /// <summary>
    /// Publishes <see cref="Count"/> from the packet-id store after the value column is appended. This single volatile
    /// write is what makes the row visible to readers; the packet-id store holds exactly the appended rows because the
    /// writer appends to it once per row (and the timestamp and value columns before this call).
    /// </summary>
    private protected void PublishCount() =>
        _Count = _PacketIds.Count;

    /// <summary>
    /// Clears packet-id / timestamp stores, count, and monotonic flags. Writer only; the caller holds the series append
    /// gate. Each store swaps in a fresh generation with one volatile write (see <c>ChunkedSlotStore</c>), so readers never
    /// observe a half-cleared column. A clear is destructive: a reader that loaded a count before the clear and indexes
    /// afterwards reads the default values of the fresh generation; only a reader that loads the count after the clear is
    /// consistent (it sees zero rows).
    /// </summary>
    private protected void ClearKeyColumns()
    {
        // Publish the empty count before the stores are swapped. In the other order a reader that loads the count between the
        // swap and the reset would see the old row count over empty columns and read default values for rows that no longer exist.
        _Count = 0;
        _PacketIds.Clear();
        _Timestamps.Clear();
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

    /// <summary>
    /// Clamps a caller-supplied snapshot count to [0, published <see cref="Count"/>]. Chunk readers pass the result as the
    /// published bound, so a caller that passes a stale, negative, or <see cref="int.MaxValue"/> count can never see slots
    /// the writer has not published yet.
    /// </summary>
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

    /// <summary>
    /// Throw helper kept out of line (NoInlining) so the inlined index getters stay small: the cold throw path adds no code
    /// to the callers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private protected static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");

    #endregion
}
