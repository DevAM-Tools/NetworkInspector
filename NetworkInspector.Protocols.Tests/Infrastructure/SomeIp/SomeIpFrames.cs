// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// Frame helpers for SOME/IP tests: a UDP datagram around a hand-built message, a SOME/IP-TP segment, and a datagram that IPv4
/// fragments (so the SOME/IP unit lives in a buffer that the packet binds).
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpFrames
{
    private const int _MtuFrameBytes = 1500;

    /// <summary>Wraps a payload in Ethernet, IPv4 and UDP.</summary>
    /// <param name="payload">The UDP payload.</param>
    /// <param name="srcPort">The source port.</param>
    /// <param name="dstPort">The destination port (default: the well-known SOME/IP port).</param>
    internal static byte[] Udp(byte[] payload, ushort srcPort = 40000, ushort dstPort = 30490) =>
        FrameBuilderTestExtensions.WrapRawUdp(payload, srcPort, dstPort);

    /// <summary>
    /// A SOME/IP-TP message: the header with the TP flag, the 4-byte TP word (the byte offset, which is a multiple of 16, with the
    /// more-segments flag in bit 0), and the segment's bytes.
    /// </summary>
    /// <param name="offset">The byte offset of the segment in the message (a multiple of 16).</param>
    /// <param name="data">The segment's bytes.</param>
    /// <param name="more">Whether further segments follow.</param>
    /// <param name="service">Service id.</param>
    /// <param name="method">Method id.</param>
    /// <param name="client">Client id.</param>
    /// <param name="session">Session id.</param>
    /// <param name="messageType">Message type byte with the TP flag (default: segmented response).</param>
    /// <param name="interfaceVersion">Interface version.</param>
    internal static byte[] TpMessage(
        uint offset,
        byte[] data,
        bool more,
        ushort service = 0x1234,
        ushort method = 0x0042,
        ushort client = 0x0001,
        ushort session = 0x0001,
        byte messageType = 0xA0,
        byte interfaceVersion = 1)
    {
        uint moreBit = more
            ? 1u
            : 0u;
        byte[] payload = new SomeIpBytes().U32(offset | moreBit).Bytes(data).ToArray();
        return SomeIpBytes.Message(service, method, payload, client, session, messageType, 0, interfaceVersion);
    }

    /// <summary>A SOME/IP-TP segment in a UDP frame to the well-known port.</summary>
    /// <inheritdoc cref="TpMessage"/>
    internal static byte[] TpFrame(
        uint offset,
        byte[] data,
        bool more,
        ushort service = 0x1234,
        ushort method = 0x0042,
        ushort client = 0x0001,
        ushort session = 0x0001,
        byte messageType = 0xA0,
        byte interfaceVersion = 1) =>
        Udp(TpMessage(offset, data, more, service, method, client, session, messageType, interfaceVersion));

    /// <summary>
    /// Cuts a UDP datagram into IPv4 fragments of at most one Ethernet frame each. The fragments are separate frames; the last one
    /// completes the datagram, which the stack then parses as a buffer it binds.
    /// </summary>
    /// <param name="payload">The UDP payload.</param>
    /// <param name="srcPort">The source port.</param>
    /// <param name="dstPort">The destination port.</param>
    internal static List<byte[]> FragmentedUdp(byte[] payload, ushort srcPort, ushort dstPort)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]),
            maxFrameSize: _MtuFrameBytes);
        IPv4Layer ip = new(IPv4Address.FromBytes([10, 0, 0, 1]), IPv4Address.FromBytes([10, 0, 0, 2]), dontFragment: false);
        UdpLayer udp = new(srcPort, dstPort, Auto.Explicit((ushort)0));

        CreatedStack<
            StatelessStack<UdpLayer,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();

        using FrameSequence<
            StatelessStack<UdpLayer,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> sequence = stack.Build(payload);
        List<byte[]> frames = [];
        byte[] scratch = new byte[_MtuFrameBytes];
        while (sequence.MoveNext(scratch, out int written))
        {
            frames.Add(scratch.AsSpan(0, written).ToArray());
        }

        // A silently empty or single-frame sequence would make a reassembly test pass without ever reaching the reassembler.
        if (sequence.Status != BuildStatus.Success || frames.Count < 2)
        {
            throw new InvalidOperationException("The fragmenting frame build must produce several frames.");
        }

        return frames;
    }
}
