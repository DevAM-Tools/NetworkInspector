// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The adapter between SOME/IP-TP and the Core reassembler, driven directly with any arrival order: which options it chooses, how
/// the key separates transfers, and that the limits of the settings reach the reassembler. The segments carry recognizable bytes
/// (the byte value is the index of the 16-byte block), so a reassembled payload shows which bytes were kept.
/// </summary>
internal sealed class SomeIpTpSessionsTests
{
    private static readonly SomeIpTpKey _Key = new(0x1234, 0x0042, 1, 1, 0x80, 1);

    private static byte[] _Blocks(int firstBlock, int blocks)
    {
        byte[] data = new byte[blocks * 16];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(firstBlock + (i / 16));
        }

        return data;
    }

    private static SomeIpTpSessions _Sessions(Func<SomeIpSettings, SomeIpSettings>? customize = null)
    {
        SomeIpSettings settings = SomeIpSettings.Default;
        if (customize is not null)
        {
            settings = customize(settings);
        }

        return new SomeIpTpSessions(in settings);
    }

    [Test]
    public async Task SegmentsInTheOrderTwoOneThreeCompleteOnTheLastAndLeaveNothingBehind()
    {
        SomeIpTpSessions sessions = _Sessions();

        FragmentOutcome second = sessions.Add(in _Key, 16, _Blocks(1, 1), true, packetId: 0);
        FragmentOutcome first = sessions.Add(in _Key, 0, _Blocks(0, 1), true, packetId: 1);
        FragmentOutcome last = sessions.Add(in _Key, 32, new byte[8], false, packetId: 2);

        await Assert.That(second.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(first.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(last.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(last.Payload!.Length).IsEqualTo(40);
        await Assert.That(last.Contributors).IsEquivalentTo(_Contributors(1, 0, 2));
        await Assert.That(sessions.PendingCount).IsEqualTo(0);
        await Assert.That(sessions.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ARequestAndAResponseWithTheSameIdsAreSeparateTransfers()
    {
        SomeIpTpSessions sessions = _Sessions();
        SomeIpTpKey request = _Key with { BaseType = 0x00 };
        SomeIpTpKey otherVersion = _Key with { InterfaceVersion = 2 };

        _ = sessions.Add(in request, 0, _Blocks(10, 1), true, 0);
        _ = sessions.Add(in _Key, 0, _Blocks(20, 1), true, 1);
        _ = sessions.Add(in otherVersion, 0, _Blocks(30, 1), true, 2);
        FragmentOutcome doneRequest = sessions.Add(in request, 16, _Blocks(11, 1), false, 3);
        FragmentOutcome doneResponse = sessions.Add(in _Key, 16, _Blocks(21, 1), false, 4);
        FragmentOutcome doneOtherVersion = sessions.Add(in otherVersion, 16, _Blocks(31, 1), false, 5);

        await Assert.That(doneRequest.Payload![0]).IsEqualTo((byte)10);
        await Assert.That(doneResponse.Payload![0]).IsEqualTo((byte)20);
        await Assert.That(doneOtherVersion.Payload![0]).IsEqualTo((byte)30);
        await Assert.That(sessions.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ASegmentAtOffsetZeroRestartsATransferAndTheMessageHasOnlyTheNewBytes()
    {
        SomeIpTpSessions sessions = _Sessions();

        _ = sessions.Add(in _Key, 0, _Blocks(1, 1), true, 0);
        _ = sessions.Add(in _Key, 16, _Blocks(2, 1), true, 1);
        FragmentOutcome restarted = sessions.Add(in _Key, 0, _Blocks(3, 1), true, 2);
        FragmentOutcome done = sessions.Add(in _Key, 16, _Blocks(4, 1), false, 3);

        await Assert.That((restarted.Flags & FragmentOutcomeFlags.Restarted) != 0).IsTrue();
        await Assert.That(done.Payload![0]).IsEqualTo((byte)3);
        await Assert.That(done.Payload![16]).IsEqualTo((byte)4);
        await Assert.That(done.Contributors).IsEquivalentTo(_Contributors(2, 3));
    }

    [Test]
    public async Task OverlappingBytesKeepTheFirstArrivalAndOnlyDifferingContentIsFlagged()
    {
        SomeIpTpSessions sessions = _Sessions();

        // The first segment covers [0-1023]; the bytes of block n carry the value n.
        _ = sessions.Add(in _Key, 0, _Blocks(0, 64), true, 0);
        FragmentOutcome conflict = sessions.Add(in _Key, 512, _Blocks(100, 32), true, 1);
        FragmentOutcome identical = sessions.Add(in _Key, 512, _Blocks(32, 32), true, 2);
        FragmentOutcome end = sessions.Add(in _Key, 1024, _Blocks(64, 1), false, 3);

        await Assert.That((conflict.Flags & FragmentOutcomeFlags.OverlapConflict) != 0).IsTrue();
        await Assert.That((identical.Flags & FragmentOutcomeFlags.OverlapConflict) != 0).IsFalse();
        await Assert.That(end.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(end.Payload![512]).IsEqualTo((byte)32);
    }

    [Test]
    public async Task ASegmentThatOverlapsAKeptRangeStoresOnlyTheBytesThatAreNew()
    {
        SomeIpTpSessions sessions = _Sessions();

        _ = sessions.Add(in _Key, 512, _Blocks(32, 32), true, 0);
        _ = sessions.Add(in _Key, 256, _Blocks(100, 32), true, 1);
        _ = sessions.Add(in _Key, 0, _Blocks(0, 16), true, 2);
        FragmentOutcome end = sessions.Add(in _Key, 1024, _Blocks(64, 1), false, 3);

        // [256-511] is new (value 100 + block), [512-767] keeps the first arrival (value 32 + block).
        await Assert.That(end.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(end.Payload![256]).IsEqualTo((byte)100);
        await Assert.That(end.Payload![512]).IsEqualTo((byte)32);
    }


    [Test]
    public async Task WithTwoSessionsAllowedTheLeastRecentlyUsedOneIsEvictedWhenAThirdOpens()
    {
        SomeIpTpSessions sessions = _Sessions(static s => s with { TpMaxSessions = 2 });
        SomeIpTpKey a = _Key with { SessionId = 1 };
        SomeIpTpKey b = _Key with { SessionId = 2 };
        SomeIpTpKey c = _Key with { SessionId = 3 };

        _ = sessions.Add(in a, 0, _Blocks(1, 1), true, 0);
        _ = sessions.Add(in b, 0, _Blocks(2, 1), true, 1);
        _ = sessions.Add(in a, 16, _Blocks(1, 1), true, 2);
        FragmentOutcome opens = sessions.Add(in c, 0, _Blocks(3, 1), true, 3);

        await Assert.That((opens.Flags & FragmentOutcomeFlags.EvictedForCount) != 0).IsTrue();
        await Assert.That(sessions.Add(in a, 32, _Blocks(1, 1), false, 4).Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(sessions.Add(in c, 16, _Blocks(3, 1), false, 5).Kind).IsEqualTo(FragmentOutcomeKind.Complete);

        // B was evicted: its last segment starts a fresh session without offset 0, which stays pending.
        FragmentOutcome late = sessions.Add(in b, 16, _Blocks(2, 1), false, 6);
        await Assert.That(late.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
    }

    [Test]
    public async Task TheDefaultNumberOfSessionsKeepsAllThree()
    {
        SomeIpTpSessions sessions = _Sessions();
        for (ushort session = 1; session <= 3; session++)
        {
            _ = sessions.Add(_Key with { SessionId = session }, 0, _Blocks(session, 1), true, session);
        }

        await Assert.That(sessions.PendingCount).IsEqualTo(3);
        for (ushort session = 1; session <= 3; session++)
        {
            await Assert.That(sessions.Add(_Key with { SessionId = session }, 16, _Blocks(session, 1), false, 10 + session).Kind)
                .IsEqualTo(FragmentOutcomeKind.Complete);
        }
    }

    [Test]
    public async Task ASegmentBeyondTheMessageSizeLimitIsRefusedAndAMessageOfExactlyThatSizeCompletes()
    {
        SomeIpTpSessions sessions = _Sessions(static s => s with { TpMaxMessageSize = 1024 });

        FragmentOutcome beyond = sessions.Add(in _Key, 1024, _Blocks(0, 1), false, 0);
        _ = sessions.Add(in _Key, 0, _Blocks(0, 63), true, 1);
        FragmentOutcome exact = sessions.Add(in _Key, 1008, _Blocks(63, 1), false, 2);

        await Assert.That(beyond.Kind).IsEqualTo(FragmentOutcomeKind.Discarded);
        await Assert.That(beyond.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(exact.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(exact.Payload!.Length).IsEqualTo(1024);
    }

    [Test]
    public async Task ASingleSegmentAtAHugeOffsetIsRefusedWithoutRequestingMemory()
    {
        SomeIpTpSessions sessions = _Sessions();
        byte[] data = new byte[16];

        // Warm-up so the measurement sees only the call itself.
        _ = sessions.Add(in _Key, 0xFFFFFFF0u, data, false, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        FragmentOutcome outcome = sessions.Add(in _Key, 0xFFFFFFF0u, data, false, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(outcome.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(sessions.PendingCount).IsEqualTo(0);
        await Assert.That(sessions.BufferedBytes).IsEqualTo(0);
        await Assert.That(allocated).IsLessThan(1024).Because($"the refused segment allocated {allocated} bytes");
    }

    [Test]
    public async Task TheBufferedBytesLimitEvictsTheLeastRecentlyUsedSessionAndNeverExceedsIt()
    {
        SomeIpTpSessions sessions = _Sessions(static s => s with { TpMaxBufferedBytes = 4096 });

        _ = sessions.Add(_Key with { SessionId = 1 }, 0, _Blocks(1, 128), true, 0);
        _ = sessions.Add(_Key with { SessionId = 2 }, 0, _Blocks(2, 128), true, 1);
        FragmentOutcome third = sessions.Add(_Key with { SessionId = 3 }, 0, _Blocks(3, 128), true, 2);

        await Assert.That((third.Flags & FragmentOutcomeFlags.EvictedForBudget) != 0).IsTrue();
        await Assert.That(sessions.BufferedBytes).IsLessThanOrEqualTo(4096);

        // One session that alone needs more than the limit is dropped.
        FragmentOutcome alone = sessions.Add(_Key with { SessionId = 4 }, 0, _Blocks(4, 300), true, 3);
        await Assert.That(alone.Kind).IsEqualTo(FragmentOutcomeKind.Discarded);
        await Assert.That(alone.DiscardReason).IsEqualTo(FragmentDiscardReason.BufferLimit);
        await Assert.That(sessions.BufferedBytes).IsLessThanOrEqualTo(4096);
    }

    [Test]
    public async Task ANonLastSegmentThatIsNotAMultipleOfSixteenOrIsEmptyIsRefusedAndCreatesNoSession()
    {
        SomeIpTpSessions sessions = _Sessions();

        FragmentOutcome twenty = sessions.Add(in _Key, 0, new byte[20], true, 0);
        FragmentOutcome empty = sessions.Add(in _Key, 0, [], true, 1);
        for (int i = 0; i < 1000; i++)
        {
            _ = sessions.Add(_Key with { SessionId = (ushort)(i + 10) }, 0, [], true, 100 + i);
        }

        await Assert.That(twenty.DiscardReason).IsEqualTo(FragmentDiscardReason.Alignment);
        await Assert.That(empty.DiscardReason).IsEqualTo(FragmentDiscardReason.Alignment);
        await Assert.That(sessions.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ALateSegmentThatClosesTheGapDeliversTheFullMessageAfterAPartialPrefix()
    {
        SomeIpTpSessions sessions = _Sessions();

        _ = sessions.Add(in _Key, 0, _Blocks(0, 64), true, 0);
        FragmentOutcome partial = sessions.Add(in _Key, 2048, _Blocks(128, 64), false, 1);
        FragmentOutcome full = sessions.Add(in _Key, 1024, _Blocks(64, 64), true, 2);
        FragmentOutcome duplicate = sessions.Add(in _Key, 1024, _Blocks(64, 64), true, 3);

        await Assert.That(partial.Kind).IsEqualTo(FragmentOutcomeKind.PartialPrefix);
        await Assert.That(partial.PrefixLength).IsEqualTo(1024);
        await Assert.That(partial.DatagramLength).IsEqualTo(3072);
        await Assert.That(partial.Gaps![0]).IsEqualTo(new FragmentRange(1024, 1024));
        await Assert.That(partial.CoveredAfterGap![0]).IsEqualTo(new FragmentRange(2048, 1024));
        await Assert.That(full.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(full.Payload!.Length).IsEqualTo(3072);
        await Assert.That(full.Contributors).IsEquivalentTo(_Contributors(0, 2, 1));
        await Assert.That(duplicate.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
    }

    [Test]
    public async Task AnInOrderTransferNeverReportsAPartialPrefix()
    {
        SomeIpTpSessions sessions = _Sessions();

        FragmentOutcome one = sessions.Add(in _Key, 0, _Blocks(0, 64), true, 0);
        FragmentOutcome two = sessions.Add(in _Key, 1024, _Blocks(64, 64), true, 1);
        FragmentOutcome three = sessions.Add(in _Key, 2048, _Blocks(128, 64), false, 2);

        await Assert.That(one.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(two.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(three.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
    }

    [Test]
    public async Task ClearForgetsEverySession()
    {
        SomeIpTpSessions sessions = _Sessions();
        _ = sessions.Add(in _Key, 0, _Blocks(0, 1), true, 0);

        sessions.Clear();

        await Assert.That(sessions.PendingCount).IsEqualTo(0);
        await Assert.That(sessions.BufferedBytes).IsEqualTo(0);
    }

    private static int[] _Contributors(params int[] ids) => ids;
}
