// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Weight-bounded FIFO set that tracks recently evicted keys.
/// Each entry records the weight it had at eviction time.
/// Used by the 2Q cache to detect re-accessed keys and promote them to Am.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a simple count-based ghost set, this implementation tracks per-entry
/// weight so the ghost budget aligns with the cache's weight-based eviction
/// semantics (matching the Rust implementation). Removal is O(1) via
/// tracked <see cref="LinkedListNode{T}"/> references in the dictionary.
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

    /// <summary>FIFO order: first node = oldest, last node = newest.</summary>
    private readonly LinkedList<(TKey Key, int Weight)> _Order;

    /// <summary>Key → node mapping for O(1) membership test and O(1) removal.</summary>
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, int Weight)>> _Index;

    #endregion

    #region Constructors

    /// <summary>Creates a ghost set with the specified maximum weight budget.</summary>
    /// <param name="maxWeight">Maximum tracked weight, or null to disable ghost tracking.</param>
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

    /// <summary>Total tracked weight of all ghost entries.</summary>
    internal int TotalWeight { get; private set; }

    /// <summary>Returns true if the ghost set contains no entries.</summary>
    internal bool IsEmpty => _Index.Count == 0;

    /// <summary>
    /// Number of <see cref="Add"/> calls that were rejected because the entry's weight
    /// exceeded the entire ghost budget. Useful diagnostic counter for sizing the budget.
    /// </summary>
    internal long DroppedCount { get; private set; }

    #endregion

    #region Internal API

    /// <summary>Checks if the key is in the ghost set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Contains(TKey key) => _Index.ContainsKey(key);

    /// <summary>
    /// Adds a key with its eviction weight to the ghost set.
    /// Oldest entries are evicted when total weight would exceed budget.
    /// No-op if ghost tracking is disabled or key already present.
    /// </summary>
    internal void Add(TKey key, int weight)
    {
        if (_MaxWeight is not { } max)
        {
            return;
        }

        if (_Index.ContainsKey(key))
        {
            return;
        }

        if (weight < 1)
        {
            return;
        }

        if (weight > max)
        {
            DroppedCount++;
            return;
        }

        // Evict oldest entries until the new entry fits
        while (TotalWeight > max - weight && _Order.First is not null)
        {
            LinkedListNode<(TKey Key, int Weight)> oldest = _Order.First;
            _Index.Remove(oldest.Value.Key);
            TotalWeight -= oldest.Value.Weight;
            _Order.RemoveFirst();
        }

        LinkedListNode<(TKey Key, int Weight)> node = _Order.AddLast((key, weight));
        _Index[key] = node;
        TotalWeight += weight;
    }

    /// <summary>Removes a key from the ghost set. O(1) via tracked node reference.</summary>
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
