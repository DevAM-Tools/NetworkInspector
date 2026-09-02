// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Zero-allocation read-only view over a <see cref="ValueCache"/>.
/// <para>
/// Warning: do not cast this struct to <c>object</c> or store it in an interface local.
/// Those conversions box. Session APIs return this struct by value.
/// </para>
/// </summary>
public readonly struct ValueCacheReaderView
{
    #region Fields

    private ValueCache _Cache { get; }

    #endregion

    #region Constructors

    /// <summary>Creates a view over <paramref name="cache"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is <see langword="null"/>.</exception>
    public ValueCacheReaderView(ValueCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _Cache = cache;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The live cache this view aliases, or <see langword="null"/> when the view is
    /// <c>default</c> (not constructed via <see cref="ValueCacheReaderView(ValueCache)"/>).
    /// Internal so Session listeners cannot call <see cref="ValueCache.RecordPacket"/>
    /// on a parse-time writer.
    /// </summary>
    internal ValueCache? Source => _Cache;

    /// <summary>
    /// Whether the aliased writer was evicted. <see langword="false"/> when this view is
    /// <c>default</c>.
    /// </summary>
    public bool IsAbandoned => _Cache is { IsAbandoned: true };

    /// <summary>Stack this cache was built against.</summary>
    public Stack Stack => _Cache.Stack;

    /// <summary>Whether construction used <see cref="ValueCacheBuildOptions.RecordAllFields"/>.</summary>
    public bool RecordAllFields => _Cache.RecordAllFields;

    /// <summary>Sticky strictly-increasing packet-id flag. See <see cref="ValueCache.PacketIdsStrictlyIncreasing"/>.</summary>
    public bool PacketIdsStrictlyIncreasing => _Cache.PacketIdsStrictlyIncreasing;

    /// <summary>Sticky strictly-increasing timestamp flag. See <see cref="ValueCache.TimestampsStrictlyIncreasing"/>.</summary>
    public bool TimestampsStrictlyIncreasing => _Cache.TimestampsStrictlyIncreasing;

    /// <summary>Sticky capacity flag. See <see cref="ValueCache.IsCapacityReached"/>.</summary>
    public bool IsCapacityReached => _Cache.IsCapacityReached;

    /// <summary>Sticky materialization-cap flag. See <see cref="ValueCache.IsMaterializationIncomplete"/>.</summary>
    public bool IsMaterializationIncomplete => _Cache.IsMaterializationIncomplete;

    /// <summary>Sum of series <see cref="ValueCacheSeries.ByteSize"/> values.</summary>
    public long ByteSize => _Cache.ByteSize;

    /// <summary>All series façades.</summary>
    public IReadOnlyList<ValueCacheSeries> Series => _Cache.Series;

    #endregion

    #region Public API

    /// <inheritdoc cref="ValueCache.TryGetSeries{T}(FieldId, out ValueCacheSeries{T})"/>
    public bool TryGetSeries<T>(FieldId fieldId, out ValueCacheSeries<T>? series)
        where T : unmanaged =>
        _Cache.TryGetSeries(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetSeries{T}(string, out ValueCacheSeries{T})"/>
    public bool TryGetSeries<T>(string fieldName, out ValueCacheSeries<T>? series)
        where T : unmanaged =>
        _Cache.TryGetSeries(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetSeries{T}(FieldId)"/>
    public ValueCacheSeries<T> GetSeries<T>(FieldId fieldId)
        where T : unmanaged =>
        _Cache.GetSeries<T>(fieldId);

    /// <inheritdoc cref="ValueCache.TryGetCustomTextSeries(FieldId, out ValueCacheStringSeries)"/>
    public bool TryGetCustomTextSeries(FieldId fieldId, out ValueCacheStringSeries? series) =>
        _Cache.TryGetCustomTextSeries(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetCustomTextSeries(string, out ValueCacheStringSeries)"/>
    public bool TryGetCustomTextSeries(string fieldName, out ValueCacheStringSeries? series) =>
        _Cache.TryGetCustomTextSeries(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetCustomTextSeries"/>
    public ValueCacheStringSeries GetCustomTextSeries(FieldId fieldId) =>
        _Cache.GetCustomTextSeries(fieldId);

    /// <inheritdoc cref="ValueCache.TryGetCustomRepresentationSeries(FieldId, out ValueCacheStringSeries)"/>
    public bool TryGetCustomRepresentationSeries(FieldId fieldId, out ValueCacheStringSeries? series) =>
        _Cache.TryGetCustomRepresentationSeries(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetCustomRepresentationSeries(string, out ValueCacheStringSeries)"/>
    public bool TryGetCustomRepresentationSeries(string fieldName, out ValueCacheStringSeries? series) =>
        _Cache.TryGetCustomRepresentationSeries(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetCustomRepresentationSeries"/>
    public ValueCacheStringSeries GetCustomRepresentationSeries(FieldId fieldId) =>
        _Cache.GetCustomRepresentationSeries(fieldId);

    /// <inheritdoc cref="ValueCache.TryGetIPv6Series(FieldId, out ValueCacheIPv6Series)"/>
    public bool TryGetIPv6Series(FieldId fieldId, out ValueCacheIPv6Series? series) =>
        _Cache.TryGetIPv6Series(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetIPv6Series(string, out ValueCacheIPv6Series)"/>
    public bool TryGetIPv6Series(string fieldName, out ValueCacheIPv6Series? series) =>
        _Cache.TryGetIPv6Series(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetIPv6Series"/>
    public ValueCacheIPv6Series GetIPv6Series(FieldId fieldId) =>
        _Cache.GetIPv6Series(fieldId);

    /// <inheritdoc cref="ValueCache.TryGetUuidSeries(FieldId, out ValueCacheUuidSeries)"/>
    public bool TryGetUuidSeries(FieldId fieldId, out ValueCacheUuidSeries? series) =>
        _Cache.TryGetUuidSeries(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetUuidSeries(string, out ValueCacheUuidSeries)"/>
    public bool TryGetUuidSeries(string fieldName, out ValueCacheUuidSeries? series) =>
        _Cache.TryGetUuidSeries(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetUuidSeries"/>
    public ValueCacheUuidSeries GetUuidSeries(FieldId fieldId) =>
        _Cache.GetUuidSeries(fieldId);

    /// <inheritdoc cref="ValueCache.TryGetBytesSeries(FieldId, out ValueCacheBytesSeries)"/>
    public bool TryGetBytesSeries(FieldId fieldId, out ValueCacheBytesSeries? series) =>
        _Cache.TryGetBytesSeries(fieldId, out series);

    /// <inheritdoc cref="ValueCache.TryGetBytesSeries(string, out ValueCacheBytesSeries)"/>
    public bool TryGetBytesSeries(string fieldName, out ValueCacheBytesSeries? series) =>
        _Cache.TryGetBytesSeries(fieldName, out series);

    /// <inheritdoc cref="ValueCache.GetBytesSeries"/>
    public ValueCacheBytesSeries GetBytesSeries(FieldId fieldId) =>
        _Cache.GetBytesSeries(fieldId);

    #endregion
}
