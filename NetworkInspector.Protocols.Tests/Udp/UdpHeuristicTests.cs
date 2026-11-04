// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the UDP content-based fallback: the <c>udp.heuristic</c> table is tried only after no port parser consumed the
/// payload, the ports are visible to the parser it dispatches to, and a re-parse repeats the same decisions.
/// </summary>
internal sealed class UdpHeuristicTests
{
    /// <summary>An application protocol that records how often it ran and the UDP ports it saw.</summary>
    private sealed class PortRecordingProtocol : IProtocol
    {
        public string Name => "udp.probe";

        public string UiName => "UDP Probe";

        public int ParseCount { get; private set; }

        public List<(ushort Source, ushort Destination)> Ports { get; } = [];

        public bool PortsMissing { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            ParseCount++;
            if (UdpPortCache.TryGet(parentField.Packet.Id, out ushort source, out ushort destination))
            {
                Ports.Add((source, destination));
            }
            else
            {
                PortsMissing = true;
            }

            return data.Length;
        }
    }

    /// <summary>A heuristic parser that accepts everything and counts its tests.</summary>
    private sealed class AcceptAllParser(ProtocolId protocolId) : IHeuristicParser
    {
        public string Name => "accept.all";

        public string UiName => "Accept All";

        public int TestCount { get; private set; }

        public ProtocolId ProtocolId { get; } = protocolId;

        public bool Test(ReadOnlyMemory<byte> data)
        {
            TestCount++;
            return true;
        }
    }

    private static Stack _BuildStack(PortRecordingProtocol? probe, out AcceptAllParser? parser)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        _ = builder.RegisterStandardProtocols();
        parser = null;
        if (probe is not null)
        {
            ProtocolId id = builder.RegisterProtocol(probe);
            HeuristicProtocolTableId table = builder.GetHeuristicProtocolTableId(UdpProtocol.HeuristicTableName)!.Value;
            parser = new AcceptAllParser(id);
            builder.RegisterHeuristicParser(table, parser);
        }

        return builder.Build();
    }

    private static byte[] _UdpFrame(ushort source, ushort destination, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = AutomotiveEthUdpFrames.TestEthernet();
        IPv4Layer ip = AutomotiveEthUdpFrames.TestIpv4();
        UdpLayer udp = new(source, destination);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static (bool Before, bool OtherId, bool AfterClear) _CacheRoundTrip()
    {
        UdpPortCache.Publish(5, 1111, 2222);
        bool before = UdpPortCache.TryGet(5, out ushort source, out ushort destination)
            && source == 1111
            && destination == 2222;
        bool other = UdpPortCache.TryGet(6, out _, out _);
        UdpPortCache.Clear();
        bool after = UdpPortCache.TryGet(5, out _, out _);
        return (before, other, after);
    }

    [Test]
    public async Task APortParserKeepsTheDatagramFromTheHeuristic()
    {
        PortRecordingProtocol probe = new();
        using Stack stack = _BuildStack(probe, out AcceptAllParser? parser);

        // Port 53 has a parser (DNS) that consumes the payload; the heuristic must not be asked.
        _ = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(15000, 53, new byte[12]), 0, Timestamp.FromSecs(0));

        await Assert.That(parser!.TestCount).IsEqualTo(0);
        await Assert.That(probe.ParseCount).IsEqualTo(0);
    }

    [Test]
    public async Task AnUnclaimedPortCallsTheHeuristicOnceAndDispatchesToItAgainOnReparse()
    {
        PortRecordingProtocol probe = new();
        using Stack stack = _BuildStack(probe, out AcceptAllParser? parser);
        byte[] frame = _UdpFrame(12345, 40000, new byte[] { 1, 2, 3, 4 });

        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromSecs(0));
        int firstTests = parser!.TestCount;
        int firstRuns = probe.ParseCount;
        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromSecs(0));

        // The decision is stateless: a re-parse asks the heuristic again and reaches the same parser.
        await Assert.That(firstTests).IsEqualTo(1);
        await Assert.That(firstRuns).IsEqualTo(1);
        await Assert.That(parser.TestCount).IsEqualTo(2);
        await Assert.That(probe.ParseCount).IsEqualTo(2);
    }

    [Test]
    public async Task WithoutAHeuristicParserThePayloadStaysUdpPayload()
    {
        using Stack stack = _BuildStack(probe: null, out _);

        Packet packet = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(12345, 40000, new byte[] { 1, 2, 3, 4 }), 0, Timestamp.FromSecs(0));

        await ProtocolTestHelper.AssertFieldExists(stack, packet, "udp.payload");
    }

    [Test]
    public async Task TheDispatchedParserSeesTheUdpPorts()
    {
        PortRecordingProtocol probe = new();
        using Stack stack = _BuildStack(probe, out _);

        _ = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(12345, 40000, new byte[] { 1, 2, 3, 4 }), 0, Timestamp.FromSecs(0));

        await Assert.That(probe.Ports.Count).IsEqualTo(1);
        await Assert.That(probe.Ports[0]).IsEqualTo(((ushort)12345, (ushort)40000));
        await Assert.That(probe.PortsMissing).IsFalse();
    }

    [Test]
    public async Task ThePortCacheAnswersOnlyForThePacketItWasPublishedFor()
    {
        (bool before, bool otherId, bool afterClear) = _CacheRoundTrip();

        await Assert.That(before).IsTrue();
        await Assert.That(otherId).IsFalse();
        await Assert.That(afterClear).IsFalse();
    }

    [Test]
    public async Task ThePortsAreWithdrawnWhenTheDatagramIsDone()
    {
        PortRecordingProtocol probe = new();
        using Stack stack = _BuildStack(probe, out _);

        _ = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(12345, 40000, new byte[] { 1, 2, 3, 4 }), 0, Timestamp.FromSecs(0));

        // After the parse the thread holds no ports: a parser reached later without UDP must not find them.
        await Assert.That(UdpPortCache.TryGet(0, out _, out _)).IsFalse();
    }

    [Test]
    public async Task TheStandardStackOffersTheUdpHeuristicTable()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        await Assert.That(stack.GetHeuristicProtocolTableId(UdpProtocol.HeuristicTableName)).IsNotNull();
        await Assert.That(UdpProtocol.HeuristicTableName).IsEqualTo("udp.heuristic");
    }
}
