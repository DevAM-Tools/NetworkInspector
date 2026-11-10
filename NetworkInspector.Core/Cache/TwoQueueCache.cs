// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Factory methods for <see cref="TwoQueueCache{TKey, TValue}"/>.
/// </summary>
public static class TwoQueueCache
{
    /// <summary>
    /// Creates an unbounded 2Q cache. No eviction will occur.
    /// The weigher is still called for accurate total-weight reporting.
    /// </summary>
    /// <typeparam name="TKey">Key type; compared with the default equality comparer.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="weigher">Weigher for entries, or null for a weight of 1 per entry.</param>
    /// <returns>An empty cache without a weight budget.</returns>
    /// <remarks>
    /// Every entry stays in the first-access queue (A1in) because nothing is ever evicted, so the ghost set is
    /// disabled (null budget). <see cref="TwoQueueCache{TKey, TValue}.TotalWeight"/> is an <see cref="int"/> that reports
    /// <see cref="int.MaxValue"/> when the summed weights of an unbounded cache exceed it (the sums are kept in a <see cref="long"/>).
    /// </remarks>
    public static TwoQueueCache<TKey, TValue> CreateUnbounded<TKey, TValue>(
        IWeigher<TKey, TValue>? weigher = null)
        where TKey : notnull
        => new(null, null, null, weigher);

    /// <summary>
    /// Creates a simple weight-bounded cache. Eviction is FIFO-first from A1in with no
    /// separate A1in budget. Use <see cref="Create2Q"/> for the full scan-resistant algorithm.
    /// </summary>
    /// <typeparam name="TKey">Key type; compared with the default equality comparer.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="maxWeight">Total weight budget; 0 creates a cache that stores nothing.</param>
    /// <param name="weigher">Weigher for entries, or null for a weight of 1 per entry.</param>
    /// <param name="ghostMaxWeight">
    /// Weight budget of the ghost set; null defaults to <paramref name="maxWeight"/> (the ghost set then remembers about
    /// as much evicted weight as the cache holds).
    /// </param>
    /// <returns>An empty cache with the given total budget.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxWeight"/> or <paramref name="ghostMaxWeight"/> is negative.
    /// </exception>
    public static TwoQueueCache<TKey, TValue> CreateBounded<TKey, TValue>(
        int maxWeight,
        IWeigher<TKey, TValue>? weigher = null,
        int? ghostMaxWeight = null)
        where TKey : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWeight);

        if (ghostMaxWeight is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(ghostMaxWeight.Value);
        }

        // A zero-capacity cache never evicts (it never stores), so a ghost set would only ever stay empty.
        int? effectiveGhostMaxWeight = maxWeight == 0
            ? null
            : ghostMaxWeight ?? maxWeight;
        return new TwoQueueCache<TKey, TValue>(maxWeight, null, effectiveGhostMaxWeight, weigher);
    }

    /// <summary>
    /// Creates a bounded 2Q cache with the standard partitioning from the original paper:
    /// A1in receives 25 % of <paramref name="maxWeight"/> and ghost tracks 50 %.
    /// </summary>
    /// <typeparam name="TKey">Key type; compared with the default equality comparer.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="maxWeight">Total weight budget; 0 creates a cache that stores nothing.</param>
    /// <param name="weigher">Weigher for entries, or null for a weight of 1 per entry.</param>
    /// <returns>An empty cache with 25 % A1in and 50 % ghost shares.</returns>
    /// <remarks>
    /// The shares are those recommended by the original 2Q paper (Johnson and Shasha). Integer division is used, so a
    /// <paramref name="maxWeight"/> below 4 gives A1in a budget of 0: new entries then leave A1in immediately and
    /// are only retained once they come back via the ghost set. An entry heavier than the A1in share is evicted by its
    /// own insertion; use <see cref="Create2QCustom"/> when entries can be that large relative to the budget.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWeight"/> is negative.</exception>
    public static TwoQueueCache<TKey, TValue> Create2Q<TKey, TValue>(
        int maxWeight,
        IWeigher<TKey, TValue>? weigher = null)
        where TKey : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWeight);

        // Disabled cache: Put returns immediately, so no partition budgets or ghost set are needed.
        if (maxWeight == 0)
        {
            return new TwoQueueCache<TKey, TValue>(0, 0, null, weigher);
        }

        int a1InMaxWeight = maxWeight / 4;
        int ghostMaxWeight = maxWeight / 2;
        return new TwoQueueCache<TKey, TValue>(maxWeight, a1InMaxWeight, ghostMaxWeight, weigher);
    }

    /// <summary>
    /// Creates a bounded 2Q cache with explicit partition budgets.
    /// Use this only when measured workloads require different A1in or ghost ratios.
    /// </summary>
    /// <typeparam name="TKey">Key type; compared with the default equality comparer.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="maxWeight">Total weight budget (A1in plus Am); 0 creates a cache that stores nothing.</param>
    /// <param name="a1InMaxWeight">Weight budget of the first-access queue; must not exceed <paramref name="maxWeight"/>.</param>
    /// <param name="ghostMaxWeight">Weight budget of the ghost set of evicted A1in keys; independent of <paramref name="maxWeight"/>.</param>
    /// <param name="weigher">Weigher for entries, or null for a weight of 1 per entry.</param>
    /// <returns>An empty cache with the given budgets.</returns>
    /// <remarks>
    /// Setting <paramref name="a1InMaxWeight"/> equal to <paramref name="maxWeight"/> lets new entries use the whole
    /// budget, which keeps an entry that is larger than a quarter of the budget from being evicted by its own insertion.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A weight is negative, or <paramref name="a1InMaxWeight"/> exceeds <paramref name="maxWeight"/>.
    /// </exception>
    public static TwoQueueCache<TKey, TValue> Create2QCustom<TKey, TValue>(
        int maxWeight,
        int a1InMaxWeight,
        int ghostMaxWeight,
        IWeigher<TKey, TValue>? weigher = null)
        where TKey : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWeight);
        ArgumentOutOfRangeException.ThrowIfNegative(a1InMaxWeight);
        ArgumentOutOfRangeException.ThrowIfNegative(ghostMaxWeight);

        if (a1InMaxWeight > maxWeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(a1InMaxWeight),
                a1InMaxWeight,
                "A1in max weight must not exceed the total max weight.");
        }

        return new TwoQueueCache<TKey, TValue>(maxWeight, a1InMaxWeight, ghostMaxWeight, weigher);
    }
}

