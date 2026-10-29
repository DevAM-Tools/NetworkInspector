// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that a reassembled datagram or message which cannot be handed to the next protocol, because the packet already
/// holds the maximum of 255 bound buffers, is reported by name instead of reaching the next protocol as empty data.
/// A probe protocol on a UDP port fills the packet's buffer table and then calls the protocol under test with the datagram
/// carried in the UDP payload.
/// </summary>
internal sealed class BoundBufferLimitTests
{
    private const ushort _ProbePort = 15600;
    private const int _AdditionalBufferLimit = 255;

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>Binds 255 buffers to the packet and then calls the protocol with the given name.</summary>
    private sealed class BufferFillerProtocol(string targetProtocolName) : IProtocol
    {
        public string Name => "buffer.filler";

        public string UiName => "Buffer Filler";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            for (int index = 0; index < _AdditionalBufferLimit; index++)
            {
                if (parentField.BindParseBuffer(new byte[1]).IsEmpty)
                {
                    return NetworkInspector.Core.Errors.ParseError.Custom(Name, "The probe could not fill the buffer table.");
                }
            }

            ProtocolId? target = context.Stack?.GetProtocolId(targetProtocolName);
            if (target is null)
            {
                return NetworkInspector.Core.Errors.ParseError.Custom(Name, "The target protocol is not registered.");
            }

            return parentField.CallProtocol(target.Value, data, in context);
        }
    }

    private static Stack _BuildStack(string targetProtocolName, params (string Name, SettingValue Value)[] settingOverrides)
    {
        using SettingsManager settings = new();
        foreach ((string name, SettingValue value) in settingOverrides)
        {
            settings.PreloadValue(name, value);
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(new BufferFillerProtocol(targetProtocolName), static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(UdpProtocol.PortTableName, _ProbePort, id);
        });
        return builder.Build();
    }

    private static byte[] _UdpFrame(ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(40000, _ProbePort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Parses the two fragment datagrams as packets 0 and 1 and returns the second packet.</summary>
    private static Packet _ParseTwoFragments(Stack stack, byte[] firstFragment, byte[] secondFragment)
    {
        _ = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(firstFragment), 0, Timestamp.FromMillis(0));
        return ProtocolTestHelper.ParseFrame(stack, _UdpFrame(secondFragment), 1, Timestamp.FromMillis(1));
    }

    private static byte[] _Ipv4Fragment(ushort flagsAndOffset)
    {
        // 20-byte header, no options, protocol 253 (experimental, no parser), 8 bytes of payload.
        byte[] datagram = new byte[28];
        datagram[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)datagram.Length);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(6), flagsAndOffset);
        datagram[8] = 64;
        datagram[9] = 253;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(12), 0xC0A80101);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16), 0xC0A80102);
        return datagram;
    }

    private static byte[] _Ipv6Fragment(ushort offsetAndMoreFlag)
    {
        // 40-byte header (next header 44 = fragment), 8-byte fragment header (next header 253), 8 bytes of payload.
        byte[] datagram = new byte[56];
        datagram[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), 16);
        datagram[6] = 44;
        datagram[7] = 64;
        datagram[8] = 0x20;
        datagram[9] = 0x01;
        datagram[23] = 0x01;
        datagram[24] = 0x20;
        datagram[25] = 0x01;
        datagram[39] = 0x02;
        datagram[40] = 253;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(42), offsetAndMoreFlag);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(44), 0xCAFEBABE);
        return datagram;
    }

    private static byte[] _EthernetFrame(byte[] ipv4Datagram)
    {
        byte[] frame = new byte[14 + ipv4Datagram.Length];
        new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }.CopyTo(frame, 0);
        new byte[] { 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB }.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        ipv4Datagram.CopyTo(frame, 14);
        return frame;
    }

    [Test]
    public async Task Ipv4DatagramReassembledWithAFullBufferTableIsReported()
    {
        using Stack stack = _BuildStack("ip");

        Packet packet = _ParseTwoFragments(stack, _Ipv4Fragment(0x2000), _Ipv4Fragment(0x0001));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    [Test]
    public async Task Ipv6DatagramReassembledWithAFullBufferTableIsReported()
    {
        using Stack stack = _BuildStack("ipv6");

        // Fragment offsets count 8-byte units in bits 15..3; bit 0 is the More Fragments flag.
        Packet packet = _ParseTwoFragments(stack, _Ipv6Fragment(0x0001), _Ipv6Fragment(0x0008));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    [Test]
    public async Task SomeIpTpMessageReassembledWithAFullBufferTableIsReported()
    {
        // The probe calls SOME/IP directly on its own port, so the datagram is accepted through content-based detection.
        using Stack stack = _BuildStack("someip", ("someip.heuristic", SettingValue.Bool(true)));

        // A single last segment at offset 0 completes the message at once.
        byte[] message = SomeIpFrames.TpMessage(0, new byte[16], more: false, messageType: 0x20);
        byte[] frame = _UdpFrame(message);

        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    [Test]
    public async Task Ipv4DatagramReassembledWithRoomInTheBufferTableIsNotAnError()
    {
        // Control: the same fragments without the filler are reassembled normally.
        using Stack stack = ProtocolTestHelper.BuildStack();

        _ = ProtocolTestHelper.ParseFrame(stack, _EthernetFrame(_Ipv4Fragment(0x2000)), 0, Timestamp.FromMillis(0));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, _EthernetFrame(_Ipv4Fragment(0x0001)), 1, Timestamp.FromMillis(1));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }
}
