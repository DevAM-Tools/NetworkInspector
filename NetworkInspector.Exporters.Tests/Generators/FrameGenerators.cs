// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building raw network frame data for exporter tests.
/// Produces standard Ethernet, IPv4/UDP frames as byte arrays.
/// </summary>
internal static class FrameGenerators
{
    /// <summary>Standard broadcast destination MAC.</summary>
    private static readonly byte[] _BroadcastMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Standard test source MAC.</summary>
    private static readonly byte[] _TestSrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Standard test destination MAC.</summary>
    private static readonly byte[] _TestDstMac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

    /// <summary>
    /// Builds a simple Ethernet II frame: dst(6) + src(6) + ethertype(2 BE) + payload.
    /// </summary>
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
    internal static byte[] BuildIpv4Packet(
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        byte protocol,
        ReadOnlySpan<byte> payload)
    {
        int totalLength = 20 + payload.Length;
        byte[] packet = new byte[totalLength];
        packet[0] = 0x45; // Version 4, IHL 5
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)totalLength);
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
    internal static byte[] BuildUdpDatagram(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        int length = 8 + payload.Length;
        byte[] datagram = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)length);
        payload.CopyTo(datagram.AsSpan(8));
        return datagram;
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame with a payload.
    /// </summary>
    internal static byte[] BuildEthernetIpv4UdpFrame(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> srcIp = [192, 168, 1, 1];
        ReadOnlySpan<byte> dstIp = [192, 168, 1, 2];

        byte[] udp = BuildUdpDatagram(12345, 53, payload);
        byte[] ipv4 = BuildIpv4Packet(srcIp, dstIp, 17, udp); // 17 = UDP
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame with a sequentially-filled payload.
    /// </summary>
    /// <param name="payloadSize">Number of payload bytes.</param>
    internal static byte[] BuildEthernetIpv4UdpFrame(int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        return BuildEthernetIpv4UdpFrame(payload);
    }

    /// <summary>
    /// Builds a simple broadcast Ethernet frame with the given ethertype and payload.
    /// </summary>
    internal static byte[] BuildSimpleEthernetFrame(ushort etherType, ReadOnlySpan<byte> payload) =>
        BuildEthernetFrame(_BroadcastMac, _TestSrcMac, etherType, payload);

    /// <summary>
    /// Builds a minimal RFC 1035 DNS "A" query message: a 12-byte header (one question, no
    /// answers/authority/additional records) followed by a single question for
    /// <paramref name="domainName"/>.
    /// </summary>
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
            message[pos++] = (byte)labelBytes.Length;
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
    internal static byte[] BuildDnsQueryFrame(string domainName)
    {
        byte[] dnsMessage = _BuildDnsQuery(domainName);
        ReadOnlySpan<byte> srcIp = [192, 168, 1, 1];
        ReadOnlySpan<byte> dstIp = [192, 168, 1, 53];

        byte[] udp = BuildUdpDatagram(53000, 53, dnsMessage);
        byte[] ipv4 = BuildIpv4Packet(srcIp, dstIp, 17, udp); // 17 = UDP
        return BuildEthernetFrame(_TestDstMac, _TestSrcMac, 0x0800, ipv4);
    }
}