/// <summary>
/// Two-Queue (2Q) eviction cache with weight-based capacity.
/// <list type="bullet">
///   <item><description>A1in: FIFO queue for recently inserted items (first access).</description></item>
///   <item><description>Am: LRU queue for frequently accessed items (second+ access).</description></item>
///   <item><description>Ghost: tracks recently evicted A1in keys with their weights for re-access detection.</description></item>
/// </list>
/// </summary>
/// <typeparam name="TKey">Key type; compared with the default equality comparer.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
/// <remarks>
/// <para>
/// Use <see cref="TwoQueueCache.CreateBounded{TKey, TValue}"/> for simple weight-capped caching with FIFO-first eviction.
/// Use <see cref="TwoQueueCache.Create2Q{TKey, TValue}"/> for the default 2Q partitioning (25% A1in, 50% ghost),
/// <see cref="TwoQueueCache.Create2QCustom{TKey, TValue}"/> for explicit control, or <see cref="TwoQueueCache.CreateUnbounded{TKey, TValue}"/>
/// for no eviction.
/// </para>
/// <para>
/// When <c>A1InMaxWeight</c> is configured, eviction uses a two-phase algorithm: phase 1 trims
/// A1in to its own limit (moving evicted keys to ghost), phase 2 trims total weight by preferring
/// Am eviction. This matches the original 2Q paper.
/// </para>
/// <para>
/// <b>Why 2Q:</b> A plain LRU lets a single large scan of never-reused keys flush the entries that are hit repeatedly.
/// Here a key is first held in the FIFO A1in; only a key that is requested again after having been evicted from
/// A1in (found in the ghost set) is admitted to Am. One-time keys therefore pass through A1in without displacing Am.
/// Only keys evicted from A1in are recorded in the ghost set; entries evicted from Am are forgotten.
/// </para>
/// <para>
/// <b>Entries heavier than a budget:</b> Insertion happens first and eviction afterwards, evicting the oldest entries
/// first. An entry heavier than the A1in budget (two-phase mode) or heavier than the total budget (simple mode) is
/// therefore inserted, then every older entry of that queue is evicted, and finally the new entry itself. Callers whose
/// values can be large relative to the budget must size the A1in budget accordingly.
/// </para>
/// <para>
/// <b>Promotion semantics:</b> A re-inserted key that is found in the ghost set is promoted to
/// <c>Am</c> immediately, regardless of how often it was previously seen. This is standard 2Q
/// behaviour — there is no frequency tracking beyond ghost membership (no W-TinyLFU-style
/// frequency sketch). If frequency-aware admission is required, wrap the cache or use a
/// dedicated implementation.
/// </para>
/// <para>
/// <b>MaxWeight = 0:</b> A bounded cache may be configured with <c>maxWeight = 0</c>; in that
/// configuration the cache stores nothing but still serves as a no-op pass-through. Lookups
/// always miss and <c>GetOrAdd</c> still invokes its factory — callers that need to disable
/// caching entirely without paying factory cost should short-circuit at the call site.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Caller synchronization required. This includes lookups: <see cref="TryGet"/>
/// reorders the Am queue on a hit, so concurrent readers also need mutual exclusion (a plain lock, not a reader-writer
/// lock in shared mode). No lock is taken inside the cache so that a caller can pick one lock for the cache and
/// its surrounding state; weights and the weigher callback run under that caller lock.
/// </para>
/// <para>
/// <b>Overflow:</b> Weights are <see cref="int"/>, but the running sums are kept in <see cref="long"/> (at most
/// 2^31 entries of 2^31 weight each cannot overflow it), so the budget checks stay correct even when two entries together
/// exceed <see cref="int.MaxValue"/>. A bounded cache keeps its total at or below its (int) budget after every insertion.
/// <see cref="TotalWeight"/> reports <see cref="int.MaxValue"/> if an unbounded cache holds more than that.
/// </para>
/// </remarks>
public sealed class TwoQueueCache<TKey, TValue> where TKey : notnull
{
    #region Configuration and state

