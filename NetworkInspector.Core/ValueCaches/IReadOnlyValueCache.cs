// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Read-only view of a <see cref="ValueCache"/>. Query series and flags without
/// <see cref="ValueCache.RecordPacket"/> or <see cref="ValueCache.Abandon"/>.
/// <para>
/// <b>Boxing:</b> <see cref="ValueCache"/> is a class — storing it as this interface does not
/// allocate. <see cref="ReadOnlyValueCache"/> is a struct; casting the view, storing it in an
/// <see cref="IReadOnlyValueCache"/> field, or passing it to a non-generic parameter of this type
/// boxes. Hot-path APIs must take <c>TCache where TCache : IReadOnlyValueCache</c>
/// (or the concrete <see cref="ValueCache"/> / <see cref="ReadOnlyValueCache"/>).
/// </para>
/// <para>
/// Series getters return <see cref="ReadOnlyValueCacheSeries{T}"/> so callers cannot reach
/// writer-only members. Keep that compile-time type; assigning to
/// <see cref="IReadOnlyValueCacheSeries{T}"/> boxes.
/// </para>
/// </summary>
public interface IReadOnlyValueCache
{
    #region Properties

    /// <summary>Whether the aliased writer was evicted.</summary>
    bool IsAbandoned
    {
        get;
    }

    /// <summary>Stack this cache was built against.</summary>
    Stack Stack
    {
        get;
    }

    /// <summary>Whether construction used <see cref="ValueCacheBuildOptions.RecordAllFields"/>.</summary>
    bool RecordAllFields
    {
        get;
    }

    /// <summary>Log₂ of rows per inner column chunk used by every series of this cache.</summary>
    int ChunkShift
    {
        get;
    }

    /// <summary>Sticky strictly-increasing packet-id flag.</summary>
    bool PacketIdsStrictlyIncreasing
    {
        get;
    }

    /// <summary>Sticky strictly-increasing timestamp flag.</summary>
    bool TimestampsStrictlyIncreasing
    {
        get;
    }

    /// <summary>Sticky materialization-cap flag.</summary>
    bool IsMaterializationIncomplete
    {
        get;
    }

    /// <summary>
    /// All series as read-only struct views. Under <see cref="RecordAllFields"/> the list can grow;
    /// re-read <see cref="IReadOnlyCollection{T}.Count"/>. Distinct from
    /// <see cref="ValueCache.Series"/>, which returns writer series on the class.
    /// </summary>
    IReadOnlyList<ReadOnlyValueCacheSeries> AllSeries
    {
        get;
    }

    #endregion

    #region Series lookup

    /// <summary>
    /// Returns the payload series for <paramref name="fieldId"/>.
    /// Keep the compile-time type as <see cref="ReadOnlyValueCacheSeries{T}"/>.
    /// </summary>
    ReadOnlyValueCacheSeries<T> GetSeries<T>(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetSeries{T}(FieldId)"/>.</summary>
    bool TryGetSeries<T>(FieldId fieldId, out ReadOnlyValueCacheSeries<T> series);

    /// <summary>Looks up a field by ordinal name, then <see cref="TryGetSeries{T}(FieldId, out ReadOnlyValueCacheSeries{T})"/>.</summary>
    bool TryGetSeries<T>(string fieldName, out ReadOnlyValueCacheSeries<T> series);

    /// <summary>Custom-text series for <paramref name="fieldId"/>.</summary>
    ReadOnlyValueCacheSeries<string> GetCustomTextSeries(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetCustomTextSeries"/>.</summary>
    bool TryGetCustomTextSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Looks up custom text by ordinal name.</summary>
    bool TryGetCustomTextSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Custom-representation series for <paramref name="fieldId"/>.</summary>
    ReadOnlyValueCacheSeries<string> GetCustomRepresentationSeries(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetCustomRepresentationSeries"/>.</summary>
    bool TryGetCustomRepresentationSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Looks up custom representation by ordinal name.</summary>
    bool TryGetCustomRepresentationSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series);

    #endregion
}
