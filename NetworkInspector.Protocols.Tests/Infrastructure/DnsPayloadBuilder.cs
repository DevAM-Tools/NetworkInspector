// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Builds DNS application-layer payloads for protocol tests.
/// Pure byte-level helper — the IP/UDP/TCP frame is composed by the caller via
/// <see cref="FrameStack"/>. There is no FrameBuilder layer for DNS because DNS
/// is application data, and tests typically need control over the exact RR layout.
/// <para>
/// All methods produce wire-format bytes per RFC 1035 (header + questions + RRs).
/// Names are encoded uncompressed unless <see cref="EncodeNamePointer"/> is used
/// explicitly to test pointer / compression handling.
/// </para>
/// <para>Thread safety: stateless static methods.</para>
/// </summary>
internal static class DnsPayloadBuilder
{
    internal const int HeaderSize = 12; // bytes

    /// <summary>QTYPE / RR TYPE constants (subset).</summary>
    internal static class Type
    {
        internal const ushort A = 1;
        internal const ushort NS = 2;
        internal const ushort CNAME = 5;
        internal const ushort SOA = 6;
        internal const ushort PTR = 12;
        internal const ushort MX = 15;
        internal const ushort TXT = 16;
        internal const ushort AAAA = 28;
        internal const ushort SRV = 33;
        internal const ushort OPT = 41;
    }

    internal const ushort ClassIn = 1;

