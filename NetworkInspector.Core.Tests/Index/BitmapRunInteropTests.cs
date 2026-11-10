// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the direct BitmapContainer × RunContainer set operations.
/// Validates that the new ulong-mask paths produce identical results to the
/// reference value-by-value behavior.
/// </summary>
internal sealed class BitmapRunInteropTests
{
    private static BitmapContainer _DenseBitmap()
    {
        BitmapContainer b = new();
        // Set every odd value in [0..10000)
        for (int v = 1; v < 10000; v += 2)
        {
            b.Add((ushort)v);
        }
        return b;
    }

    private static RunContainer _Runs(params (ushort Start, ushort Length)[] runs)
    {
        RunContainer r = new();
        foreach ((ushort Start, ushort Length) in runs)
        {
            for (int v = Start; v <= Start + Length; v++)
            {
                r.Add((ushort)v);
            }
        }
        return r;
    }

    private static int _CountManually(IContainer c)
    {
        int n = 0;
        for (int v = 0; v <= ushort.MaxValue; v++)
        {
            if (c.Contains((ushort)v))
            {
                n++;
            }
            if (v == ushort.MaxValue)
            {
                break;
            }
        }
        return n;
    }

    [Test]
    public async Task Or_BitmapWithRun_SingleRun()
    {
        BitmapContainer b = _DenseBitmap();
        RunContainer r = _Runs((100, 9)); // sets 100..109 (10 values)

        IContainer result = b.Or(r);
        // Expected cardinality: original odd-only bits + new even bits within [100..109]
        // Odd values 101,103,105,107,109 already present (5). New even: 100,102,104,106,108 (5).
        // Original bitmap cardinality:
        int originalCard = b.Cardinality;
        await Assert.That(result.Cardinality).IsEqualTo(originalCard + 5);
        await Assert.That(result.Contains(100)).IsTrue();
        await Assert.That(result.Contains(108)).IsTrue();
    }

    [Test]
    public async Task Or_BitmapWithRun_MultipleRuns_ExactCount()
    {
        BitmapContainer b = new();
        b.Add(50);
        b.Add(70000 % 65536); // 4464

        RunContainer r = _Runs((10, 4), (60, 9), (1000, 0));

        IContainer result = b.Or(r);
        // Brute-force expected
        bool[] expected = new bool[65536];
        expected[50] = true;
        expected[4464] = true;
        for (int i = 10; i <= 14; i++)
        {
            expected[i] = true;
        }
        for (int i = 60; i <= 69; i++)
        {
            expected[i] = true;
        }
        expected[1000] = true;
        int expectedCount = expected.Count(x => x);
        await Assert.That(result.Cardinality).IsEqualTo(expectedCount);
    }

    [Test]
    public async Task AndNot_BitmapWithRun_ClearsRangeBits()
    {
        BitmapContainer b = new();
        for (int v = 0; v <= 200; v++)
        {
            b.Add((ushort)v);
        }
        RunContainer r = _Runs((50, 49)); // 50..99 (50 values)
        IContainer result = b.AndNot(r);
        await Assert.That(result.Cardinality).IsEqualTo(201 - 50);
        await Assert.That(result.Contains(50)).IsFalse();
        await Assert.That(result.Contains(99)).IsFalse();
        await Assert.That(result.Contains(49)).IsTrue();
        await Assert.That(result.Contains(100)).IsTrue();
    }

    [Test]
    public async Task Xor_BitmapWithRun_TogglesRangeBits()
    {
        BitmapContainer b = new();
        for (int v = 0; v < 100; v++)
        {
            b.Add((ushort)v);
        }
        RunContainer r = _Runs((50, 99)); // 50..149
        IContainer result = b.Xor(r);
        // [0..49] kept (50), [50..99] toggled off (50 cleared), [100..149] toggled on (50 added)
        await Assert.That(result.Cardinality).IsEqualTo(100);
        await Assert.That(result.Contains(0)).IsTrue();
        await Assert.That(result.Contains(50)).IsFalse();
        await Assert.That(result.Contains(99)).IsFalse();
        await Assert.That(result.Contains(149)).IsTrue();
    }

