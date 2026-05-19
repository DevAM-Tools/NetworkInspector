// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Index;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="RoaringTreemap"/>: 64-bit bitmap with partitioned RoaringBitmap buckets.
/// Covers add, contains, cardinality, and all set operations including aliasing regression tests.
/// </summary>
internal sealed class RoaringTreemapTests
{
    // === Basic operations ===

    [Test]
    public async Task EmptyTreemap()
    {
        RoaringTreemap tm = new();
        await Assert.That(tm.IsEmpty).IsTrue();
        await Assert.That(tm.Cardinality).IsEqualTo(0L);
    }

    [Test]
    public async Task AddAndContains()
    {
        RoaringTreemap tm = new();
        tm.Add(42);
        await Assert.That(tm.Contains(42)).IsTrue();
        await Assert.That(tm.Contains(43)).IsFalse();
    }

    [Test]
    public async Task AddLargeValue()
    {
        RoaringTreemap tm = new();
        ulong largeVal = (ulong)uint.MaxValue + 100;
        tm.Add(largeVal);
        await Assert.That(tm.Contains(largeVal)).IsTrue();
        await Assert.That(tm.Contains(42)).IsFalse();
        await Assert.That(tm.Cardinality).IsEqualTo(1L);
    }

    [Test]
    public async Task AddMultipleBuckets()
    {
        RoaringTreemap tm = new();
        // Values in different high-32 buckets
        tm.Add(0);
        tm.Add(1UL << 32);
        tm.Add(2UL << 32);
        tm.Add((1UL << 32) + 5);

        await Assert.That(tm.Cardinality).IsEqualTo(4L);
        await Assert.That(tm.Contains(0)).IsTrue();
        await Assert.That(tm.Contains(1UL << 32)).IsTrue();
        await Assert.That(tm.Contains(2UL << 32)).IsTrue();
        await Assert.That(tm.Contains((1UL << 32) + 5)).IsTrue();
    }

    [Test]
    public async Task DuplicateAdd_DoesNotIncreaseCardinality()
    {
        RoaringTreemap tm = new();
        tm.Add(100);
        tm.Add(100);
        await Assert.That(tm.Cardinality).IsEqualTo(1L);
    }

    [Test]
    public async Task MaxUint64Value()
    {
        RoaringTreemap tm = new();
        tm.Add(ulong.MaxValue);
        await Assert.That(tm.Contains(ulong.MaxValue)).IsTrue();
        await Assert.That(tm.Cardinality).IsEqualTo(1L);
    }

    // === AND (intersection) ===

    [Test]
    public async Task And_Intersection()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(1UL << 32);
        a.Add(2UL << 32);

        RoaringTreemap b = new();
        b.Add(1UL << 32);
        b.Add(2UL << 32);
        b.Add(3UL << 32);

