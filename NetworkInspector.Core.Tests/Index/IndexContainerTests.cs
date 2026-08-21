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
        await Assert.That(bmp.AndNot(run).Cardinality).IsGreaterThan(0);
        await Assert.That(a.Xor(bmp).Cardinality).IsGreaterThan(0);
    }

    [Test]
    public async Task ArrayContainer_Or_DoesNotMutateOtherOperand()
    {
        ArrayContainer array = new();
        array = (ArrayContainer)array.Add(1);
        array = (ArrayContainer)array.Add(3);
        array = (ArrayContainer)array.Add(5);

        BitmapContainer bitmap = new();
        bitmap = (BitmapContainer)bitmap.Add(3);
        bitmap = (BitmapContainer)bitmap.Add(9);
        int bitmapCardinality = bitmap.Cardinality;

        RunContainer run = new();
        run = (RunContainer)run.Add(20);
        run = (RunContainer)run.Add(21);
        int runCardinality = run.Cardinality;

        IContainer orWithBitmap = array.Or(bitmap);
        IContainer orWithRun = array.Or(run);

        await Assert.That(orWithBitmap.Cardinality).IsEqualTo(4);
        await Assert.That(orWithBitmap.Contains((ushort)9)).IsTrue();
        await Assert.That(bitmap.Cardinality).IsEqualTo(bitmapCardinality);
        await Assert.That(bitmap.Contains((ushort)3)).IsTrue();
        await Assert.That(bitmap.Contains((ushort)9)).IsTrue();
        await Assert.That(bitmap.Contains((ushort)1)).IsFalse();

        await Assert.That(orWithRun.Cardinality).IsEqualTo(5);
        await Assert.That(run.Cardinality).IsEqualTo(runCardinality);
        await Assert.That(run.Contains((ushort)20)).IsTrue();
        await Assert.That(run.Contains((ushort)21)).IsTrue();
        await Assert.That(run.Contains((ushort)1)).IsFalse();
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

    [Test]
    public async Task BitmapContainer_EmptyMinMax_ReturnsZero()
    {
        BitmapContainer empty = new();
        await Assert.That(empty.Cardinality).IsEqualTo(0);
        await Assert.That(empty.Min).IsEqualTo((ushort)0);
        await Assert.That(empty.Max).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task BitmapContainer_And_DenseResult_StaysBitmap()
    {
        BitmapContainer a = new();
        BitmapContainer b = new();
        for (ushort v = 0; v < 5000; v++)
        {
            a = (BitmapContainer)a.Add(v);
            b = (BitmapContainer)b.Add(v);
        }

        IContainer result = a.And(b);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5000);
    }

    [Test]
    public async Task BitmapContainer_AndNot_DenseBitmapResult_StaysBitmap()
    {
        BitmapContainer a = new();
        for (ushort v = 0; v < 6000; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        BitmapContainer b = new();
        for (ushort v = 1000; v < 1500; v++)
        {
            b = (BitmapContainer)b.Add(v);
        }

        IContainer result = a.AndNot(b);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5500);
    }

    [Test]
    public async Task BitmapContainer_AndNot_DenseRunResult_StaysBitmap()
    {
        BitmapContainer a = new();
        for (ushort v = 0; v < 6000; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        RunContainer run = new();
        for (ushort v = 2000; v <= 2500; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        IContainer result = a.AndNot(run);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5499);
    }

    [Test]
    public async Task BitmapContainer_AndNot_ArrayResult_StaysBitmap()
    {
        BitmapContainer a = new();
        for (ushort v = 0; v < 6000; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        ArrayContainer array = new();
        for (ushort v = 0; v < 100; v++)
        {
            array = (ArrayContainer)array.Add(v);
        }

        IContainer result = a.AndNot(array);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5900);
    }

    [Test]
    public async Task BitmapContainer_Xor_DenseBitmapResult_StaysBitmap()
    {
        BitmapContainer a = new();
        BitmapContainer b = new();
        for (ushort v = 0; v < 3000; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        for (ushort v = 2500; v < 5500; v++)
        {
            b = (BitmapContainer)b.Add(v);
        }

        IContainer result = a.Xor(b);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5000);
    }

    [Test]
    public async Task BitmapContainer_Xor_DenseRunResult_StaysBitmap()
    {
        BitmapContainer a = new();
        for (ushort v = 0; v < 5000; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        RunContainer run = new();
        for (ushort v = 4500; v <= 6500; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        IContainer result = a.Xor(run);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsGreaterThan(ArrayContainer.MaxCapacity);
    }

    [Test]
    public async Task BitmapContainer_Xor_ArrayResult_StaysBitmap()
    {
        BitmapContainer a = new();
        for (ushort v = 0; v < 5500; v++)
        {
            a = (BitmapContainer)a.Add(v);
        }

        ArrayContainer array = new();
        for (ushort v = 5000; v < 5100; v++)
        {
            array = (ArrayContainer)array.Add(v);
        }

        IContainer result = a.Xor(array);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(5400);
    }

    [Test]
    public async Task ArrayContainer_SimdLinearContains_EarlyExitInVectorRange()
    {
        ArrayContainer array = new();
        for (ushort v = 10; v <= 24; v += 2)
        {
            array = (ArrayContainer)array.Add(v);
        }

        await Assert.That(array.Contains((ushort)5)).IsFalse();
        await Assert.That(array.Contains((ushort)14)).IsTrue();
    }

    [Test]
    public async Task RunContainer_Add_ExtendsNextRun()
    {
        RunContainer run = new();
        run = (RunContainer)run.Add(11);
        run = (RunContainer)run.Add(10);

        await Assert.That(run.Contains((ushort)10)).IsTrue();
        await Assert.That(run.Contains((ushort)11)).IsTrue();
        await Assert.That(run.RunCount).IsEqualTo(1);
    }

    [Test]
    public async Task RunContainer_Or_LargeUnion_PromotesToBitmap()
    {
        RunContainer run = new();
        for (ushort v = 0; v < 3000; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        ArrayContainer array = new();
        for (ushort v = 3000; v < 6000; v++)
        {
            array = (ArrayContainer)array.Add(v);
        }

        IContainer result = run.Or(array);
        await Assert.That(result).IsTypeOf<BitmapContainer>();
        await Assert.That(result.Cardinality).IsEqualTo(6000);
    }

    [Test]
    public async Task RunContainer_Add_MergingRuns_RemovesSecondRun()
    {
        RunContainer run = new();
        for (ushort v = 10; v <= 15; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        for (ushort v = 17; v <= 20; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        await Assert.That(run.RunCount).IsEqualTo(2);
        run = (RunContainer)run.Add(16);
        await Assert.That(run.RunCount).IsEqualTo(1);
        await Assert.That(run.Contains((ushort)20)).IsTrue();
        await Assert.That(run.Min).IsEqualTo((ushort)10);
        await Assert.That(run.Max).IsEqualTo((ushort)20);
    }

    [Test]
    public async Task RunContainer_And_WithBitmap_UsesRangePath()
    {
        RunContainer run = new();
        for (ushort v = 200; v <= 210; v++)
        {
            run = (RunContainer)run.Add(v);
        }

        BitmapContainer bmp = new();
        for (ushort v = 205; v <= 215; v++)
        {
            bmp = (BitmapContainer)bmp.Add(v);
        }

        IContainer and = run.And(bmp);
        await Assert.That(and.Cardinality).IsEqualTo(6);
        await Assert.That(and.Contains((ushort)205)).IsTrue();
        await Assert.That(and.Contains((ushort)215)).IsFalse();
    }

    [Test]
    public async Task RunContainer_AndNot_LargeResult_PromotesToBitmap()
    {
        RunContainer self = new();
        for (ushort v = 0; v < 5000; v++)
        {
            self = (RunContainer)self.Add(v);
        }

        RunContainer other = new();
        for (ushort v = 0; v < 100; v++)
        {
            other = (RunContainer)other.Add(v);
        }

        IContainer diff = self.AndNot(other);
        await Assert.That(diff).IsTypeOf<BitmapContainer>();
        await Assert.That(diff.Cardinality).IsGreaterThan(ArrayContainer.MaxCapacity);
    }

    [Test]
    public async Task RunContainer_And_FallbackProbePath()
    {
        RunContainer run = new();
        run = (RunContainer)run.Add(42);
        run = (RunContainer)run.Add(43);

        StubProbeContainer stub = new(42);
        IContainer and = run.And(stub);
        await Assert.That(and.Cardinality).IsEqualTo(1);
        await Assert.That(and.Contains((ushort)42)).IsTrue();
    }

    /// <summary>Minimal <see cref="IContainer"/> stub for exercising RunContainer fallback set-op paths.</summary>
    private sealed class StubProbeContainer(ushort value) : IContainer
    {
        public int Cardinality => 1;

        public ushort Min => value;

        public ushort Max => value;

        public bool Contains(ushort probe) => probe == value;

        public IContainer Add(ushort _) => this;

        public IContainer Clone() => this;

        public IContainer And(IContainer _) => this;

        public IContainer Or(IContainer _) => this;

        public IContainer AndNot(IContainer _) => this;

        public IContainer Xor(IContainer _) => this;
    }
}
