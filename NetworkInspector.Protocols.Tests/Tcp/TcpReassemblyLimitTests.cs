// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the limits of TCP reassembly in one packet: how many reassembled PDUs one segment may hand to the application
/// protocol (a packet holds at most 255 additional buffers), and what happens when the reassembly buffer refuses a segment.
/// </summary>
internal sealed class TcpReassemblyLimitTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);
    private const ushort _ClientPort = 52000;
    private const ushort _ProbePort = 65000;

    /// <summary>Counts the PDUs it receives and accepts every one.</summary>
    private sealed class CountingProtocol : IProtocol
    {
        public string Name => "count.probe";
        public string UiName => "Count Probe";

        public int Calls { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (!parentField.TryGetEffectLayerKey(data, out _))
            {
                return NetworkInspector.Core.Errors.ParseError.Custom(Name, "Parse data is not a slice of this packet's buffers.");
            }

            Calls++;
            return data.Length;
        }
    }

    private static Stack _BuildStack(out CountingProtocol probe)
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        CountingProtocol counting = new();
        probe = counting;
        builder.RegisterProtocol(counting, static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(TcpProtocol.PortTableName, _ProbePort, id);
            b.RegisterStreamReassemblyConfig(id, new StreamReassemblyConfig
            {
                BoundaryDetector = new LengthPrefixDetector(
                    lengthOffset: 0,
                    lengthSize: 2,
                    bigEndian: true,
                    lengthIncludesHeader: false,
                    headerSize: 2),
            });
        });
        return builder.Build();
    }

    private static byte[] _ClientFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(_ClientPort, _ProbePort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _ServerFrame(uint seq, uint ack, byte flags)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(_ProbePort, _ClientPort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Opens a connection and returns the packet that carries <paramref name="pduCount"/> one-byte PDUs in one segment.</summary>
    private static Packet _ParseSegmentWithPdus(Stack stack, int pduCount)
    {
        const uint clientIsn = 100;
        const uint serverIsn = 5000;
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

        // Each PDU is a 2-byte length prefix (1) and one octet.
        byte[] payload = new byte[pduCount * 3];
        for (int index = 0; index < pduCount; index++)
        {
            payload[(index * 3) + 1] = 1;
            payload[(index * 3) + 2] = 0x41;
        }

        return ProtocolTestHelper.ParseFrame(
            stack, _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.PshAck, payload), 3, Timestamp.FromMillis(3));
    }

    [Test]
    public async Task SegmentWith255PdusDispatchesAllOfThem()
    {
        using Stack stack = _BuildStack(out CountingProtocol probe);

        Packet packet = _ParseSegmentWithPdus(stack, 255);

        await Assert.That(probe.Calls).IsEqualTo(255);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task SegmentWith256PdusReportsThePacketBufferLimit()
    {
        using Stack stack = _BuildStack(out CountingProtocol probe);

        Packet packet = _ParseSegmentWithPdus(stack, 256);

        await Assert.That(probe.Calls).IsEqualTo(255);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }
}
