// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building raw network frame data for exporter tests.
/// Produces standard Ethernet, IPv4/UDP frames as byte arrays.
/// </summary>
internal static class FrameGenerators
{
    #region Building blocks and plain UDP frames

    /// <summary>Standard broadcast destination MAC.</summary>
    private static readonly byte[] _BroadcastMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Standard test source MAC.</summary>
    private static readonly byte[] _TestSrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Standard test destination MAC.</summary>
    private static readonly byte[] _TestDstMac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

    /// <summary>
    /// Builds a simple Ethernet II frame: dst(6) + src(6) + ethertype(2 BE) + payload.
    /// </summary>
    /// <param name="dstMac">The destination MAC address; at least six bytes, of which the first six are used.</param>
    /// <param name="srcMac">The source MAC address; at least six bytes, of which the first six are used.</param>
    /// <param name="etherType">The ethertype, written big-endian.</param>
    /// <param name="payload">The bytes after the Ethernet header.</param>
    /// <returns>The frame bytes: the 14-byte header followed by the payload.</returns>
    internal static byte[] BuildEthernetFrame(
        ReadOnlySpan<byte> dstMac,
        ReadOnlySpan<byte> srcMac,
        ushort etherType,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[14 + payload.Length];
        dstMac[..6].CopyTo(frame);
        srcMac[..6].CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), etherType);
        payload.CopyTo(frame.AsSpan(14));
        return frame;
    }

    /// <summary>
    /// Builds a minimal IPv4 header (20 bytes, zero checksum) + payload.
    /// </summary>
    /// <param name="srcIp">The source address; at least four bytes, of which the first four are used.</param>
    /// <param name="dstIp">The destination address; at least four bytes, of which the first four are used.</param>
    /// <param name="protocol">The IP protocol number of the payload.</param>
    /// <param name="payload">The bytes after the IPv4 header.</param>
    /// <returns>The packet bytes: the 20-byte header followed by the payload.</returns>
    internal static byte[] BuildIpv4Packet(
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        byte protocol,
        ReadOnlySpan<byte> payload)
    {
        int totalLength = 20 + payload.Length;
        byte[] packet = new byte[totalLength];
        packet[0] = 0x45; // Version 4, IHL 5
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)totalLength));
        packet[6] = 0x40; // Don't Fragment
        packet[8] = 64;   // TTL
        packet[9] = protocol;
        srcIp[..4].CopyTo(packet.AsSpan(12));
        dstIp[..4].CopyTo(packet.AsSpan(16));
        payload.CopyTo(packet.AsSpan(20));
        return packet;
    }

    /// <summary>
    /// Builds a minimal UDP datagram (8-byte header, zero checksum) + payload.
    /// </summary>
    /// <param name="srcPort">The source port.</param>
    /// <param name="dstPort">The destination port.</param>
    /// <param name="payload">The bytes after the UDP header.</param>
    /// <returns>The datagram bytes: the 8-byte header followed by the payload.</returns>
    internal static byte[] BuildUdpDatagram(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        int length = 8 + payload.Length;
        byte[] datagram = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), checked((ushort)length));
        payload.CopyTo(datagram.AsSpan(8));
        return datagram;
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame with a payload.
    /// </summary>
    /// <param name="payload">The UDP payload.</param>
    /// <returns>The frame bytes, from <c>192.168.1.1:12345</c> to <c>192.168.1.2:53</c>.</returns>
    internal static byte[] BuildEthernetIpv4UdpFrame(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> srcIp = [192, 168, 1, 1];
        ReadOnlySpan<byte> dstIp = [192, 168, 1, 2];

        byte[] udp = BuildUdpDatagram(12345, 53, payload);
        byte[] ipv4 = BuildIpv4Packet(srcIp, dstIp, 17, udp); // 17 = UDP
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    #endregion

    #region Frames for specific dissectors

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP SYN frame with one option, the maximum segment size 1460, so the TCP dissector writes the
    /// field <c>tcp.options.mss</c> whose custom text is <c>Maximum Segment Size: 1460 bytes</c>.
    /// </summary>
    /// <returns>The frame bytes.</returns>
    internal static byte[] BuildEthernetIpv4TcpSynFrame()
    {
        // 24-byte TCP header: ports, sequence 1, acknowledgment 0, data offset 6 words, flag SYN, window 65535, checksum 0, no
        // urgent pointer, then the option kind 2 (MSS), length 4, value 1460.
        byte[] tcp = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, 1234);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), 80);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), 1);
        tcp[12] = 6 << 4;
        tcp[13] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(14), 0xFFFF);
        tcp[20] = 2;
        tcp[21] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(22), 1460);
        byte[] ipv4 = BuildIpv4Packet([192, 168, 1, 1], [192, 168, 1, 2], 6, tcp); // 6 = TCP
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    /// <summary>
    /// Builds an Ethernet frame that carries IPv4 in IPv4 (outer protocol 4) with a UDP datagram inside, so a packet has two
    /// <c>ip.src</c> values: the outer source first, the inner source second.
    /// </summary>
    /// <param name="outerSource">Outer source address (big-endian value, for example <c>0x0A000001</c> for 10.0.0.1).</param>
    /// <param name="innerSource">Inner source address.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] BuildIpInIpUdpFrame(uint outerSource, uint innerSource)
    {
        Span<byte> outer = stackalloc byte[4];
        Span<byte> inner = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(outer, outerSource);
        BinaryPrimitives.WriteUInt32BigEndian(inner, innerSource);
        byte[] udp = BuildUdpDatagram(40000, 53, [1, 2, 3, 4]);
        byte[] innerIpv4 = BuildIpv4Packet(inner, [192, 168, 0, 2], 17, udp); // 17 = UDP
        byte[] outerIpv4 = BuildIpv4Packet(outer, [10, 0, 0, 2], 4, innerIpv4); // 4 = IPv4 in IPv4
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, outerIpv4);
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame with a sequentially-filled payload.
    /// </summary>
    /// <param name="payloadSize">Number of payload bytes.</param>
    /// <returns>The frame bytes; payload byte <c>i</c> holds <c>i mod 256</c>.</returns>
    internal static byte[] BuildEthernetIpv4UdpFrame(int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++)
        {
            payload[i] = checked((byte)(i & 0xFF));
        }

        return BuildEthernetIpv4UdpFrame(payload);
    }

    /// <summary>
    /// Builds an Ethernet + IPv4 frame that carries one fragment of a datagram (RFC 791 section 3.2): the identification, the more
    /// fragments flag, and the fragment offset are set, the header checksum is zero. The IPv4 dissector holds a fragment until the
    /// datagram is complete, and the fragment that completes it binds the reassembled payload as an additional packet buffer.
    /// </summary>
    /// <param name="identification">The datagram's identification; all fragments of one datagram share it.</param>
    /// <param name="byteOffset">Offset of this fragment's payload in the datagram, in bytes; a multiple of 8.</param>
    /// <param name="moreFragments">Whether more fragments follow (false for the last one).</param>
    /// <param name="protocol">The IP protocol number of the datagram.</param>
    /// <param name="payload">This fragment's slice of the datagram payload.</param>
    /// <returns>The frame bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteOffset"/> is negative, not a multiple of 8, or above 65 528.</exception>
    internal static byte[] BuildEthernetIpv4FragmentFrame(
        ushort identification, int byteOffset, bool moreFragments, byte protocol, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, 65_528); // 13 offset bits in units of eight bytes
        if (byteOffset % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), byteOffset, "The fragment offset must be a multiple of 8.");
        }

        byte[] ipv4 = BuildIpv4Packet([192, 168, 7, 1], [192, 168, 7, 2], protocol, payload);
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(4), identification);

        // Flags and offset share one 16-bit field: the more-fragments flag is bit 13, the offset is in units of eight bytes.
        int moreFragmentsBit = moreFragments
            ? 0x2000
            : 0;
        ushort flagsAndOffset = checked((ushort)((byteOffset / 8) | moreFragmentsBit));
        BinaryPrimitives.WriteUInt16BigEndian(ipv4.AsSpan(6), flagsAndOffset);
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP frame with the flags PSH and ACK, a 20-byte TCP header, and a zero checksum. A client to server
    /// frame (<paramref name="fromServer"/> false) goes from <c>192.168.8.1</c> to <c>192.168.8.2</c>.
    /// </summary>
    /// <param name="clientPort">The client's TCP port; the server side is always port 80.</param>
    /// <param name="sequence">The sequence number of the first payload byte.</param>
    /// <param name="payload">The TCP payload.</param>
    /// <param name="fromServer">Whether the frame travels from the server (port 80) to the client.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] BuildEthernetIpv4TcpDataFrame(ushort clientPort, uint sequence, ReadOnlySpan<byte> payload, bool fromServer)
    {
        byte[] tcp = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(
            tcp,
            fromServer
                ? (ushort)80
                : clientPort);
        BinaryPrimitives.WriteUInt16BigEndian(
            tcp.AsSpan(2),
            fromServer
                ? clientPort
                : (ushort)80);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(8), 1);
        tcp[12] = 5 << 4; // data offset: five 32-bit words, no options
        tcp[13] = 0x18; // PSH | ACK
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(14), 0xFFFF);
        payload.CopyTo(tcp.AsSpan(20));

        ReadOnlySpan<byte> client = [192, 168, 8, 1];
        ReadOnlySpan<byte> server = [192, 168, 8, 2];
        byte[] ipv4 = fromServer
            ? BuildIpv4Packet(server, client, 6, tcp)
            : BuildIpv4Packet(client, server, 6, tcp);
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    /// <summary>
    /// Builds one masked WebSocket text frame (RFC 6455 section 5.2): FIN set, opcode 1, the mask bit set, the masking key, and the
    /// payload XORed with the key. The WebSocket dissector unmasks it and binds the unmasked payload as an additional packet buffer.
    /// </summary>
    /// <param name="payload">The plain payload; at most 125 bytes (the short length form).</param>
    /// <param name="maskingKey">The masking key; non-zero, so unmasking changes the bytes.</param>
    /// <returns>The frame bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than 125 bytes.</exception>
    internal static byte[] BuildMaskedWebSocketTextFrame(ReadOnlySpan<byte> payload, uint maskingKey)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 125);
        byte[] frame = new byte[2 + 4 + payload.Length];
        frame[0] = 0x81; // FIN, opcode 1 (text)
        frame[1] = checked((byte)(0x80 | payload.Length)); // mask bit and the 7-bit length
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), maskingKey);
        for (int i = 0; i < payload.Length; i++)
        {
            // The masking key byte for this position, most significant first; the mask keeps exactly one byte of the shifted key.
            byte keyByte = checked((byte)((maskingKey >> (8 * (3 - (i % 4)))) & 0xFF));
            frame[6 + i] = checked((byte)(payload[i] ^ keyByte));
        }

        return frame;
    }

    #endregion

    #region Plain Ethernet and DNS frames

    /// <summary>
    /// Builds a simple broadcast Ethernet frame with the given ethertype and payload.
    /// </summary>
    /// <param name="etherType">The ethertype, written big-endian.</param>
    /// <param name="payload">The bytes after the Ethernet header.</param>
    /// <returns>The frame bytes, from the test source MAC to the broadcast MAC.</returns>
    internal static byte[] BuildSimpleEthernetFrame(ushort etherType, ReadOnlySpan<byte> payload) =>
        BuildEthernetFrame(_BroadcastMac, _TestSrcMac, etherType, payload);

    /// <summary>
    /// Builds a minimal RFC 1035 DNS "A" query message: a 12-byte header (one question, no
    /// answers/authority/additional records) followed by a single question for
    /// <paramref name="domainName"/>.
    /// </summary>
    /// <param name="domainName">The dotted name to ask for, for example <c>example.test</c>.</param>
    /// <returns>The DNS message bytes.</returns>
    private static byte[] _BuildDnsQuery(string domainName)
    {
        string[] labels = domainName.Split('.');
        int qnameLength = 1; // trailing zero-length root label
        foreach (string label in labels)
        {
            qnameLength += 1 + Encoding.ASCII.GetByteCount(label);
        }

        byte[] message = new byte[12 + qnameLength + 4]; // header + QNAME + QTYPE(2) + QCLASS(2)
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), 0x1234); // Transaction ID
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x0100); // Standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1); // QDCOUNT
        // ANCOUNT, NSCOUNT, ARCOUNT already zero-initialized.

        int pos = 12;
        foreach (string label in labels)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            message[pos++] = checked((byte)labelBytes.Length);
            labelBytes.CopyTo(message, pos);
            pos += labelBytes.Length;
        }
        message[pos++] = 0; // root label

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(pos), 1); // QTYPE = A
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(pos + 2), 1); // QCLASS = IN

        return message;
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame carrying a single-question DNS "A" query
    /// for <paramref name="domainName"/>, addressed to UDP port 53 so the shared protocol stack
    /// dispatches it to <c>DnsProtocol</c> and populates the <c>dns.qry.name</c> string field.
    /// </summary>
    /// <param name="domainName">The dotted name to ask for, for example <c>example.test</c>.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] BuildDnsQueryFrame(string domainName)
    {
        byte[] dnsMessage = _BuildDnsQuery(domainName);
        ReadOnlySpan<byte> srcIp = [192, 168, 1, 1];
        ReadOnlySpan<byte> dstIp = [192, 168, 1, 53];

        byte[] udp = BuildUdpDatagram(53000, 53, dnsMessage);
        byte[] ipv4 = BuildIpv4Packet(srcIp, dstIp, 17, udp); // 17 = UDP
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    #endregion
}
