// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="GhostSet{TKey}.DroppedCount"/> (F-CA-02): entries whose individual
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

        // 20 > 10 budget → cannot fit even by evicting everything
        g.Add("huge", 20);
        await Assert.That(g.Count).IsEqualTo(0); // small evicted, huge dropped
        await Assert.That(g.DroppedCount).IsEqualTo(1L);
        await Assert.That(g.Contains("huge")).IsFalse();
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
}
