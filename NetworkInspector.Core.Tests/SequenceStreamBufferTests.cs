// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Sequence-ordered stream buffer: holes, overlap, and retransmission.</summary>
internal sealed class SequenceStreamBufferTests
{
    private static SequenceStreamBuffer _Buffer() => new(new StreamReassemblyConfig
    {
        MaxBufferSize = 1024,
    });

    /// <summary>A one-byte segment whose value is the low byte of <paramref name="index"/>, so a stream position names its content.</summary>
    private static byte[] _OneByte(int index) => [unchecked((byte)index)];

    [Test]
    public async Task InOrderHalves_FormOnePrefix()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
    }

    [Test]
    public async Task RetransmissionOfTheFirstHalf_DoesNotGrowThePrefix()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
    }

    [Test]
    public async Task HoleBlocksThePrefixUntilFilled()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(10);

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
        await Assert.That(buffer.ContiguousSpan[0]).IsEqualTo((byte)1);
        await Assert.That(buffer.ContiguousSpan[10]).IsEqualTo((byte)11);
    }

    [Test]
    public async Task OverlapKeepsOnlyTheNewTail()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[] { 1, 2, 3, 4, 5 })).IsTrue();
        await Assert.That(buffer.TryAppend(103, new byte[] { 4, 5, 6, 7 })).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(7);
        await Assert.That(buffer.ContiguousSpan[5]).IsEqualTo((byte)6);
        await Assert.That(buffer.ContiguousSpan[6]).IsEqualTo((byte)7);
    }

    [Test]
    public async Task SizeCheckNearIntMaxRefusesInsteadOfWrapping()
    {
        // The byte counts are set by reflection: 2 GiB of data are not needed to reach the edge of the int range.
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = int.MaxValue });
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(SequenceStreamBuffer).GetField("_ContiguousLength", Private)!.SetValue(buffer, int.MaxValue - 10);
        System.Reflection.FieldInfo held = typeof(SequenceStreamBuffer).GetField("_HeldBytes", Private)!;
        System.Reflection.MethodInfo wouldExceed = typeof(SequenceStreamBuffer).GetMethod("_WouldExceedMax", Private)!;
        bool Exceeds(int length) => (bool)wouldExceed.Invoke(buffer, [length])!;

        held.SetValue(buffer, 20);
        await Assert.That(Exceeds(1)).IsTrue();
        await Assert.That(Exceeds(int.MaxValue)).IsTrue();
        held.SetValue(buffer, 5);
        await Assert.That(Exceeds(5)).IsFalse();
        await Assert.That(Exceeds(6)).IsTrue();
    }

    [Test]
    public async Task DrainedHeldSegmentIsCopiedBeforeItsArrayReturnsToThePool()
    {
        // A held segment drained into the prefix keeps its bytes even when the pool hands its array out again.
        SequenceStreamBuffer buffer = _Buffer();
        byte[] first = [1, 2, 3, 4];
        byte[] second = [5, 6, 7, 8];

        await Assert.That(buffer.TryAppend(104, second)).IsTrue();
        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        byte[] reused = ArrayPool<byte>.Shared.Rent(4);
        reused.AsSpan().Fill(0xEE);
        ArrayPool<byte>.Shared.Return(reused);

        await Assert.That(buffer.ContiguousLength).IsEqualTo(8);
        await Assert.That(buffer.ContiguousSpan.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    #region Held segments

    [Test]
    public async Task DescendingSegmentsFormOnePrefix()
    {
        // Every descending segment starts before the origin, so each one rebuilds the prefix; the segment that fills the
        // hole at the front then joins all of them. The test proves content and order, not speed.
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 100_000, MaxRebases = int.MaxValue });
        byte[] one = [7];
        const int count = 10_000;

        int rejected = 0;
        for (int index = count; index >= 1; index--)
        {
            if (!buffer.TryAppend((uint)(1000 + index), one))
            {
                rejected++;
            }
        }

        int lengthBeforeFill = buffer.ContiguousLength;
        bool filled = buffer.TryAppend(1000, one);

        await Assert.That(rejected).IsEqualTo(0);
        await Assert.That(lengthBeforeFill).IsEqualTo(count);
        await Assert.That(filled).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(count + 1);
    }

    [Test]
    public async Task DescendingSegmentsStopRebasingAtTheLimit()
    {
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 100_000, MaxRebases = 8 });
        byte[] one = [7];
        StreamAppendRefusal last = StreamAppendRefusal.None;
        int refused = 0;

        // Distinct packet ids keep one contributor piece per segment: the expensive shape of a rebase.
        for (int index = 100; index >= 1; index--)
        {
            if (!buffer.TryAppend((uint)(1000 + index), one, packetId: index, out last))
            {
                refused++;
            }
        }

        await Assert.That(buffer.RebaseCount).IsEqualTo(8);
        await Assert.That(refused).IsEqualTo(91);
        await Assert.That(last).IsEqualTo(StreamAppendRefusal.RebaseLimit);
    }

    [Test]
    public async Task AnOversizeSegmentIsRefusedByTheBufferLimitAndNamesIt()
    {
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 4 });

        bool stored = buffer.TryAppend(1000, new byte[5], 0, out StreamAppendRefusal refusal);

        await Assert.That(stored).IsFalse();
        await Assert.That(refusal).IsEqualTo(StreamAppendRefusal.BufferSize);
    }

    [Test]
    public async Task ASwitchedOffRebaseLimitAllowsEveryRebase()
    {
        // A switched-off limit reaches the buffer as the largest int (WithLimits does this for the live holder).
        StreamReassemblyConfig config = new() { MaxBufferSize = 100_000, MaxRebases = int.MaxValue };
        SequenceStreamBuffer buffer = new(config);
        byte[] one = [7];
        int refused = 0;

        for (int index = 100; index >= 1; index--)
        {
            if (!buffer.TryAppend((uint)(1000 + index), one, packetId: index))
            {
                refused++;
            }
        }

        await Assert.That(refused).IsEqualTo(0);
        await Assert.That(buffer.RebaseCount).IsEqualTo(99);
    }
    [Test]
    public async Task AscendingSegmentsBehindAHoleDrainInStreamOrder()
    {
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 100_000 });
        byte[] first = [0];
        await Assert.That(buffer.TryAppend(1000, first, 0)).IsTrue();

        // 1001 is missing; 1002..11001 arrive in order and are held behind the hole.
        for (int index = 2; index <= 10_001; index++)
        {
            _ = buffer.TryAppend((uint)(1000 + index), _OneByte(index), index);
        }

        await Assert.That(buffer.ContiguousLength).IsEqualTo(1);
        await Assert.That(buffer.TryAppend(1001, _OneByte(1), 1)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(10_002);
        await Assert.That(buffer.ContiguousSpan[10_001]).IsEqualTo(_OneByte(10_001)[0]);
    }

    [Test]
    public async Task ShuffledSegmentsBehindAHoleDrainInStreamOrder()
    {
        // A fixed seed keeps the arrival order the same on every run; the heap must return the lowest segment each time.
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 100_000 });
        await Assert.That(buffer.TryAppend(1000, _OneByte(0), 0)).IsTrue();
        int[] order = [.. Enumerable.Range(2, 4_999)];
        new Random(12345).Shuffle(order);

        foreach (int index in order)
        {
            _ = buffer.TryAppend((uint)(1000 + index), _OneByte(index), index);
        }

        bool appended = buffer.TryAppend(1001, _OneByte(1), 1);
        bool inOrder = appended && buffer.ContiguousLength == 5_001;
        for (int index = 0; inOrder && index < buffer.ContiguousLength; index++)
        {
            inOrder = buffer.ContiguousSpan[index] == _OneByte(index)[0];
        }

        await Assert.That(inOrder).IsTrue();
    }

    [Test]
    public async Task HeldSegmentsAreOrderedAcrossTheSequenceWrap()
    {
        // Serial-number order: 0x00000002 comes after 0xFFFFFFFE, so the held list must not sort by plain value.
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(0xFFFFFFFC, new byte[] { 1, 2 })).IsTrue();
        await Assert.That(buffer.TryAppend(2, new byte[] { 7, 8 })).IsTrue();
        await Assert.That(buffer.TryAppend(0, new byte[] { 5, 6 })).IsTrue();
        await Assert.That(buffer.TryAppend(0xFFFFFFFE, new byte[] { 3, 4 })).IsTrue();

        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
        await Assert.That(buffer.ContiguousSpan.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task BufferedLengthCountsThePrefixAndTheHeldBytes()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[10])).IsTrue();
        await Assert.That(buffer.TryAppend(130, new byte[5])).IsTrue();

        await Assert.That(buffer.ContiguousLength).IsEqualTo(10);
        await Assert.That(buffer.BufferedLength).IsEqualTo(15);

        buffer.Consume(4);

        await Assert.That(buffer.BufferedLength).IsEqualTo(11);
    }

    [Test]
    public async Task HeldDataBeyondTheCapIsRefusedAndLeavesTheBufferUnchanged()
    {
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 16 });
        await Assert.That(buffer.TryAppend(100, new byte[10])).IsTrue();

        await Assert.That(buffer.TryAppend(200, new byte[10])).IsFalse();

        await Assert.That(buffer.BufferedLength).IsEqualTo(10);
    }

    #endregion

    #region Contributing packets

    [Test]
    public async Task ContributingPacketIdsNameThePacketsBehindAPrefix()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 4)).IsTrue();
        await Assert.That(buffer.TryAppend(110, new byte[10], packetId: 5)).IsTrue();
        await Assert.That(buffer.TryAppend(120, new byte[10], packetId: 6)).IsTrue();

        await Assert.That(buffer.GetContributingPacketIds(30)).IsEquivalentTo([4, 5, 6]);
        await Assert.That(buffer.GetContributingPacketIds(10)).IsEquivalentTo([4]);
        // A packet that straddles the end of the range is named too.
        await Assert.That(buffer.GetContributingPacketIds(11)).IsEquivalentTo([4, 5]);
    }

    [Test]
    public async Task ContributingPacketIdsFollowTheConsumedOffset()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 4)).IsTrue();
        await Assert.That(buffer.TryAppend(110, new byte[10], packetId: 5)).IsTrue();

        buffer.Consume(10);

        await Assert.That(buffer.GetContributingPacketIds(10)).IsEquivalentTo([5]);
    }

    [Test]
    public async Task ContributingPacketIdsOfAFilledHoleStayInStreamOrder()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(110, new byte[10], packetId: 9)).IsTrue();
        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 8)).IsTrue();

        await Assert.That(buffer.GetContributingPacketIds(20)).IsEquivalentTo([8, 9]);
    }

    [Test]
    public async Task UntrackedAppendsAreSkippedInTheContributingIds()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[10])).IsTrue();
        await Assert.That(buffer.TryAppend(110, new byte[10], packetId: 3)).IsTrue();

        await Assert.That(buffer.GetContributingPacketIds(20)).IsEquivalentTo([3]);
        await Assert.That(buffer.GetContributingPacketIds(0)).IsEmpty();
    }

    [Test]
    public async Task RetransmissionDoesNotChangeTheContributingIds()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 1)).IsTrue();
        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 2)).IsTrue();

        await Assert.That(buffer.GetContributingPacketIds(10)).IsEquivalentTo([1]);
    }

    #endregion

    #region Rebase

    [Test]
    public async Task EarlierSegmentBeforeAnyConsumeRebasesAndKeepsThePacketIds()
    {
        // The capture sees a later segment first; the earlier one then moves the left edge back. The ids of the rebuilt
        // prefix must still name the packets that delivered each part.
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(110, new byte[10], packetId: 2)).IsTrue();
        await Assert.That(buffer.RebaseCount).IsEqualTo(0);

        await Assert.That(buffer.TryAppend(100, new byte[10], packetId: 1)).IsTrue();

        await Assert.That(buffer.RebaseCount).IsEqualTo(1);
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
        await Assert.That(buffer.GetContributingPacketIds(20)).IsEquivalentTo([1, 2]);
    }

    [Test]
    public async Task InOrderTrafficNeverRebases()
    {
        SequenceStreamBuffer buffer = _Buffer();
        for (uint index = 0; index < 5; index++)
        {
            await Assert.That(buffer.TryAppend(100 + (index * 10), new byte[10], (int)index)).IsTrue();
        }

        await Assert.That(buffer.RebaseCount).IsEqualTo(0);
    }

    #endregion
}
