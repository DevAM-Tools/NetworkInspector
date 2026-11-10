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
    #region Configuration and state

    /// <summary>
    /// Packet-id column of the structure-of-arrays layout. The store publishes its own count as soon as its append finishes,
    /// which is before the value column (on the derived type) has the row. That is why readers must never use the store
    /// counts and why <see cref="_Count"/> is the only visibility boundary.
    /// </summary>
    private readonly ChunkedGrowOnlyStore<int> _PacketIds;

    /// <summary>Timestamp column (nanoseconds) of the structure-of-arrays layout. Same publication rule as <see cref="_PacketIds"/>.</summary>
    private readonly ChunkedGrowOnlyStore<long> _Timestamps;

    /// <summary>How the writer treats several occurrences of the field in one packet; fixed at construction.</summary>
    private readonly ValueCaptureMode _CaptureMode;

    /// <summary>
    /// Published row count: written last by the single writer (volatile write = release, so every column slot below
    /// this count is visible to a reader that loaded the count). Readers use it as the exclusive upper bound.
    /// </summary>
    private volatile int _Count;

    /// <summary>
    /// 1 while every published packet id is at least its predecessor, 0 once one decreased; never goes back to 1
    /// until <see cref="ClearKeyColumns"/>. Consumers use the flag to binary-search the column instead of scanning it. The
    /// writer clears a flag before it publishes the offending row, so a flag read after <see cref="_Count"/> can only be too
    /// pessimistic (0 although the loaded prefix is still sorted), never too optimistic. Hence <see cref="Handle"/> must
    /// load the count first.
    /// </summary>
    private volatile int _PacketIdsNonDecreasing = 1;

    /// <summary>The same flag as <see cref="_PacketIdsNonDecreasing"/> for the timestamp column, with the same ordering rule.</summary>
    private volatile int _TimestampsNonDecreasing = 1;

    /// <summary>
    /// Packet id of the last appended row, or -1 when empty. Writer-only state (guarded by the series append gate of the
    /// derived type); readers never touch it.
    /// </summary>
    private int _LastPacketId = -1;

    /// <summary>Timestamp of the last appended row. Writer-only state, same guard as <see cref="_LastPacketId"/>.</summary>
    private long _LastTimestampNanos;

    /// <summary>Creates empty packet-id and timestamp stores.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <param name="fieldType">Payload type of the stack field, or <see cref="FieldType.String"/> for text series.</param>
    /// <param name="captureMode">How several occurrences in one packet are captured.</param>
    /// <param name="chunkShift">Log₂ of rows per inner column chunk, in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is outside [4, 20] (rejected by the column stores).</exception>
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

    #region Published state and reads

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

    /// <summary>Packet id at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet id of that row.</returns>
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
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet timestamp of that row in nanoseconds.</returns>
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
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="observedCount">
    /// Exclusive upper bound of the rows to expose. A negative value is treated as 0; a value above the published count is
    /// lowered to it, so unpublished rows are never exposed.
    /// </param>
    /// <param name="span">The rows of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    public bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _PacketIds.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="Handle"/> for scans.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="observedCount">
    /// Exclusive upper bound of the rows to expose. A negative value is treated as 0; a value above the published count is
    /// lowered to it, so unpublished rows are never exposed.
    /// </param>
    /// <param name="span">The rows of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    public bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _Timestamps.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Zero-allocation read-only view. Keep the compile-time type as
    /// <see cref="ReadOnlyValueCacheSeries"/>; assigning to <see cref="IReadOnlyValueCacheSeries"/> boxes.
    /// </summary>
    /// <returns>A view that forwards to this series and offers no writer members.</returns>
    public ReadOnlyValueCacheSeries AsReadOnlyView() => new(this);

    /// <inheritdoc/>
    public ValueCacheSeriesHandle GetHandle() => Handle;

    #endregion

    #region Row append and clear

    /// <summary>Last appended packet id, or -1 when empty. Writer only (FirstOccurrence skip).</summary>
    private protected int LastPacketId => _LastPacketId;

    /// <summary>Drops published rows so this series can be filled again. Writer only.</summary>
    internal abstract void ClearRows();

    /// <summary>
    /// Appends packet-id and timestamp columns and updates monotonic flags. Does not publish
    /// <see cref="Count"/>. Writer only; caller holds the series append gate.
    /// </summary>
    /// <param name="packetId">Packet id of the new row.</param>
    /// <param name="timestampNanos">Packet timestamp of the new row in nanoseconds.</param>
    /// <exception cref="InvalidOperationException">The store reached its maximum number of rows.</exception>
    private protected void AppendKeys(int packetId, long timestampNanos)
    {
        // Monotonic flags: compare against the previous row only. The published count (not the store count) tells whether
        // a previous row exists. The comparison is made first but the flag is cleared only after the first append succeeded:
        // an index-limit failure must leave the series exactly as it was. The flag is still cleared before the new row is
        // published, see the flag fields.
        bool packetIdsDecrease = _Count != 0 && packetId < _LastPacketId;
        bool timestampsDecrease = _Count != 0 && timestampNanos < _LastTimestampNanos;

        // AppendOwned skips the store's own writer gate: the derived series serializes all appends and clears with its gate.
        // All stores share one row index, so an index-limit failure surfaces from this first append, before anything changed.
        _PacketIds.AppendOwned(packetId);
        _Timestamps.AppendOwned(timestampNanos);
        if (packetIdsDecrease)
        {
            _PacketIdsNonDecreasing = 0;
        }

        if (timestampsDecrease)
        {
            _TimestampsNonDecreasing = 0;
        }

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

    #endregion

    #region Unchecked reads and bounds

    /// <summary>Packet id with no live-<see cref="Count"/> check. Caller proved <paramref name="index"/> is in a snapshot prefix.</summary>
    /// <param name="index">Row index below a count the caller loaded earlier.</param>
    /// <returns>The packet id of that row; the unset default for an index that is not published.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int PacketIdAt(int index) =>
        _PacketIds.Get(index);

    /// <summary>Timestamp with no live-<see cref="Count"/> check. Caller proved <paramref name="index"/> is in a snapshot prefix.</summary>
    /// <param name="index">Row index below a count the caller loaded earlier.</param>
    /// <returns>The timestamp of that row in nanoseconds; the unset default for an index that is not published.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long TimestampAt(int index) =>
        _Timestamps.Get(index);

    /// <summary>
    /// Clamps a caller-supplied snapshot count to [0, published <see cref="Count"/>]. Chunk readers pass the result as the
    /// published bound, so a caller that passes a stale, negative, or <see cref="int.MaxValue"/> count can never see slots
    /// the writer has not published yet.
    /// </summary>
    /// <param name="observedCount">The caller's count; any value.</param>
    /// <returns>The count, lowered to the published count and raised to 0.</returns>
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

    /// <summary>
    /// Throw helper kept out of line (NoInlining) so the inlined index getters stay small: the cold throw path adds no code
    /// to the callers.
    /// </summary>
    /// <param name="index">The rejected index, reported in the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private protected static void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");

    #endregion
}
