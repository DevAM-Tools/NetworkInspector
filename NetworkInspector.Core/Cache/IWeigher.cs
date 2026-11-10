// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Computes the "weight" (memory cost) of a cache entry.
/// <para>
/// The cache budget is expressed in weight units, so the unit is whatever the weigher chooses: 1 per entry gives an
/// entry-count limit (<see cref="UnitWeigher{TKey, TValue}"/>), the payload length in bytes gives a memory limit.
/// The cache calls <see cref="Weigh"/> once per insertion or update and remembers the result; it does not re-weigh
/// later, so the weight of a stored value must not change while it is cached.
/// </para>
/// <para>
/// Implementations must be fast, deterministic, side-effect free, and must not call back into the cache. They are
/// invoked under the caller's synchronization of the (not thread-safe) cache, so they should not block.
/// </para>
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public interface IWeigher<in TKey, in TValue>
{
    /// <summary>
    /// Returns the weight of the given key-value pair (minimum 1; the cache raises smaller results to 1 so that
    /// every entry consumes budget; the ghost set also rejects weights below 1).
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value being cached.</param>
    /// <returns>The weight in the unit of the cache budget.</returns>
    int Weigh(TKey key, TValue value);

}

/// <summary>
/// Default weigher that assigns weight 1 to every entry, turning the weight budget into an entry-count limit.
/// Stateless and therefore safe to share between threads and caches.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class UnitWeigher<TKey, TValue> : IWeigher<TKey, TValue>
{
    /// <summary>Shared singleton instance (the weigher has no state, so one instance per type pair is enough).</summary>
    public static readonly UnitWeigher<TKey, TValue> Instance = new();

    /// <inheritdoc/>
    public int Weigh(TKey key, TValue value) => 1;
}
