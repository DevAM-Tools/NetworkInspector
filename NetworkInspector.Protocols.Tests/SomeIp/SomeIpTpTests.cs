// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// SOME/IP-TP at tree level: the TP word, the segment that delivers a payload (list, statistics, regions behind a gap), the
/// diagnostics of every outcome, the limits as settings, and the guarantee that a packet shows the same tree on every parse. A
/// segment of the tests carries a recognizable fill byte, so a reassembled payload shows which bytes were kept.
/// </summary>
internal sealed class SomeIpTpTests
{
    private static byte[] _Fill(int length, byte value)
    {
        byte[] data = new byte[length];
        data.AsSpan().Fill(value);
        return data;
    }

    private static T[] _Items<T>(params T[] values) => values;

    private static byte[] _Frame(
        uint offset, byte[] data, bool more, ushort session = 1, byte messageType = 0xA0, ushort service = 0x1234, byte interfaceVersion = 1) =>
        SomeIpFrames.TpFrame(offset, data, more, service: service, session: session, messageType: messageType, interfaceVersion: interfaceVersion);

    #region TP word

    [Test]
    [Arguments(0x00000001u, 0UL, true, 0UL)]
    [Arguments(0x00000400u, 1024UL, false, 0UL)]
    [Arguments(0x0000000Eu, 0UL, false, 7UL)]
    [Arguments(0xFFFFFFF0u, 4294967280UL, false, 0UL)]
    public async Task TheTpWordIsSplitIntoOffsetMoreAndReserved(uint word, ulong offset, bool more, ulong reserved)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0x1234, 0x0042, new SomeIpBytes().U32(word).Bytes(new byte[16]).ToArray(), messageType: 0x20);

        Packet packet = s.Parse(SomeIpFrames.Udp(message));

        await Assert.That(s.U64s(packet, "someip.tp.offset")).IsEquivalentTo(_Items(offset));
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip.tp.more", more);
        await Assert.That(s.U64s(packet, "someip.tp.reserved")).IsEquivalentTo(_Items(reserved));
    }

    [Test]
    public async Task AMessageWithoutTheTpFlagShowsNoSegmentContainerEvenIfItsPayloadLooksLikeATpWord()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(SomeIpFrames.Udp(SomeIpBytes.Message(0x1234, 0x0042, [0, 0, 0, 1], messageType: 0x00)));

        await Assert.That(s.Count(packet, "someip.tp")).IsEqualTo(0);
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Items(4));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ATpFlagWithoutAWholeTpHeaderIsMalformedAndShowsRawBytes(int payloadLength)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] payload = _Fill(payloadLength, 1);
        Packet packet = s.Parse(SomeIpFrames.Udp(SomeIpBytes.Message(0x1234, 0x0042, payload, messageType: 0x20)));

        // The bytes stay raw (none for an empty payload), the error names the shortfall, and nothing is decoded from them.
        await Assert.That(s.Count(packet, "someip.tp")).IsEqualTo(0);
        int[] expectedRawLengths = payloadLength == 0
            ? []
            : _Items(payloadLength);
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(expectedRawLengths);
        await Assert.That(s.Strings(packet, "someip.tp.error.malformed"))
            .IsEquivalentTo(_Items($"The TP header needs 4 bytes but only {payloadLength} follow the SOME/IP header."));
    }

    [Test]
    public async Task ATpFlagWithAWholeTpHeaderAndASingleLastSegmentIsNotMalformed()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] segment = new SomeIpBytes().U32(0x00000000).Bytes(_Fill(16, 1)).ToArray();
        Packet packet = s.Parse(SomeIpFrames.Udp(SomeIpBytes.Message(0x1234, 0x0042, segment, messageType: 0x20)));

        await Assert.That(s.Count(packet, "someip.tp")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.tp.error.malformed")).IsEqualTo(0);
    }

    #endregion

    #region Reassembly and the segment list

    private static (byte[] First, byte[] Second, byte[] Third) _ThreeSegments(ushort session = 1) =>
        (_Frame(0, _Fill(16, 1), true, session), _Frame(16, _Fill(16, 2), true, session), _Frame(32, _Fill(8, 3), false, session));

    [Test]
    public async Task SegmentsArrivingTwoOneThreeDeliverTheMessageOnTheLastAndEarlierSegmentsShowOnlyTheirTpWord()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        (byte[] one, byte[] two, byte[] three) = _ThreeSegments();

        Packet p2 = s.Parse(two);
        Packet p1 = s.Parse(one);
        Packet p3 = s.Parse(three);

        await Assert.That(s.ByteLengths(p3, "someip.payload")).IsEquivalentTo(_Items(40));
        foreach (Packet early in _Items(p1, p2))
        {
            await Assert.That(s.Count(early, "someip.payload")).IsEqualTo(0);
            await Assert.That(s.Count(early, "someip.tp.segments")).IsEqualTo(0);
            await Assert.That(s.Count(early, "someip.tp.segment_count")).IsEqualTo(0);
            await Assert.That(s.Count(early, "someip.tp")).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheSegmentListNamesTheContributorsInOffsetOrderAndOnlyTheDeliveringSegmentHasIt()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        (byte[] one, byte[] two, byte[] three) = _ThreeSegments();
        Packet p2 = s.ParseIndexed(two, out PacketIndex i2);
        Packet p1 = s.ParseIndexed(one, out PacketIndex i1);
        Packet p3 = s.ParseIndexed(three, out PacketIndex i3);

        // Offset order: the segment of packet 1 (offset 0), packet 0 (offset 16), packet 2 (offset 32).
        await Assert.That(s.U64s(p3, "someip.tp.segment")).IsEquivalentTo(_Items(1UL, 0UL, 2UL));
        await Assert.That(s.HasGroup(i3, p3, "someip.tp.segments")).IsTrue();
        await Assert.That(s.HasGroup(i1, p1, "someip.tp.segments")).IsFalse();
        await Assert.That(s.HasGroup(i2, p2, "someip.tp.segments")).IsFalse();

        // The tree of the first segment is the same before and after the message completed.
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, p1, s.Reparse(one, 1));
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, p2, s.Reparse(two, 0));
    }

    [Test]
    public async Task TheStatisticsOfTheDeliveringSegmentCountTheSegmentsAndTheLength()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet a = s.Parse(_Frame(0, _Fill(1024, 1), true));
        Packet b = s.Parse(_Frame(1024, _Fill(1024, 2), true));
        Packet c = s.Parse(_Frame(2048, _Fill(512, 3), false));

        await Assert.That(s.U64s(c, "someip.tp.segment_count")).IsEquivalentTo(_Items(3UL));
        await Assert.That(s.U64s(c, "someip.tp.reassembled_length")).IsEquivalentTo(_Items(2560UL));
        await ProtocolTestHelper.AssertBoolField(s.Stack, c, "someip.tp.partial", false);
        await Assert.That(s.Count(a, "someip.tp.segment_count")).IsEqualTo(0);
        await Assert.That(s.Count(b, "someip.tp.segment_count")).IsEqualTo(0);
    }

    [Test]
    public async Task ARequestAndAResponseWithTheSameIdsCompleteSeparatelyAndSoDoTwoInterfaceVersions()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(16, 10), true, messageType: 0x20));
        _ = s.Parse(_Frame(0, _Fill(16, 20), true, messageType: 0xA0));
        _ = s.Parse(_Frame(0, _Fill(16, 30), true, messageType: 0xA0, interfaceVersion: 2));
        Packet request = s.Parse(_Frame(16, _Fill(16, 11), false, messageType: 0x20));
        Packet response = s.Parse(_Frame(16, _Fill(16, 21), false, messageType: 0xA0));
        Packet otherVersion = s.Parse(_Frame(16, _Fill(16, 31), false, messageType: 0xA0, interfaceVersion: 2));

        await Assert.That(s.ByteLengths(request, "someip.payload")).IsEquivalentTo(_Items(32));
        await Assert.That(s.ByteLengths(response, "someip.payload")).IsEquivalentTo(_Items(32));
        await Assert.That(s.ByteLengths(otherVersion, "someip.payload")).IsEquivalentTo(_Items(32));
        await Assert.That(s.U64s(request, "someip.tp.segment")).IsEquivalentTo(_Items(0UL, 3UL));
        await Assert.That(s.U64s(response, "someip.tp.segment")).IsEquivalentTo(_Items(1UL, 4UL));
        await Assert.That(s.U64s(otherVersion, "someip.tp.segment")).IsEquivalentTo(_Items(2UL, 5UL));
    }

    [Test]
    public async Task ASegmentedDiscoveryMessageIsExpandedWhenItIsComplete()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] payload = new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3).Ipv4Option([192, 168, 1, 100], 17, 30512).BuildPayload();
        int cut = 16 * ((payload.Length / 2) / 16);
        Packet first = s.Parse(SomeIpFrames.Udp(SomeIpFrames.TpMessage(0, payload[..cut], true, service: 0xFFFF, method: 0x8100, messageType: 0x22)));
        Packet last = s.Parse(SomeIpFrames.Udp(SomeIpFrames.TpMessage((uint)cut, payload[cut..], false, service: 0xFFFF, method: 0x8100, messageType: 0x22)));

        await Assert.That(s.Count(first, "someip_sd")).IsEqualTo(0);
        await Assert.That(s.Count(last, "someip_sd")).IsEqualTo(1);
        await Assert.That(s.Count(last, "someip_sd.entry")).IsEqualTo(1);
        await Assert.That(s.Count(last, "someip.payload")).IsEqualTo(0);
    }

    #endregion

    #region Gaps, resets and overlaps

    [Test]
    public async Task ALastSegmentBehindAGapDeliversThePrefixAndShowsTheBytesBehindTheGap()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(1024, 1), true));
        Packet last = s.Parse(_Frame(2048, _Fill(1024, 3), false));

        await Assert.That(s.ByteLengths(last, "someip.payload")).IsEquivalentTo(_Items(1024));
        await ProtocolTestHelper.AssertBoolField(s.Stack, last, "someip.tp.partial", true);
        await Assert.That(s.ByteLengths(last, "someip.tp.region")).IsEquivalentTo(_Items(1024));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, last, "someip.tp.region", "[2048-3071] 1024 bytes");
        await Assert.That(s.Strings(last, "someip.tp.error.gap")[0]).Contains("1024-2047");
        await Assert.That(s.U64s(last, "someip.tp.reassembled_length")).IsEquivalentTo(_Items(3072UL));
        await Assert.That(s.U64s(last, "someip.tp.segment")).IsEquivalentTo(_Items(0UL, 1UL));
    }

    [Test]
    public async Task ALateSegmentThatClosesTheGapDeliversTheFullMessageAndAnotherCopyOpensNothing()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(1024, 1), true));
        Packet partial = s.Parse(_Frame(2048, _Fill(1024, 3), false));
        Packet full = s.Parse(_Frame(1024, _Fill(1024, 2), true));
        Packet duplicate = s.Parse(_Frame(1024, _Fill(1024, 2), true));

        await ProtocolTestHelper.AssertBoolField(s.Stack, partial, "someip.tp.partial", true);
        await Assert.That(s.ByteLengths(full, "someip.payload")).IsEquivalentTo(_Items(3072));
        await ProtocolTestHelper.AssertBoolField(s.Stack, full, "someip.tp.partial", false);
        await Assert.That(s.U64s(full, "someip.tp.segment")).IsEquivalentTo(_Items(0UL, 2UL, 1UL));
        await Assert.That(s.Count(full, "someip.tp.error.gap")).IsEqualTo(0);
        await Assert.That(s.Count(duplicate, "someip.payload")).IsEqualTo(0);
    }

    [Test]
    public async Task AnInOrderTransferNeverReportsAPartialPrefix()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(1024, 1), true));
        _ = s.Parse(_Frame(1024, _Fill(1024, 2), true));
        Packet last = s.Parse(_Frame(2048, _Fill(1024, 3), false));

        await ProtocolTestHelper.AssertBoolField(s.Stack, last, "someip.tp.partial", false);
        await Assert.That(s.Count(last, "someip.tp.error.gap")).IsEqualTo(0);
    }

    [Test]
    public async Task ASecondStartRestartsTheTransferAndTheMessageHasOnlyTheNewBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(1024, 1), true));
        _ = s.Parse(_Frame(1024, _Fill(1024, 2), true));
        Packet third = s.Parse(_Frame(0, _Fill(1024, 3), true));
        Packet fourth = s.Parse(_Frame(1024, _Fill(1024, 4), false));

        await Assert.That(s.Count(third, "someip.tp.error.reset")).IsEqualTo(1);
        await Assert.That(s.ByteLengths(fourth, "someip.payload")).IsEquivalentTo(_Items(2048));
        await Assert.That(s.U64s(fourth, "someip.tp.segment")).IsEquivalentTo(_Items(2UL, 3UL));
    }

    [Test]
    public async Task DifferingOverlapIsFlaggedOnceOnTheSecondSegmentAndIdenticalOverlapIsNot()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Frame(0, _Fill(512, 1), true));
        _ = s.Parse(_Frame(512, _Fill(512, 2), true));
        Packet different = s.Parse(_Frame(512, _Fill(512, 9), true));
        Packet identical = s.Parse(_Frame(512, _Fill(512, 2), true));
        Packet last = s.Parse(_Frame(1024, _Fill(16, 3), false));

        await Assert.That(s.Count(different, "someip.tp.error.overlap")).IsEqualTo(1);
        await Assert.That(s.Count(identical, "someip.tp.error.overlap")).IsEqualTo(0);
        await Assert.That(s.Count(last, "someip.tp.error.overlap")).IsEqualTo(0);
        await Assert.That(s.ByteLengths(last, "someip.payload")).IsEquivalentTo(_Items(1040));
    }

    #endregion

    #region Limits and malformed segments

    [Test]
    public async Task TheSessionLimitEvictsTheLeastRecentlyUsedSessionAndNamesTheSetting()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tp_max_sessions", SettingValue.U64(2)));
        _ = s.Parse(_Frame(0, _Fill(16, 1), true, session: 1));
        _ = s.Parse(_Frame(0, _Fill(16, 2), true, session: 2));
        _ = s.Parse(_Frame(16, _Fill(16, 1), true, session: 1));
        Packet opensThird = s.Parse(_Frame(0, _Fill(16, 3), true, session: 3));
        Packet a = s.Parse(_Frame(32, _Fill(16, 1), false, session: 1));
        Packet c = s.Parse(_Frame(16, _Fill(16, 3), false, session: 3));
        Packet b = s.Parse(_Frame(16, _Fill(16, 2), false, session: 2));

        List<string> evicted = s.Strings(opensThird, "someip.tp.error.evicted");
        await Assert.That(evicted.Count).IsEqualTo(1);
        await Assert.That(evicted[0]).Contains("someip.tp_max_sessions");
        await Assert.That(evicted[0]).Contains("2");
        await Assert.That(s.ByteLengths(a, "someip.payload")).IsEquivalentTo(_Items(48));
        await Assert.That(s.ByteLengths(c, "someip.payload")).IsEquivalentTo(_Items(32));
        await Assert.That(s.Count(b, "someip.payload")).IsEqualTo(0);
    }

    [Test]
    public async Task ASegmentBeyondTheMessageSizeLimitIsRefusedWithTheSettingNamedAndAMessageOfExactlyThatSizeCompletes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tp_max_message_size", SettingValue.U64(1024)));
        Packet beyond = s.Parse(_Frame(1024, _Fill(16, 1), false, session: 1));
        _ = s.Parse(_Frame(0, _Fill(1008, 2), true, session: 2));
        Packet exact = s.Parse(_Frame(1008, _Fill(16, 3), false, session: 2));

        List<string> refused = s.Strings(beyond, "someip.tp.error.size_limit");
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0]).Contains("someip.tp_max_message_size");
        await Assert.That(refused[0]).Contains("1024");
        await Assert.That(s.ByteLengths(exact, "someip.payload")).IsEquivalentTo(_Items(1024));
    }

    [Test]
    public async Task ALastSegmentAtAHugeOffsetIsRefusedAndNothingIsBuffered()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Frame(0xFFFFFFF0u, _Fill(16, 1), false));

        await Assert.That(s.Count(packet, "someip.tp.error.size_limit")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.payload")).IsEqualTo(0);
        await Assert.That(s.U64s(packet, "someip.tp.offset")).IsEquivalentTo(_Items(4294967280UL));
    }

    [Test]
    [Arguments(0UL)]
    [Arguments(32UL * 1024 * 1024 * 1024)]
    public async Task ABufferedBytesLimitOutsideItsRangeIsRefusedAndTheRangeIsNamed(ulong value)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tp_max_buffered_bytes", SettingValue.U64(value)));

        await Assert.That(s.Warnings.Count).IsEqualTo(1);
        await Assert.That(s.Warnings[0].Message).Contains("1048576 to 17179869184");
    }

    [Test]
    public async Task ANonLastSegmentOfTheWrongSizeIsDroppedWithTheSixteenByteRuleNamed()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet twenty = s.Parse(_Frame(0, _Fill(20, 1), true, session: 1));
        Packet empty = s.Parse(_Frame(0, [], true, session: 2));

        List<string> texts = s.Strings(twenty, "someip.tp.error.malformed");
        await Assert.That(texts.Count).IsEqualTo(1);
        await Assert.That(texts[0]).Contains("16");
        await Assert.That(s.Count(empty, "someip.tp.error.malformed")).IsEqualTo(1);
    }

    #endregion

    #region Identity of a re-parse

    [Test]
    public async Task EverySegmentOfTheGapResetAndEvictionScenariosReparsesIdentically()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tp_max_sessions", SettingValue.U64(2)));
        byte[][] frames =
        [
            _Frame(0, _Fill(1024, 1), true, session: 1),
            _Frame(2048, _Fill(1024, 3), false, session: 1),
            _Frame(1024, _Fill(1024, 2), true, session: 1),
            _Frame(0, _Fill(16, 1), true, session: 5),
            _Frame(0, _Fill(16, 2), true, session: 5),
            _Frame(0, _Fill(16, 4), true, session: 6),
            _Frame(0, _Fill(16, 5), true, session: 7),
            _Frame(16, _Fill(16, 5), false, session: 7),
        ];
        Packet[] first = [.. frames.Select(s.Parse)];

        for (int i = 0; i < frames.Length; i++)
        {
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], s.Reparse(frames[i], i));
        }
    }

    [Test]
    public async Task TheSegmentListOfTheDeliveringSegmentIsTheSameOnEveryThread()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        (byte[] one, byte[] two, byte[] three) = _ThreeSegments();
        _ = s.Parse(two);
        _ = s.Parse(one);
        Packet delivering = s.Parse(three);
        PacketFieldComparer.PacketFieldSnapshot snapshot = PacketFieldComparer.CaptureFields(delivering);

        Task[] workers = new Task[4];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = Task.Run(async () =>
            {
                for (int round = 0; round < 25; round++)
                {
                    Packet reparse = s.Reparse(three, 2);
                    await PacketFieldComparer.AssertMatchesSnapshot(s.Stack, snapshot, reparse).ConfigureAwait(false);
                    await Assert.That(s.U64s(reparse, "someip.tp.segment")).IsEquivalentTo(_Items(1UL, 0UL, 2UL));
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    [Test]
    public async Task WholeSequencesOfSegmentsReparseIdenticallyAndTheLastSegmentAloneToo()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[][] frames = [_Frame(0, _Fill(1024, 1), true), _Frame(1024, _Fill(1024, 2), true), _Frame(2048, _Fill(952, 3), false)];
        Packet[] first = [.. frames.Select(s.Parse)];

        await Assert.That(s.ByteLengths(first[2], "someip.payload")).IsEquivalentTo(_Items(3000));
        for (int i = 0; i < frames.Length; i++)
        {
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], s.Reparse(frames[i], i));
        }

        await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[2], s.Reparse(frames[2], 2));
    }

    [Test]
    public async Task SegmentsInsideReassembledIpv4DatagramsReplayTheirOutcome()
    {
        // Each segment carries about 3,000 bytes, so each UDP datagram is cut into three IPv4 fragments and the SOME/IP unit lives in a
        // buffer that the packet binds (buffer index 1): the layer key of the segment effect carries that index.
        using SomeIpTestStack s = SomeIpTestStack.Create();
        List<byte[]> frames = [];
        frames.AddRange(SomeIpFrames.FragmentedUdp(SomeIpFrames.TpMessage(0, _Fill(3008, 1), true), 40000, 30490));
        frames.AddRange(SomeIpFrames.FragmentedUdp(SomeIpFrames.TpMessage(3008, _Fill(3008, 2), true), 40000, 30490));
        frames.AddRange(SomeIpFrames.FragmentedUdp(SomeIpFrames.TpMessage(6016, _Fill(3008, 3), false), 40000, 30490));
        Packet[] first = [.. frames.Select(s.Parse)];

        await Assert.That(s.ByteLengths(first[^1], "someip.payload")).IsEquivalentTo(_Items(9024));
        for (int i = 0; i < frames.Count; i++)
        {
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], s.Reparse(frames[i], i));
        }
    }

    #endregion

    #region Index

    [Test]
    public async Task TheEagerIndexEqualsTheGroupsOfTheTreeForCompletingPartialAndGapFillingSegments()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[][] frames =
        [
            _Frame(0, _Fill(1024, 1), true),
            _Frame(2048, _Fill(1024, 3), false),
            _Frame(1024, _Fill(1024, 2), true),
            _Frame(0, _Fill(16, 9), false, session: 2),
            _Frame(0, _Fill(20, 9), true, session: 3),
        ];

        foreach (byte[] frame in frames)
        {
            Packet packet = s.ParseIndexed(frame, out PacketIndex index);
            await s.AssertIndexExact(index, packet);
        }
    }

    #endregion
}
