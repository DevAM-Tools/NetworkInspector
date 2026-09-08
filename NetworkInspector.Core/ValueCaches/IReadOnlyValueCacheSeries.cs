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
/// </summary>
public interface IReadOnlyValueCacheSeries
{
    #region Properties

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

    #region Methods

    /// <summary>
    /// Frozen prefix: loads <see cref="Count"/> and monotonic flags once.
    /// Prefer the <c>Handle</c> property on the concrete series type (already a struct).
    /// This method is the untyped interface surface so <see cref="IReadOnlyValueCacheSeries{T}"/>
    /// can expose a typed <see cref="ValueCacheSeriesHandle{T}"/> as <c>Handle</c>.
    /// </summary>
    ValueCacheSeriesHandle GetHandle();

    /// <summary>Packet id at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    int GetPacketId(int index);

    /// <summary>Timestamp (nanoseconds) at <paramref name="index"/> against the live published <see cref="Count"/>.</summary>
    long GetTimestamp(int index);

    /// <summary>
    /// Returns the published packet-id chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="GetHandle"/> or the concrete <c>Handle</c> for scans.
    /// </summary>
    bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="GetHandle"/> or the concrete <c>Handle</c> for scans.
    /// </summary>
    bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span);

    #endregion
}
