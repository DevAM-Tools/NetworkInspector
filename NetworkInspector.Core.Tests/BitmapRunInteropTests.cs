// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the direct BitmapContainer × RunContainer set operations (F-IX-01).
/// Validates that the new ulong-mask paths produce identical results to the
/// reference value-by-value behavior.
/// </summary>
internal sealed class BitmapRunInteropTests
{
    private static BitmapContainer DenseBitmap()
    {
        BitmapContainer b = new();
        // Set every odd value in [0..10000)
        for (int v = 1; v < 10000; v += 2)
        {
            b.Add((ushort)v);
        }
        return b;
    }

    private static RunContainer Runs(params (ushort Start, ushort Length)[] runs)
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

    private static int CountManually(IContainer c)
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
        BitmapContainer b = DenseBitmap();
        RunContainer r = Runs((100, 9)); // sets 100..109 (10 values)

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

        RunContainer r = Runs((10, 4), (60, 9), (1000, 0));

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
        RunContainer r = Runs((50, 49)); // 50..99 (50 values)
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
        RunContainer r = Runs((50, 99)); // 50..149
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
        RunContainer r = Runs((0, 63)); // bits 0..63 inclusive — entire first word
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
        RunContainer r = Runs((30, 100));
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
        RunContainer left = Runs((1, 3));    // values 1..4 → 4 values
        RunContainer right = Runs((10, 3)); // values 10..13 → 4 values
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
        RunContainer left = Runs((20, 5)); // values 20..25 → 6 values
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
        RunContainer left = Runs((50, 2)); // values 50..52 → 3 values
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
}