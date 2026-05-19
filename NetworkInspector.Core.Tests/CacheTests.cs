// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Cache;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for TwoQueueCache (2Q eviction policy): insertion, retrieval, eviction, and ghost promotion.
/// </summary>
internal sealed class CacheTests
{
    // ─────────────────────────────────────────
    // Basic operations (FIFO-first bounded factory)
    // ─────────────────────────────────────────

    [Test]
    public async Task TwoQueueCache_InsertAndRetrieve()
    {
        TwoQueueCache<string, int> cache = TwoQueueCache<string, int>.CreateBounded(10);
        cache.Put("key1", 100);

        bool found = cache.TryGet("key1", out int value);
        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(100);
    }

    [Test]
    public async Task TwoQueueCache_MissReturnsDefault()
    {
        TwoQueueCache<string, int> cache = TwoQueueCache<string, int>.CreateBounded(10);

        bool found = cache.TryGet("missing", out int value);
        await Assert.That(found).IsFalse();
        await Assert.That(value).IsEqualTo(0); // default(int)
    }

    [Test]
    public async Task TwoQueueCache_Count()
    {
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(10);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.IsEmpty).IsTrue();

        cache.Put(1, "one");
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.IsEmpty).IsFalse();

        cache.Put(2, "two");
        await Assert.That(cache.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TwoQueueCache_UpdateExistingKey()
    {
        TwoQueueCache<string, int> cache = TwoQueueCache<string, int>.CreateBounded(10);
        cache.Put("key", 1);
        cache.Put("key", 2);

        bool found = cache.TryGet("key", out int value);
        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(2);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TwoQueueCache_Eviction_WhenOverCapacity()
    {
        // maxWeight=3 with unit weigher means max 3 entries
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(3);
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");
        await Assert.That(cache.Count).IsEqualTo(3);

        // Adding a 4th entry should evict the oldest from A1in
        cache.Put(4, "d");
        await Assert.That(cache.Count).IsEqualTo(3);

        // The first entry (1) should have been evicted
        bool foundFirst = cache.TryGet(1, out _);
        await Assert.That(foundFirst).IsFalse();

        // Recent entries should still be present
        bool foundFourth = cache.TryGet(4, out string? val4);
        await Assert.That(foundFourth).IsTrue();
        await Assert.That(val4).IsEqualTo("d");
    }

    [Test]
    public async Task TwoQueueCache_GhostPromotion()
    {
        // Ghost promotion: item evicted from A1in, then re-inserted → goes to Am
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(3);

        // Fill cache
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");

        // Force eviction of 1
        cache.Put(4, "d");
        bool found1 = cache.TryGet(1, out _);
        await Assert.That(found1).IsFalse();

        // Re-insert 1 → should be promoted to Am (frequent) via ghost set
        cache.Put(1, "a-promoted");

        bool foundPromoted = cache.TryGet(1, out string? val);
        await Assert.That(foundPromoted).IsTrue();
        await Assert.That(val).IsEqualTo("a-promoted");
    }

    [Test]
    public async Task TwoQueueCache_Remove()
    {
        TwoQueueCache<string, int> cache = TwoQueueCache<string, int>.CreateBounded(10);
        cache.Put("key", 42);

        bool removed = cache.Remove("key");
        await Assert.That(removed).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(0);

        bool found = cache.TryGet("key", out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TwoQueueCache_Remove_NonExistent()
    {
        TwoQueueCache<string, int> cache = TwoQueueCache<string, int>.CreateBounded(10);
        bool removed = cache.Remove("nope");
        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task TwoQueueCache_Clear()
    {
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.CreateBounded(10);
        cache.Put(1, 10);
        cache.Put(2, 20);
        cache.Put(3, 30);

        cache.Clear();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.IsEmpty).IsTrue();

        bool found = cache.TryGet(1, out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TwoQueueCache_TotalWeight()
    {
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.CreateBounded(100);
        cache.Put(1, 10);
        cache.Put(2, 20);
        // With unit weigher, each entry has weight 1
        await Assert.That(cache.TotalWeight).IsEqualTo(2);
    }

    [Test]
    public async Task TwoQueueCache_CustomWeigher()
    {
        // Custom weigher: weight = value length
        TwoQueueCache<string, string> cache = TwoQueueCache<string, string>.CreateBounded(
            10,
            new StringLengthWeigher());
        cache.Put("k1", "short");    // weight 5
        cache.Put("k2", "tiny");     // weight 4

        await Assert.That(cache.TotalWeight).IsEqualTo(9);

        // Adding one more that exceeds capacity should trigger eviction
        cache.Put("k3", "ab");       // weight 2, total would be 11 → evict

        // At least one older entry should have been evicted
        await Assert.That(cache.TotalWeight).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task TwoQueueCache_ManyEntries()
    {
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.CreateBounded(100);

        for (int i = 0; i < 200; i++)
        {
            cache.Put(i, i * 10);
        }

        // Count should not exceed maxWeight with unit weigher
        await Assert.That(cache.Count).IsLessThanOrEqualTo(100);

        // Recent entries should be findable
        bool foundRecent = cache.TryGet(199, out int val);
        await Assert.That(foundRecent).IsTrue();
        await Assert.That(val).IsEqualTo(1990);
    }

    // ─────────────────────────────────────────
    // Factory methods
    // ─────────────────────────────────────────

    [Test]
    public async Task TwoQueueCache_CreateBounded_ZeroBudgetDisablesCaching()
    {
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.CreateBounded(0);

        cache.Put(1, 1);

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.TotalWeight).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task TwoQueueCache_CreateUnbounded_NoEviction()
    {
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.CreateUnbounded();

        // Insert many entries — no eviction should occur
        for (int i = 0; i < 1000; i++)
        {
            cache.Put(i, i);
        }

        await Assert.That(cache.Count).IsEqualTo(1000);
        await Assert.That(cache.TotalWeight).IsEqualTo(1000);

        // All entries should still be present
        bool foundFirst = cache.TryGet(0, out int val0);
        await Assert.That(foundFirst).IsTrue();
        await Assert.That(val0).IsEqualTo(0);

        bool foundLast = cache.TryGet(999, out int val999);
        await Assert.That(foundLast).IsTrue();
        await Assert.That(val999).IsEqualTo(999);
    }

    [Test]
    public async Task TwoQueueCache_CreateScanResistant_TwoPhaseEviction()
    {
        // 25% of 100 = A1in max 25, ghost max 50
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.Create2Q(100);

        // Fill A1in beyond its 25-entry limit (with unit weigher)
        for (int i = 0; i < 50; i++)
        {
            cache.Put(i, $"v{i}");
        }

        // Total should not exceed 100
        await Assert.That(cache.TotalWeight).IsLessThanOrEqualTo(100);
        await Assert.That(cache.Count).IsLessThanOrEqualTo(100);

        // Ghost promotion: re-access evicted keys promotes them to Am
        // Keys evicted early (0..24) should be in ghost set.
        // Re-inserting should promote to Am.
        cache.Put(0, "promoted");
        bool found = cache.TryGet(0, out string? val);
        await Assert.That(found).IsTrue();
        await Assert.That(val).IsEqualTo("promoted");
    }

    [Test]
    public async Task TwoQueueCache_Create2QCustom_ExplicitLimits()
    {
        // maxWeight=10, A1in=3, ghost=5
        TwoQueueCache<int, int> cache = TwoQueueCache<int, int>.Create2QCustom(10, 3, 5);

        for (int i = 0; i < 20; i++)
        {
            cache.Put(i, i * 10);
        }

        await Assert.That(cache.TotalWeight).IsLessThanOrEqualTo(10);
        await Assert.That(cache.Count).IsLessThanOrEqualTo(10);
    }

    // ─────────────────────────────────────────
    // Ghost set weight-based tracking
    // ─────────────────────────────────────────

    [Test]
    public async Task TwoQueueCache_GhostWeightTracking_CustomWeigher()
    {
        // With string length weigher, ghost entries track actual eviction weight
        TwoQueueCache<string, string> cache = TwoQueueCache<string, string>.CreateBounded(
            20,
            new StringLengthWeigher());

        cache.Put("k1", "aaaaaaaaaa"); // weight 10
        cache.Put("k2", "bbbbbbbbbb"); // weight 10, total=20

        // Insert a new entry that forces eviction
        cache.Put("k3", "ccc");        // weight 3, total would be 23 → evict k1
        await Assert.That(cache.TotalWeight).IsLessThanOrEqualTo(20);

        // k1 should be evicted but tracked in ghost with weight 10
        bool found1 = cache.TryGet("k1", out _);
        await Assert.That(found1).IsFalse();

        // Re-insert k1 → promoted to Am via ghost
        cache.Put("k1", "aaaa");       // weight 4
        bool foundPromoted = cache.TryGet("k1", out string? val);
        await Assert.That(foundPromoted).IsTrue();
        await Assert.That(val).IsEqualTo("aaaa");
    }

    // ─────────────────────────────────────────
    // A1in update preserves FIFO order
    // ─────────────────────────────────────────

    [Test]
    public async Task TwoQueueCache_A1InUpdate_PreservesFifoOrder()
    {
        // Updates to A1in entries should not change their FIFO position
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(3);

        cache.Put(1, "a"); // oldest in A1in
        cache.Put(2, "b");
        cache.Put(3, "c"); // newest in A1in

        // Update key 1 (oldest) — should stay at back of FIFO
        cache.Put(1, "a-updated");

        // Insert a 4th entry to trigger eviction — key 1 should still be evicted
        // because it remains at the back of the FIFO queue
        cache.Put(4, "d");

        bool found1 = cache.TryGet(1, out _);
        await Assert.That(found1).IsFalse();
    }

    // ─────────────────────────────────────────
    // Am LRU promotion via TryGet
    // ─────────────────────────────────────────

    [Test]
    public async Task TwoQueueCache_AmAccess_MovesToFront()
    {
        // Keys in Am are moved to front on access, protecting them from eviction
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(5);

        // Insert 5 entries
        for (int i = 1; i <= 5; i++)
        {
            cache.Put(i, $"v{i}");
        }

        // Evict key 1 by inserting key 6
        cache.Put(6, "v6");
        await Assert.That(cache.TryGet(1, out _)).IsFalse();

        // Re-insert key 1 (should go to Am via ghost promotion)
        cache.Put(1, "promoted");
        await Assert.That(cache.TryGet(1, out _)).IsTrue();

        // Accessing key 1 in Am should protect it from eviction
        cache.TryGet(1, out _); // LRU touch

        // Insert many entries to fill and evict — key 1 should survive
        cache.Put(10, "x");
        bool found1 = cache.TryGet(1, out string? val1);
        await Assert.That(found1).IsTrue();
        await Assert.That(val1).IsEqualTo("promoted");
    }

    [Test]
    public async Task TwoQueueCache_GetOrAdd_CacheMissStoresValue()
    {
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(10);

        string value = cache.GetOrAdd(42, static key => $"value-{key}");

        await Assert.That(value).IsEqualTo("value-42");
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.TryGet(42, out string? cached)).IsTrue();
        await Assert.That(cached).IsEqualTo("value-42");
    }

    [Test]
    public async Task TwoQueueCache_GetOrAdd_CacheHitSkipsFactory()
    {
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(10);
        cache.Put(42, "existing");

        string value = cache.GetOrAdd(42, static _ => throw new InvalidOperationException("Should not run."));

        await Assert.That(value).IsEqualTo("existing");
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TwoQueueCache_GetOrAdd_StateOverload_PassesArgument()
    {
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(10);

        string value = cache.GetOrAdd(7, static (key, prefix) => $"{prefix}-{key}", "prefix");

        await Assert.That(value).IsEqualTo("prefix-7");
        await Assert.That(cache.TryGet(7, out string? cached)).IsTrue();
        await Assert.That(cached).IsEqualTo("prefix-7");
    }

    [Test]
    public async Task TwoQueueCache_GetOrAdd_FactoryExceptionDoesNotCache()
    {
        TwoQueueCache<int, string> cache = TwoQueueCache<int, string>.CreateBounded(10);

        await Assert.That(() => cache.GetOrAdd(42, static _ => throw new InvalidOperationException("boom")))
            .Throws<InvalidOperationException>();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.TryGet(42, out _)).IsFalse();
    }

    // ─────────────────────────────────────────
    // LinkedMap internal tests via InternalsVisibleTo
    // ─────────────────────────────────────────

    [Test]
    public async Task LinkedMap_InsertFront_DoesNotMoveOnDuplicate()
    {
        LinkedMap<string, int> map = new();

        map.InsertFront("a", 1); // tail
        map.InsertFront("b", 2);
        map.InsertFront("c", 3); // head

        // Update "a" (tail) — should NOT move to front
        bool isNew = map.InsertFront("a", 10);
        await Assert.That(isNew).IsFalse();

        // Pop back should return "a" since it's still at the tail
        bool popped = map.PopBack(out string? key, out int value);
        await Assert.That(popped).IsTrue();
        await Assert.That(key).IsEqualTo("a");
        await Assert.That(value).IsEqualTo(10); // value was updated
    }

    [Test]
    public async Task LinkedMap_TryUpdateInPlace()
    {
        LinkedMap<string, int> map = new();
        map.InsertFront("a", 1);
        map.InsertFront("b", 2);
        map.InsertFront("c", 3);

        bool updated = map.TryUpdateInPlace("b", 20, out int oldValue);
        await Assert.That(updated).IsTrue();
        await Assert.That(oldValue).IsEqualTo(2);

        // Value should be updated
        bool found = map.TryGetValue("b", out int newValue);
        await Assert.That(found).IsTrue();
        await Assert.That(newValue).IsEqualTo(20);

        // Position unchanged — pop back should still return "a"
        map.PopBack(out string? backKey, out _);
        await Assert.That(backKey).IsEqualTo("a");
    }

    [Test]
    public async Task LinkedMap_TryUpdateAndMoveToFront()
    {
        LinkedMap<string, int> map = new();
        map.InsertFront("a", 1); // tail
        map.InsertFront("b", 2);
        map.InsertFront("c", 3); // head

        // Update "a" and move to front
        bool updated = map.TryUpdateAndMoveToFront("a", 10, out int oldValue);
        await Assert.That(updated).IsTrue();
        await Assert.That(oldValue).IsEqualTo(1);

        // "a" is now at front, "b" is at tail
        map.PopBack(out string? backKey, out _);
        await Assert.That(backKey).IsEqualTo("b");
    }

    [Test]
    public async Task LinkedMap_TryGetAndMoveToFront()
    {
        LinkedMap<string, int> map = new();
        map.InsertFront("a", 1); // tail
        map.InsertFront("b", 2);
        map.InsertFront("c", 3); // head

        // Get "a" and move to front
        bool found = map.TryGetAndMoveToFront("a", out int value);
        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(1);

        // "a" is now at front, "b" is at tail
        map.PopBack(out string? backKey, out _);
        await Assert.That(backKey).IsEqualTo("b");
    }

    [Test]
    public async Task LinkedMap_TryUpdateInPlace_MissReturnsFalse()
    {
        LinkedMap<string, int> map = new();
        map.InsertFront("a", 1);

        bool updated = map.TryUpdateInPlace("missing", 99, out int oldValue);
        await Assert.That(updated).IsFalse();
        await Assert.That(oldValue).IsEqualTo(0);
    }

    // ─────────────────────────────────────────
    // GhostSet weight-based internal tests
    // ─────────────────────────────────────────

    [Test]
    public async Task GhostSet_WeightBasedEviction()
    {
        // Budget = 30 weight units
        GhostSet<int> ghost = new(30);

        ghost.Add(1, 10);
        ghost.Add(2, 10);
        ghost.Add(3, 10);
        await Assert.That(ghost.Count).IsEqualTo(3);
        await Assert.That(ghost.TotalWeight).IsEqualTo(30);

        // Adding a 4th entry with weight 10 exceeds budget → evicts oldest (1)
        ghost.Add(4, 10);
        await Assert.That(ghost.Count).IsEqualTo(3);
        await Assert.That(ghost.TotalWeight).IsEqualTo(30);
        await Assert.That(ghost.Contains(1)).IsFalse();
        await Assert.That(ghost.Contains(4)).IsTrue();
    }

    [Test]
    public async Task GhostSet_O1Removal()
    {
        GhostSet<int> ghost = new(100);
        ghost.Add(1, 10);
        ghost.Add(2, 20);
        ghost.Add(3, 30);

        bool removed = ghost.Remove(2);
        await Assert.That(removed).IsTrue();
        await Assert.That(ghost.Count).IsEqualTo(2);
        await Assert.That(ghost.TotalWeight).IsEqualTo(40); // 10 + 30
        await Assert.That(ghost.Contains(2)).IsFalse();
    }

    [Test]
    public async Task GhostSet_DisabledTracking_DiscardsSilently()
    {
        // null max weight = disabled
        GhostSet<int> ghost = new(null);
        ghost.Add(1, 10);
        await Assert.That(ghost.Count).IsEqualTo(0);
        await Assert.That(ghost.Contains(1)).IsFalse();
    }

    [Test]
    public async Task GhostSet_HeavyEntryEvictsMultiple()
    {
        // Budget = 30. Three entries of weight 10 = 30 total.
        GhostSet<int> ghost = new(30);
        ghost.Add(1, 10);
        ghost.Add(2, 10);
        ghost.Add(3, 10);

        // New entry weight 25 → must evict 1, 2, 3 to make room
        ghost.Add(4, 25);
        await Assert.That(ghost.Count).IsEqualTo(1);
        await Assert.That(ghost.Contains(4)).IsTrue();
        await Assert.That(ghost.TotalWeight).IsEqualTo(25);
    }

    [Test]
    public async Task GhostSet_DuplicateInsert_IsNoop()
    {
        GhostSet<int> ghost = new(100);
        ghost.Add(1, 10);
        ghost.Add(1, 10);
        await Assert.That(ghost.Count).IsEqualTo(1);
        await Assert.That(ghost.TotalWeight).IsEqualTo(10);
    }

    /// <summary>Custom weigher that uses string length as weight.</summary>
    private sealed class StringLengthWeigher : IWeigher<string, string>
    {
        public int Weigh(string key, string value) => value.Length;
    }
}