// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Direct unit tests for roaring index containers: <see cref="ArrayContainer"/>,
/// <see cref="BitmapContainer"/>, and <see cref="RunContainer"/>.
/// </summary>
internal sealed class IndexContainerTests
{
    // === ArrayContainer ===

    [Test]
    public async Task ArrayContainer_AddContainsMinMaxClone()
    {
        ArrayContainer a = new();
        a = (ArrayContainer)a.Add(10);
        a = (ArrayContainer)a.Add(5);
        a = (ArrayContainer)a.Add(20);

        await Assert.That(a.Cardinality).IsEqualTo(3);
        await Assert.That(a.Contains(5)).IsTrue();
        await Assert.That(a.Contains(15)).IsFalse();
        await Assert.That(a.Min).IsEqualTo((ushort)5);
        await Assert.That(a.Max).IsEqualTo((ushort)20);
        await Assert.That(a.ValueAt(0)).IsEqualTo((ushort)5);

        ArrayContainer clone = (ArrayContainer)a.Clone();
        await Assert.That(clone.Cardinality).IsEqualTo(3);
        await Assert.That(clone.Contains(10)).IsTrue();
    }

    [Test]
    public async Task ArrayContainer_SimdLinearScanThreshold()
    {
        ArrayContainer a = new();
        for (ushort v = 0; v < 32; v += 2)
        {
            a = (ArrayContainer)a.Add(v);
        }
        await Assert.That(a.Contains((ushort)30)).IsTrue();
        await Assert.That(a.Contains((ushort)31)).IsFalse();
    }

    [Test]
    public async Task ArrayContainer_DuplicateAdd_IsIdempotent()
    {
        ArrayContainer a = new();
        a = (ArrayContainer)a.Add(42);
        IContainer same = a.Add(42);
        await Assert.That(ReferenceEquals(a, same)).IsTrue();
        await Assert.That(a.Cardinality).IsEqualTo(1);
    }

    [Test]
    public async Task ArrayContainer_SetOps_WithArrayAndOtherTypes()
    {
        ArrayContainer a = new();
        a = (ArrayContainer)a.Add(1);
        a = (ArrayContainer)a.Add(3);
        a = (ArrayContainer)a.Add(5);

        ArrayContainer b = new();
        b = (ArrayContainer)b.Add(3);
        b = (ArrayContainer)b.Add(7);

        IContainer and = a.And(b);
        await Assert.That(and.Cardinality).IsEqualTo(1);
        await Assert.That(and.Contains(3)).IsTrue();

        IContainer or = a.Or(b);
        await Assert.That(or.Cardinality).IsEqualTo(4);

        IContainer andNot = a.AndNot(b);
        await Assert.That(andNot.Cardinality).IsEqualTo(2);
        await Assert.That(andNot.Contains(1)).IsTrue();

        IContainer xor = a.Xor(b);
        await Assert.That(xor.Cardinality).IsEqualTo(3);
    }

    [Test]
    public async Task ArrayContainer_PromotesToBitmapAtCapacity()
    {
        ArrayContainer a = new();
        for (ushort v = 0; v < ArrayContainer.MaxCapacity; v++)
        {
            a = (ArrayContainer)a.Add(v);
        }
        IContainer promoted = a.Add(ArrayContainer.MaxCapacity);
        await Assert.That(promoted).IsTypeOf<BitmapContainer>();
        await Assert.That(promoted.Cardinality).IsEqualTo(ArrayContainer.MaxCapacity + 1);
    }

