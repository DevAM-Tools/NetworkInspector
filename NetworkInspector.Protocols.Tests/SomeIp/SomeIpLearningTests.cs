// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// Port learning: UDP ports that service discovery announced decode from the announcement on, only when the user switched it on,
/// never for TCP, never for the ignore list, never for the "other service" entry, and never retroactively.
/// </summary>
internal sealed class SomeIpLearningTests
{
    private static readonly byte[] _Ipv4 = [192, 168, 1, 100];

    private static readonly (string Name, SettingValue Value) _LearningOn = ("someip.learn_sd_ports", SettingValue.Bool(true));

    private static byte[] _Request(ushort port) =>
        FrameBuilderTestExtensions.WrapRawUdp(SomeIpBytes.Message(0x1234, 0x0001, [1, 2]), 40000, port);

    private static byte[] _Offer(ushort service, ushort port, byte protocol = 17, uint ttl = 3) =>
        new SomeIpSdBuilder().Offer(service, 1, 1, 0, ttl).Ipv4Option(_Ipv4, protocol, port).BuildFrame();

    [Test]
    public async Task AnAnnouncedUdpPortDecodesFromTheAnnouncementOnAndIsMarked()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        Packet before = s.Parse(_Request(30512));
        _ = s.Parse(_Offer(0x1234, 30512));
        Packet after = s.Parse(_Request(30512));

        await Assert.That(s.Count(before, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(before, "udp.payload")).IsEqualTo(1);
        await Assert.That(s.Count(after, "someip")).IsEqualTo(1);
        await ProtocolTestHelper.AssertBoolField(s.Stack, after, "someip.learned_port", true);
    }

    [Test]
    public async Task WithLearningOffAnnouncedPortsStayUndecoded()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        _ = s.Parse(_Offer(0x1234, 30512));
        Packet after = s.Parse(_Request(30512));

        await Assert.That(s.Count(after, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(after, "udp.payload")).IsEqualTo(1);
    }

    [Test]
    public async Task ATcpEndpointAndATcpConnectionAreNeverLearned()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        _ = s.Parse(_Offer(0x1234, 30600, protocol: 6));
        Packet udpOnTheTcpPort = s.Parse(_Request(30600));

        TcpTestConversation conversation = new(30600);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Message(0x1234, 0x0001, [1, 2]));
        Packet[] tcpPackets = conversation.ParseAll(s.Stack, firstPacketId: 2);

        await Assert.That(s.Count(udpOnTheTcpPort, "someip")).IsEqualTo(0);
        await Assert.That(tcpPackets.Sum(p => s.Count(p, "someip"))).IsEqualTo(0);
        await Assert.That(tcpPackets.Sum(p => s.Count(p, "tcp.payload"))).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task AUdpAnnouncementStillDecodesAfterATcpOne()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        _ = s.Parse(_Offer(0x1234, 30600, protocol: 6));
        _ = s.Parse(_Offer(0x1234, 30512));
        Packet after = s.Parse(_Request(30512));

        await Assert.That(s.Count(after, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task PortsOnTheIgnoreListAreNeverLearned()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(
            null, _LearningOn, ("someip.learn_sd_ports_ignore", SettingValue.U64Array([30512UL])));
        _ = s.Parse(new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3, count1: 2).Ipv4Option(_Ipv4, 17, 30512).Ipv4Option(_Ipv4, 17, 30513).BuildFrame());
        Packet ignored = s.Parse(_Request(30512));
        Packet learned = s.Parse(_Request(30513));

        await Assert.That(s.Count(ignored, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(learned, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task TheOtherServiceEntryAndAWithdrawnOfferTeachNothing()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        _ = s.Parse(_Offer(0xFFFE, 30700));
        _ = s.Parse(_Offer(0x1234, 30701, ttl: 0));
        Packet otherService = s.Parse(_Request(30700));
        Packet withdrawn = s.Parse(_Request(30701));

        await Assert.That(s.Count(otherService, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(withdrawn, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task AnOptionThatNoEntryReferencesTeachesNothing()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);

        // The offer references the option at index 0; the second option (index 1) is announced by nobody.
        _ = s.Parse(new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3, count1: 1).Ipv4Option(_Ipv4, 17, 30512).Ipv4Option(_Ipv4, 17, 30513).BuildFrame());
        Packet referenced = s.Parse(_Request(30512));
        Packet unreferenced = s.Parse(_Request(30513));

        await Assert.That(s.Count(referenced, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(unreferenced, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task ASubscribeTeachesTheEndpointOfItsClient()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        _ = s.Parse(new SomeIpSdBuilder().Entry(0x06, 0x1234, 1, 1, 3, 0x00000001, count1: 1).Ipv4Option(_Ipv4, 17, 30800).BuildFrame());
        Packet after = s.Parse(_Request(30800));

        await Assert.That(s.Count(after, "someip")).IsEqualTo(1);
    }

    [Test]
    public async Task AReparseOfAPacketParsedBeforeTheAnnouncementEqualsItsFirstParseAndNeitherCrossesOver()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, _LearningOn);
        byte[][] frames = [_Request(30512), _Offer(0x1234, 30512), _Request(30512)];
        Packet[] first = [.. frames.Select(s.Parse)];

        await Assert.That(s.Count(first[0], "someip")).IsEqualTo(0);
        await Assert.That(s.Count(first[2], "someip")).IsEqualTo(1);

        // Re-parsed after the announcement and after the second request: packet 0 is still undecoded, packet 2 still decoded.
        Packet again0 = s.Reparse(frames[0], 0);
        Packet again2 = s.Reparse(frames[2], 2);
        await Assert.That(s.Count(again0, "someip")).IsEqualTo(0);
        await Assert.That(s.Count(again2, "someip")).IsEqualTo(1);
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[0], again0);
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[2], again2);
    }

    [Test]
    public async Task TwoStacksNeverShareWhatTheyLearned()
    {
        using SomeIpTestStack a = SomeIpTestStack.Create(null, _LearningOn);
        using SomeIpTestStack b = SomeIpTestStack.Create(null, _LearningOn);
        _ = a.Parse(_Offer(0x1234, 30512));
        Packet inA = a.Parse(_Request(30512));
        Packet inB = b.Parse(_Request(30512));

        await Assert.That(a.Count(inA, "someip")).IsEqualTo(1);
        await Assert.That(b.Count(inB, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task ReadersSeeEveryPortThatWasLearnedBeforeTheyAskedAndNothingElse()
    {
        SomeIpLearnedPorts ports = new([5000]);
        ports.Learn(30512);
        ports.Learn(5000);
        ports.Learn(0);

        await Assert.That(ports.Contains(30512)).IsTrue();
        await Assert.That(ports.Contains(5000)).IsFalse();
        await Assert.That(ports.Contains(0)).IsFalse();
        await Assert.That(ports.Contains(30513)).IsFalse();
        await Assert.That(ports.Contains(ushort.MaxValue)).IsFalse();
        ports.Learn(ushort.MaxValue);
        await Assert.That(ports.Contains(ushort.MaxValue)).IsTrue();
    }
}