    /// <summary>
    /// Writes the 12-byte DNS header to <paramref name="dst"/>.
    /// </summary>
    internal static void WriteHeader(
        Span<byte> dst,
        ushort id, ushort flags,
        ushort qdCount, ushort anCount, ushort nsCount, ushort arCount)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..4], flags);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], qdCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..8], anCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..10], nsCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..12], arCount);
    }

    /// <summary>
    /// Encodes a domain name as a sequence of length-prefixed labels terminated by 0x00.
    /// Returns the encoded bytes.
    /// </summary>
    internal static byte[] EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == ".")
        {
            return [0x00];
        }
        // Strip trailing dot
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }
        string[] labels = name.Split('.');
        // total = sum(1 + len) + 1 terminator
        int total = 1;
        foreach (string lbl in labels)
        {
            total += 1 + Encoding.ASCII.GetByteCount(lbl);
        }
        byte[] buf = new byte[total];
        int idx = 0;
        foreach (string lbl in labels)
        {
            int len = Encoding.ASCII.GetByteCount(lbl);
            if (len > 63)
            {
                throw new ArgumentException($"DNS label too long: '{lbl}'");
            }
            buf[idx++] = (byte)len;
            Encoding.ASCII.GetBytes(lbl, buf.AsSpan(idx, len));
            idx += len;
        }
        buf[idx] = 0x00; // terminator
        return buf;
    }

    /// <summary>
    /// Encodes a 2-byte name pointer (RFC 1035 §4.1.4). The top 2 bits are set.
    /// </summary>
    internal static byte[] EncodeNamePointer(ushort offsetInPacket)
    {
        byte[] ptr = new byte[2];
        ptr[0] = (byte)(0xC0 | ((offsetInPacket >> 8) & 0x3F));
        ptr[1] = (byte)(offsetInPacket & 0xFF);
        return ptr;
    }

    /// <summary>
    /// Builds a DNS query payload (single question, no RRs).
    /// Flags default to 0x0100 (standard query, recursion desired).
    /// </summary>
    internal static byte[] BuildQuery(ushort id, string queryName, ushort qtype, ushort flags = 0x0100)
    {
        byte[] name = EncodeName(queryName);
        byte[] payload = new byte[HeaderSize + name.Length + 4];
        WriteHeader(payload, id, flags, qdCount: 1, anCount: 0, nsCount: 0, arCount: 0);
        name.CopyTo(payload.AsSpan(HeaderSize));
        int off = HeaderSize + name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), qtype);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off + 2, 2), ClassIn);
        return payload;
    }

    /// <summary>
    /// Builds a DNS response with one question and one answer RR. The answer name
    /// is encoded as a pointer back to the question (offset 12 in the packet),
    /// matching the typical wire layout produced by real resolvers.
    /// </summary>
    internal static byte[] BuildResponseSingleRR(
        ushort id, string queryName, ushort qtype,
        ReadOnlySpan<byte> rdata, uint ttlSeconds, ushort flags = 0x8180)
    {
        byte[] name = EncodeName(queryName);
        byte[] ptr = EncodeNamePointer(HeaderSize); // points back to QNAME
        // header(12) + QNAME + 4 (qtype/qclass) + 2 (name ptr) + 2(type) + 2(class) + 4(TTL) + 2(RDLENGTH) + RDATA
        int total = HeaderSize + name.Length + 4 + 2 + 2 + 2 + 4 + 2 + rdata.Length;
        byte[] payload = new byte[total];
        WriteHeader(payload, id, flags, qdCount: 1, anCount: 1, nsCount: 0, arCount: 0);
        int off = HeaderSize;
        name.CopyTo(payload.AsSpan(off));
        off += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), qtype);
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), ClassIn);
        off += 2;
        // Answer
        ptr.CopyTo(payload.AsSpan(off));
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), qtype);
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), ClassIn);
        off += 2;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(off, 4), ttlSeconds);
        off += 4;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), (ushort)rdata.Length);
        off += 2;
        rdata.CopyTo(payload.AsSpan(off));
        return payload;
    }

    /// <summary>
    /// Builds the RDATA for an MX record: 2-byte preference + uncompressed mail exchange name.
    /// </summary>
    internal static byte[] BuildMxRdata(ushort preference, string exchange)
    {
        byte[] name = EncodeName(exchange);
        byte[] rdata = new byte[2 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0, 2), preference);
        name.CopyTo(rdata.AsSpan(2));
        return rdata;
    }

    /// <summary>
    /// Builds the RDATA for an SRV record: priority(2) + weight(2) + port(2) + target name.
    /// </summary>
    internal static byte[] BuildSrvRdata(ushort priority, ushort weight, ushort port, string target)
    {
        byte[] name = EncodeName(target);
        byte[] rdata = new byte[6 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0, 2), priority);
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(2, 2), weight);
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(4, 2), port);
        name.CopyTo(rdata.AsSpan(6));
        return rdata;
    }

    /// <summary>
    /// Builds the RDATA for a TXT record: one or more &lt;length&gt;&lt;chars&gt; segments.
    /// </summary>
    internal static byte[] BuildTxtRdata(params string[] strings)
    {
        int total = 0;
        foreach (string s in strings)
        {
            total += 1 + Encoding.ASCII.GetByteCount(s);
        }
        byte[] rdata = new byte[total];
        int off = 0;
        foreach (string s in strings)
        {
            int len = Encoding.ASCII.GetByteCount(s);
            rdata[off++] = (byte)len;
            Encoding.ASCII.GetBytes(s, rdata.AsSpan(off, len));
            off += len;
        }
        return rdata;
    }

    /// <summary>
    /// Wraps a DNS UDP payload into an Ethernet+IPv4+UDP frame using FrameStack.
    /// </summary>
    internal static byte[] WrapUdp(ReadOnlySpan<byte> dnsPayload, ushort srcPort = 5353, ushort dstPort = 53)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dnsPayload);
    }

    /// <summary>
    /// Wraps a DNS TCP payload (prepends the 2-byte length prefix per RFC 1035 §4.2.2)
    /// into an Ethernet+IPv4+TCP frame.
    /// </summary>
    internal static byte[] WrapTcp(ReadOnlySpan<byte> dnsPayload, ushort srcPort = 12345, ushort dstPort = 53)
    {
        // 2-byte length prefix + payload
        byte[] tcpPayload = new byte[2 + dnsPayload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcpPayload.AsSpan(0, 2), (ushort)dnsPayload.Length);
        dnsPayload.CopyTo(tcpPayload.AsSpan(2));

        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(srcPort, dstPort, seqNum: 1, ackNum: 0, flags: 0x18 /* PSH+ACK */);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(tcpPayload);
    }
}