    /// <summary>Maximum total weight (A1in + Am), or null for unbounded.</summary>
    private readonly int? _MaxWeight;

    /// <summary>Maximum weight for A1in alone, or null for simple eviction.</summary>
    private readonly int? _A1InMaxWeight;

    /// <summary>Weigher used on every insertion or update; never null (defaults to a weight of 1 per entry).</summary>
    private readonly IWeigher<TKey, TValue> _Weigher;

    /// <summary>First-access queue, FIFO: new keys enter at the front, reads do not reorder, eviction takes the back.</summary>
    private readonly LinkedMap<TKey, (TValue Value, int Weight)> _A1In;

    /// <summary>Frequently-used queue, LRU: hits and updates move an entry to the front, eviction takes the back.</summary>
    private readonly LinkedMap<TKey, (TValue Value, int Weight)> _Am;

    /// <summary>Recently evicted A1in keys (without values) used to recognize a key that returns.</summary>
    private readonly GhostSet<TKey> _Ghost;

    /// <summary>
    /// Running sum of the weights stored in <see cref="_A1In"/>. The weight is stored next to each value, so eviction and
    /// removal can subtract exactly what was added, without calling the weigher again (no extra cost, and the sum stays
    /// consistent even if the weigher would answer differently for the same value later). A <see cref="long"/> so that the
    /// sum of int weights cannot wrap: a wrapped (negative) total would pass the budget check and let the cache grow.
    /// </summary>
    private long _A1InWeight;

    /// <summary>Running sum of the weights stored in <see cref="_Am"/>.</summary>
    private long _AmWeight;

    /// <summary>
    /// Initial capacity of each queue map. The weight budget is not an entry count (a byte weigher makes it millions),
    /// so the maps start small and grow on demand.
    /// </summary>
    private const int _InitialMapCapacity = 16;

    /// <summary>
    /// Constructor for the <see cref="TwoQueueCache"/> factory methods, which are the only intended callers
    /// (the factories validate the budget combinations).
    /// </summary>
    /// <param name="maxWeight">Total weight budget; null for unbounded, 0 for a cache that stores nothing.</param>
    /// <param name="a1InMaxWeight">A1in budget; null selects simple FIFO-first eviction without a separate A1in limit.</param>
    /// <param name="ghostMaxWeight">Ghost budget; null disables the ghost set (no promotion to Am).</param>
    /// <param name="weigher">Weigher, or null for a weight of 1 per entry.</param>
    /// <remarks>Arguments are not validated here: the factory methods reject negative budgets and an A1in budget above the total.</remarks>
    internal TwoQueueCache(
        int? maxWeight,
        int? a1InMaxWeight,
        int? ghostMaxWeight,
        IWeigher<TKey, TValue>? weigher)
    {
        _MaxWeight = maxWeight;
        _A1InMaxWeight = a1InMaxWeight;
        _Weigher = weigher ?? (IWeigher<TKey, TValue>)UnitWeigher<TKey, TValue>.Instance;
        _A1In = new LinkedMap<TKey, (TValue, int)>(_InitialMapCapacity);
        _Am = new LinkedMap<TKey, (TValue, int)>(_InitialMapCapacity);
        _Ghost = new GhostSet<TKey>(ghostMaxWeight);
    }

