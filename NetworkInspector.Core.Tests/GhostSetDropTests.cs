// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="GhostSet{TKey}.DroppedCount"/> and its public view
/// <see cref="TwoQueueCache{TKey, TValue}.GhostDroppedCount"/>: entries whose individual
/// weight exceeds the entire ghost budget must be silently dropped and counted.
/// </summary>
internal sealed class GhostSetDropTests
{
    [Test]
    public async Task Add_OversizedEntry_IsDroppedAndCounted()
    {
        GhostSet<string> g = new(maxWeight: 10);
        g.Add("small", 5);
        await Assert.That(g.Count).IsEqualTo(1);
        await Assert.That(g.DroppedCount).IsEqualTo(0L);

        // 20 > 10 budget → cannot fit; existing entry is preserved
        g.Add("huge", 20);
        await Assert.That(g.Count).IsEqualTo(1);
        await Assert.That(g.DroppedCount).IsEqualTo(1L);
        await Assert.That(g.Contains("huge")).IsFalse();
        await Assert.That(g.Contains("small")).IsTrue();
    }

    [Test]
    public async Task Add_NegativeOrZeroWeight_IsNoOpAndPreservesTotalWeight()
    {
        GhostSet<int> g = new(maxWeight: 10);
        g.Add(1, 5);
        await Assert.That(g.TotalWeight).IsEqualTo(5);

        g.Add(2, 0);
        g.Add(3, -1);

        await Assert.That(g.Count).IsEqualTo(1);
        await Assert.That(g.TotalWeight).IsEqualTo(5);
        await Assert.That(g.Contains(2)).IsFalse();
        await Assert.That(g.Contains(3)).IsFalse();
    }

    [Test]
    public async Task Add_IntMaxWeight_ExceedingBudget_IsDropped()
    {
        GhostSet<int> g = new(maxWeight: 10);
        g.Add(1, int.MaxValue);

        await Assert.That(g.Count).IsEqualTo(0);
        await Assert.That(g.DroppedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task Add_UnderBudget_AfterEviction_Succeeds()
    {
        GhostSet<int> g = new(maxWeight: 10);
        g.Add(1, 6);
        g.Add(2, 6); // forces eviction of key 1
        await Assert.That(g.Count).IsEqualTo(1);
        await Assert.That(g.Contains(2)).IsTrue();
        await Assert.That(g.Contains(1)).IsFalse();
        await Assert.That(g.DroppedCount).IsEqualTo(0L);
    }

    [Test]
    public async Task Clear_ResetsDroppedCount()
    {
        GhostSet<int> g = new(maxWeight: 5);
        g.Add(1, 100);
        g.Add(2, 100);
        await Assert.That(g.DroppedCount).IsEqualTo(2L);
        g.Clear();
        await Assert.That(g.DroppedCount).IsEqualTo(0L);
    }

    [Test]
    public async Task DisabledGhost_AddIsNoOp_NoDrops()
    {
        GhostSet<int> g = new(maxWeight: null);
        g.Add(1, int.MaxValue);
        await Assert.That(g.Count).IsEqualTo(0);
        await Assert.That(g.DroppedCount).IsEqualTo(0L);
    }

    [Test]
    public async Task TwoQueueCache_ExposesTheGhostDropCount_AndClearResetsIt()
    {
        // Budget 4 with A1in 4 and a ghost budget of 2: the second insert evicts the first entry (weight 3), which is too heavy
        // for the ghost budget, so the ghost set drops it and the cache reports it.
        TwoQueueCache<int, int> cache = TwoQueueCache.Create2QCustom<int, int>(4, 4, 2, new ValueIsWeightWeigher());
        cache.Put(1, 3);
        await Assert.That(cache.GhostDroppedCount).IsEqualTo(0L);

        cache.Put(2, 3);
        await Assert.That(cache.GhostDroppedCount).IsEqualTo(1L);

        cache.Clear();
        await Assert.That(cache.GhostDroppedCount).IsEqualTo(0L);
    }

    /// <summary>Weigher that uses the int value itself as weight.</summary>
    private sealed class ValueIsWeightWeigher : IWeigher<int, int>
    {
        public int Weigh(int key, int value) => value;
    }
}