    [Test]
    public async Task Or_RunSpansFullWord_64Bits()
    {
        // Force a run aligned to a single 64-bit word
        BitmapContainer b = new();
        RunContainer r = _Runs((0, 63)); // bits 0..63 inclusive — entire first word
        IContainer result = b.Or(r);
        await Assert.That(result.Cardinality).IsEqualTo(64);
        for (int v = 0; v <= 63; v++)
        {
            await Assert.That(result.Contains((ushort)v)).IsTrue();
        }
    }

    [Test]
    public async Task Or_RunSpansMultipleWords_AcrossBoundary()
    {
        BitmapContainer b = new();
        // Run from 30..130 — crosses at least two 64-bit word boundaries
        RunContainer r = _Runs((30, 100));
        IContainer result = b.Or(r);
        await Assert.That(result.Cardinality).IsEqualTo(101);
        await Assert.That(result.Contains(30)).IsTrue();
        await Assert.That(result.Contains(130)).IsTrue();
        await Assert.That(result.Contains(29)).IsFalse();
        await Assert.That(result.Contains(131)).IsFalse();
    }

    // === RunContainer.Or operand-safety (regression for HIGH-1) ===

    [Test]
    public async Task RunContainer_Or_RunOperand_DoesNotMutateLeftOperand()
    {
        // Regression for HIGH-1: the small-union path in RunContainer.Or previously
        // started the merge from `this` rather than `Clone()`, causing RunContainer.Add
        // (which is in-place) to silently append the right operand's runs to the left
        // operand. After the fix, `left` must contain only its original values.
        RunContainer left = _Runs((1, 3));    // values 1..4 → 4 values
        RunContainer right = _Runs((10, 3)); // values 10..13 → 4 values
        int cardinalityBefore = left.Cardinality;

        IContainer _ = left.Or(right);

        // Left operand must be unchanged.
        await Assert.That(left.Cardinality).IsEqualTo(cardinalityBefore);
        await Assert.That(left.Contains(1)).IsTrue();
        await Assert.That(left.Contains(4)).IsTrue();
        await Assert.That(left.Contains(10)).IsFalse();
        await Assert.That(left.Contains(13)).IsFalse();
    }

    [Test]
    public async Task RunContainer_Or_ArrayOperand_DoesNotMutateLeftOperand()
    {
        // Same small-union path is taken when the right operand is an ArrayContainer
        // with few enough values that totalRuns < 16.
        RunContainer left = _Runs((20, 5)); // values 20..25 → 6 values
        ArrayContainer right = new();
        right.Add(100);
        right.Add(101);
        int cardinalityBefore = left.Cardinality;

        IContainer _ = left.Or(right);

        await Assert.That(left.Cardinality).IsEqualTo(cardinalityBefore);
        await Assert.That(left.Contains(100)).IsFalse();
        await Assert.That(left.Contains(101)).IsFalse();
    }

    [Test]
    public async Task RunContainer_Or_BitmapOperand_DoesNotMutateLeftOperand()
    {
        // Verify left operand immutability when right is a BitmapContainer.
        RunContainer left = _Runs((50, 2)); // values 50..52 → 3 values
        BitmapContainer right = new();
        for (int v = 200; v < 210; v++)
        {
            right.Add((ushort)v);
        }
        int cardinalityBefore = left.Cardinality;

        IContainer _ = left.Or(right);

        await Assert.That(left.Cardinality).IsEqualTo(cardinalityBefore);
        await Assert.That(left.Contains(200)).IsFalse();
        await Assert.That(left.Contains(209)).IsFalse();
    }

