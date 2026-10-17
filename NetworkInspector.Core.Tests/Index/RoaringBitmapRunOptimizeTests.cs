// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="RoaringBitmap.RunOptimize"/> and the run optimization of completed chunks in
/// <see cref="PacketIndex"/>: containers convert to run containers only when that is smaller, values never change,
/// and every operation on an optimized bitmap gives the result of the unoptimized one.
/// </summary>
internal sealed class RoaringBitmapRunOptimizeTests
{
    private static readonly System.Reflection.FieldInfo _ContainersField =
        typeof(RoaringBitmap).GetField("_Containers", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly System.Reflection.FieldInfo _GroupBitmapsField =
        typeof(PacketIndex).GetField("_GroupBitmaps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The container types of a bitmap, in key order.</summary>
    private static List<Type> _ContainerTypes(RoaringBitmap bitmap)
    {
        IContainer?[] containers = (IContainer?[])_ContainersField.GetValue(bitmap)!;
        List<Type> types = [];
        foreach (IContainer? container in containers)
        {
            if (container is not null)
            {
                types.Add(container.GetType());
            }
        }

        return types;
    }

    /// <summary>Chunk 0: dense ranges (bitmap); chunk 1: sparse odd values (array); chunk 2: a run ending at 65535 and
    /// runs across word boundaries (bitmap); chunk 3: two short runs (array).</summary>
    private static RoaringBitmap _MixedBitmap()
    {
        RoaringBitmap bitmap = new();
        for (uint v = 0; v < 10_000; v++)
        {
            bitmap.Add(v);
        }

        for (uint v = 20_000; v < 30_000; v++)
        {
            bitmap.Add(v);
        }

        for (uint v = 65_536 + 1; v < 65_536 + 6_000; v += 2)
        {
            bitmap.Add(v);
        }

        for (uint v = (2 * 65_536) + 60_000; v < 3 * 65_536; v++)
        {
            bitmap.Add(v);
        }

        for (uint v = (2 * 65_536) + 63; v < (2 * 65_536) + 5_000; v++)
        {
            bitmap.Add(v);
        }

        for (uint v = (3 * 65_536) + 10; v < (3 * 65_536) + 110; v++)
        {
            bitmap.Add(v);
            bitmap.Add(v + 1_000);
        }

        return bitmap;
    }

    private static async Task _AssertSameSet(RoaringBitmap actual, RoaringBitmap expected)
    {
        await Assert.That(actual.Cardinality).IsEqualTo(expected.Cardinality);
        for (long position = 0; position < expected.Cardinality; position++)
        {
            if (actual.Select(position) != expected.Select(position))
            {
                await Assert.That(actual.Select(position)).IsEqualTo(expected.Select(position));
            }
        }
    }

    [Test]
    public async Task RunOptimizeConvertsOnlyWhereRunsAreSmaller()
    {
        RoaringBitmap bitmap = _MixedBitmap();

        bool converted = bitmap.RunOptimize();

        await Assert.That(converted).IsTrue();
        List<Type> types = _ContainerTypes(bitmap);
        await Assert.That(types[0]).IsEqualTo(typeof(RunContainer));
        await Assert.That(types[1]).IsEqualTo(typeof(ArrayContainer));
        await Assert.That(types[2]).IsEqualTo(typeof(RunContainer));
        await Assert.That(types[3]).IsEqualTo(typeof(RunContainer));
    }

    [Test]
    public async Task RunOptimizeKeepsTheValues()
    {
        RoaringBitmap expected = _MixedBitmap();
        RoaringBitmap optimized = expected.Clone();

        _ = optimized.RunOptimize();

        await _AssertSameSet(optimized, expected);
        await Assert.That(optimized.Contains((2 * 65_536) + 65_535)).IsTrue();
        await Assert.That(optimized.Contains((2 * 65_536) + 62)).IsFalse();
        await Assert.That(optimized.Rank((2 * 65_536) + 5_000)).IsEqualTo(expected.Rank((2 * 65_536) + 5_000));
    }

    [Test]
    public async Task RunOptimizeSecondCallConvertsNothing()
    {
        RoaringBitmap bitmap = _MixedBitmap();
        _ = bitmap.RunOptimize();

        await Assert.That(bitmap.RunOptimize()).IsFalse();
    }

    [Test]
    public async Task RunOptimizeEmptyBitmapConvertsNothing()
    {
        RoaringBitmap bitmap = new();

        await Assert.That(bitmap.RunOptimize()).IsFalse();
        await Assert.That(bitmap.Cardinality).IsEqualTo(0L);
    }

    [Test]
    public async Task RunOptimizeSingleValueStaysAnArray()
    {
        // One value: array 4 bytes, run 6 bytes.
        RoaringBitmap bitmap = new();
        bitmap.Add(7);

        await Assert.That(bitmap.RunOptimize()).IsFalse();
        await Assert.That(_ContainerTypes(bitmap)[0]).IsEqualTo(typeof(ArrayContainer));
    }

    [Test]
    public async Task OptimizedBitmapSetOperationsMatchTheUnoptimizedOnes()
    {
        RoaringBitmap plain = _MixedBitmap();
        RoaringBitmap optimized = plain.Clone();
        _ = optimized.RunOptimize();
        RoaringBitmap other = new();
        for (uint v = 5_000; v < 140_000; v += 3)
        {
            other.Add(v);
        }

        await _AssertSameSet(optimized.And(other), plain.And(other));
        await _AssertSameSet(optimized.Or(other), plain.Or(other));
        await _AssertSameSet(optimized.AndNot(other), plain.AndNot(other));
        await _AssertSameSet(optimized.Xor(other), plain.Xor(other));
        await _AssertSameSet(other.AndNot(optimized), other.AndNot(plain));
    }

    [Test]
    public async Task OptimizedBitmapInPlaceOperationsMatchTheUnoptimizedOnes()
    {
        // PresenceQuery clones an index bitmap and mutates the clone in place, so run containers reach the in-place
        // operations on both sides of the call.
        RoaringBitmap plain = _MixedBitmap();
        RoaringBitmap optimized = plain.Clone();
        _ = optimized.RunOptimize();
        RoaringBitmap other = new();
        for (uint v = 5_000; v < 140_000; v += 3)
        {
            other.Add(v);
        }

        foreach (Action<RoaringBitmap, RoaringBitmap> operation in new Action<RoaringBitmap, RoaringBitmap>[]
        {
            static (target, operand) => target.AndWith(operand),
            static (target, operand) => target.OrWith(operand),
            static (target, operand) => target.AndNotWith(operand),
            static (target, operand) => target.XorWith(operand),
        })
        {
            RoaringBitmap expectedLeft = plain.Clone();
            RoaringBitmap actualLeft = optimized.Clone();
            operation(expectedLeft, other);
            operation(actualLeft, other);
            await _AssertSameSet(actualLeft, expectedLeft);

            RoaringBitmap expectedRight = other.Clone();
            RoaringBitmap actualRight = other.Clone();
            operation(expectedRight, plain);
            operation(actualRight, optimized);
            await _AssertSameSet(actualRight, expectedRight);
        }

        // The operands are unchanged (no aliasing of an adopted run container).
        await _AssertSameSet(optimized, plain);
    }

    [Test]
    public async Task OptimizedBitmapAcceptsLaterAdds()
    {
        RoaringBitmap plain = _MixedBitmap();
        RoaringBitmap optimized = plain.Clone();
        _ = optimized.RunOptimize();

        foreach (uint v in new uint[] { 10_000, 15_000, 19_999, 65_535, (2 * 65_536) + 30_000 })
        {
            plain.Add(v);
            optimized.Add(v);
        }

        await _AssertSameSet(optimized, plain);
    }

    [Test]
    public async Task RunOptimizeWithALiveViewKeepsTheViewConsistent()
    {
        // A view reads the live bitmap through the seqlock: after the conversion it still sees every value.
        RoaringBitmap bitmap = _MixedBitmap();
        ReadOnlyRoaringBitmap view = bitmap.AsReadOnly();

        await Assert.That(bitmap.RunOptimize()).IsTrue();

        await Assert.That(view.Cardinality).IsEqualTo(bitmap.Cardinality);
        await Assert.That(view.Contains((2 * 65_536) + 65_535)).IsTrue();
    }

    [Test]
    public async Task PacketIndexRunOptimizesCompletedChunks()
    {
        // 70 000 packets, all in one index group: committing packet 65 536 completes chunk 0, whose group bitmap
        // container (one run of 65 536 values) becomes a run container. Queries are unchanged.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        IndexGroupId group = builder.GetOrCreateIndexGroup("test.group");
        using Stack stack = builder.Build();
        PacketIndex index = new(stack);

        for (int id = 0; id < 70_000; id++)
        {
            await Assert.That(index.TryBeginPacket(id)).IsTrue();
            index.RecordGroupPresence(group);
            index.EndPacket();
        }

        RoaringBitmap[] groups = (RoaringBitmap[])_GroupBitmapsField.GetValue(index)!;
        List<Type> types = _ContainerTypes(groups[group.Value]);
        await Assert.That(types[0]).IsEqualTo(typeof(RunContainer));
        await Assert.That(types[1]).IsNotEqualTo(typeof(RunContainer));
        await Assert.That(index.GroupCardinality(group)).IsEqualTo(70_000L);
        await Assert.That(index.TryBeginPacket(65_535)).IsFalse();
        await Assert.That(index.TryBeginPacket(70_000)).IsTrue();
        index.EndPacket();
    }
}
