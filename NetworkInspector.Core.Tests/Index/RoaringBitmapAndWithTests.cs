// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Regression tests for <see cref="RoaringBitmap"/> in-place set operations and operand isolation.
/// </summary>
internal sealed class RoaringBitmapAndWithTests
{
    [Test]
    public async Task Or_SingleSideInsert_DoesNotAliasOperand()
    {
        RoaringBitmap left = new();
        left.Add(1);

        RoaringBitmap right = new();
        right.Add(100);

        RoaringBitmap result = left.Or(right);
        result.Add(200);

        await Assert.That(right.Contains(200u)).IsFalse();
        await Assert.That(left.Contains(200u)).IsFalse();
        await Assert.That(right.Contains(100u)).IsTrue();
    }

    [Test]
    public async Task OrWith_ThenAdd_DoesNotCorruptOperand()
    {
        RoaringBitmap left = new();
        left.Add(1);
        left.Add(2);

        RoaringBitmap right = new();
        right.Add(100);

        left.OrWith(right);
        left.Add(200);

        await Assert.That(right.Contains(200u)).IsFalse();
        await Assert.That(right.Contains(100u)).IsTrue();
        await Assert.That(left.Contains(200u)).IsTrue();
    }

    [Test]
    public async Task OrWith_ArrayAndBitmapChunk_DoesNotCorruptOperand()
    {
        RoaringBitmap sparse = new();
        sparse.Add(1);
        sparse.Add(3);

        RoaringBitmap dense = new();
        for (uint i = 0; i < 4097; i++)
        {
            dense.Add(i);
        }

        long denseCardinalityBefore = dense.Cardinality;

        RoaringBitmap merged = new();
        merged.OrWith(sparse);
        merged.OrWith(dense);
        merged.Add(50_000);

        await Assert.That(dense.Cardinality).IsEqualTo(denseCardinalityBefore);
        await Assert.That(dense.Contains(50_000u)).IsFalse();
        await Assert.That(dense.Contains(0u)).IsTrue();
    }

    [Test]
    public async Task XorWith_ThenAdd_DoesNotCorruptOperand()
    {
        RoaringBitmap left = new();
        left.Add(1);
        left.Add(2);

        RoaringBitmap right = new();
        right.Add(2);
        right.Add(100);

        left.XorWith(right);
        left.Add(200);

        await Assert.That(right.Contains(200u)).IsFalse();
        await Assert.That(right.Contains(100u)).IsTrue();
        await Assert.That(left.Contains(1u)).IsTrue();
        await Assert.That(left.Contains(2u)).IsFalse();
    }

    [Test]
    public async Task ConcurrentAdd_HeldViewContains_DoesNotLosePublishedValues()
    {
        RoaringBitmap bitmap = new();
        ReadOnlyRoaringBitmap view = bitmap.AsReadOnly();
        const int count = 8000;
        Exception? readerFailure = null;
        int vanished = 0;

        Task writer = Task.Run(() =>
        {
            for (uint i = 0; i < count; i++)
            {
                bitmap.Add(i);
            }
        });

        Task reader = Task.Run(() =>
        {
            try
            {
                bool[] seen = new bool[count];
                while (!writer.IsCompleted)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (view.Contains(i))
                        {
                            seen[i] = true;
                        }
                        else if (seen[i])
                        {
                            Interlocked.Increment(ref vanished);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                readerFailure = ex;
            }
        });

        await Task.WhenAll(writer, reader);
        await Assert.That(readerFailure).IsNull();
        await Assert.That(vanished).IsEqualTo(0);
        await Assert.That(view.Contains((uint)(count - 1))).IsTrue();
        await Assert.That(view.Cardinality).IsEqualTo(count);
    }

    [Test]
    public async Task AndWith_AfterOrWithAliasedContainer_DoesNotCorruptOperand()
    {
        RoaringBitmap left = new();
        left.Add(100);
        left.Add(200);
        left.Add(65536);

        RoaringBitmap right = new();
        right.Add(65537);

        RoaringBitmap union = left.Or(right);

        RoaringBitmap filter = new();
        filter.Add(100);

        union.AndWith(filter);

        await Assert.That(left.Contains(200u)).IsTrue();
        await Assert.That(left.Contains(100u)).IsTrue();
        await Assert.That(left.Contains(65536u)).IsTrue();
        await Assert.That(union.Contains(100u)).IsTrue();
        await Assert.That(union.Contains(200u)).IsFalse();
        await Assert.That(union.Contains(65536u)).IsFalse();
        await Assert.That(union.Contains(65537u)).IsFalse();
    }
}
