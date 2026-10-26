// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Weight-bounded FIFO set that tracks recently evicted keys.
/// Each entry records the weight it had at eviction time.
/// Used by the 2Q cache to detect re-accessed keys and promote them to Am.
/// </summary>
/// <remarks>
/// <para>
/// Only keys and weights are stored, never the evicted values: the memory cost of a ghost entry is independent of the
/// size of the evicted value, so remembering an evicted key never keeps its (possibly large) value alive.
/// </para>
/// <para>
/// Unlike a simple count-based ghost set, this implementation tracks per-entry
/// weight so the ghost budget aligns with the cache's weight-based eviction
/// semantics (one evicted 100 MB value must not count the same as one evicted 1 KB value).
/// Removal is O(1) via tracked <see cref="LinkedListNode{T}"/> references in the dictionary; the dictionary gives
/// the O(1) membership test that the cache needs on every insertion of an unknown key.
/// </para>
/// <para>
/// <b>Ordering:</b> Strict FIFO by insertion. Adding a key that is already present does not refresh its position
/// (it is not an LRU), because the 2Q algorithm only needs "was evicted recently", not "was evicted most recently".
/// </para>
/// <para>
/// <b>Drop policy:</b> If a new entry's weight exceeds the configured ghost budget even
/// after evicting all existing entries, it is silently dropped — there is no point in
/// tracking a single ghost entry that already exceeds the budget. <see cref="DroppedCount"/>
/// counts these events so callers can detect mis-sized ghost budgets.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Caller synchronization required.
/// </para>
/// </remarks>
internal sealed class GhostSet<TKey> where TKey : notnull
{
    #region Fields

    /// <summary>Maximum total tracked weight, or null if ghost tracking is disabled.</summary>
    private readonly int? _MaxWeight;

    /// <summary>
    /// FIFO order: first node = oldest, last node = newest. The weight is stored in the node as well so that
    /// trimming the oldest entries can subtract it from <see cref="TotalWeight"/> without a second lookup.
    /// Each entry allocates one <see cref="LinkedListNode{T}"/>; this only happens when the owning cache evicts.
    /// </summary>
    private readonly LinkedList<(TKey Key, int Weight)> _Order;

    /// <summary>Key → node mapping for O(1) membership test and O(1) removal.</summary>
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, int Weight)>> _Index;

    #endregion

    #region Constructors

    /// <summary>Creates a ghost set with the specified maximum weight budget.</summary>
    /// <param name="maxWeight">
    /// Maximum tracked weight, or null to disable ghost tracking. Not validated here: the owning cache factories
    /// reject negative budgets before they reach this type. A budget of 0 behaves like an enabled set that drops everything.
    /// </param>
    /// <param name="comparer">Optional equality comparer for keys.</param>
    internal GhostSet(int? maxWeight, IEqualityComparer<TKey>? comparer = null)
    {
        _MaxWeight = maxWeight;
        _Order = new LinkedList<(TKey, int)>();
        _Index = new Dictionary<TKey, LinkedListNode<(TKey, int)>>(comparer);
    }

    #endregion

    #region Properties

    /// <summary>Number of ghost entries currently tracked.</summary>
    internal int Count => _Index.Count;

    /// <summary>
    /// Total tracked weight of all ghost entries. Never exceeds the configured maximum weight, so the sum cannot overflow
    /// an <see cref="int"/> as long as the budget itself is an <see cref="int"/>.
    /// </summary>
    internal int TotalWeight { get; private set; }

    /// <summary>Returns true if the ghost set contains no entries.</summary>
    internal bool IsEmpty => _Index.Count == 0;

    /// <summary>
    /// Number of <see cref="Add"/> calls that were rejected because the entry's weight
    /// exceeded the entire ghost budget. Useful diagnostic counter for sizing the budget.
    /// Reset by <see cref="Clear"/>.
    /// </summary>
    internal long DroppedCount { get; private set; }

    #endregion

    #region Internal API

    /// <summary>Checks if the key is in the ghost set.</summary>
    /// <param name="key">The key to look up.</param>
    /// <returns><see langword="true"/> when the key was evicted recently enough to still be tracked.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Contains(TKey key) => _Index.ContainsKey(key);

    /// <summary>
    /// Adds a key with its eviction weight to the ghost set.
    /// Oldest entries are evicted when total weight would exceed budget.
    /// No-op if ghost tracking is disabled, the key is already present, or the weight is below 1.
    /// An entry heavier than the whole budget is not stored and counted in <see cref="DroppedCount"/>.
    /// </summary>
    /// <param name="key">The evicted key.</param>
    /// <param name="weight">The weight the entry had when it was evicted from the cache.</param>
    internal void Add(TKey key, int weight)
    {
        // Disabled ghost tracking: the owning cache then behaves like a plain FIFO/LRU cache without promotion.
        if (_MaxWeight is not { } max)
        {
            return;
        }

        // Duplicates keep their original (older) position and weight; refreshing would turn the FIFO into an LRU.
        if (_Index.ContainsKey(key))
        {
            return;
        }

        // The cache never reports a weight below 1; guard so a bad caller cannot corrupt TotalWeight.
        if (weight < 1)
        {
            return;
        }

        // Even an empty ghost set could not hold this entry; evicting everything else for it would be pointless.
        if (weight > max)
        {
            DroppedCount++;
            return;
        }

        // Evict oldest entries until the new entry fits. "TotalWeight > max - weight" is the overflow-safe form of
        // "TotalWeight + weight > max": weight is in [1, max], so max - weight cannot underflow, while the sum could overflow.
        while (TotalWeight > max - weight && _Order.First is not null)
        {
            LinkedListNode<(TKey Key, int Weight)> oldest = _Order.First;
            _Index.Remove(oldest.Value.Key);
            TotalWeight -= oldest.Value.Weight;
            _Order.RemoveFirst();
        }

        // Both structures must be updated together: the list defines eviction order, the dictionary membership.
        LinkedListNode<(TKey Key, int Weight)> node = _Order.AddLast((key, weight));
        _Index[key] = node;
        TotalWeight += weight;
    }

    /// <summary>
    /// Removes a key from the ghost set. O(1) via tracked node reference. The cache calls this when a ghost key is
    /// re-inserted, so a promoted key is no longer tracked as evicted.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns><see langword="true"/> when the key was tracked and has been removed.</returns>
    internal bool Remove(TKey key)
    {
        if (!_Index.Remove(key, out LinkedListNode<(TKey Key, int Weight)>? node))
        {
            return false;
        }

        TotalWeight -= node.Value.Weight;
        _Order.Remove(node); // O(1) because we have the node reference directly
        return true;
    }

    /// <summary>Clears all ghost entries and resets tracked weight.</summary>
    internal void Clear()
    {
        _Index.Clear();
        _Order.Clear();
        TotalWeight = 0;
        DroppedCount = 0;
    }

    #endregion
}
