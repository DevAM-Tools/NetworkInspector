// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

    /// <summary>Sum of weights of all tracked ghost entries.</summary>
    private int _TotalWeight;

    /// <summary>Count of entries that were rejected because their weight exceeded the entire budget.</summary>
    private long _DroppedCount;

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
    internal int TotalWeight => _TotalWeight;

    /// <summary>Returns true if the ghost set contains no entries.</summary>
    internal bool IsEmpty => _Index.Count == 0;

    /// <summary>
    /// Number of <see cref="Add"/> calls that were rejected because the entry's weight
    /// exceeded the entire ghost budget. Useful diagnostic counter for sizing the budget.
    /// </summary>
    internal long DroppedCount => _DroppedCount;

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

        // Evict oldest entries until the new entry fits
        while (_TotalWeight + weight > max && _Order.First is { } oldest)
        {
            _Index.Remove(oldest.Value.Key);
            _TotalWeight -= oldest.Value.Weight;
            _Order.RemoveFirst();
        }

        // The new entry alone is heavier than the entire budget — drop it and record the event
        // so callers can detect chronically mis-sized budgets.
        if (_TotalWeight + weight > max)
        {
            _DroppedCount++;
            return;
        }

        LinkedListNode<(TKey Key, int Weight)> node = _Order.AddLast((key, weight));
        _Index[key] = node;
        _TotalWeight += weight;
    }

    /// <summary>Removes a key from the ghost set. O(1) via tracked node reference.</summary>
    internal bool Remove(TKey key)
    {
        if (!_Index.Remove(key, out LinkedListNode<(TKey Key, int Weight)>? node))
        {
            return false;
        }

        _TotalWeight -= node.Value.Weight;
        _Order.Remove(node); // O(1) because we have the node reference directly
        return true;
    }

    /// <summary>Clears all ghost entries and resets tracked weight.</summary>
    internal void Clear()
    {
        _Index.Clear();
        _Order.Clear();
        _TotalWeight = 0;
        _DroppedCount = 0;
    }

    #endregion
}