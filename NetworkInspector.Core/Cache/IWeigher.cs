// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Computes the "weight" (memory cost) of a cache entry.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public interface IWeigher<in TKey, in TValue>
{
    #region Methods

    /// <summary>Returns the weight of the given key-value pair (minimum 1).</summary>
    int Weigh(TKey key, TValue value);

    #endregion
}

/// <summary>Default weigher that assigns weight 1 to every entry.</summary>
public sealed class UnitWeigher<TKey, TValue> : IWeigher<TKey, TValue>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly UnitWeigher<TKey, TValue> Instance = new();
    /// <inheritdoc/>
    public int Weigh(TKey key, TValue value) => 1;
}
