// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Thread-safe read-only interface for querying cached field values.
/// Exposed through <see cref="IPacketIndexReader"/> to external consumers.
/// </summary>
public interface IValueCacheReader
{
    #region Methods

    /// <summary>Returns <see langword="true"/> if a cache series exists for the given field.</summary>
    bool HasSeries(FieldId fieldId);

    /// <summary>
    /// Returns the cache series for the given field, or <see langword="null"/> if not cached.
    /// </summary>
    ValueCacheSeries? GetSeries(FieldId fieldId);

    /// <summary>
    /// Returns the cache series for the given field.
    /// Throws <see cref="InvalidOperationException"/> if not cached.
    /// </summary>
    /// <exception cref="InvalidOperationException">No cache exists for the given field.</exception>
    ValueCacheSeries GetSeriesRequired(FieldId fieldId);

    /// <summary>
    /// Tries to get the cache series for the given field.
    /// Returns <see langword="true"/> and the series if found, <see langword="false"/> otherwise.
    /// </summary>
    bool TryGetSeries(FieldId fieldId, [NotNullWhen(true)] out ValueCacheSeries? series);

    #endregion

    #region Properties

    /// <summary>List of all cached field IDs.</summary>
    IReadOnlyList<FieldId> CachedFields
    {
        get;
    }

    /// <summary>Number of cached fields.</summary>
    int SeriesCount
    {
        get;
    }

    /// <summary>Total memory used by all series combined (bytes).</summary>
    long TotalMemoryUsage
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>Returns detailed info about all cached series for profiling/diagnostics.</summary>
    IReadOnlyList<ValueCacheFieldInfo> GetFieldInfos();

    #endregion
}
