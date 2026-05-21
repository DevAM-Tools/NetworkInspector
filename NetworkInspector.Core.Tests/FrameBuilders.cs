// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Utility methods for constructing synthetic network frames used in tests.
/// </summary>
internal static class FrameBuilders
{
    /// <summary>
    /// Generates a static UDP-over-IPv4-over-Ethernet frame of the specified total size.
    /// </summary>
    /// <param name="totalSize">Total frame size in bytes (minimum 42).</param>
    /// <returns>A byte array containing the complete frame.</returns>
    internal static byte[] GenerateStaticUdpFrame(int totalSize = 512)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        const int minSize = ethSize + ipv4Size + udpSize;

        totalSize = Math.Max(totalSize, minSize);
        byte[] frame = new byte[totalSize];

        int payloadSize = totalSize - minSize;
        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payloadSize);
        ushort udpLen = (ushort)(udpSize + payloadSize);

        // Ethernet header (14 bytes)
        // Dst MAC: 00:11:22:33:44:55
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        // Src MAC: 66:77:88:99:AA:BB
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        // EtherType: IPv4 (0x0800)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header (20 bytes)
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45; // Version 4, IHL 5 (20 bytes)
        frame[ipOffset + 1] = 0x00; // DSCP 0, ECN 0
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x1234); // Identification
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000); // Don't Fragment
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 17; // Protocol: UDP
        // Checksum at offset 10-11: computed below
        // Source: 192.168.1.1
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 1;
        // Destination: 192.168.1.2
        frame[ipOffset + 16] = 192;
        frame[ipOffset + 17] = 168;
        frame[ipOffset + 18] = 1;
        frame[ipOffset + 19] = 2;

        // IPv4 header checksum
        ushort checksum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), checksum);

        // UDP header (8 bytes)
        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 12345); // Src port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 53); // Dst port: DNS
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6), 0); // Checksum (optional for IPv4)

        // Fill payload with repeating pattern
        for (int i = 0; i < payloadSize; i++)
        {
            frame[minSize + i] = (byte)(i & 0xFF);
        }

        return frame;
    }

    /// <summary>
    /// Generates a static UDP-over-IPv6-over-Ethernet frame of the specified total size.
    /// </summary>
    /// <param name="totalSize">Total frame size in bytes (minimum 62).</param>
    /// <returns>A byte array containing the complete frame.</returns>
    internal static byte[] GenerateStaticUdpIpv6Frame(int totalSize = 512)
    {
        const int ethSize = 14;
        const int ipv6Size = 40;
        const int udpSize = 8;
        const int minSize = ethSize + ipv6Size + udpSize;

        totalSize = Math.Max(totalSize, minSize);
        byte[] frame = new byte[totalSize];

        int payloadSize = totalSize - minSize;
        ushort udpLen = (ushort)(udpSize + payloadSize);

        // Ethernet header (14 bytes)
        // Dst MAC: 00:11:22:33:44:55
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        // Src MAC: 66:77:88:99:AA:BB
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        // EtherType: IPv6 (0x86DD)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);

        // IPv6 header (40 bytes)
        int ipOffset = ethSize;
        // Version (6) + Traffic Class (0) + Flow Label (0x12345)
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(ipOffset), 0x60012345);
        // Payload Length
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), udpLen);
        // Next Header: UDP (17)
        frame[ipOffset + 6] = 17;
        // Hop Limit: 64
        frame[ipOffset + 7] = 64;
        // Source: 2001:db8::1
        frame[ipOffset + 8] = 0x20;
        frame[ipOffset + 9] = 0x01;
        frame[ipOffset + 10] = 0x0d;
        frame[ipOffset + 11] = 0xb8;
        // bytes 12-22 are zero (already zeroed)
        frame[ipOffset + 23] = 0x01;
        // Destination: 2001:db8::2
        frame[ipOffset + 24] = 0x20;
        frame[ipOffset + 25] = 0x01;
        frame[ipOffset + 26] = 0x0d;
        frame[ipOffset + 27] = 0xb8;
        // bytes 28-38 are zero
        frame[ipOffset + 39] = 0x02;

        // UDP header (8 bytes)
        int udpOffset = ipOffset + ipv6Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 12345); // Src port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 53); // Dst port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6), 0); // Checksum

        // Fill payload with repeating pattern
        for (int i = 0; i < payloadSize; i++)
        {
            frame[minSize + i] = (byte)(i & 0xFF);
        }

        return frame;
    }

    /// <summary>Calculates the IPv4 header checksum (one's complement of one's complement sum).</summary>
    private static ushort CalculateIpv4Checksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (int i = 0; i < header.Length - 1; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        }
        if ((header.Length & 1) != 0)
        {
            sum += (uint)(header[^1] << 8);
        }
        // Fold carry
        while (sum > 0xFFFF)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    // === ARP Frame Builders ===

    /// <summary>
    /// Generates an ARP request frame (Ethernet + ARP, 42 bytes total).
    /// </summary>
    internal static byte[] GenerateArpRequestFrame(
        byte[] senderMac, byte[] senderIp,
        byte[] targetMac, byte[] targetIp)
        => GenerateArpFrame(1, senderMac, senderIp, targetMac, targetIp);

    /// <summary>
    /// Generates an ARP reply frame (Ethernet + ARP, 42 bytes total).
    /// </summary>
    internal static byte[] GenerateArpReplyFrame(
        byte[] senderMac, byte[] senderIp,
        byte[] targetMac, byte[] targetIp)
        => GenerateArpFrame(2, senderMac, senderIp, targetMac, targetIp);

    /// <summary>
    /// Generates an ARP frame with the specified opcode.
    /// Ethernet header (14) + ARP (28) = 42 bytes.
    /// </summary>
    private static byte[] GenerateArpFrame(
        ushort opcode, byte[] senderMac, byte[] senderIp,
        byte[] targetMac, byte[] targetIp)
    {
        byte[] frame = new byte[42];

        // Ethernet header: dst=broadcast, src=senderMac, type=ARP (0x0806)
        frame[0] = 0xFF;
        frame[1] = 0xFF;
        frame[2] = 0xFF;
        frame[3] = 0xFF;
        frame[4] = 0xFF;
        frame[5] = 0xFF;
        Array.Copy(senderMac, 0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0806);

        // ARP header (28 bytes)
        int arpOffset = 14;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(arpOffset), 1);     // Hardware type: Ethernet
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(arpOffset + 2), 0x0800); // Protocol type: IPv4
        frame[arpOffset + 4] = 6;  // Hardware size
        frame[arpOffset + 5] = 4;  // Protocol size
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(arpOffset + 6), opcode);
        Array.Copy(senderMac, 0, frame, arpOffset + 8, 6);
        Array.Copy(senderIp, 0, frame, arpOffset + 14, 4);
        Array.Copy(targetMac, 0, frame, arpOffset + 18, 6);
        Array.Copy(targetIp, 0, frame, arpOffset + 24, 4);

        return frame;
    }

    // === ICMP Frame Builders ===

    /// <summary>
    /// Generates an ICMP Echo Request frame (Ethernet + IPv4 + ICMP, variable size).
    /// </summary>
    internal static byte[] GenerateIcmpEchoRequestFrame(
        ushort identifier, ushort sequence, byte[] payload)
        => GenerateIcmpFrame(8, 0, identifier, sequence, payload);

    /// <summary>
    /// Generates an ICMP Echo Reply frame (Ethernet + IPv4 + ICMP, variable size).
    /// </summary>
    internal static byte[] GenerateIcmpEchoReplyFrame(
        ushort identifier, ushort sequence, byte[] payload)
        => GenerateIcmpFrame(0, 0, identifier, sequence, payload);

    /// <summary>
    /// Generates an ICMP Destination Unreachable frame (Ethernet + IPv4 + ICMP).
    /// </summary>
    internal static byte[] GenerateIcmpDestUnreachFrame(byte code)
        => GenerateIcmpFrame(3, code, 0, 0, new byte[28]); // 28 bytes = original IP header stub

    /// <summary>
    /// Generates a complete ICMP frame with Ethernet + IPv4 headers.
    /// </summary>
    private static byte[] GenerateIcmpFrame(
        byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int icmpHeaderSize = 8;

        int icmpLen = icmpHeaderSize + payload.Length;
        int totalSize = ethSize + ipv4Size + icmpLen;
        byte[] frame = new byte[totalSize];

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45; // Version 4, IHL 5
        ushort ipTotalLen = (ushort)(ipv4Size + icmpLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000); // DF
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 1;  // Protocol: ICMP
        // src: 192.168.1.1, dst: 192.168.1.2
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 1;
        frame[ipOffset + 16] = 192;
        frame[ipOffset + 17] = 168;
        frame[ipOffset + 18] = 1;
        frame[ipOffset + 19] = 2;
        ushort ipChecksum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipChecksum);

        // ICMP header + payload
        int icmpOffset = ipOffset + ipv4Size;
        frame[icmpOffset] = type;
        frame[icmpOffset + 1] = code;
        // Checksum zeroed initially (bytes 2-3)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 4), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 6), sequence);
        Array.Copy(payload, 0, frame, icmpOffset + icmpHeaderSize, payload.Length);

        // Compute ICMP checksum over entire ICMP message
        ushort icmpChecksum = CalculateIpv4Checksum(frame.AsSpan(icmpOffset, icmpLen));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 2), icmpChecksum);

        return frame;
    }

    // === ICMPv6 Frame Builders ===

    /// <summary>
    /// Generates an ICMPv6 Echo Request frame (Ethernet + IPv6 + ICMPv6).
    /// Note: ICMPv6 checksum requires IPv6 pseudo-header computation.
    /// </summary>
    internal static byte[] GenerateIcmpv6EchoRequestFrame(
        ushort identifier, ushort sequence, byte[] payload)
        => GenerateIcmpv6Frame(128, 0, identifier, sequence, payload);

    /// <summary>
    /// Generates an ICMPv6 Echo Reply frame (Ethernet + IPv6 + ICMPv6).
    /// </summary>
    internal static byte[] GenerateIcmpv6EchoReplyFrame(
        ushort identifier, ushort sequence, byte[] payload)
        => GenerateIcmpv6Frame(129, 0, identifier, sequence, payload);

    /// <summary>
    /// Generates a complete ICMPv6 frame with Ethernet + IPv6 headers.
    /// Uses IPv6 pseudo-header for checksum computation.
    /// </summary>
    private static byte[] GenerateIcmpv6Frame(
        byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
    {
        const int ethSize = 14;
        const int ipv6Size = 40;
        const int icmpHeaderSize = 8;

        int icmpLen = icmpHeaderSize + payload.Length;
        int totalSize = ethSize + ipv6Size + icmpLen;
        byte[] frame = new byte[totalSize];

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);

        // IPv6 header
        int ipOffset = ethSize;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(ipOffset), 0x60000000); // Version 6
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), (ushort)icmpLen); // Payload length
        frame[ipOffset + 6] = 58;  // Next Header: ICMPv6
        frame[ipOffset + 7] = 64;  // Hop Limit
        // Source: 2001:db8::1
        frame[ipOffset + 8] = 0x20;
        frame[ipOffset + 9] = 0x01;
        frame[ipOffset + 10] = 0x0d;
        frame[ipOffset + 11] = 0xb8;
        frame[ipOffset + 23] = 0x01;
        // Destination: 2001:db8::2
        frame[ipOffset + 24] = 0x20;
        frame[ipOffset + 25] = 0x01;
        frame[ipOffset + 26] = 0x0d;
        frame[ipOffset + 27] = 0xb8;
        frame[ipOffset + 39] = 0x02;

        // ICMPv6 header + payload
        int icmpOffset = ipOffset + ipv6Size;
        frame[icmpOffset] = type;
        frame[icmpOffset + 1] = code;
        // Checksum zeroed initially
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 4), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 6), sequence);
        Array.Copy(payload, 0, frame, icmpOffset + icmpHeaderSize, payload.Length);

        // Compute ICMPv6 checksum (with IPv6 pseudo-header)
        // Pseudo-header: src(16) + dst(16) + length(4) + next-header(4) = 40 bytes
        uint pseudoSum = 0;
        // Sum source address (16 bytes = 8 u16 words)
        for (int i = 0; i < 16; i += 2)
        {
            pseudoSum += BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(ipOffset + 8 + i));
        }
        // Sum destination address (16 bytes)
        for (int i = 0; i < 16; i += 2)
        {
            pseudoSum += BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(ipOffset + 24 + i));
        }
        // Upper-layer packet length
        pseudoSum += (uint)(icmpLen >> 16);
        pseudoSum += (uint)(icmpLen & 0xFFFF);
        // Next header (58 for ICMPv6)
        pseudoSum += 58;

        // Sum ICMPv6 header + payload
        ReadOnlySpan<byte> icmpSpan = frame.AsSpan(icmpOffset, icmpLen);
        for (int i = 0; i < icmpSpan.Length - 1; i += 2)
        {
            pseudoSum += BinaryPrimitives.ReadUInt16BigEndian(icmpSpan[i..]);
        }
        if ((icmpSpan.Length & 1) != 0)
        {
            pseudoSum += (uint)(icmpSpan[^1] << 8);
        }

        // Fold and finalize
        while (pseudoSum > 0xFFFF)
        {
            pseudoSum = (pseudoSum & 0xFFFF) + (pseudoSum >> 16);
        }
        ushort icmpv6Checksum = (ushort)~pseudoSum;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(icmpOffset + 2), icmpv6Checksum);

        return frame;
    }

    // === TCP Frame Builders ===

    /// <summary>TCP flag bit constants used by frame builders.</summary>
    private const byte TcpFin = 0x01;
    private const byte TcpSyn = 0x02;
    private const byte TcpRst = 0x04;
    private const byte TcpPsh = 0x08;
    private const byte TcpAck = 0x10;

    /// <summary>
    /// Generates a TCP SYN frame (Ethernet + IPv4 + TCP, 54 bytes, no payload).
    /// </summary>
    internal static byte[] GenerateTcpSynFrame(
        ushort srcPort = 12345, ushort dstPort = 80,
        uint seq = 1000)
        => GenerateTcpFrame(srcPort, dstPort, seq, 0, TcpSyn, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Generates a TCP SYN-ACK frame (Ethernet + IPv4 + TCP, 54 bytes, no payload).
    /// </summary>
    internal static byte[] GenerateTcpSynAckFrame(
        ushort srcPort = 80, ushort dstPort = 12345,
        uint seq = 2000, uint ack = 1001)
        => GenerateTcpFrame(srcPort, dstPort, seq, ack, TcpSyn | TcpAck, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Generates a TCP ACK frame (Ethernet + IPv4 + TCP, 54 bytes, no payload).
    /// </summary>
    internal static byte[] GenerateTcpAckFrame(
        ushort srcPort = 12345, ushort dstPort = 80,
        uint seq = 1001, uint ack = 2001)
        => GenerateTcpFrame(srcPort, dstPort, seq, ack, TcpAck, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Generates a TCP data frame with PSH+ACK flags (Ethernet + IPv4 + TCP + payload).
    /// </summary>
    internal static byte[] GenerateTcpDataFrame(
        ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte[] payload)
        => GenerateTcpFrame(srcPort, dstPort, seq, ack, TcpPsh | TcpAck, payload);

    /// <summary>
    /// Generates a TCP FIN-ACK frame (Ethernet + IPv4 + TCP, 54 bytes).
    /// </summary>
    internal static byte[] GenerateTcpFinAckFrame(
        ushort srcPort = 12345, ushort dstPort = 80,
        uint seq = 1001, uint ack = 2001)
        => GenerateTcpFrame(srcPort, dstPort, seq, ack, TcpFin | TcpAck, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Generates a TCP RST frame (Ethernet + IPv4 + TCP, 54 bytes).
    /// </summary>
    internal static byte[] GenerateTcpRstFrame(
        ushort srcPort = 12345, ushort dstPort = 80,
        uint seq = 1001)
        => GenerateTcpFrame(srcPort, dstPort, seq, 0, TcpRst, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Generates a complete TCP frame with Ethernet + IPv4 headers.
    /// IPv4 src=192.168.1.1, dst=192.168.1.2. TCP checksum is computed correctly.
    /// </summary>
    private static byte[] GenerateTcpFrame(
        ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte flags,
        ReadOnlySpan<byte> payload)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int tcpHeaderSize = 20;

        int tcpLen = tcpHeaderSize + payload.Length;
        int totalSize = ethSize + ipv4Size + tcpLen;
        byte[] frame = new byte[totalSize];

        // Ethernet header (dst=00:11:22:33:44:55, src=66:77:88:99:AA:BB, type=IPv4)
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45; // Version 4, IHL 5
        ushort ipTotalLen = (ushort)(ipv4Size + tcpLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x5678); // Identification
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000); // DF
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 6;  // Protocol: TCP
        // src: 192.168.1.1, dst: 192.168.1.2
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 1;
        frame[ipOffset + 16] = 192;
        frame[ipOffset + 17] = 168;
        frame[ipOffset + 18] = 1;
        frame[ipOffset + 19] = 2;
        ushort ipChecksum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipChecksum);

        // TCP header (20 bytes, data offset = 5)
        int tcpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 2), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 8), ack);
        frame[tcpOffset + 12] = 0x50; // Data offset = 5 (20 bytes), reserved = 0
        frame[tcpOffset + 13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 14), 65535); // Window size
        // Checksum at offset 16-17: computed below
        // Urgent pointer at offset 18-19: 0

        // Copy payload
        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(tcpOffset + tcpHeaderSize));
        }

        // Compute TCP checksum with IPv4 pseudo-header
        ushort tcpChecksum = CalculateTcpChecksum(
            frame.AsSpan(ipOffset + 12, 4),  // src IP
            frame.AsSpan(ipOffset + 16, 4),  // dst IP
            frame.AsSpan(tcpOffset, tcpLen));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 16), tcpChecksum);

        return frame;
    }

    /// <summary>
    /// Calculates the TCP checksum including IPv4 pseudo-header.
    /// </summary>
    private static ushort CalculateTcpChecksum(
        ReadOnlySpan<byte> srcIp, ReadOnlySpan<byte> dstIp,
        ReadOnlySpan<byte> tcpSegment)
    {
        uint sum = 0;

        // Pseudo-header: src IP (4 bytes)
        for (int i = 0; i < 4; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(srcIp[i..]);
        }
        // Pseudo-header: dst IP (4 bytes)
        for (int i = 0; i < 4; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(dstIp[i..]);
        }
        // Pseudo-header: zero + protocol (TCP=6)
        sum += 6;
        // Pseudo-header: TCP length
        sum += (uint)tcpSegment.Length;

        // TCP segment (header + payload)
        for (int i = 0; i < tcpSegment.Length - 1; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(tcpSegment[i..]);
        }
        if ((tcpSegment.Length & 1) != 0)
        {
            sum += (uint)(tcpSegment[^1] << 8);
        }

        // Fold carry
        while (sum > 0xFFFF)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    // === SLL Frame Builders ===

    /// <summary>
    /// Generates an SLL v1 frame (16-byte SLL header + IPv4 + UDP payload).
    /// Link type 113, used for Linux cooked captures.
    /// </summary>
    internal static byte[] GenerateSllFrame(
        ushort packetType = 0,  // Unicast
        ushort etherType = 0x0800) // IPv4
    {
        const int sllSize = 16;
        const int ipv4Size = 20;
        const int udpSize = 8;
        const int payloadSize = 10;
        int totalSize = sllSize + ipv4Size + udpSize + payloadSize;
        byte[] frame = new byte[totalSize];

        // SLL v1 header (16 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), packetType);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 1);    // ARPHRD: Ethernet
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);    // Address length
        // Source address (8 bytes, first 6 used): AA:BB:CC:DD:EE:FF
        frame[6] = 0xAA;
        frame[7] = 0xBB;
        frame[8] = 0xCC;
        frame[9] = 0xDD;
        frame[10] = 0xEE;
        frame[11] = 0xFF;
        // Remaining 2 bytes of address field are zero
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), etherType);

        // IPv4 + UDP payload (same as GenerateStaticUdpFrame's payload section)
        int ipOffset = sllSize;
        frame[ipOffset] = 0x45;
        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000);
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 17; // UDP
        frame[ipOffset + 12] = 10;
        frame[ipOffset + 13] = 0;
        frame[ipOffset + 14] = 0;
        frame[ipOffset + 15] = 1;
        frame[ipOffset + 16] = 10;
        frame[ipOffset + 17] = 0;
        frame[ipOffset + 18] = 0;
        frame[ipOffset + 19] = 2;
        ushort ipCsum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum);

        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 5000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 5001);
        ushort udpLen = (ushort)(udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);
        // UDP checksum = 0 (optional for IPv4)

        // Fill payload
        for (int i = 0; i < payloadSize; i++)
        {
            frame[sllSize + ipv4Size + udpSize + i] = (byte)(0xA0 + i);
        }

        return frame;
    }

    /// <summary>
    /// Generates an SLL v2 frame (20-byte SLL2 header + IPv4 + UDP payload).
    /// Link type 276, used for Linux cooked captures v2.
    /// </summary>
    internal static byte[] GenerateSll2Frame(
        ushort etherType = 0x0800, // IPv4
        byte packetType = 0,       // Unicast
        uint interfaceIndex = 1)
    {
        const int sll2Size = 20;
        const int ipv4Size = 20;
        const int udpSize = 8;
        const int payloadSize = 10;
        int totalSize = sll2Size + ipv4Size + udpSize + payloadSize;
        byte[] frame = new byte[totalSize];

        // SLL v2 header (20 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), etherType);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);           // Reserved
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), interfaceIndex);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), 1);           // ARPHRD: Ethernet
        frame[10] = packetType;
        frame[11] = 6; // Address length
        // Source address (8 bytes, first 6 used): 11:22:33:44:55:66
        frame[12] = 0x11;
        frame[13] = 0x22;
        frame[14] = 0x33;
        frame[15] = 0x44;
        frame[16] = 0x55;
        frame[17] = 0x66;

        // IPv4 + UDP (same pattern)
        int ipOffset = sll2Size;
        frame[ipOffset] = 0x45;
        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000);
        frame[ipOffset + 8] = 64;
        frame[ipOffset + 9] = 17;
        frame[ipOffset + 12] = 10;
        frame[ipOffset + 13] = 0;
        frame[ipOffset + 14] = 0;
        frame[ipOffset + 15] = 1;
        frame[ipOffset + 16] = 10;
        frame[ipOffset + 17] = 0;
        frame[ipOffset + 18] = 0;
        frame[ipOffset + 19] = 2;
        ushort ipCsum2 = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum2);

        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 5000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 5001);
        ushort udpLen2 = (ushort)(udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen2);

        for (int i = 0; i < payloadSize; i++)
        {
            frame[sll2Size + ipv4Size + udpSize + i] = (byte)(0xB0 + i);
        }

        return frame;
    }

    /// <summary>
    /// Generates an IEEE 802.3 frame with LLC SNAP header encapsulating IPv4+UDP.
    /// Ethernet length field (instead of EtherType), then LLC (AA:AA:03) + SNAP (00:00:00 + 0x0800).
    /// </summary>
    internal static byte[] GenerateLlcSnapFrame()
    {
        const int ethSize = 14;
        const int llcSnapSize = 8; // LLC(3) + SNAP(5)
        const int ipv4Size = 20;
        const int udpSize = 8;
        const int payloadSize = 10;
        int innerLen = llcSnapSize + ipv4Size + udpSize + payloadSize;
        int totalSize = ethSize + innerLen;
        byte[] frame = new byte[totalSize];

        // Ethernet header with length field (not EtherType)
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        // Length field (must be <= 1500 to trigger 802.3)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)innerLen);

        // LLC header: DSAP=0xAA (SNAP), SSAP=0xAA, Control=0x03 (Unnumbered)
        int llcOffset = ethSize;
        frame[llcOffset] = 0xAA;     // DSAP
        frame[llcOffset + 1] = 0xAA; // SSAP
        frame[llcOffset + 2] = 0x03; // Control: UI

        // SNAP header: OUI=00:00:00, Type=0x0800 (IPv4)
        // OUI bytes (llcOffset+3..+5) already zero from array init
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(llcOffset + 6), 0x0800);

        // IPv4 + UDP (same pattern)
        int ipOffset = llcOffset + llcSnapSize;
        frame[ipOffset] = 0x45;
        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 6), 0x4000);
        frame[ipOffset + 8] = 64;
        frame[ipOffset + 9] = 17;
        frame[ipOffset + 12] = 10;
        frame[ipOffset + 13] = 0;
        frame[ipOffset + 14] = 0;
        frame[ipOffset + 15] = 1;
        frame[ipOffset + 16] = 10;
        frame[ipOffset + 17] = 0;
        frame[ipOffset + 18] = 0;
        frame[ipOffset + 19] = 2;
        ushort ipCsum3 = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum3);

        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 5000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 5001);
        ushort udpLen3 = (ushort)(udpSize + payloadSize);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen3);

        for (int i = 0; i < payloadSize; i++)
        {
            frame[ipOffset + ipv4Size + udpSize + i] = (byte)(0xC0 + i);
        }

        return frame;
    }

    // ─── DNS Frame Builders ──────────────────────────────────────────────────

    /// <summary>
    /// Generates a DNS query frame (Ethernet + IPv4 + UDP + DNS).
    /// Creates a standard A record query for the given domain name.
    /// </summary>
    internal static byte[] GenerateDnsQueryFrame(
        string queryName = "www.example.com",
        ushort transactionId = 0x1234,
        ushort queryType = 1, // A record
        ushort queryClass = 1) // IN
    {
        // Encode the DNS query name as labels
        byte[] dnsNameBytes = EncodeDnsName(queryName);
        int dnsPayloadLen = 12 + dnsNameBytes.Length + 4; // header + name + qtype(2) + qclass(2)

        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        int totalSize = ethSize + ipv4Size + udpSize + dnsPayloadLen;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + dnsPayloadLen);
        ushort udpLen = (ushort)(udpSize + dnsPayloadLen);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x5678);
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 17; // UDP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 8;
        frame[ipOffset + 17] = 8;
        frame[ipOffset + 18] = 8;
        frame[ipOffset + 19] = 8;
        ushort ipCsum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum);

        // UDP header
        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 54321); // src port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 53); // dst port (DNS)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);

        // DNS header (12 bytes)
        int dnsOffset = udpOffset + udpSize;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset), transactionId);
        // Flags: 0x0100 = standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset + 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset + 4), 1); // QDCOUNT = 1
        // ANCOUNT, NSCOUNT, ARCOUNT = 0 (already zeroed)

        // DNS question: name + qtype + qclass
        int qOffset = dnsOffset + 12;
        dnsNameBytes.CopyTo(frame.AsSpan(qOffset));
        qOffset += dnsNameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(qOffset), queryType);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(qOffset + 2), queryClass);

        return frame;
    }

    /// <summary>
    /// Generates a DNS response frame with a single A record answer.
    /// </summary>
    internal static byte[] GenerateDnsResponseFrame(
        string queryName = "www.example.com",
        ushort transactionId = 0x1234,
        byte ip1 = 93, byte ip2 = 184, byte ip3 = 216, byte ip4 = 34,
        uint ttl = 300)
    {
        byte[] dnsNameBytes = EncodeDnsName(queryName);
        // DNS payload: header(12) + question(name + 4) + answer(name-ptr(2) + type(2) + class(2) + ttl(4) + rdlen(2) + rdata(4))
        int questionLen = dnsNameBytes.Length + 4;
        int answerLen = 2 + 2 + 2 + 4 + 2 + 4; // name pointer + type + class + ttl + rdlen + A record
        int dnsPayloadLen = 12 + questionLen + answerLen;

        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        int totalSize = ethSize + ipv4Size + udpSize + dnsPayloadLen;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + dnsPayloadLen);
        ushort udpLen = (ushort)(udpSize + dnsPayloadLen);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header — response comes from 8.8.8.8 to 192.168.1.100
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 4), 0x5679);
        frame[ipOffset + 8] = 64;
        frame[ipOffset + 9] = 17; // UDP
        frame[ipOffset + 12] = 8;
        frame[ipOffset + 13] = 8;
        frame[ipOffset + 14] = 8;
        frame[ipOffset + 15] = 8;
        frame[ipOffset + 16] = 192;
        frame[ipOffset + 17] = 168;
        frame[ipOffset + 18] = 1;
        frame[ipOffset + 19] = 100;
        ushort ipCsum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum);

        // UDP header — source 53, dest arbitrary high port
        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);

        // DNS header
        int dnsOffset = udpOffset + udpSize;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset), transactionId);
        // Flags: 0x8180 = response, recursion desired + recursion available, no error
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset + 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset + 4), 1); // QDCOUNT = 1
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(dnsOffset + 6), 1); // ANCOUNT = 1

        // Question section
        int qOffset = dnsOffset + 12;
        dnsNameBytes.CopyTo(frame.AsSpan(qOffset));
        qOffset += dnsNameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(qOffset), 1); // A
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(qOffset + 2), 1); // IN
        qOffset += 4;

        // Answer section — use name compression pointer to question name
        int answerOffset = qOffset;
        // Name: pointer to offset 12 within DNS data (dnsOffset + 12 - dnsOffset = offset 12 in DNS payload)
        int namePtrTarget = 12; // Offset within the DNS packet where the name starts
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(answerOffset), (ushort)(0xC000 | namePtrTarget));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(answerOffset + 2), 1); // Type A
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(answerOffset + 4), 1); // Class IN
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(answerOffset + 6), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(answerOffset + 10), 4); // RDLENGTH = 4
        frame[answerOffset + 12] = ip1;
        frame[answerOffset + 13] = ip2;
        frame[answerOffset + 14] = ip3;
        frame[answerOffset + 15] = ip4;

        return frame;
    }

    /// <summary>Encodes a domain name as DNS labels (e.g. "www.example.com" → 3www7example3com0).</summary>
    private static byte[] EncodeDnsName(string name)
    {
        string[] labels = name.Split('.');
        int totalLen = 1; // null terminator
        foreach (string label in labels)
        {
            totalLen += 1 + label.Length; // length byte + label chars
        }

        byte[] result = new byte[totalLen];
        int pos = 0;
        foreach (string label in labels)
        {
            result[pos++] = (byte)label.Length;
            foreach (char c in label)
            {
                result[pos++] = (byte)c;
            }
        }
        result[pos] = 0; // null terminator

        return result;
    }

    // ─── TLS Frame Builders ──────────────────────────────────────────────────

    /// <summary>
    /// Generates a TLS Client Hello frame (Ethernet + IPv4 + TCP + TLS Record + Handshake).
    /// Creates a minimal Client Hello with specifiable SNI and cipher suites.
    /// </summary>
    internal static byte[] GenerateTlsClientHelloFrame(
        string serverName = "example.com",
        ushort[] cipherSuites = null!,
        ushort tlsVersion = 0x0303) // TLS 1.2
    {
        cipherSuites ??= [0x1301, 0x1302, 0x1303, 0xC02F, 0xC030]; // TLS 1.3 + common ECDHE

        // Build the Client Hello body
        byte[] sniExtData = BuildSniExtension(serverName);
        byte[] clientHelloBody = BuildClientHelloBody(cipherSuites, tlsVersion, sniExtData);

        // Handshake header: type(1) + length(3)
        int handshakeLen = clientHelloBody.Length;
        byte[] handshakeMsg = new byte[4 + handshakeLen];
        handshakeMsg[0] = 1; // Client Hello
        handshakeMsg[1] = (byte)(handshakeLen >> 16);
        handshakeMsg[2] = (byte)(handshakeLen >> 8);
        handshakeMsg[3] = (byte)handshakeLen;
        clientHelloBody.CopyTo(handshakeMsg.AsSpan(4));

        // TLS Record: content_type(1) + version(2) + length(2) + data
        int recordPayloadLen = handshakeMsg.Length;
        byte[] tlsRecord = new byte[5 + recordPayloadLen];
        tlsRecord[0] = 22; // Handshake
        BinaryPrimitives.WriteUInt16BigEndian(tlsRecord.AsSpan(1), 0x0301); // Record version TLS 1.0 (for compatibility)
        BinaryPrimitives.WriteUInt16BigEndian(tlsRecord.AsSpan(3), (ushort)recordPayloadLen);
        handshakeMsg.CopyTo(tlsRecord.AsSpan(5));

        // Wrap in Ethernet + IPv4 + TCP frame
        return WrapInTcpFrame(tlsRecord, srcPort: 54321, dstPort: 443);
    }

    /// <summary>
    /// Generates a TLS Server Hello frame.
    /// </summary>
    internal static byte[] GenerateTlsServerHelloFrame(
        ushort selectedCipherSuite = 0x1301,
        ushort tlsVersion = 0x0303)
    {
        // Server Hello body: version(2) + random(32) + session_id_len(1) + session_id(32) + cipher(2) + comp(1)
        byte[] body = new byte[2 + 32 + 1 + 32 + 2 + 1];
        BinaryPrimitives.WriteUInt16BigEndian(body, tlsVersion);
        // Random: fill with 0x01..0x20
        for (int i = 0; i < 32; i++)
        {
            body[2 + i] = (byte)(i + 1);
        }
        body[34] = 32; // session ID length
        // Session ID: fill with 0xAA
        body.AsSpan(35, 32).Fill(0xAA);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(67), selectedCipherSuite);
        body[69] = 0; // null compression

        // Handshake header
        byte[] handshakeMsg = new byte[4 + body.Length];
        handshakeMsg[0] = 2; // Server Hello
        handshakeMsg[1] = (byte)(body.Length >> 16);
        handshakeMsg[2] = (byte)(body.Length >> 8);
        handshakeMsg[3] = (byte)body.Length;
        body.CopyTo(handshakeMsg.AsSpan(4));

        // TLS Record
        byte[] tlsRecord = new byte[5 + handshakeMsg.Length];
        tlsRecord[0] = 22;
        BinaryPrimitives.WriteUInt16BigEndian(tlsRecord.AsSpan(1), 0x0303);
        BinaryPrimitives.WriteUInt16BigEndian(tlsRecord.AsSpan(3), (ushort)handshakeMsg.Length);
        handshakeMsg.CopyTo(tlsRecord.AsSpan(5));

        return WrapInTcpFrame(tlsRecord, srcPort: 443, dstPort: 54321);
    }

    /// <summary>Builds a TLS Client Hello body (after handshake header).</summary>
    private static byte[] BuildClientHelloBody(
        ushort[] cipherSuites, ushort version, byte[] sniExtData)
    {
        int cipherSuitesLen = cipherSuites.Length * 2;
        // Version(2) + Random(32) + SessionIdLen(1) + CipherSuitesLen(2) + CipherSuites +
        // CompMethodsLen(1) + CompMethod(1) + ExtensionsLen(2) + SNI ext
        int totalExtLen = sniExtData.Length;
        int bodyLen = 2 + 32 + 1 + 2 + cipherSuitesLen + 1 + 1 + 2 + totalExtLen;
        byte[] body = new byte[bodyLen];
        int pos = 0;

        // Version
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(pos), version);
        pos += 2;

        // Random (32 bytes of 0x42)
        body.AsSpan(pos, 32).Fill(0x42);
        pos += 32;

        // Session ID: 0 length
        body[pos++] = 0;

        // Cipher Suites
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(pos), (ushort)cipherSuitesLen);
        pos += 2;
        foreach (ushort suite in cipherSuites)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(pos), suite);
            pos += 2;
        }

        // Compression methods: 1 method (null)
        body[pos++] = 1;
        body[pos++] = 0;

        // Extensions
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(pos), (ushort)totalExtLen);
        pos += 2;
        sniExtData.CopyTo(body.AsSpan(pos));

        return body;
    }

    /// <summary>
    /// Builds a Server Name Indication extension.
    /// Format: type(2) + len(2) + server_name_list_len(2) + name_type(1) + name_len(2) + name(N)
    /// </summary>
    private static byte[] BuildSniExtension(string serverName)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(serverName);
        int sniListLen = 1 + 2 + nameBytes.Length; // name_type + name_len + name
        int extDataLen = 2 + sniListLen; // server_name_list_len + list
        int totalLen = 4 + extDataLen; // type + ext_len + data
        byte[] result = new byte[totalLen];

        int pos = 0;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), 0); // type = server_name
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)extDataLen);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)sniListLen);
        pos += 2;
        result[pos++] = 0; // host_name
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)nameBytes.Length);
        pos += 2;
        nameBytes.CopyTo(result.AsSpan(pos));

        return result;
    }

    /// <summary>Wraps a payload in Ethernet + IPv4 + TCP frame.</summary>
    private static byte[] WrapInTcpFrame(byte[] tlsPayload, ushort srcPort, ushort dstPort)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int tcpSize = 20; // No options
        int totalSize = ethSize + ipv4Size + tcpSize + tlsPayload.Length;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + tcpSize + tlsPayload.Length);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 6;  // TCP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 93;
        frame[ipOffset + 17] = 184;
        frame[ipOffset + 18] = 216;
        frame[ipOffset + 19] = 34;
        ushort ipCsum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum);

        // TCP header (minimal, no options)
        int tcpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 2), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 4), 1000); // seq
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 8), 2000); // ack
        frame[tcpOffset + 12] = 0x50; // data offset = 5 (20 bytes)
        frame[tcpOffset + 13] = 0x18; // PSH+ACK
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 14), 65535); // window

        // TLS payload
        tlsPayload.CopyTo(frame.AsSpan(tcpOffset + tcpSize));

        return frame;
    }

    // ========================================================================
    // CAN (SocketCAN) Frame Builders
    // ========================================================================

    /// <summary>
    /// Generates a classic CAN frame in SocketCAN format (link type 227).
    /// The returned data is the raw SocketCAN payload (no Ethernet/IP headers).
    /// </summary>
    /// <param name="canId">CAN identifier (11-bit standard or 29-bit extended).</param>
    /// <param name="payload">CAN data payload (0-8 bytes).</param>
    /// <param name="isExtended">Whether to set the Extended Frame Format flag.</param>
    /// <param name="isRtr">Whether to set the Remote Transmission Request flag.</param>
    internal static byte[] GenerateCanFrame(
        uint canId = 0x123,
        byte[]? payload = null,
        bool isExtended = false,
        bool isRtr = false)
    {
        payload ??= [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        byte dlc = (byte)Math.Min(payload.Length, 8);

        // SocketCAN frame: 8-byte header + data (padded to 8 bytes)
        byte[] frame = new byte[8 + 8]; // header(8) + data(max 8)

        // Build CAN ID with flags (little-endian in SocketCAN)
        uint rawId = canId;
        if (isExtended)
        {
            rawId |= 0x80000000; // EFF flag
        }
        if (isRtr)
        {
            rawId |= 0x40000000; // RTR flag
        }
        // LINKTYPE_CAN_SOCKETCAN (DLT 227): CAN-ID/flags word in network byte order (big-endian).
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), rawId);

        // DLC
        frame[4] = dlc;
        // Byte 5: flags (0 for classic CAN)
        // Bytes 6-7: reserved

        // Data
        payload.AsSpan(0, dlc).CopyTo(frame.AsSpan(8));

        return frame;
    }

    /// <summary>
    /// Generates a CAN FD frame in SocketCAN format (link type 227).
    /// </summary>
    /// <param name="canId">CAN identifier (29-bit extended).</param>
    /// <param name="payload">CAN FD data payload (0-64 bytes).</param>
    /// <param name="brs">Bit Rate Switch flag.</param>
    /// <param name="esi">Error State Indicator flag.</param>
    internal static byte[] GenerateCanFdFrame(
        uint canId = 0x1ABCDEF,
        byte[]? payload = null,
        bool brs = true,
        bool esi = false)
    {
        payload ??= new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        // SocketCAN FD frame: 8-byte header + data (up to 64 bytes).
        // SocketCAN's struct canfd_frame stores the actual byte length in
        // byte 4 (no DLC encoding) and the flag bits per Linux spec are
        // BRS=0x01, ESI=0x02, FDF=0x04 in byte 5.
        int dataLen = payload.Length;
        byte[] frame = new byte[8 + dataLen];

        // CAN ID with EFF flag in network byte order (LINKTYPE_CAN_SOCKETCAN).
        uint rawId = canId | 0x80000000; // Extended format
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), rawId);

        // Byte 4: actual payload byte count.
        frame[4] = (byte)dataLen;

        // Byte 5: FD flags.
        byte flags = 0x04; // FDF (FD Format) — Linux SocketCAN spec
        if (brs)
        {
            flags |= 0x01;
        }
        if (esi)
        {
            flags |= 0x02;
        }
        frame[5] = flags;

        // Data
        payload.AsSpan().CopyTo(frame.AsSpan(8));

        return frame;
    }

    // ========================================================================
    // CAN XL Frame Builders
    // ========================================================================

    /// <summary>
    /// Generates a CAN XL frame in SocketCAN format (link type 227).
    /// The returned data is the raw SocketCAN CAN XL payload (no Ethernet/IP headers).
    /// </summary>
    /// <param name="priority">11-bit CAN XL priority (0-2047).</param>
    /// <param name="vcid">8-bit Virtual CAN Network ID (0-255).</param>
    /// <param name="sduType">SDU (Service Data Unit) type.</param>
    /// <param name="acceptanceField">32-bit acceptance field for filtering/routing.</param>
    /// <param name="payload">CAN XL data payload (1-2048 bytes).</param>
    /// <param name="sec">Simple Extended Content flag.</param>
    /// <param name="rrs">Remote Request Substitution flag.</param>
    internal static byte[] GenerateCanXlFrame(
        uint priority = 5,
        byte vcid = 0x1A,
        byte sduType = 0x03,
        uint acceptanceField = 0x12345678,
        byte[]? payload = null,
        bool sec = false,
        bool rrs = false)
    {
        payload ??= [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        int payloadLen = Math.Min(payload.Length, 2048);

        // CAN XL: 12-byte header + variable payload
        byte[] frame = new byte[12 + payloadLen];

        // Build priority/VCID field (BE u32 for LINKTYPE_CAN_SOCKETCAN): bits 0-10 = priority, bits 16-23 = VCID.
        // Wireshark: "The priority/VCID field is big-endian in LINKTYPE_CAN_SOCKETCAN captures, for historical reasons."
        uint rawPrio = (priority & 0x7FF) | ((uint)vcid << 16);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), rawPrio);

        // Flags: XLF (0x80) always set, plus optional SEC(0x01) and RRS(0x02)
        byte flags = 0x80;
        if (sec)
        {
            flags |= 0x01;
        }
        if (rrs)
        {
            flags |= 0x02;
        }
        frame[4] = flags;

        // SDU Type
        frame[5] = sduType;

        // Payload length (LE u16)
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)payloadLen);

        // Acceptance field (LE u32)
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), acceptanceField);

        // Data payload
        payload.AsSpan(0, payloadLen).CopyTo(frame.AsSpan(12));

        return frame;
    }

    // ========================================================================
    // SOME/IP Frame Builders
    // ========================================================================

    /// <summary>
    /// Generates a SOME/IP message wrapped in Ethernet + IPv4 + UDP frame.
    /// </summary>
    /// <param name="serviceId">SOME/IP Service ID.</param>
    /// <param name="methodId">SOME/IP Method ID.</param>
    /// <param name="payload">Optional payload after SOME/IP header.</param>
    /// <param name="messageType">SOME/IP message type (default: REQUEST = 0x00).</param>
    /// <param name="returnCode">SOME/IP return code (default: E_OK = 0x00).</param>
    internal static byte[] GenerateSomeIpFrame(
        ushort serviceId = 0x0123,
        ushort methodId = 0x4567,
        byte[]? payload = null,
        byte messageType = 0x00,
        byte returnCode = 0x00)
    {
        payload ??= [0x01, 0x02, 0x03, 0x04];

        // SOME/IP header: 16 bytes + payload
        int someipLen = 16 + payload.Length;
        // SOME/IP Length field = 8 (clientId..returnCode) + payload
        uint lengthField = (uint)(8 + payload.Length);

        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        int totalSize = ethSize + ipv4Size + udpSize + someipLen;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + someipLen);
        ushort udpLen = (ushort)(udpSize + someipLen);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 17; // UDP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 10;
        frame[ipOffset + 17] = 0;
        frame[ipOffset + 18] = 0;
        frame[ipOffset + 19] = 1;
        ushort ipCsum = CalculateIpv4Checksum(frame.AsSpan(ipOffset, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), ipCsum);

        // UDP header (port 30490 = SOME/IP default)
        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), 54321); // src port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), 30490); // dst port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);

        // SOME/IP header (16 bytes, big-endian)
        int sOffset = udpOffset + udpSize;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(sOffset), serviceId);       // Service ID
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(sOffset + 2), methodId);    // Method ID
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(sOffset + 4), lengthField); // Length
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(sOffset + 8), 0x0001);      // Client ID
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(sOffset + 10), 0x0001);     // Session ID
        frame[sOffset + 12] = 0x01; // Protocol Version
        frame[sOffset + 13] = 0x01; // Interface Version
        frame[sOffset + 14] = messageType;
        frame[sOffset + 15] = returnCode;

        // Payload
        payload.CopyTo(frame.AsSpan(sOffset + 16));

        return frame;
    }

    /// <summary>
    /// Generates a SOME/IP-TP message with TP header after the SOME/IP header.
    /// The message type has bit 5 (0x20) set for TP flag.
    /// TP header is 4 bytes: upper 28 bits = offset, bit 0 = more segments.
    /// </summary>
    /// <param name="serviceId">SOME/IP Service ID.</param>
    /// <param name="methodId">SOME/IP Method ID.</param>
    /// <param name="byteOffset">TP byte offset (must be multiple of 16).</param>
    /// <param name="moreSegments">TP more segments flag.</param>
    /// <param name="payload">Payload after TP header.</param>
    /// <param name="baseMessageType">Base message type before TP flag is ORed in.</param>
    internal static byte[] GenerateSomeIpTpFrame(
        ushort serviceId = 0x0123,
        ushort methodId = 0x4567,
        uint byteOffset = 0,
        bool moreSegments = true,
        byte[]? payload = null,
        byte baseMessageType = 0x00)
    {
        payload ??= [0x10, 0x20, 0x30, 0x40];

        // TP header: 4 bytes
        const int tpHeaderSize = 4;

        // Build TP header raw value: upper 28 bits = offset, bit 0 = more
        uint tpRaw = (byteOffset & 0xFFFFFFF0u) | (moreSegments ? 1u : 0u);

        // Combine TP header + payload as the SOME/IP payload region
        byte[] tpPayload = new byte[tpHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(tpPayload.AsSpan(0), tpRaw);
        payload.CopyTo(tpPayload.AsSpan(tpHeaderSize));

        // Message type with TP flag (bit 5 = 0x20)
        byte msgType = (byte)(baseMessageType | 0x20);

        return GenerateSomeIpFrame(serviceId, methodId, tpPayload, msgType);
    }

    /// <summary>
    /// Generates a SOME/IP-SD frame (message ID = 0xFFFF8100, msg type = NOTIFICATION).
    /// SD payload: flags(1) + reserved(3) + entries_length(4) + entries + options_length(4) + options.
    /// </summary>
    /// <param name="flags">SD flags byte (default: reboot + unicast = 0xC0).</param>
    /// <param name="entries">Raw SD entries (each 16 bytes). Null for default single OfferService.</param>
    /// <param name="options">Raw SD options. Null for default IPv4 endpoint option.</param>
    internal static byte[] GenerateSomeIpSdFrame(
        byte flags = 0xC0,
        byte[]? entries = null,
        byte[]? options = null)
    {
        // Default: single OfferService entry
        entries ??= BuildSdOfferEntry(0x0001, 0x0001, majorVer: 1, ttl: 3, minorVer: 0);

        // Default: single IPv4 endpoint option (9-byte payload)
        options ??= BuildSdIpv4EndpointOption(192, 168, 1, 100, proto: 17, port: 30490);

        // SD payload layout: flags(1) + reserved(3) + entries_length(4) + entries
        //                  + options_length(4) + options
        int sdLen = 1 + 3 + 4 + entries.Length + 4 + options.Length;
        byte[] sdPayload = new byte[sdLen];
        int pos = 0;

        // Flags + reserved
        sdPayload[pos++] = flags;
        sdPayload[pos++] = 0x00; // reserved
        sdPayload[pos++] = 0x00;
        sdPayload[pos++] = 0x00;

        // Entries length + entries
        BinaryPrimitives.WriteUInt32BigEndian(sdPayload.AsSpan(pos), (uint)entries.Length);
        pos += 4;
        entries.CopyTo(sdPayload.AsSpan(pos));
        pos += entries.Length;

        // Options length + options
        BinaryPrimitives.WriteUInt32BigEndian(sdPayload.AsSpan(pos), (uint)options.Length);
        pos += 4;
        options.CopyTo(sdPayload.AsSpan(pos));

        // SD message: service = 0xFFFF, method = 0x8100, msg type = NOTIFICATION (0x02)
        return GenerateSomeIpFrame(
            serviceId: 0xFFFF,
            methodId: 0x8100,
            payload: sdPayload,
            messageType: 0x02);
    }

    /// <summary>Builds a 16-byte SD OfferService entry.</summary>
    internal static byte[] BuildSdOfferEntry(
        ushort serviceId, ushort instanceId, byte majorVer, uint ttl, uint minorVer)
    {
        byte[] entry = new byte[16];
        entry[0] = 0x01; // OfferService type
        entry[1] = 0; // index1
        entry[2] = 0; // index2
        entry[3] = 0x10; // numOpt1=1, numOpt2=0
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(4), serviceId);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), instanceId);
        entry[8] = majorVer;
        // TTL: 24-bit big-endian in bytes 9-11
        entry[9] = (byte)((ttl >> 16) & 0xFF);
        entry[10] = (byte)((ttl >> 8) & 0xFF);
        entry[11] = (byte)(ttl & 0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(12), minorVer);
        return entry;
    }

    /// <summary>Builds a 12-byte SD IPv4 Endpoint option (length=9, type=0x04).</summary>
    internal static byte[] BuildSdIpv4EndpointOption(
        byte ip1, byte ip2, byte ip3, byte ip4, byte proto, ushort port)
    {
        // Wire format: length(2) + type(1) + reserved(1) + ipv4(4) + reserved(1) + proto(1) + port(2) = 12 bytes
        byte[] option = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(option.AsSpan(0), 9); // length = 9 (after type byte)
        option[2] = 0x04; // IPv4 Endpoint type
        option[3] = 0x00; // reserved
        option[4] = ip1;
        option[5] = ip2;
        option[6] = ip3;
        option[7] = ip4;
        option[8] = 0x00; // reserved
        option[9] = proto;
        BinaryPrimitives.WriteUInt16BigEndian(option.AsSpan(10), port);
        return option;
    }

    // ========================================================================
    // FlexRay Frame Builders
    // ========================================================================

    /// <summary>
    /// Generates a FlexRay frame in DLT_FLEXRAY format (link type 210).
    /// Raw FlexRay frame data without Ethernet/IP encapsulation.
    /// Uses the LINKTYPE_FLEXRAY format per tcpdump.org specification.
    /// </summary>
    /// <param name="frameId">11-bit FlexRay slot/frame ID (0-2047).</param>
    /// <param name="cycle">6-bit cycle count (0-63).</param>
    /// <param name="payload">FlexRay payload data (0-254 bytes).</param>
    /// <param name="channelB">True for Channel B, false for Channel A.</param>
    /// <param name="nfi">Null Frame Indicator (true = NOT null frame).</param>
    /// <param name="sfi">Sync Frame Indicator.</param>
    /// <param name="stfi">Startup Frame Indicator.</param>
    /// <param name="ppi">Payload Preamble Indicator.</param>
    /// <param name="headerCrc">11-bit Header CRC value.</param>
    /// <param name="errorFlags">Error flags byte (bits: [4]FCRC [3]HCRC [2]FES [1]COD [0]TSS).</param>
    internal static byte[] GenerateFlexRayFrame(
        ushort frameId = 42,
        byte cycle = 3,
        byte[]? payload = null,
        bool channelB = false,
        bool nfi = true,
        bool sfi = false,
        bool stfi = false,
        bool ppi = false,
        ushort headerCrc = 0,
        byte errorFlags = 0)
    {
        payload ??= new byte[32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        // Payload length in 16-bit words (rounded up)
        byte payloadWords = (byte)((payload.Length + 1) / 2);
        int actualPayloadSize = payloadWords * 2;

        // LINKTYPE_FLEXRAY: 7-byte header (2 measurement + 5 frame header) + payload
        byte[] frame = new byte[7 + actualPayloadSize];

        // Byte 0: Measurement Header — [7] CH | [6:0] Type Index (0x01 = Frame)
        frame[0] = (byte)((channelB ? 0x80 : 0x00) | 0x01);

        // Byte 1: Error Flags
        frame[1] = errorFlags;

        // Byte 2: [7] Reserved=0 | [6] PPI | [5] NFI | [4] SFI | [3] STFI | [2:0] FID[10:8]
        frame[2] = (byte)(
            (ppi ? 0x40 : 0) |
            (nfi ? 0x20 : 0) |
            (sfi ? 0x10 : 0) |
            (stfi ? 0x08 : 0) |
            ((frameId >> 8) & 0x07));

        // Byte 3: FID[7:0]
        frame[3] = (byte)(frameId & 0xFF);

        // Byte 4: [7:1] Payload Length (7 bits) | [0] HCRC[10]
        frame[4] = (byte)((payloadWords << 1) | ((headerCrc >> 10) & 0x01));

        // Byte 5: HCRC[9:2]
        frame[5] = (byte)((headerCrc >> 2) & 0xFF);

        // Byte 6: [7:6] HCRC[1:0] | [5:0] Cycle Count (6 bits)
        frame[6] = (byte)(((headerCrc & 0x03) << 6) | (cycle & 0x3F));

        // Payload (starts at offset 7)
        payload.CopyTo(frame.AsSpan(7));

        return frame;
    }

    // ========================================================================
    // LIN Frame Builders
    // ========================================================================

    /// <summary>
    /// Generates a LIN frame in DLT_LIN format (link type 212).
    /// Raw LIN frame data without Ethernet/IP encapsulation.
    /// </summary>
    /// <param name="pid">Protected ID (6-bit ID + 2-bit parity).</param>
    /// <param name="payload">LIN data payload (0-8 bytes).</param>
    /// <param name="checksum">Checksum byte.</param>
    internal static byte[] GenerateLinFrame(
        byte pid = 0x3C,
        byte[]? payload = null,
        byte checksum = 0xAB)
    {
        payload ??= [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte dataLen = (byte)Math.Min(payload.Length, 15); // max 15 — fits in 4-bit nibble

        // DLT_LIN (per Wireshark packet-lin.h): 8-byte header + data
        // Byte 0: message format revision (1)
        // Bytes 1-3: reserved
        // Byte 4: payload length[7:4] | msg type[3:2] | checksum type[1:0]
        //         msgType=0 (Frame), checksumType=2 (enhanced)
        // Byte 5: PID
        // Byte 6: checksum
        // Byte 7: error flags
        byte[] frame = new byte[8 + dataLen];

        frame[0] = 0x01;                                          // msgFormatRev
        frame[1] = 0x00;                                          // reserved
        frame[2] = 0x00;                                          // reserved
        frame[3] = 0x00;                                          // reserved
        frame[4] = (byte)((dataLen << 4) | (0 << 2) | 0x02);     // payloadLen | msgType=Frame | checksumType=Enhanced
        frame[5] = pid;                                           // PID
        frame[6] = checksum;                                      // checksum byte
        frame[7] = 0x00;                                          // error flags

        payload.AsSpan(0, dataLen).CopyTo(frame.AsSpan(8));

        return frame;
    }
}
