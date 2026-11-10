// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Read-only façade for one typed series (payload column plus keys on
/// <see cref="IReadOnlyValueCacheSeries"/>).
/// <para>
/// <b>Boxing:</b> <see cref="ValueCacheSeries{T}"/> is a class. <see cref="ReadOnlyValueCacheSeries{T}"/>
/// is a struct; do not store it as <see cref="IReadOnlyValueCacheSeries{T}"/>. Prefer
/// <c>TSeries where TSeries : IReadOnlyValueCacheSeries{T}</c>.
/// </para>
/// <para>
/// Thread safety: as for <see cref="IReadOnlyValueCacheSeries"/>; the value column is covered by the same published
/// <see cref="IReadOnlyValueCacheSeries.Count"/> (a row is visible only when all three columns hold it).
/// </para>
/// </summary>
/// <typeparam name="T">Payload type selected from the stack <see cref="FieldType"/>.</typeparam>
public interface IReadOnlyValueCacheSeries<T> : IReadOnlyValueCacheSeries
{
    /// <summary>
    /// Frozen prefix including the value column.
    /// Keep the compile-time type as <see cref="ValueCacheSeriesHandle{T}"/>.
    /// </summary>
    ValueCacheSeriesHandle<T> Handle
    {
        get;
    }

    /// <summary>Gathered row at <paramref name="index"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    ValueCacheRow<T> this[int index]
    {
        get;
    }

    /// <summary>Payload at <paramref name="index"/> against the live published <see cref="IReadOnlyValueCacheSeries.Count"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The stored payload of that row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    T GetValue(int index);

    /// <summary>
    /// Returns the published value chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="Handle"/> for scans.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="IReadOnlyValueCacheSeries.ChunkShift"/>).</param>
    /// <param name="observedCount">
    /// Exclusive upper bound of the rows to expose, normally a count the caller loaded earlier. A negative value is treated as 0;
    /// a value above the published count is lowered to it, so unpublished rows are never exposed.
    /// </param>
    /// <param name="span">The values of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    bool TryGetValueChunk(int chunkIndex, int observedCount, out ReadOnlySpan<T> span);
}
