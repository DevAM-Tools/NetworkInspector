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
/// </summary>
/// <typeparam name="T">Payload type selected from the stack <see cref="FieldType"/>.</typeparam>
public interface IReadOnlyValueCacheSeries<T> : IReadOnlyValueCacheSeries
{
    #region Properties

    /// <summary>
    /// Frozen prefix including the value column.
    /// Keep the compile-time type as <see cref="ValueCacheSeriesHandle{T}"/>.
    /// </summary>
    ValueCacheSeriesHandle<T> Handle
    {
        get;
    }

    /// <summary>Gathered row at <paramref name="index"/>.</summary>
    ValueCacheRow<T> this[int index]
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>Payload at <paramref name="index"/> against the live published <see cref="IReadOnlyValueCacheSeries.Count"/>.</summary>
    T GetValue(int index);

    /// <summary>
    /// Returns the published value chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>. Prefer <see cref="Handle"/> for scans.
    /// </summary>
    bool TryGetValueChunk(int chunkIndex, int observedCount, out ReadOnlySpan<T> span);

    #endregion
}