        RoaringTreemap result = a.And(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1UL << 32)).IsTrue();
        await Assert.That(result.Contains(2UL << 32)).IsTrue();
        await Assert.That(result.Contains(1)).IsFalse();
        await Assert.That(result.Contains(3UL << 32)).IsFalse();
    }

    [Test]
    public async Task And_Disjoint()
    {
        RoaringTreemap a = new();
        a.Add(1);

        RoaringTreemap b = new();
        b.Add(1UL << 32);

        RoaringTreemap result = a.And(b);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    [Test]
    public async Task And_WithEmpty()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(2);

        RoaringTreemap empty = new();
        RoaringTreemap result = a.And(empty);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    // === OR (union) ===

    [Test]
    public async Task Or_Union()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(1UL << 32);

        RoaringTreemap b = new();
        b.Add(1UL << 32);
        b.Add(2UL << 32);

        RoaringTreemap result = a.Or(b);
        await Assert.That(result.Cardinality).IsEqualTo(3L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(1UL << 32)).IsTrue();
        await Assert.That(result.Contains(2UL << 32)).IsTrue();
    }

    [Test]
    public async Task Or_WithEmpty()
    {
        RoaringTreemap a = new();
        a.Add(1);

        RoaringTreemap empty = new();
        RoaringTreemap result = a.Or(empty);
        await Assert.That(result.Cardinality).IsEqualTo(1L);
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Or_ResultClone_IsIndependentOfOperands()
    {
        // RoaringTreemap.Or shares bitmap refs with operands for single-side chunks (see
        // class doc "Aliasing model"). Callers needing an independent copy use Clone().
        RoaringTreemap left = new();
        left.Add(1);

        RoaringTreemap right = new();
        right.Add(2UL << 32);

        RoaringTreemap result = left.Or(right).Clone();

        // Mutate the cloned result — must not affect operands.
        result.Add(999);
        result.Add((2UL << 32) + 999);

        await Assert.That(left.Contains(999)).IsFalse();
        await Assert.That(right.Contains((2UL << 32) + 999)).IsFalse();
        await Assert.That(left.Cardinality).IsEqualTo(1L);
        await Assert.That(right.Cardinality).IsEqualTo(1L);
    }

    // === ANDNOT (difference) ===

    [Test]
    public async Task AndNot_Difference()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(1UL << 32);

        RoaringTreemap b = new();
        b.Add(2);
        b.Add(1UL << 32);

        RoaringTreemap result = a.AndNot(b);
        await Assert.That(result.Cardinality).IsEqualTo(1L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(2)).IsFalse();
    }

    [Test]
    public async Task AndNot_NoOverlap_KeepsAll()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(1UL << 32);

        RoaringTreemap b = new();
        b.Add(2UL << 32);

        RoaringTreemap result = a.AndNot(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(1UL << 32)).IsTrue();
    }

    [Test]
    public async Task AndNot_ResultClone_IsIndependentOfSource()
    {
        // RoaringTreemap.AndNot shares bitmap refs with the source for chunks that have no
        // overlap with `other`. Callers needing isolation use Clone().
        RoaringTreemap source = new();
        source.Add(1);
        source.Add(1UL << 32);

        RoaringTreemap other = new();
        other.Add(2UL << 32);

        RoaringTreemap result = source.AndNot(other).Clone();

        // Mutate the cloned result — must not affect the source.
        result.Add(999);
        result.Add((1UL << 32) + 999);

        await Assert.That(source.Contains(999)).IsFalse();
        await Assert.That(source.Contains((1UL << 32) + 999)).IsFalse();
        await Assert.That(source.Cardinality).IsEqualTo(2L);
    }

    // === XOR (symmetric difference) ===

    [Test]
    public async Task Xor_SymmetricDifference()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(1UL << 32);

        RoaringTreemap b = new();
        b.Add(2);
        b.Add(3);
        b.Add(1UL << 32);

        RoaringTreemap result = a.Xor(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
        await Assert.That(result.Contains(2)).IsFalse();
        await Assert.That(result.Contains(1UL << 32)).IsFalse();
    }

    [Test]
    public async Task Xor_Disjoint_ReturnsAll()
    {
        RoaringTreemap a = new();
        a.Add(1);

        RoaringTreemap b = new();
        b.Add(2UL << 32);

        RoaringTreemap result = a.Xor(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(2UL << 32)).IsTrue();
    }

    [Test]
    public async Task Xor_ResultClone_IsIndependentOfOperands()
    {
        // RoaringTreemap.Xor shares bitmap refs for single-side chunks; Clone() detaches.
        RoaringTreemap left = new();
        left.Add(1);

        RoaringTreemap right = new();
        right.Add(2UL << 32);

        RoaringTreemap result = left.Xor(right).Clone();

        // Mutate the cloned result — must not affect operands.
        result.Add(999);
        result.Add((2UL << 32) + 999);

        await Assert.That(left.Contains(999)).IsFalse();
        await Assert.That(right.Contains((2UL << 32) + 999)).IsFalse();
        await Assert.That(left.Cardinality).IsEqualTo(1L);
        await Assert.That(right.Cardinality).IsEqualTo(1L);
    }

    [Test]
    public async Task Xor_Identical_ReturnsEmpty()
    {
        RoaringTreemap a = new();
        a.Add(1);
        a.Add(1UL << 32);

        RoaringTreemap b = new();
        b.Add(1);
        b.Add(1UL << 32);

        RoaringTreemap result = a.Xor(b);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    // === Entries (internal) ===

    [Test]
    public async Task Entries_ReturnsAllBuckets()
    {
        RoaringTreemap tm = new();
        tm.Add(1);
        tm.Add(1UL << 32);
        tm.Add(2UL << 32);

        int bucketCount = tm.Entries.Count();
        await Assert.That(bucketCount).IsEqualTo(3);
    }
}