// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Sequence-ordered stream buffer: holes, overlap, and retransmission.</summary>
internal sealed class SequenceStreamBufferTests
{
    private static SequenceStreamBuffer _Buffer() => new(new StreamReassemblyConfig
    {
        MaxBufferSize = 1024,
    });

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
    public async Task TenThousandHeldSegmentsFillInReverseOrderInBoundedTime()
    {
        // The held list is kept sorted, so the insertion of a segment is a binary search plus a move, and filling the hole
        // at the front drains the whole list in one pass. A scan per insertion would make this quadratic.
        SequenceStreamBuffer buffer = new(new StreamReassemblyConfig { MaxBufferSize = 100_000 });
        byte[] one = [7];
        const int count = 10_000;

        Stopwatch watch = Stopwatch.StartNew();
        for (int index = count; index >= 1; index--)
        {
            await Assert.That(buffer.TryAppend((uint)(1000 + index), one)).IsTrue();
        }

        await Assert.That(buffer.ContiguousLength).IsEqualTo(count);
        await Assert.That(buffer.TryAppend(1000, one)).IsTrue();
        watch.Stop();

        await Assert.That(buffer.ContiguousLength).IsEqualTo(count + 1);
        await Assert.That(watch.Elapsed.TotalSeconds).IsLessThan(5);
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