    [Test]
    public async Task ArrayContainer_SetOps_WithBitmapAndRun()
    {
        ArrayContainer a = new();
        for (ushort v = 100; v < 120; v++)
        {
            a = (ArrayContainer)a.Add(v);
        }

        BitmapContainer bmp = new();
        for (ushort v = 110; v < 130; v++)
        {
            bmp = (BitmapContainer)bmp.Add(v);
        }

        RunContainer run = new();
        for (ushort v = 200; v <= 210; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        await Assert.That(a.And(bmp).Cardinality).IsGreaterThan(0);
        await Assert.That(a.Or(run).Contains((ushort)200)).IsTrue();
        await Assert.That(a.AndNot(run).Cardinality).IsGreaterThan(0);
        await Assert.That(a.Xor(bmp).Cardinality).IsGreaterThan(0);
    }

    // === BitmapContainer ===

    [Test]
    public async Task BitmapContainer_AddContainsMinMaxClone()
    {
        BitmapContainer b = new();
        b = (BitmapContainer)b.Add(0);
        b = (BitmapContainer)b.Add(100);
        b = (BitmapContainer)b.Add(65535);

        await Assert.That(b.Cardinality).IsEqualTo(3);
        await Assert.That(b.Contains((ushort)100)).IsTrue();
        await Assert.That(b.Contains((ushort)50)).IsFalse();
        await Assert.That(b.Min).IsEqualTo((ushort)0);
        await Assert.That(b.Max).IsEqualTo((ushort)65535);

        BitmapContainer clone = (BitmapContainer)b.Clone();
        await Assert.That(clone.Cardinality).IsEqualTo(3);
    }

    [Test]
    public async Task BitmapContainer_DuplicateAdd_IsIdempotent()
    {
        BitmapContainer b = new();
        b = (BitmapContainer)b.Add(7);
        IContainer same = b.Add(7);
        await Assert.That(ReferenceEquals(b, same)).IsTrue();
        await Assert.That(b.Cardinality).IsEqualTo(1);
    }

    [Test]
    public async Task BitmapContainer_SetOps_BitmapBitmap()
    {
        BitmapContainer a = new();
        a = (BitmapContainer)a.Add(1);
        a = (BitmapContainer)a.Add(2);
        a = (BitmapContainer)a.Add(3);

        BitmapContainer b = new();
        b = (BitmapContainer)b.Add(2);
        b = (BitmapContainer)b.Add(3);
        b = (BitmapContainer)b.Add(4);

        await Assert.That(a.And(b).Cardinality).IsEqualTo(2);
        await Assert.That(a.Or(b).Cardinality).IsEqualTo(4);
        await Assert.That(a.AndNot(b).Cardinality).IsEqualTo(1);
        await Assert.That(a.Xor(b).Cardinality).IsEqualTo(2);
    }

    [Test]
    public async Task BitmapContainer_SparseResult_DowngradesToArray()
    {
        BitmapContainer a = new();
        a = (BitmapContainer)a.Add(10);
        BitmapContainer b = new();
        b = (BitmapContainer)b.Add(20);
        IContainer and = a.And(b);
        await Assert.That(and.Cardinality).IsEqualTo(0);
    }

    [Test]
    public async Task BitmapContainer_DenseAndNot()
    {
        BitmapContainer b = new();
        for (ushort v = 0; v < 5000; v++)
        {
            b = (BitmapContainer)b.Add(v);
        }
        RunContainer r = new();
        for (ushort v = 1000; v <= 2000; v++)
        {
            r = (RunContainer)r.Add(v);
        }
        IContainer result = b.AndNot(r);
        await Assert.That(result.Cardinality).IsEqualTo(5000 - 1001);
    }

    // === RunContainer ===

    [Test]
    public async Task RunContainer_AddContainsMinMaxClone()
    {
        RunContainer r = new();
        for (ushort v = 50; v <= 60; v++)
        {
            r = (RunContainer)r.Add(v);
        }
        r = (RunContainer)r.Add(100);

        await Assert.That(r.Cardinality).IsEqualTo(12);
        await Assert.That(r.Contains((ushort)55)).IsTrue();
        await Assert.That(r.Contains((ushort)61)).IsFalse();
        await Assert.That(r.Min).IsEqualTo((ushort)50);
        await Assert.That(r.Max).IsEqualTo((ushort)100);

        RunContainer clone = (RunContainer)r.Clone();
        await Assert.That(clone.Cardinality).IsEqualTo(12);
    }

    [Test]
    public async Task RunContainer_DuplicateAdd_IsIdempotent()
    {
        RunContainer r = new();
        r = (RunContainer)r.Add(5);
        IContainer same = r.Add(5);
        await Assert.That(ReferenceEquals(r, same)).IsTrue();
    }

    [Test]
    public async Task RunContainer_SetOps_RunRun()
    {
        RunContainer a = new();
        for (ushort v = 10; v <= 20; v++)
        {
            a = (RunContainer)a.Add(v);
        }
        RunContainer b = new();
        for (ushort v = 15; v <= 25; v++)
        {
            b = (RunContainer)b.Add(v);
        }

        await Assert.That(a.And(b).Cardinality).IsEqualTo(6);
        await Assert.That(a.Or(b).Cardinality).IsEqualTo(16);
        await Assert.That(a.AndNot(b).Cardinality).IsEqualTo(5);
        await Assert.That(a.Xor(b).Cardinality).IsEqualTo(10);
    }

    [Test]
    public async Task RunContainer_SetOps_WithArrayAndBitmap()
    {
        RunContainer r = new();
        for (ushort v = 0; v < 100; v++)
        {
            r = (RunContainer)r.Add(v);
        }

        ArrayContainer a = new();
        a = (ArrayContainer)a.Add(50);
        a = (ArrayContainer)a.Add(200);

        BitmapContainer b = new();
        for (ushort v = 80; v < 120; v++)
        {
            b = (BitmapContainer)b.Add(v);
        }

        await Assert.That(r.And(a).Cardinality).IsEqualTo(1);
        await Assert.That(r.Or(a).Contains((ushort)200)).IsTrue();
        await Assert.That(r.AndNot(b).Cardinality).IsLessThan(r.Cardinality);
        await Assert.That(r.Xor(b).Cardinality).IsGreaterThan(0);
    }

    [Test]
    public async Task RunContainer_EmptyMinMax()
    {
        RunContainer r = new();
        await Assert.That(r.Cardinality).IsEqualTo(0);
        await Assert.That(r.Min).IsEqualTo((ushort)0);
        await Assert.That(r.Max).IsEqualTo((ushort)0);
    }
}