    /// <summary>Total number of entries across both queues (ghost keys are not counted; they hold no value).</summary>
    public int Count => _A1In.Count + _Am.Count;

    /// <summary>Total weight across both queues (ghost entries are not counted); <see cref="int.MaxValue"/> when the sum is larger.</summary>
    public int TotalWeight => (int)Math.Min(int.MaxValue, _A1InWeight + _AmWeight);

    /// <summary>
    /// Entries that were too heavy for the ghost budget and were not remembered; a growing value means the ghost budget is too
    /// small for the entry weights. Reset by <see cref="Clear"/>.
    /// </summary>
    public long GhostDroppedCount => _Ghost.DroppedCount;

    /// <summary>Whether the cache is empty.</summary>
    public bool IsEmpty => Count == 0;

    #endregion

    #region Lookup, insertion, and removal

    /// <summary>
    /// Gets a value by key. Accessing in Am moves it to the front (LRU promotion).
    /// A1in access does not promote (promotion happens via ghost set on re-access).
    /// A hit in Am changes the cache state, so this is not a read-only operation for synchronization purposes.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The cached value, or default on a miss.</param>
    /// <returns><see langword="true"/> on a hit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        // Check Am (frequent) first — single-lookup move to front on hit
        if (_Am.TryGetAndMoveToFront(key, out (TValue Value, int Weight) amEntry))
        {
            value = amEntry.Value;
            return true;
        }

        // Check A1in (recent) — no promotion, FIFO order preserved
        if (_A1In.TryGetValue(key, out (TValue Value, int Weight) a1Entry))
        {
            value = a1Entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets a value by key or creates and inserts it on cache miss.
    /// When caching is disabled, the factory result is returned without storing it.
    /// Lookup and insertion are separate steps, so with shared use the caller's lock must span the whole call,
    /// including the factory, or two threads can both run the factory for the same key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="valueFactory">Factory that creates a value for a missing key.</param>
    /// <returns>The cached value or the newly created value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        // Hit: no factory call and no allocation.
        if (TryGet(key, out TValue? value))
        {
            return value;
        }

        // Miss: create outside any internal state change, then insert (Put decides the queue and may evict).
        TValue createdValue = valueFactory(key);
        Put(key, createdValue);
        return createdValue;
    }

    /// <summary>
    /// Gets a value by key or creates and inserts it on cache miss using an additional factory argument.
    /// This overload allows hot paths to avoid closure allocations: the state a factory needs is passed in
    /// <paramref name="factoryArgument"/> so the caller can use a <see langword="static"/> lambda instead of a capturing one
    /// (a capturing lambda allocates a closure and a delegate per call). The same locking rule as for
    /// <see cref="GetOrAdd(TKey, Func{TKey, TValue})"/> applies.
    /// </summary>
    /// <typeparam name="TArg">Additional factory argument type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="valueFactory">Factory that creates a value for a missing key.</param>
    /// <param name="factoryArgument">Additional argument forwarded to <paramref name="valueFactory"/>.</param>
    /// <returns>The cached value or the newly created value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> valueFactory, TArg factoryArgument)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        if (TryGet(key, out TValue? value))
        {
            return value;
        }

