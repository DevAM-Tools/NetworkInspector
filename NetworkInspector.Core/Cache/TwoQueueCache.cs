// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Two-Queue (2Q) eviction cache with weight-based capacity.
/// <list type="bullet">
///   <item><description>A1in: FIFO queue for recently inserted items (first access).</description></item>
///   <item><description>Am: LRU queue for frequently accessed items (second+ access).</description></item>
///   <item><description>Ghost: tracks recently evicted A1in keys with their weights for re-access detection.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="CreateBounded"/> for simple weight-capped caching with FIFO-first eviction.
/// Use <see cref="Create2Q"/> for the default 2Q partitioning (25% A1in, 50% ghost),
/// <see cref="Create2QCustom"/> for explicit control, or <see cref="CreateUnbounded"/>
/// for no eviction.
/// </para>
/// <para>
/// When <c>A1InMaxWeight</c> is configured, eviction uses a two-phase algorithm: phase 1 trims
/// A1in to its own limit (moving evicted keys to ghost), phase 2 trims total weight by preferring
/// Am eviction. This matches the original 2Q paper and the Rust implementation.
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
/// <b>Thread-safety:</b> Not thread-safe. Caller synchronization required.
/// </para>
/// </remarks>
public sealed class TwoQueueCache<TKey, TValue> where TKey : notnull
{
    #region Fields

    /// <summary>Maximum total weight (A1in + Am), or null for unbounded.</summary>
    private readonly int? _MaxWeight;

    /// <summary>Maximum weight for A1in alone, or null for simple eviction.</summary>
    private readonly int? _A1InMaxWeight;

    private readonly IWeigher<TKey, TValue> _Weigher;
    private readonly LinkedMap<TKey, (TValue Value, int Weight)> _A1In;
    private readonly LinkedMap<TKey, (TValue Value, int Weight)> _Am;
    private readonly GhostSet<TKey> _Ghost;
    private int _A1InWeight;
    private int _AmWeight;

    #endregion

    #region Constructors

    /// <summary>Private constructor for factory methods with full control.</summary>
    private TwoQueueCache(
        int? maxWeight,
        int? a1InMaxWeight,
        int? ghostMaxWeight,
        IWeigher<TKey, TValue>? weigher)
    {
        _MaxWeight = maxWeight;
        _A1InMaxWeight = a1InMaxWeight;
        _Weigher = weigher ?? (IWeigher<TKey, TValue>)UnitWeigher<TKey, TValue>.Instance;
        int capacity = Math.Max(1, maxWeight ?? 16);
        _A1In = new LinkedMap<TKey, (TValue, int)>(capacity / 2);
        _Am = new LinkedMap<TKey, (TValue, int)>(capacity / 2);
        _Ghost = new GhostSet<TKey>(ghostMaxWeight);
    }

    #endregion

    #region Factory Methods

    /// <summary>
    /// Creates an unbounded 2Q cache. No eviction will occur.
    /// The weigher is still called for accurate <see cref="TotalWeight"/> reporting.
    /// </summary>
    public static TwoQueueCache<TKey, TValue> CreateUnbounded(
        IWeigher<TKey, TValue>? weigher = null) => new(null, null, null, weigher);

    /// <summary>
    /// Creates a simple weight-bounded cache. Eviction is FIFO-first from A1in with no
    /// separate A1in budget. Use <see cref="Create2Q"/> for the full scan-resistant algorithm.
    /// </summary>
    /// <param name="maxWeight">
    /// Maximum total weight. A value of zero disables caching while still allowing lookups.
    /// </param>
    /// <param name="weigher">Weight calculator for entries. Defaults to unit weigher.</param>
    /// <param name="ghostMaxWeight">
    /// Optional ghost-set budget. When null, the ghost budget matches <paramref name="maxWeight"/>.
    /// Use zero to disable ghost tracking explicitly.
    /// </param>
    public static TwoQueueCache<TKey, TValue> CreateBounded(
        int maxWeight,
        IWeigher<TKey, TValue>? weigher = null,
        int? ghostMaxWeight = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWeight);

