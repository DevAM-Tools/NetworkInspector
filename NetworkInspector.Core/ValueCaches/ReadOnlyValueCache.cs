// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Zero-allocation read-only view over a <see cref="ValueCache"/>.
/// <para>
/// When the compile-time type is this struct, member calls can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IReadOnlyValueCache"/>
/// so the JIT does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IReadOnlyValueCache"/>, store it in that
/// interface type, or pass it to a non-generic parameter of that type. Those conversions box.
/// Prefer <see cref="ValueCache.ReadOnly"/> or <see cref="ValueCache.AsReadOnlyView"/> and keep
/// the compile-time type as this struct (or use a generic constraint).
/// </para>
/// </summary>
public readonly struct ReadOnlyValueCache : IReadOnlyValueCache
{
    #region Fields

    private readonly ValueCache _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlyValueCache(ValueCache owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _Owner = owner;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public bool IsAbandoned => _Owner.IsAbandoned;

    /// <inheritdoc/>
    public Stack Stack => _Owner.Stack;

    /// <inheritdoc/>
    public bool RecordAllFields => _Owner.RecordAllFields;

    /// <inheritdoc/>
    public int ChunkShift => _Owner.ChunkShift;

    /// <inheritdoc/>
    public bool PacketIdsStrictlyIncreasing => _Owner.PacketIdsStrictlyIncreasing;

    /// <inheritdoc/>
    public bool TimestampsStrictlyIncreasing => _Owner.TimestampsStrictlyIncreasing;

    /// <inheritdoc/>
    public bool IsMaterializationIncomplete => _Owner.IsMaterializationIncomplete;

    /// <inheritdoc/>
    public IReadOnlyList<ReadOnlyValueCacheSeries> AllSeries => _Owner.AllSeries;

    /// <summary>
    /// Same list as <see cref="AllSeries"/>. On this struct the list is read-only views;
    /// <see cref="ValueCache.Series"/> on the class still returns writer series.
    /// </summary>
    public IReadOnlyList<ReadOnlyValueCacheSeries> Series => AllSeries;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public ReadOnlyValueCacheSeries<T> GetSeries<T>(FieldId fieldId) =>
        _Owner.GetSeries<T>(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    public bool TryGetSeries<T>(FieldId fieldId, out ReadOnlyValueCacheSeries<T> series)
    {
        if (!_Owner.TryGetSeries(fieldId, out ValueCacheSeries<T>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetSeries<T>(string fieldName, out ReadOnlyValueCacheSeries<T> series)
    {
        if (!_Owner.TryGetSeries(fieldName, out ValueCacheSeries<T>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    /// <inheritdoc/>
    public ReadOnlyValueCacheSeries<string> GetCustomTextSeries(FieldId fieldId) =>
        _Owner.GetCustomTextSeries(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    public bool TryGetCustomTextSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series)
    {
        if (!_Owner.TryGetCustomTextSeries(fieldId, out ValueCacheSeries<string>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetCustomTextSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series)
    {
        if (!_Owner.TryGetCustomTextSeries(fieldName, out ValueCacheSeries<string>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    /// <inheritdoc/>
    public ReadOnlyValueCacheSeries<string> GetCustomRepresentationSeries(FieldId fieldId) =>
        _Owner.GetCustomRepresentationSeries(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    public bool TryGetCustomRepresentationSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series)
    {
        if (!_Owner.TryGetCustomRepresentationSeries(fieldId, out ValueCacheSeries<string>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetCustomRepresentationSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series)
    {
        if (!_Owner.TryGetCustomRepresentationSeries(fieldName, out ValueCacheSeries<string>? live) || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    #endregion
}
