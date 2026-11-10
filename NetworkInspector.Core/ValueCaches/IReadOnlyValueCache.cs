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
/// <para>
/// Thread safety: every member is a read and may be called from any thread while the single writer fills the cache;
/// the cache can still grow under <see cref="RecordAllFields"/> and the sticky flags can change, so re-read rather than cache
/// them. See <see cref="ValueCache"/> for the single-writer / multi-reader contract.
/// </para>
/// </summary>
public interface IReadOnlyValueCache
{
    #region Cache state

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

    /// <summary>
    /// Sticky strictly-increasing packet-id flag: true until a recorded packet has a lower id than the packet before it.
    /// Once false, it stays false.
    /// </summary>
    bool PacketIdsStrictlyIncreasing
    {
        get;
    }

    /// <summary>
    /// Sticky strictly-increasing timestamp flag: true until a recorded packet has a timestamp that is not later than the
    /// previous packet's. Once false, it stays false.
    /// </summary>
    bool TimestampsStrictlyIncreasing
    {
        get;
    }

    /// <summary>
    /// Sticky materialization-cap flag: set when expanding lazy containers hit the pass cap, so some columns may be
    /// incomplete. Never reset.
    /// </summary>
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
    /// <typeparam name="T">
    /// The CLR type the cache stores for the field's <see cref="FieldType"/> (for example <c>ulong</c> for U64, MAC, and EUI-64
    /// fields, <c>uint</c> for IPv4); a different <typeparamref name="T"/> finds no series.
    /// </typeparam>
    /// <param name="fieldId">The recorded field.</param>
    /// <returns>A view of the live series; it follows the writer, so its <c>Count</c> grows.</returns>
    /// <exception cref="ArgumentException">No series, or <typeparamref name="T"/> does not match the stack field type.</exception>
    ReadOnlyValueCacheSeries<T> GetSeries<T>(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetSeries{T}(FieldId)"/>.</summary>
    /// <typeparam name="T">See <see cref="GetSeries{T}(FieldId)"/>.</typeparam>
    /// <param name="fieldId">The recorded field.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the payload series exists and <typeparamref name="T"/> matches its type.</returns>
    bool TryGetSeries<T>(FieldId fieldId, out ReadOnlyValueCacheSeries<T> series);

    /// <summary>Looks up a field by ordinal name, then <see cref="TryGetSeries{T}(FieldId, out ReadOnlyValueCacheSeries{T})"/>.</summary>
    /// <typeparam name="T">See <see cref="GetSeries{T}(FieldId)"/>.</typeparam>
    /// <param name="fieldName">The ordinal (case-sensitive) field name; <see langword="null"/> or unknown finds nothing.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the name is known and its payload series exists with a matching <typeparamref name="T"/>.</returns>
    bool TryGetSeries<T>(string fieldName, out ReadOnlyValueCacheSeries<T> series);

    /// <summary>Returns the custom-text series (the display text of each recorded occurrence) of <paramref name="fieldId"/>.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <returns>A view of the live string series.</returns>
    /// <exception cref="ArgumentException">The cache records no custom text for this field.</exception>
    ReadOnlyValueCacheSeries<string> GetCustomTextSeries(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetCustomTextSeries"/>.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the cache records custom text for the field.</returns>
    bool TryGetCustomTextSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Looks up the custom-text series by ordinal field name.</summary>
    /// <param name="fieldName">The ordinal (case-sensitive) field name; <see langword="null"/> or unknown finds nothing.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the name is known and the cache records custom text for the field.</returns>
    bool TryGetCustomTextSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Returns the custom-representation series (the alternative text of each recorded value) of <paramref name="fieldId"/>.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <returns>A view of the live string series.</returns>
    /// <exception cref="ArgumentException">The cache records no custom representation for this field.</exception>
    ReadOnlyValueCacheSeries<string> GetCustomRepresentationSeries(FieldId fieldId);

    /// <summary>Try-get counterpart of <see cref="GetCustomRepresentationSeries"/>.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the cache records a custom representation for the field.</returns>
    bool TryGetCustomRepresentationSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series);

    /// <summary>Looks up the custom-representation series by ordinal field name.</summary>
    /// <param name="fieldName">The ordinal (case-sensitive) field name; <see langword="null"/> or unknown finds nothing.</param>
    /// <param name="series">The view when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the name is known and the cache records a custom representation for the field.</returns>
    bool TryGetCustomRepresentationSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series);

    #endregion
}
