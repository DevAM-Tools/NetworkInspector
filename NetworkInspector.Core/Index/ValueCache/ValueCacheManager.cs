// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Mutable container that holds <see cref="ValueCacheSeries"/> instances and implements
/// <see cref="IValueCacheReader"/>. Thread-safe: series are published via
/// <see cref="Volatile.Write{T}"/> on the backing dictionary reference; readers holding
/// an older snapshot continue to work without crashing.
/// <para>
/// <b>Series are append-only:</b> Once a series is registered (via <see cref="TryAddSeries"/>
/// or <see cref="SetSeries"/>) it stays in the manager for the lifetime of the index. There is
/// no removal API — this guarantees readers can hold a series reference indefinitely without
/// risk of seeing a torn or recycled entry.
/// </para>
/// </summary>
internal sealed class ValueCacheManager : IValueCacheReader
{
    #region Fields

    // Copy-on-write dictionary: replaced atomically on add/remove.
    // Reads are lock-free via Volatile.Read.
    private Dictionary<int, ValueCacheSeries> _Series;

    #endregion

    #region Constructors

    /// <summary>Creates an empty value cache manager.</summary>
    internal ValueCacheManager()
    {
        _Series = new Dictionary<int, ValueCacheSeries>();
    }

    #endregion

    #region Internal API

    // ── Mutation (internal, under external serialization) ────

    /// <summary>
    /// Publishes a completed series. Returns <see langword="false"/> if a series for this field already exists.
    /// </summary>
    internal bool TryAddSeries(ValueCacheSeries series)
    {
        Dictionary<int, ValueCacheSeries> snapshot = _Series;
        int key = series.FieldId.Value;
        if (snapshot.ContainsKey(key))
        {
            return false;
        }

        // Copy-on-write: create new dictionary with the addition
        Dictionary<int, ValueCacheSeries> next = new(snapshot.Count + 1);
        foreach (KeyValuePair<int, ValueCacheSeries> kv in snapshot)
        {
            next[kv.Key] = kv.Value;
        }
        next[key] = series;
        Volatile.Write(ref _Series, next);
        return true;
    }

    /// <summary>Replaces or adds a series (unconditionally).</summary>
    internal void SetSeries(ValueCacheSeries series)
    {
        Dictionary<int, ValueCacheSeries> snapshot = _Series;
        int key = series.FieldId.Value;

        Dictionary<int, ValueCacheSeries> next = new(snapshot.Count + 1);
        foreach (KeyValuePair<int, ValueCacheSeries> kv in snapshot)
        {
            next[kv.Key] = kv.Value;
        }
        next[key] = series;
        Volatile.Write(ref _Series, next);
    }

    #endregion

    #region IValueCacheReader

    // ── IValueCacheReader ────────────────────────────────────
    // All reader methods use Volatile.Read(ref _Series) to acquire-fence the
    // copy-on-write dictionary reference published by Volatile.Write in writers.
    // Without this, ARM and other weakly-ordered architectures can see a stale
    // dictionary reference, missing newly added or updated series.

    /// <inheritdoc/>
    public bool HasSeries(FieldId fieldId) =>
        Volatile.Read(ref _Series).ContainsKey(fieldId.Value);

    /// <inheritdoc/>
    public ValueCacheSeries? GetSeries(FieldId fieldId) =>
        Volatile.Read(ref _Series).TryGetValue(fieldId.Value, out ValueCacheSeries? series) ? series : null;

    /// <inheritdoc/>
    public ValueCacheSeries GetSeriesRequired(FieldId fieldId)
    {
        if (!Volatile.Read(ref _Series).TryGetValue(fieldId.Value, out ValueCacheSeries? series))
        {
            throw new InvalidOperationException(
                $"No ValueCache series for field {fieldId.Value}. Check HasSeries() before calling GetSeriesRequired().");
        }
        return series;
    }

    /// <inheritdoc/>
    public bool TryGetSeries(FieldId fieldId, [NotNullWhen(true)] out ValueCacheSeries? series) =>
        Volatile.Read(ref _Series).TryGetValue(fieldId.Value, out series);

    /// <inheritdoc/>
    public IReadOnlyList<FieldId> CachedFields
    {
        get
        {
            Dictionary<int, ValueCacheSeries> snapshot = Volatile.Read(ref _Series);
            FieldId[] fields = new FieldId[snapshot.Count];
            int idx = 0;
            foreach (int key in snapshot.Keys)
            {
                fields[idx++] = new FieldId(key);
            }
            return fields;
        }
    }

    /// <inheritdoc/>
    public int SeriesCount => Volatile.Read(ref _Series).Count;

    /// <inheritdoc/>
    public long TotalMemoryUsage
    {
        get
        {
            long total = 0;
            foreach (ValueCacheSeries series in Volatile.Read(ref _Series).Values)
            {
                total += series.MemoryUsage;
            }
            return total;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ValueCacheFieldInfo> GetFieldInfos()
    {
        Dictionary<int, ValueCacheSeries> snapshot = Volatile.Read(ref _Series);
        ValueCacheFieldInfo[] infos = new ValueCacheFieldInfo[snapshot.Count];
        int idx = 0;
        foreach (ValueCacheSeries series in snapshot.Values)
        {
            infos[idx++] = new ValueCacheFieldInfo
            {
                FieldId = series.FieldId,
                OriginalFieldType = series.OriginalFieldType,
                StorageMode = series.StorageMode,
                EntryCount = series.Count,
                MemoryUsage = series.MemoryUsage,
                Completeness = series.Completeness,
            };
        }
        return infos;
    }

    #endregion
}