        if (ghostMaxWeight is { } resolvedGhostMaxWeight)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(resolvedGhostMaxWeight);
        }

        int? effectiveGhostMaxWeight = maxWeight == 0 ? null : ghostMaxWeight ?? maxWeight;
        return new TwoQueueCache<TKey, TValue>(maxWeight, null, effectiveGhostMaxWeight, weigher);
    }

    /// <summary>
    /// Creates a bounded 2Q cache with the standard partitioning from the original paper:
    /// A1in receives 25 % of <paramref name="maxWeight"/> and ghost tracks 50 %.
    /// </summary>
    /// <param name="maxWeight">
    /// Maximum total weight. A value of zero disables caching while still allowing lookups.
    /// </param>
    /// <param name="weigher">Weight calculator for entries. Defaults to unit weigher.</param>
    public static TwoQueueCache<TKey, TValue> Create2Q(
        int maxWeight,
        IWeigher<TKey, TValue>? weigher = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWeight);

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
    /// <param name="maxWeight">Maximum total weight across A1in and Am.</param>
    /// <param name="a1InMaxWeight">Maximum weight allowed in A1in before phase-1 trimming.</param>
    /// <param name="ghostMaxWeight">Maximum total weight tracked by the ghost set.</param>
    /// <param name="weigher">Weight calculator for entries. Defaults to unit weigher.</param>
    public static TwoQueueCache<TKey, TValue> Create2QCustom(
        int maxWeight,
        int a1InMaxWeight,
        int ghostMaxWeight,
        IWeigher<TKey, TValue>? weigher = null)
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

    #endregion

    #region Properties

    /// <summary>Total number of entries across both queues.</summary>
    public int Count => _A1In.Count + _Am.Count;

    /// <summary>Total weight across both queues.</summary>
    public int TotalWeight => _A1InWeight + _AmWeight;

    /// <summary>Whether the cache is empty.</summary>
    public bool IsEmpty => Count == 0;

    #endregion

    #region Public API

    /// <summary>
    /// Gets a value by key. Accessing in Am moves it to the front (LRU promotion).
    /// A1in access does not promote (promotion happens via ghost set on re-access).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out TValue value)
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

        value = default!;
        return false;
    }

    /// <summary>
    /// Gets a value by key or creates and inserts it on cache miss.
    /// When caching is disabled, the factory result is returned without storing it.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="valueFactory">Factory that creates a value for a missing key.</param>
    /// <returns>The cached value or the newly created value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        if (TryGet(key, out TValue value))
        {
            return value;
        }

        TValue createdValue = valueFactory(key);
        Put(key, createdValue);
        return createdValue;
    }

    /// <summary>
    /// Gets a value by key or creates and inserts it on cache miss using an additional factory argument.
    /// This overload allows hot paths to avoid closure allocations.
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

        if (TryGet(key, out TValue value))
        {
            return value;
        }

        TValue createdValue = valueFactory(key, factoryArgument);
        Put(key, createdValue);
        return createdValue;
    }

    /// <summary>
    /// Inserts or updates a value. Promotes from ghost→Am if previously evicted.
    /// </summary>
    public void Put(TKey key, TValue value)
    {
        if (_MaxWeight == 0)
        {
            return;
        }

        int weight = Math.Max(1, _Weigher.Weigh(key, value));

        // Case 1: Key in Am — single-lookup update + LRU promotion
        if (_Am.TryUpdateAndMoveToFront(key, (value, weight), out (TValue Value, int Weight) amOld))
        {
            _AmWeight = _AmWeight - amOld.Weight + weight;
            EvictIfNeeded();
            return;
        }

        // Case 2: Key in A1in — single-lookup update in place, preserve FIFO order
        if (_A1In.TryUpdateInPlace(key, (value, weight), out (TValue Value, int Weight) a1Old))
        {
            _A1InWeight = _A1InWeight - a1Old.Weight + weight;
            EvictIfNeeded();
            return;
        }

        // Case 3: Key in ghost set → promote to Am (recognized as frequently accessed)
        if (_Ghost.Remove(key))
        {
            _Am.InsertFront(key, (value, weight));
            _AmWeight += weight;
            EvictIfNeeded();
            return;
        }

        // Case 4: New entry → A1in (first-time access)
        _A1In.InsertFront(key, (value, weight));
        _A1InWeight += weight;
        EvictIfNeeded();
    }

    /// <summary>Removes a key from the cache.</summary>
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

    #region Private Helpers

    /// <summary>
    /// Enforces the 2Q eviction policy.
    /// Phase 1: Trim A1in to its own limit (when configured via factory methods).
    /// Phase 2: Trim total weight by evicting from the appropriate queue.
    /// </summary>
    private void EvictIfNeeded()
    {
        // Phase 1: Trim A1in to its own limit (only when A1in has a separate budget)
        if (_A1InMaxWeight is { } a1InLimit)
        {
            while (_A1InWeight > a1InLimit && !_A1In.IsEmpty)
            {
                if (!EvictFromA1In())
                {
                    break;
                }
            }
        }

        if (_MaxWeight is not { } max)
        {
            return;
        } // unbounded — no total trim

        // Phase 2: Trim total weight to max
        while (_A1InWeight + _AmWeight > max)
        {
            // When A1in has its own limit (two-phase mode), prefer Am eviction
            // since A1in was already trimmed in phase 1.
            // Otherwise (simple mode), prefer A1in eviction (FIFO-first).
            if (_A1InMaxWeight is not null)
            {
                if (!_Am.IsEmpty)
                {
                    if (!EvictFromAm())
                    {
                        break;
                    }
                }
                else if (!EvictFromA1In())
                {
                    break;
                }
            }
            else
            {
                if (!_A1In.IsEmpty)
                {
                    if (!EvictFromA1In())
                    {
                        break;
                    }
                }
                else if (!EvictFromAm())
                {
                    break;
                }
            }
        }
    }

    /// <summary>Evicts the tail of A1in and moves the key to the ghost set with its weight.</summary>
    private bool EvictFromA1In()
    {
        if (!_A1In.PopBack(out TKey? key, out (TValue Value, int Weight) entry))
        {
            return false;
        }

        _A1InWeight -= entry.Weight;
        _Ghost.Add(key, entry.Weight); // Record with weight for budget tracking
        return true;
    }

    /// <summary>Evicts the tail of Am (least recently used frequent entry).</summary>
    private bool EvictFromAm()
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