        TValue createdValue = valueFactory(key, factoryArgument);
        Put(key, createdValue);
        return createdValue;
    }

    /// <summary>
    /// Inserts or updates a value. Promotes from ghost→Am if previously evicted.
    /// With a total budget of 0 nothing is stored (and the weigher is not called). After insertion, older entries
    /// (and possibly the new one, if it is heavier than the budget) are evicted until the budgets hold again.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to store.</param>
    public void Put(TKey key, TValue value)
    {
        // Disabled cache (see the MaxWeight = 0 remark): skip even the weigher call.
        if (_MaxWeight == 0)
        {
            return;
        }

        // The weight is clamped to at least 1 and stored with the entry so later accounting never re-weighs the value.
        int weight = Math.Max(1, _Weigher.Weigh(key, value));

        // Case 1: Key in Am — single-lookup update + LRU promotion
        if (_Am.TryUpdateAndMoveToFront(key, (value, weight), out (TValue Value, int Weight) amOld))
        {
            _AmWeight = _AmWeight - amOld.Weight + weight;
            _EvictIfNeeded();
            return;
        }

        // Case 2: Key in A1in — single-lookup update in place, preserve FIFO order
        if (_A1In.TryUpdateInPlace(key, (value, weight), out (TValue Value, int Weight) a1Old))
        {
            _A1InWeight = _A1InWeight - a1Old.Weight + weight;
            _EvictIfNeeded();
            return;
        }

        // Case 3: Key in ghost set → promote to Am (recognized as frequently accessed)
        if (_Ghost.Remove(key))
        {
            _Am.InsertFront(key, (value, weight));
            _AmWeight += weight;
            _EvictIfNeeded();
            return;
        }

        // Case 4: New entry → A1in (first-time access)
        _A1In.InsertFront(key, (value, weight));
        _A1InWeight += weight;
        _EvictIfNeeded();
    }

    /// <summary>
    /// Removes a key from the cache. An explicit removal is not an eviction, so the key is not recorded in the ghost set
    /// and a later <see cref="Put"/> of the same key starts in A1in again.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns><see langword="true"/> when the key was cached and has been removed.</returns>
    public bool Remove(TKey key)
    {
        if (_Am.Remove(key, out (TValue Value, int Weight) amEntry))
        {
            _AmWeight -= amEntry.Weight;
            return true;
        }
        if (_A1In.Remove(key, out (TValue Value, int Weight) a1Entry))
        {
            _A1InWeight -= a1Entry.Weight;
            return true;
        }
        return false;
    }

    /// <summary>Clears the entire cache including ghost set.</summary>
    public void Clear()
    {
        _A1In.Clear();
        _Am.Clear();
        _Ghost.Clear();
        _A1InWeight = 0;
        _AmWeight = 0;
    }

    #endregion

    #region Eviction

    /// <summary>
    /// Enforces the 2Q eviction policy.
    /// Phase 1: Trim A1in to its own limit (when configured via factory methods).
    /// Phase 2: Trim total weight by evicting from the appropriate queue.
    /// Called after every insertion or weight change, so the budgets hold whenever a public method returns.
    /// </summary>
    /// <remarks>
    /// The <c>break</c> statements on a failed eviction are defensive: the loops only evict from a non-empty queue, so a
    /// failure can only happen if the weight counters ever disagree with the queue contents; breaking prevents an endless
    /// loop in that case instead of hiding it.
    /// </remarks>
    private void _EvictIfNeeded()
    {
        // Phase 1: Trim A1in to its own limit (only when A1in has a separate budget).
        // Evicted keys go to the ghost set, which is how a returning key is later recognized and promoted to Am.
        if (_A1InMaxWeight is not null)
        {
            int a1InLimit = _A1InMaxWeight.Value;
            while (_A1InWeight > a1InLimit && !_A1In.IsEmpty)
            {
                if (!_EvictFromA1In())
                {
                    break;
                }
            }
        }

        // Unbounded cache: there is no total budget to enforce.
        if (_MaxWeight is not { } max)
        {
            return;
        }

        // Phase 2: Trim total weight to max
        while (_A1InWeight + _AmWeight > max)
        {
            // When A1in has its own limit (two-phase mode), prefer Am eviction
            // since A1in was already trimmed in phase 1.
            // Otherwise (simple mode), prefer A1in eviction (FIFO-first): one-time entries leave before entries that
            // were promoted to Am, which is what makes the queue scan-resistant.
            if (_A1InMaxWeight is not null)
            {
                if (!_Am.IsEmpty)
                {
                    if (!_EvictFromAm())
                    {
                        break;
                    }
                }
                else if (!_EvictFromA1In())
                {
                    break;
                }
            }
            else
            {
                if (!_A1In.IsEmpty)
                {
                    if (!_EvictFromA1In())
                    {
                        break;
                    }
                }
                else if (!_EvictFromAm())
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Evicts the tail of A1in and moves the key to the ghost set with its weight. The value is dropped here; only the
    /// key survives (in the ghost set), so a later <see cref="Put"/> of this key is recognized as a returning key.
    /// </summary>
    /// <returns><see langword="false"/> when A1in is empty.</returns>
    private bool _EvictFromA1In()
    {
        if (!_A1In.PopBack(out TKey? key, out (TValue Value, int Weight) entry))
        {
            return false;
        }

        _A1InWeight -= entry.Weight;
        _Ghost.Add(key, entry.Weight); // Record with weight for budget tracking
        return true;
    }

    /// <summary>
    /// Evicts the tail of Am (least recently used frequent entry). The key is deliberately not added to the ghost set:
    /// only first-access eviction is tracked, so an entry that was already promoted must earn its place again.
    /// </summary>
    /// <returns><see langword="false"/> when Am is empty.</returns>
    private bool _EvictFromAm()
    {
        if (!_Am.PopBack(out _, out (TValue Value, int Weight) entry))
        {
            return false;
        }

        _AmWeight -= entry.Weight;
        return true;
    }

    #endregion
}
