// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Read-only façade for one recorded series (packet-id and timestamp columns).
/// Mutation (<c>Record</c>, <c>ClearRows</c>) is not on this surface.
/// <para>
/// <b>Boxing:</b> <see cref="ValueCacheSeries"/> is a class — storing it as this interface does
/// not allocate. <see cref="ReadOnlyValueCacheSeries"/> is a struct; casting the view, storing it
/// in an <see cref="IReadOnlyValueCacheSeries"/> field, or passing it to a non-generic parameter
/// of this type boxes. Hot-path APIs must take
/// <c>TSeries where TSeries : IReadOnlyValueCacheSeries</c> (or the concrete series / struct).
/// </para>
/// <para>
/// Thread safety: every member is a read that may run on any thread while the single writer appends. The live
/// <see cref="Count"/> is the only visibility boundary: indexed reads are valid strictly below a count that was loaded
/// first. For repeatable scans take a frozen prefix with <see cref="GetHandle"/>.
/// </para>
/// </summary>
public interface IReadOnlyValueCacheSeries
{
    #region Published state

    /// <summary>Recorded field.</summary>
    FieldId FieldId
    {
        get;
    }

    /// <summary>Log₂ of rows per inner column chunk.</summary>
    int ChunkShift
    {
        get;
    }

    /// <summary>
    /// Payload <see cref="Fields.FieldType"/> of the stack field, or <see cref="FieldType.String"/>
    /// for custom-text/representation series.
    /// </summary>
    FieldType FieldType
    {
        get;
    }

    /// <summary>Capture mode used when recording occurrences.</summary>
    ValueCaptureMode CaptureMode
    {
        get;
    }

    /// <summary>
    /// Number of published rows. Volatile; readers must treat this as the exclusive upper bound
    /// for indexed reads.
    /// </summary>
    int Count
    {
        get;
    }

    /// <summary>True when published packet ids are still non-decreasing.</summary>
    bool PacketIdsMonotonic
    {
        get;
    }

    /// <summary>True when published timestamps are still non-decreasing.</summary>
    bool TimestampsMonotonic
    {
        get;
    }

    #endregion

    #region Reads

    /// <summary>
    /// Frozen prefix: loads <see cref="Count"/> and monotonic flags once.
    /// Prefer the <c>Handle</c> property on the concrete series type (already a struct).
    /// This method is the untyped interface surface so <see cref="IReadOnlyValueCacheSeries{T}"/>
    /// can expose a typed <see cref="ValueCacheSeriesHandle{T}"/> as <c>Handle</c>.
    /// </summary>
    /// <returns>A snapshot of the packet-id and timestamp columns; rows published later are not visible on it.</returns>
    ValueCacheSeriesHandle GetHandle();

    /// <summary>Packet id at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet id of that row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    int GetPacketId(int index);

    /// <summary>Timestamp (nanoseconds) at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The packet timestamp of that row in nanoseconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    long GetTimestamp(int index);

    /// <summary>
    /// Returns the published packet-id chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="GetHandle"/> or the concrete <c>Handle</c> for scans.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="observedCount">
    /// Exclusive upper bound of the rows to expose, normally a <see cref="Count"/> the caller loaded earlier. A negative value
    /// is treated as 0; a value above the published count is lowered to it, so unpublished rows are never exposed.
    /// </param>
    /// <param name="span">The rows of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="GetHandle"/> or the concrete <c>Handle</c> for scans.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ChunkShift"/>).</param>
    /// <param name="observedCount">
    /// Exclusive upper bound of the rows to expose, normally a <see cref="Count"/> the caller loaded earlier. A negative value
    /// is treated as 0; a value above the published count is lowered to it, so unpublished rows are never exposed.
    /// </param>
    /// <param name="span">The rows of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span);

    #endregion
}