    [Test]
    public async Task BitmapContainer_MinMaxClone_AndInPlaceOps()
    {
        BitmapContainer a = new();
        a.Add(100);
        a.Add(200);
        a.Add(300);

        await Assert.That(a.Min).IsEqualTo((ushort)100);
        await Assert.That(a.Max).IsEqualTo((ushort)300);

        BitmapContainer clone = (BitmapContainer)a.Clone();
        await Assert.That(clone.Cardinality).IsEqualTo(a.Cardinality);

        BitmapContainer b = new();
        b.Add(200);
        b.Add(400);
        IContainer andResult = a.And(b);
        IContainer orResult = a.Or(b);
        IContainer andNotResult = a.AndNot(b);
        IContainer xorResult = a.Xor(b);

        await Assert.That(andResult.Contains(200)).IsTrue();
        await Assert.That(orResult.Contains(400)).IsTrue();
        await Assert.That(andNotResult.Contains(100)).IsTrue();
        await Assert.That(xorResult.Contains(400)).IsTrue();

    }

    [Test]
    public async Task RunContainer_OrAndXor_OfTwoLongRuns_HaveTheExactSets()
    {
        RunContainer left = _Runs((0, 59_999));        // 0..59999
        RunContainer right = _Runs((30_000, 35_535));  // 30000..65535

        IContainer union = left.Or(right);
        IContainer symmetric = left.Xor(right);

        await Assert.That(union.Cardinality).IsEqualTo(65_536);
        await Assert.That(union.Contains(0)).IsTrue();
        await Assert.That(union.Contains(65_535)).IsTrue();
        await Assert.That(symmetric.Cardinality).IsEqualTo(35_536);
        await Assert.That(symmetric.Contains(29_999)).IsTrue();
        await Assert.That(symmetric.Contains(30_000)).IsFalse();
        await Assert.That(symmetric.Contains(59_999)).IsFalse();
        await Assert.That(symmetric.Contains(60_000)).IsTrue();

        // The operands are not mutated by either operation.
        await Assert.That(left.Cardinality).IsEqualTo(60_000);
        await Assert.That(right.Cardinality).IsEqualTo(35_536);
    }

    [Test]
    public async Task RunContainer_Or_WithAnArrayOperand_KeepsEveryValue()
    {
        RunContainer run = _Runs((0, 4_999));          // 0..4999
        ArrayContainer array = new();
        array = (ArrayContainer)array.Add(5_000);
        array = (ArrayContainer)array.Add(7_000);

        IContainer union = run.Or(array);

        await Assert.That(union.Cardinality).IsEqualTo(5_002);
        await Assert.That(union.Contains(7_000)).IsTrue();
        await Assert.That(union.Contains(5_001)).IsFalse();
    }

    [Test]
    public async Task RunContainer_Xor_WithABitmapOperand_RemovesTheOverlap()
    {
        RunContainer run = _Runs((0, 9));              // 0..9
        BitmapContainer bitmap = new();
        for (int v = 5; v <= 11; v++)
        {
            bitmap.Add((ushort)v);
        }


        IContainer symmetric = run.Xor(bitmap);

        await Assert.That(symmetric.Cardinality).IsEqualTo(7);
        await Assert.That(symmetric.Contains(0)).IsTrue();
        await Assert.That(symmetric.Contains(4)).IsTrue();
        await Assert.That(symmetric.Contains(5)).IsFalse();
        await Assert.That(symmetric.Contains(10)).IsTrue();
        await Assert.That(symmetric.Contains(11)).IsTrue();
    }

    [Test]
    public async Task BitmapContainer_SetOps_WithArrayOperand()
    {
        BitmapContainer bitmap = new();
        bitmap.Add(1);
        bitmap.Add(2);
        ArrayContainer array = new();
        array = (ArrayContainer)array.Add(2);
        array = (ArrayContainer)array.Add(3);

        await Assert.That(bitmap.And(array).Contains(2)).IsTrue();
        await Assert.That(bitmap.Or(array).Contains(3)).IsTrue();
        await Assert.That(bitmap.AndNot(array).Contains(1)).IsTrue();
        await Assert.That(bitmap.Xor(array).Contains(3)).IsTrue();
    }
}
