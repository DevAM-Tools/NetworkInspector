// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ReadOnlyRoaringBitmap"/>: read-only wrapper over RoaringBitmap.
/// Covers set operations, Rank, Select, detached copy, and null-safety.
/// </summary>
internal sealed class ReadOnlyRoaringBitmapTests
{
    // === Empty ===

    [Test]
    public async Task Empty_Bitmap()
    {
        ReadOnlyRoaringBitmap empty = ReadOnlyRoaringBitmap.Empty;
        await Assert.That(empty.IsEmpty).IsTrue();
        await Assert.That(empty.Cardinality).IsEqualTo(0L);
        await Assert.That(empty.Contains(0)).IsFalse();
        // Empty is default — value equality, not reference identity.
        await Assert.That(default(ReadOnlyRoaringBitmap).IsEmpty).IsTrue();
        await Assert.That(empty).IsEqualTo(default(ReadOnlyRoaringBitmap));
    }

    [Test]
    public async Task Empty_Min_Max_Throws()
    {
        ReadOnlyRoaringBitmap empty = ReadOnlyRoaringBitmap.Empty;
        await Assert.That(() => _ = empty.Min).Throws<InvalidOperationException>();
        await Assert.That(() => _ = empty.Max).Throws<InvalidOperationException>();
        await Assert.That(empty.TryGetMin(out uint min)).IsFalse();
        await Assert.That(min).IsEqualTo(0u);
        await Assert.That(empty.TryGetMax(out uint max)).IsFalse();
        await Assert.That(max).IsEqualTo(0u);
    }

    // === Basic operations ===

    [Test]
    public async Task Contains_ReflectsUnderlying()
    {
        RoaringBitmap bm = new();
        bm.Add(10);
        bm.Add(20);
        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();

        await Assert.That(ro.Contains(10)).IsTrue();
        await Assert.That(ro.Contains(20)).IsTrue();
        await Assert.That(ro.Contains(15)).IsFalse();
        await Assert.That(ro.Cardinality).IsEqualTo(2L);
    }

    [Test]
    public async Task Min_Max()
    {
        RoaringBitmap bm = new();
        bm.Add(5);
        bm.Add(100);
        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();

        await Assert.That(ro.Min).IsEqualTo(5u);
        await Assert.That(ro.Max).IsEqualTo(100u);
    }

    // === Rank ===

    [Test]
    public async Task Rank()
    {
        RoaringBitmap bm = new();
        bm.Add(1);
        bm.Add(5);
        bm.Add(10);
        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();

        await Assert.That(ro.Rank(0)).IsEqualTo(0L);
        await Assert.That(ro.Rank(1)).IsEqualTo(1L);
        await Assert.That(ro.Rank(5)).IsEqualTo(2L);
        await Assert.That(ro.Rank(10)).IsEqualTo(3L);
        await Assert.That(ro.Rank(100)).IsEqualTo(3L);
    }

    [Test]
    public async Task Rank_Empty()
    {
        ReadOnlyRoaringBitmap empty = ReadOnlyRoaringBitmap.Empty;
        await Assert.That(empty.Rank(100)).IsEqualTo(0L);
    }

    // === Select ===

    [Test]
    public async Task Select()
    {
        RoaringBitmap bm = new();
        bm.Add(10);
        bm.Add(20);
        bm.Add(30);
        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();

        await Assert.That(ro.Select(0)).IsEqualTo(10u);
        await Assert.That(ro.Select(1)).IsEqualTo(20u);
        await Assert.That(ro.Select(2)).IsEqualTo(30u);
        await Assert.That(ro.Select(3)).IsNull();
    }

    [Test]
    public async Task Select_Empty()
    {
        ReadOnlyRoaringBitmap empty = ReadOnlyRoaringBitmap.Empty;
        await Assert.That(empty.Select(0)).IsNull();
    }

    [Test]
    public async Task Rank_PartialWordInBitmapContainer()
    {
        RoaringBitmap bm = new();
        for (uint i = 0; i < 5000; i++)
        {
            bm.Add(i);
        }

        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();
        await Assert.That(ro.Rank(90)).IsEqualTo(91L);
    }

