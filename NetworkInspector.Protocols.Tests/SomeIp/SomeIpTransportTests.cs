// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// How SOME/IP traffic reaches the parser: the configured and the default ports of UDP and TCP, the plaintext hand-off of an
/// encrypted transport, content-based detection, and the framing of messages out of a TCP byte stream including the loss of the
/// stream position. The frames are hand-built; TCP conversations come from <see cref="TcpTestConversation"/>.
/// </summary>
internal sealed class SomeIpTransportTests
{
    private const string _EmptyConfig = """{"schema_version":"1.0","services":[]}""";

    private static byte[] _Udp(byte[] payload, ushort srcPort, ushort dstPort) =>
        FrameBuilderTestExtensions.WrapRawUdp(payload, srcPort, dstPort);

    private static string _Config(string ports) => $$"""{"schema_version":"1.0",{{ports}},"services":[]}""";

    #region UDP ports

    [Test]
    public async Task ConfiguredUdpPortsDecodeAndAnUnlistedPortStaysUdpPayload()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config(""" "udp_ports":[30490,30500], "tcp_ports":[30501] """));
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1, 2]);

        Packet onDefault = s.Parse(_Udp(message, 40000, 30490));
        Packet onListed = s.Parse(_Udp(message, 40000, 30500));
        Packet onOther = s.Parse(_Udp(message, 40000, 30502));

        await Assert.That(s.Count(onDefault, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(onListed, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(onOther, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(onOther, "udp.payload")).IsEqualTo(1);
    }

    [Test]
    public async Task WithoutConfigurationTheWellKnownPortDecodes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001), 40000, 30490));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task ServiceDiscoveryKeepsItsPortWhenTheConfigurationListsOnlyOtherUdpPorts()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config(""" "udp_ports":[30600] """));
        byte[] sd = SomeIpBytes.Message(0xFFFF, 0x8100, new byte[12], messageType: 0x02);
        byte[] data = SomeIpBytes.Message(0x1234, 0x0001);

        Packet discovery = s.Parse(_Udp(sd, 40000, 30490));
        Packet onConfigured = s.Parse(_Udp(data, 40000, 30600));

        await Assert.That(s.Count(discovery, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(onConfigured, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task APortListedTwiceIsReportedByTheCompilerAndStillBoundOnce()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config(""" "udp_ports":[30490,30490,30500,30500] """));
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001), 40000, 30500));

        await Assert.That(s.Warnings.Count).IsGreaterThan(0);
        await Assert.That(s.Warnings.All(static w => w.Message.Contains("3049", StringComparison.Ordinal) || w.Message.Contains("3050", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
    }

    #endregion

    #region Content-based detection

    [Test]
    public async Task ADetectedHeaderDecodesOnAnyPortOnlyWhenTheSwitchIsOn()
    {
        byte[] frame = _Udp(SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]), 40000, 40001);

        using SomeIpTestStack off = SomeIpTestStack.Create(_EmptyConfig);
        using SomeIpTestStack on = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));
        Packet withoutDetection = off.Parse(frame);
        Packet withDetection = on.Parse(frame);

        await Assert.That(off.Count(withoutDetection, "someip")).IsEqualTo(0);
        await Assert.That(off.Count(withoutDetection, "udp.payload")).IsEqualTo(1);
        await Assert.That(on.Count(withDetection, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task DetectionDoesNotClaimPayloadsThatDoNotLookLikeAHeader()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));

        Packet wrongVersion = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, protocolVersion: 2), 40000, 40001));
        Packet wrongType = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: 0x05), 40000, 40001));
        Packet tooShort = s.Parse(_Udp(new byte[15], 40000, 40001));
        Packet lengthBelowMinimum = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, lengthOverride: 7), 40000, 40001));

        await Assert.That(s.Count(wrongVersion, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(wrongType, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(tooShort, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(lengthBelowMinimum, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task DetectionAlsoAppliesToATcpStreamOnAnUnknownPort()
    {
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]);
        using SomeIpTestStack off = SomeIpTestStack.Create(_EmptyConfig);
        using SomeIpTestStack on = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));

        TcpTestConversation conversation = new(40001);
        conversation.Handshake();
        conversation.ClientSend(message);
        Packet[] declined = conversation.ParseAll(off.Stack);
        TcpTestConversation again = new(40001);
        again.Handshake();
        again.ClientSend(message);
        Packet[] accepted = again.ParseAll(on.Stack);

        await Assert.That(declined.Sum(p => off.Count(p, "someip"))).IsEqualTo(0);
        await Assert.That(accepted.Sum(p => on.Count(p, "someip"))).IsEqualTo(1);
    }

    [Test]
    public async Task ADatagramThatDeclaresMoreThanItHoldsIsNotGuessedOnAnUnknownPortButDecodesOnAListedOne()
    {
        // 100 bytes declared, 3 present: on a stream the rest could follow, in a datagram nothing can.
        byte[] cut = SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3], lengthOverride: 100);
        using SomeIpTestStack s = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));

        Packet unknownPort = s.Parse(_Udp(cut, 40000, 40001));
        Packet wellKnownPort = s.Parse(_Udp(cut, 40000, 30490));

        await Assert.That(s.Count(unknownPort, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(wellKnownPort, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(wellKnownPort, "someip.error.truncated")).IsEqualTo(1);
    }

    [Test]
    public async Task TheDecisionOfTheFirstParseIsReplayedByEveryReparse()
    {
        using SomeIpTestStack on = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));
        using SomeIpTestStack off = SomeIpTestStack.Create(_EmptyConfig);
        byte[] frame = _Udp(SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]), 40000, 40001);

        Packet acceptedFirst = on.Parse(frame);
        Packet declinedFirst = off.Parse(frame);

        await PacketFieldComparer.AssertFieldIdentical(on.Stack, acceptedFirst, on.Reparse(frame, 0));
        await PacketFieldComparer.AssertFieldIdentical(off.Stack, declinedFirst, off.Reparse(frame, 0));
    }

    [Test]
    public async Task AUnitInsideAReassembledDatagramReplaysItsAcceptance()
    {
        // The datagram is fragmented by IPv4, so the SOME/IP unit lives in a buffer that the packet binds (buffer index 1), which
        // is where the layer key of an effect carries a buffer index and not just an offset.
        using SomeIpTestStack s = SomeIpTestStack.Create(_EmptyConfig, ("someip.heuristic", SettingValue.Bool(true)));
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, new byte[3000]);
        List<byte[]> fragments = SomeIpFrames.FragmentedUdp(message, 40000, 40001);

        Packet[] first = [.. fragments.Select(s.Parse)];
        Packet last = first[^1];

        await Assert.That(s.Count(last, "someip")).IsEqualTo(1);
        for (int i = 0; i < fragments.Count; i++)
        {
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], s.Reparse(fragments[i], i));
        }
    }

    #endregion

    #region DTLS detection

    /// <summary>A DTLS 1.2 record: content type, version, epoch 0, sequence 1, length, and a body of that length.</summary>
    private static byte[] _DtlsRecord(byte contentType, int bodyLength) =>
        new SomeIpBytes().U8(contentType).U16(0xFEFD).U16(0).U16(0).U32(1).U16((ushort)bodyLength).Fill(0, bodyLength).ToArray();

    [Test]
    public async Task ADtlsRecordOnASomeIpPortGoesToDtlsOnlyWithDetectionOnAndARealRequestStaysSomeIp()
    {
        byte[] record = _Udp(_DtlsRecord(22, 12), 40000, 30490);
        byte[] request = _Udp(SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]), 40000, 30490);
        using SomeIpTestStack off = SomeIpTestStack.Create(_EmptyConfig);
        using SomeIpTestStack on = SomeIpTestStack.Create(_EmptyConfig, ("someip.detect_dtls", SettingValue.Bool(true)));

        Packet asSomeIp = off.Parse(record);
        Packet asDtls = on.Parse(record);
        Packet stillSomeIp = on.Parse(request);

        await Assert.That(off.Count(asSomeIp, "dtls")).IsEqualTo(0);
        await Assert.That(off.Count(asSomeIp, "someip")).IsEqualTo(1);
        await Assert.That(off.Strings(asSomeIp, "someip.error.length_min").Count).IsEqualTo(1);
        await Assert.That(on.Count(asDtls, "dtls")).IsEqualTo(1);
        await Assert.That(on.Count(asDtls, "someip")).IsEqualTo(0);
        await Assert.That(on.Count(stillSomeIp, "someip")).IsEqualTo(1);
        await Assert.That(on.Count(stillSomeIp, "dtls")).IsEqualTo(0);

        // The decision depends on the bytes alone, so a re-parse repeats it.
        await PacketFieldComparer.AssertFieldIdentical(on.Stack, asDtls, on.Reparse(record, 0));
    }

    [Test]
    public async Task TheDtlsProbeNeedsAWholeRecordInAPlausibleVersionAndYieldsToARealSomeIpMessage()
    {
        byte[] record = _DtlsRecord(22, 12);

        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(record)).IsTrue();
        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(record.AsSpan(0, 20))).IsFalse();
        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(_DtlsRecord(19, 12))).IsFalse();
        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(_DtlsRecord(64, 12))).IsFalse();
        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(new byte[10])).IsFalse();

        // Two records in one datagram (a handshake flight): the first one has to fit and nothing is required of the rest.
        byte[] flight = SomeIpBytes.Concat(record, _DtlsRecord(22, 30));
        await Assert.That(SomeIpDtlsProbe.LooksLikeDtls(flight)).IsTrue();

        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]);
        await Assert.That(SomeIpDtlsProbe.LooksLikeSomeIp(message)).IsTrue();
        await Assert.That(SomeIpDtlsProbe.LooksLikeSomeIp(record)).IsFalse();
    }

    #endregion

    #region Plaintext hand-off of an encrypted transport

    private const string _EncryptedPorts = """{"schema_version":"1.0","tls_ports":[30501],"dtls_ports":[30512],"services":[]}""";

    [Test]
    public async Task PlaintextOnAListedDtlsPortDecodesAndOnAnUnlistedPortDoesNot()
    {
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1]);

        using SomeIpTestStack listed = PlaintextPublisherProtocol.BuildStack(_EncryptedPorts, PlaintextTransport.Dtls, 30512);
        using SomeIpTestStack unlisted = PlaintextPublisherProtocol.BuildStack(_EncryptedPorts, PlaintextTransport.Dtls, 30513);
        Packet decoded = listed.Parse(_Udp(message, 40000, 30512));
        Packet declined = unlisted.Parse(_Udp(message, 40000, 30513));

        // The publisher owns the DTLS table only: the DTLS list is bound without a word, the TLS list (no tls.port table) is
        // reported, which is what a stack with a DTLS-only decrypting protocol must say.
        await Assert.That(listed.Warnings.Count).IsEqualTo(1);
        await Assert.That(listed.Warnings[0].Message).Contains("tls_ports").And.Contains(PlaintextHandoff.TlsPortTableName);
        await Assert.That(listed.Count(decoded, "someip")).IsEqualTo(1);
        await Assert.That(unlisted.Count(declined, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task PlaintextOfTlsIsComparedWithTheTlsListAndNotTheDtlsList()
    {
        // 30512 is listed for DTLS only: plaintext that claims to come from TLS on that port is not accepted.
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1]);
        using SomeIpTestStack s = PlaintextPublisherProtocol.BuildStack(_EncryptedPorts, PlaintextTransport.Tls, 30512);

        TcpTestConversation conversation = new(30512);
        conversation.Handshake();
        conversation.ClientSend(message);
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip"))).IsEqualTo(0);
    }

    [Test]
    public async Task PlaintextOnAListedTlsPortDecodes()
    {
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1]);
        using SomeIpTestStack s = PlaintextPublisherProtocol.BuildStack(_EncryptedPorts, PlaintextTransport.Tls, 30501);

        TcpTestConversation conversation = new(30501);
        conversation.Handshake();
        conversation.ClientSend(message);
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip"))).IsEqualTo(1);
    }

    [Test]
    public async Task ADtlsPortThatEqualsAnUdpPortLeavesBothPathsWorking()
    {
        const string both = """{"schema_version":"1.0","udp_ports":[30490],"dtls_ports":[30490],"services":[]}""";
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1]);

        using SomeIpTestStack s = PlaintextPublisherProtocol.BuildStack(both, PlaintextTransport.Dtls, 30490);
        Packet clear = s.Parse(_Udp(message, 40000, 30490));

        await Assert.That(s.Warnings.Count).IsEqualTo(0);

        // The cleartext datagram is taken by the port table, the publisher shares the port number but is not a bound parser.
        await Assert.That(s.Count(clear, "someip")).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task AListedTlsOrDtlsPortWithoutADecryptingProtocolIsReportedOncePerList()
    {
        // The standard stack has no protocol that decrypts: the lists are compiled and compared with a marker nobody publishes, and
        // no tls.port or dtls.port table exists. The user is told once per list, by the list's name and the table's, and parsing
        // of cleartext SOME/IP is unchanged.
        using SomeIpTestStack s = SomeIpTestStack.Create(_EncryptedPorts);
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, [1]), 40000, 30490));

        await Assert.That(s.Warnings.Count).IsEqualTo(2);
        await Assert.That(s.Warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(s.Warnings[0].SettingName).IsEqualTo(SomeIpRegistration.ConfigFileSetting);
        await Assert.That(s.Warnings[0].Message).Contains("tls_ports").And.Contains(PlaintextHandoff.TlsPortTableName).And.Contains("do not decrypt");
        await Assert.That(s.Warnings[1].Message).Contains("dtls_ports").And.Contains(PlaintextHandoff.DtlsPortTableName);
        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
    }

    #endregion

    #region TCP framing

    [Test]
    public async Task AMessageSplitAcrossSegmentsIsFramedOnceAndItsNeighbourInTheSameSegmentToo()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] first = SomeIpBytes.Message(0x1234, 0x0001, new byte[24]);
        byte[] second = SomeIpBytes.Message(0x1234, 0x0002, new byte[8]);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(first, second), mss: 30);
        Packet[] packets = conversation.ParseAll(s.Stack);

        List<int> lengths = [.. packets.SelectMany(p => s.ByteLengths(p, "someip"))];
        await Assert.That(lengths).IsEquivalentTo(_Items(first.Length, second.Length));
    }

    [Test]
    public async Task AStreamThatEndsInsideAMessageShowsATruncatedMessage()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] whole = SomeIpBytes.Message(0x1234, 0x0001, [1, 2]);
        byte[] cut = SomeIpBytes.Message(0x1234, 0x0002, new byte[22])[..20];

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(whole, cut));
        conversation.ClientFin();
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip"))).IsEqualTo(2);
        await Assert.That(packets.Sum(p => s.Count(p, "someip.error.truncated"))).IsEqualTo(1);
    }

    [Test]
    public async Task FewerThanSixteenBytesBeforeTheEndOfTheStreamAreAnIncompleteHeader()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] whole = SomeIpBytes.Message(0x1234, 0x0001, [1, 2]);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(whole, [1, 2, 3, 4, 5]));
        conversation.ClientFin();
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip.error.incomplete"))).IsEqualTo(1);
        await Assert.That(packets.SelectMany(p => s.ByteLengths(p, "someip.trailing")).Single()).IsEqualTo(5);
    }

    [Test]
    public async Task AStreamThatLostItsPositionResynchronizesAndReportsTheSkippedBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] first = SomeIpBytes.Message(0x1234, 0x0001, [1, 2]);
        byte[] garbage = new byte[12];
        garbage.AsSpan().Fill(0xEE);
        byte[] second = SomeIpBytes.Message(0x1234, 0x0002, [3]);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(first, garbage, second));
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip"))).IsEqualTo(2);
        List<string> skipped = [.. packets.SelectMany(p => s.Strings(p, "tcp.error.resync"))];
        await Assert.That(skipped.Count).IsEqualTo(1);
        await Assert.That(skipped[0]).Contains("12 bytes");
    }

    [Test]
    public async Task WithResynchronizationOffALostPositionEndsTheFramingAndNothingFollowsIsDecoded()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tcp_resync", SettingValue.Bool(false)));
        byte[] first = SomeIpBytes.Message(0x1234, 0x0001, [1, 2]);
        byte[] garbage = new byte[12];
        garbage.AsSpan().Fill(0xEE);
        byte[] second = SomeIpBytes.Message(0x1234, 0x0002, [3]);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(first, garbage, second));
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.Sum(p => s.Count(p, "someip"))).IsEqualTo(1);
        await Assert.That(packets.Sum(p => s.Count(p, "tcp.error.resync"))).IsEqualTo(0);
    }

    [Test]
    public async Task AMessageAboveTheSizeLimitIsNeverFramedAndTheDropIsReportedByName()
    {
        // A header that announces about 3 MiB with the limit at the smallest sensible value for the test (1 KiB): the detector
        // never frames it, the stream buffer fills past the limit plus headroom, and the TCP layer reports the drop.
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tcp_max_message_size", SettingValue.U64(1024)));
        byte[] announcing = SomeIpBytes.Message(0x1234, 0x0001, [], lengthOverride: 3 * 1024 * 1024);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(announcing);
        byte[] filler = new byte[1400];
        for (int i = 0; i < 1000; i++)
        {
            conversation.ClientSend(filler);
        }

        Packet[] packets = conversation.ParseAll(s.Stack);

        // While the buffer fills nothing is framed; the drop is reported on the packet whose bytes pushed the buffer past its limit.
        int reportAt = Array.FindIndex(packets, p => s.Count(p, "tcp.error.reassembly_limit") > 0);
        await Assert.That(reportAt).IsGreaterThan(0);
        await Assert.That(packets.Take(reportAt).Sum(p => s.Count(p, "someip"))).IsEqualTo(0);
    }

    [Test]
    public async Task AMessageExactlyAtTheSizeLimitIsFramed()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tcp_max_message_size", SettingValue.U64(1024)));
        byte[] atLimit = SomeIpBytes.Message(0x1234, 0x0001, new byte[1024 - 16]);

        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(atLimit, mss: 400);
        Packet[] packets = conversation.ParseAll(s.Stack);

        await Assert.That(packets.SelectMany(p => s.ByteLengths(p, "someip")).Single()).IsEqualTo(1024);
    }

    #endregion

    private static T[] _Items<T>(params T[] values) => values;
}
