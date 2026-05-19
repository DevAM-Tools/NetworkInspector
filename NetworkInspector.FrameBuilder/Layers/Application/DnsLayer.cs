// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// DNS application-layer for the <see cref="FrameStack"/> API.
/// Produces a complete DNS wire-format message (RFC 1035) from static factory methods.
/// </summary>
/// <remarks>
/// <para>DNS wire format (RFC 1035 §4):</para>
/// <code>
/// Bytes  0-11: DNS header (12 bytes)
///   Bytes 0-1:  ID
///   Bytes 2-3:  Flags
///   Bytes 4-5:  QDCOUNT
///   Bytes 6-7:  ANCOUNT
///   Bytes 8-9:  NSCOUNT
///   Bytes 10-11:ARCOUNT
/// Bytes 12+:   Questions + Resource Records
/// </code>
/// <para>Use the static factory methods <see cref="BuildQuery"/> and
/// <see cref="BuildResponseSingleRR"/> to create instances, or construct
/// one directly with pre-built bytes.</para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier, no length auto-patching.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct DnsLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    /// <summary>DNS header size in bytes (RFC 1035 §4.1.1).</summary>
    public const int DnsHeaderSize = 12;

    /// <summary>IN (Internet) class code.</summary>
    public const ushort ClassIn = 1;

    private readonly ReadOnlyMemory<byte> _Message;

    /// <summary>Creates a <see cref="DnsLayer"/> from pre-built DNS wire-format bytes.</summary>
    /// <param name="message">Complete DNS message bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DnsLayer(ReadOnlyMemory<byte> message)
    {
        _Message = message;
    }

    /// <summary>
    /// Builds a DNS standard query (single question, flags 0x0100 = RD set).
    /// </summary>
    /// <param name="id">DNS transaction identifier.</param>
    /// <param name="queryName">Fully-qualified domain name (e.g. "example.com").</param>
    /// <param name="qtype">Query type (see <see cref="DnsType"/>).</param>
    /// <param name="flags">DNS flags (default 0x0100 = standard query with recursion desired).</param>
    public static DnsLayer BuildQuery(ushort id, string queryName, ushort qtype, ushort flags = 0x0100)
    {
        byte[] name = EncodeName(queryName);
        byte[] payload = new byte[DnsHeaderSize + name.Length + 4];
        WriteHeader(payload, id, flags, qdCount: 1, anCount: 0, nsCount: 0, arCount: 0);
        name.CopyTo(payload.AsSpan(DnsHeaderSize));
        int off = DnsHeaderSize + name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), qtype);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off + 2, 2), ClassIn);
        return new DnsLayer(payload);
    }

    /// <summary>
    /// Builds a DNS response with one question and one answer RR.
    /// The answer name is encoded as a compression pointer (offset 12) back to the question name.
    /// </summary>
    /// <param name="id">DNS transaction identifier.</param>
    /// <param name="queryName">Fully-qualified domain name.</param>
    /// <param name="rrType">Resource record type (see <see cref="DnsType"/>).</param>
    /// <param name="rdata">Resource data bytes.</param>
    /// <param name="ttlSeconds">Time-to-live in seconds.</param>
    /// <param name="flags">DNS flags (default 0x8180 = standard response, recursion available).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="rdata"/> length exceeds 65535 bytes (RDLENGTH is a 16-bit field).
    /// </exception>
    public static DnsLayer BuildResponseSingleRR(
        ushort id,
        string queryName,
        ushort rrType,
        ReadOnlySpan<byte> rdata,
        uint ttlSeconds,
        ushort flags = 0x8180)
    {
        if (rdata.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rdata), rdata.Length,
                "DNS RDATA must not exceed 65535 bytes.");
        }
        byte[] name = EncodeName(queryName);
        byte[] ptr = EncodeNamePointer(DnsHeaderSize); // pointer back to QNAME
        // header(12) + QNAME + 4(qtype+qclass) + 2(name ptr) + 2(type) + 2(class) + 4(TTL) + 2(RDLENGTH) + RDATA
        int total = DnsHeaderSize + name.Length + 4 + 2 + 2 + 2 + 4 + 2 + rdata.Length;
        byte[] payload = new byte[total];
        WriteHeader(payload, id, flags, qdCount: 1, anCount: 1, nsCount: 0, arCount: 0);
        int off = DnsHeaderSize;
        name.CopyTo(payload.AsSpan(off));
        off += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), rrType);
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), ClassIn);
        off += 2;
        // Answer RR
        ptr.CopyTo(payload.AsSpan(off));
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), rrType);
        off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), ClassIn);
        off += 2;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(off, 4), ttlSeconds);
        off += 4;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), (ushort)rdata.Length);
        off += 2;
        rdata.CopyTo(payload.AsSpan(off));
        return new DnsLayer(payload);
    }

    /// <summary>
    /// Encodes a domain name as a sequence of length-prefixed labels terminated by 0x00 (RFC 1035 §3.1).
    /// </summary>
    /// <param name="name">Domain name (e.g. "example.com" or "example.com."). Must contain only ASCII characters.</param>
    /// <exception cref="ArgumentException">Thrown if any label exceeds 63 octets or if the name contains non-ASCII characters.</exception>
    public static byte[] EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == ".")
        {
            return [0x00];
        }
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }
        string[] labels = name.Split('.');
        int total = 1; // terminator
        foreach (string lbl in labels)
        {
            foreach (char c in lbl)
            {
                if (c > 127)
                {
                    throw new ArgumentException(
                        $"DNS name contains non-ASCII character U+{(int)c:X4} in label '{lbl}'. " +
                        "Use Punycode (IDNA) encoding before calling EncodeName.", nameof(name));
                }
            }
            total += 1 + Encoding.ASCII.GetByteCount(lbl);
        }
        byte[] buf = new byte[total];
        int idx = 0;
        foreach (string lbl in labels)
        {
            int len = Encoding.ASCII.GetByteCount(lbl);
            if (len > 63)
            {
                throw new ArgumentException($"DNS label too long: '{lbl}'", nameof(name));
            }
            buf[idx++] = (byte)len;
            Encoding.ASCII.GetBytes(lbl, buf.AsSpan(idx, len));
            idx += len;
        }
        buf[idx] = 0x00;
        return buf;
    }

    /// <summary>
    /// Encodes a 2-byte compression pointer (top 2 bits set per RFC 1035 §4.1.4).
    /// </summary>
    /// <param name="offsetInPacket">Byte offset within the DNS message (must not exceed 0x3FFF = 16383).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="offsetInPacket"/> exceeds 0x3FFF.
    /// </exception>
    public static byte[] EncodeNamePointer(ushort offsetInPacket)
    {
        if (offsetInPacket > 0x3FFF)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetInPacket), offsetInPacket,
                "DNS compression pointer offset must not exceed 0x3FFF (16383).");
        }
        byte[] ptr = new byte[2];
        ptr[0] = (byte)(0xC0 | ((offsetInPacket >> 8) & 0x3F));
        ptr[1] = (byte)(offsetInPacket & 0xFF);
        return ptr;
    }

    /// <summary>Builds the RDATA for an MX record: 2-byte preference + uncompressed mail-exchange name.</summary>
    public static byte[] BuildMxRdata(ushort preference, string exchange)
    {
        byte[] name = EncodeName(exchange);
        byte[] rdata = new byte[2 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0, 2), preference);
        name.CopyTo(rdata.AsSpan(2));
        return rdata;
    }

    /// <summary>Builds the RDATA for an SRV record: priority(2) + weight(2) + port(2) + target name.</summary>
    public static byte[] BuildSrvRdata(ushort priority, ushort weight, ushort port, string target)
    {
        byte[] name = EncodeName(target);
        byte[] rdata = new byte[6 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0, 2), priority);
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(2, 2), weight);
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(4, 2), port);
        name.CopyTo(rdata.AsSpan(6));
        return rdata;
    }

    /// <summary>Builds the RDATA for a TXT record: one or more length-prefixed character strings.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any string's byte length exceeds 255 (TXT character-string 1-byte length prefix).
    /// </exception>
    public static byte[] BuildTxtRdata(params string[] strings)
    {
        int total = 0;
        foreach (string s in strings)
        {
            int len = Encoding.ASCII.GetByteCount(s);
            if (len > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(strings), len,
                    "TXT character-string must not exceed 255 bytes.");
            }
            total += 1 + len;
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
    /// Returns the DNS message bytes prefixed with a 2-byte length for DNS-over-TCP (RFC 1035 §4.2.2).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the DNS message exceeds 65535 bytes and cannot be sent over TCP.
    /// </exception>
    public byte[] ToTcpPayload()
    {
        if (_Message.Length > 65535)
        {
            throw new InvalidOperationException(
                $"DNS message length {_Message.Length} exceeds the maximum 65535 bytes for DNS-over-TCP.");
        }
        byte[] result = new byte[2 + _Message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)_Message.Length);
        _Message.Span.CopyTo(result.AsSpan(2));
        return result;
    }

    /// <summary>QTYPE / RR TYPE constants (subset of IANA DNS parameter registry).</summary>
    internal static class DnsType
    {
        /// <summary>A — IPv4 host address.</summary>
        public const ushort A = 1;

        /// <summary>NS — name server.</summary>
        public const ushort NS = 2;

        /// <summary>CNAME — canonical name alias.</summary>
        public const ushort Cname = 5;

        /// <summary>SOA — start of authority.</summary>
        public const ushort Soa = 6;

        /// <summary>PTR — domain name pointer.</summary>
        public const ushort Ptr = 12;

        /// <summary>MX — mail exchange.</summary>
        public const ushort Mx = 15;

        /// <summary>TXT — text record.</summary>
        public const ushort Txt = 16;

        /// <summary>AAAA — IPv6 host address.</summary>
        public const ushort Aaaa = 28;

        /// <summary>SRV — service locator.</summary>
        public const ushort Srv = 33;

        /// <summary>OPT — EDNS0 pseudo-RR.</summary>
        public const ushort Opt = 41;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Message.Length;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
        => _Message.Span.CopyTo(dst);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }

    /// <summary>Writes the 12-byte DNS header to <paramref name="dst"/>.</summary>
    private static void WriteHeader(Span<byte> dst, ushort id, ushort flags, ushort qdCount, ushort anCount, ushort nsCount, ushort arCount)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..4], flags);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], qdCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..8], anCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..10], nsCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..12], arCount);
    }
}