    [Test]
    public async Task Rank_FallbackForRunContainer()
    {
        RunContainer run = new();
        for (ushort v = 800; v <= 820; v++)
        {
            run.Add(v);
        }

        RoaringBitmap bm = new();
        MethodInfo? insert = typeof(RoaringBitmap).GetMethod(
            "_InsertChunk",
            BindingFlags.NonPublic | BindingFlags.Instance);
        insert!.Invoke(bm, [0, (ushort)0, run]);

        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();
        await Assert.That(ro.Rank(810)).IsEqualTo(11L);
    }

    [Test]
    public async Task Select_FallbackForRunContainer()
    {
        RunContainer run = new();
        for (ushort v = 800; v <= 820; v++)
        {
            run.Add(v);
        }

        RoaringBitmap bm = new();
        MethodInfo? insert = typeof(RoaringBitmap).GetMethod(
            "_InsertChunk",
            BindingFlags.NonPublic | BindingFlags.Instance);
        insert!.Invoke(bm, [0, (ushort)0, run]);

        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();
        await Assert.That(ro.Select(5)).IsEqualTo(805u);
    }

    // === ToBitmap (detached copy) ===

    [Test]
    public async Task ToBitmap_IsDetached()
    {
        RoaringBitmap bm = new();
        bm.Add(1);
        bm.Add(2);
        ReadOnlyRoaringBitmap ro = bm.AsReadOnly();

        RoaringBitmap copy = ro.ToBitmap();
        copy.Add(999);

        // Original should not be affected
        await Assert.That(ro.Contains(999)).IsFalse();
        await Assert.That(bm.Contains(999)).IsFalse();
        await Assert.That(copy.Contains(999)).IsTrue();
    }

    [Test]
    public async Task ToBitmap_Empty_ReturnsEmptyBitmap()
    {
        ReadOnlyRoaringBitmap empty = ReadOnlyRoaringBitmap.Empty;
        RoaringBitmap copy = empty.ToBitmap();
        await Assert.That(copy.IsEmpty).IsTrue();
    }

    // === Set operations ===

    [Test]
    public async Task And()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(3);
        RoaringBitmap b = new();
        b.Add(2);
        b.Add(3);
        b.Add(4);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().And(b.AsReadOnly());
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(2)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
    }

    [Test]
    public async Task And_WithEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().And(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Or()
    {
        RoaringBitmap a = new();
        a.Add(1);
        RoaringBitmap b = new();
        b.Add(2);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().Or(b.AsReadOnly());
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(2)).IsTrue();
    }

    [Test]
    public async Task Or_WithEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().Or(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.Cardinality).IsEqualTo(1L);
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Or_EmptyWithFull()
    {
        RoaringBitmap b = new();
        b.Add(1);

        ReadOnlyRoaringBitmap result = ReadOnlyRoaringBitmap.Empty.Or(b.AsReadOnly());
        await Assert.That(result.Cardinality).IsEqualTo(1L);
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task AndNot()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(3);
        RoaringBitmap b = new();
        b.Add(2);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().AndNot(b.AsReadOnly());
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
        await Assert.That(result.Contains(2)).IsFalse();
    }

    [Test]
    public async Task AndNot_WithEmpty()
    {
        ReadOnlyRoaringBitmap result = ReadOnlyRoaringBitmap.Empty.AndNot(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    [Test]
    public async Task AndNot_SubtractEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().AndNot(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Xor()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        RoaringBitmap b = new();
        b.Add(2);
        b.Add(3);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().Xor(b.AsReadOnly());
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
    }

    [Test]
    public async Task Xor_WithEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);

        ReadOnlyRoaringBitmap result = a.AsReadOnly().Xor(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Xor_EmptyWithFull()
    {
        RoaringBitmap b = new();
        b.Add(1);

        ReadOnlyRoaringBitmap result = ReadOnlyRoaringBitmap.Empty.Xor(b.AsReadOnly());
        await Assert.That(result.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Xor_BothEmpty()
    {
        ReadOnlyRoaringBitmap result = ReadOnlyRoaringBitmap.Empty.Xor(ReadOnlyRoaringBitmap.Empty);
        await Assert.That(result.IsEmpty).IsTrue();
    }
}
