// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Builds TLS / DTLS application-layer payloads for protocol tests.
/// Targets enough surface for record-layer + handshake (ClientHello, ServerHello,
/// Alert) plus the most common extensions (SNI, ALPN, supported_versions, key_share).
/// </summary>
internal static class TlsPayloadBuilder
{
    /// <summary>TLS record content types.</summary>
    internal static class ContentType
    {
        internal const byte ChangeCipherSpec = 20;
        internal const byte Alert = 21;
        internal const byte Handshake = 22;
        internal const byte ApplicationData = 23;
    }

    /// <summary>Handshake message types.</summary>
    internal static class HandshakeType
    {
        internal const byte ClientHello = 1;
        internal const byte ServerHello = 2;
        internal const byte Certificate = 11;
        internal const byte ServerKeyExchange = 12;
        internal const byte ServerHelloDone = 14;
        internal const byte Finished = 20;
    }

    /// <summary>TLS extension type ids.</summary>
    internal static class ExtensionType
    {
        internal const ushort ServerName = 0;
        internal const ushort SupportedGroups = 10;
        internal const ushort SignatureAlgorithms = 13;
        internal const ushort Alpn = 16;
        internal const ushort SupportedVersions = 43;
        internal const ushort KeyShare = 51;
    }

    internal const ushort Tls10 = 0x0301;
    internal const ushort Tls12 = 0x0303;
    internal const ushort Tls13 = 0x0304;

    /// <summary>
    /// Wraps a single TLS record (content_type + version + length + body) around <paramref name="body"/>.
    /// </summary>
    internal static byte[] BuildRecord(byte contentType, ushort version, ReadOnlySpan<byte> body)
    {
        byte[] rec = new byte[5 + body.Length];
        rec[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(1, 2), version);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(3, 2), (ushort)body.Length);
        body.CopyTo(rec.AsSpan(5));
        return rec;
    }

    /// <summary>
    /// Wraps a handshake-type message in a 4-byte handshake header (type + 3-byte length).
    /// </summary>
    internal static byte[] BuildHandshakeMessage(byte type, ReadOnlySpan<byte> body)
    {
        byte[] msg = new byte[4 + body.Length];
        msg[0] = type;
        // 3-byte length, big-endian
        msg[1] = (byte)((body.Length >> 16) & 0xFF);
        msg[2] = (byte)((body.Length >> 8) & 0xFF);
        msg[3] = (byte)(body.Length & 0xFF);
        body.CopyTo(msg.AsSpan(4));
        return msg;
    }

    /// <summary>
    /// Builds a minimal TLS ClientHello body (legacy_version + random + session_id +
    /// cipher_suites + compression_methods + extensions). All extension bytes must
    /// already include the per-extension type/length prefix (see <see cref="BuildExtension"/>).
    /// </summary>
    internal static byte[] BuildClientHelloBody(
        ushort legacyVersion,
        ReadOnlySpan<byte> random32,
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<ushort> cipherSuites,
        ReadOnlySpan<byte> compressionMethods,
        ReadOnlySpan<byte> extensionsConcatenated)
    {
        if (random32.Length != 32)
        {
            throw new ArgumentException("Random must be 32 bytes.");
        }

        int ciphersBytes = cipherSuites.Length * 2;
        int total =
            2 /* version */
            + 32 /* random */
            + 1 + sessionId.Length
            + 2 + ciphersBytes
            + 1 + compressionMethods.Length
            + 2 + extensionsConcatenated.Length;
        byte[] body = new byte[total];
        int o = 0;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), legacyVersion);
        o += 2;
        random32.CopyTo(body.AsSpan(o, 32));
        o += 32;
        body[o++] = (byte)sessionId.Length;
        sessionId.CopyTo(body.AsSpan(o, sessionId.Length));
        o += sessionId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), (ushort)ciphersBytes);
        o += 2;
        for (int i = 0; i < cipherSuites.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), cipherSuites[i]);
            o += 2;
        }
        body[o++] = (byte)compressionMethods.Length;
        compressionMethods.CopyTo(body.AsSpan(o, compressionMethods.Length));
        o += compressionMethods.Length;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), (ushort)extensionsConcatenated.Length);
        o += 2;
        extensionsConcatenated.CopyTo(body.AsSpan(o));
        return body;
    }

    /// <summary>
    /// Builds a minimal TLS ServerHello body (version + random + session_id + cipher_suite +
    /// compression_method + extensions).
    /// </summary>
    internal static byte[] BuildServerHelloBody(
        ushort legacyVersion,
        ReadOnlySpan<byte> random32,
        ReadOnlySpan<byte> sessionId,
        ushort cipherSuite,
        byte compressionMethod,
        ReadOnlySpan<byte> extensionsConcatenated)
    {
        if (random32.Length != 32)
        {
            throw new ArgumentException("Random must be 32 bytes.");
        }

        int total = 2 + 32 + 1 + sessionId.Length + 2 + 1 + 2 + extensionsConcatenated.Length;
        byte[] body = new byte[total];
        int o = 0;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), legacyVersion);
        o += 2;
        random32.CopyTo(body.AsSpan(o, 32));
        o += 32;
        body[o++] = (byte)sessionId.Length;
        sessionId.CopyTo(body.AsSpan(o, sessionId.Length));
        o += sessionId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), cipherSuite);
        o += 2;
        body[o++] = compressionMethod;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), (ushort)extensionsConcatenated.Length);
        o += 2;
        extensionsConcatenated.CopyTo(body.AsSpan(o));
        return body;
    }

    /// <summary>
    /// Wraps a single extension in a type+length prefix.
    /// </summary>
    internal static byte[] BuildExtension(ushort type, ReadOnlySpan<byte> data)
    {
        byte[] ext = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(2, 2), (ushort)data.Length);
        data.CopyTo(ext.AsSpan(4));
        return ext;
    }

    /// <summary>
    /// Builds the body of a Server Name Indication extension (RFC 6066) with one hostname.
    /// Caller still wraps via <see cref="BuildExtension"/>.
    /// </summary>
    internal static byte[] BuildSniExtensionBody(string hostName)
    {
        int nameLen = Encoding.ASCII.GetByteCount(hostName);
        // server_name_list_len(2) + name_type(1) + name_len(2) + name
        byte[] body = new byte[2 + 1 + 2 + nameLen];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), (ushort)(3 + nameLen));
        body[2] = 0; // host_name
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3, 2), (ushort)nameLen);
        Encoding.ASCII.GetBytes(hostName, body.AsSpan(5, nameLen));
        return body;
    }

    /// <summary>
    /// Builds the body of an ALPN extension (RFC 7301) with the given protocol list.
    /// </summary>
    internal static byte[] BuildAlpnExtensionBody(params string[] protocols)
    {
        int inner = 0;
        foreach (string p in protocols)
        {
            inner += 1 + Encoding.ASCII.GetByteCount(p);
        }
        byte[] body = new byte[2 + inner];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), (ushort)inner);
        int o = 2;
        foreach (string p in protocols)
        {
            int n = Encoding.ASCII.GetByteCount(p);
            body[o++] = (byte)n;
            Encoding.ASCII.GetBytes(p, body.AsSpan(o, n));
            o += n;
        }
        return body;
    }

    /// <summary>
    /// Builds the body of a supported_versions ClientHello extension (RFC 8446 §4.2.1).
    /// </summary>
    internal static byte[] BuildSupportedVersionsExtensionBody(params ushort[] versions)
    {
        byte inner = (byte)(versions.Length * 2);
        byte[] body = new byte[1 + inner];
        body[0] = inner;
        for (int i = 0; i < versions.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + 2 * i, 2), versions[i]);
        }
        return body;
    }

    /// <summary>
    /// Builds a TLS Alert payload body (level + description) — to be put directly into a record.
    /// </summary>
    internal static byte[] BuildAlertBody(byte level, byte description) => [level, description];

    /// <summary>
    /// Wraps a TLS application payload in Eth+IPv4+TCP (port 443) using FrameStack.
    /// </summary>
    internal static byte[] WrapTcp(ReadOnlySpan<byte> tlsBytes, ushort srcPort = 12345, ushort dstPort = 443)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(srcPort, dstPort, seqNum: 1, ackNum: 0, flags: 0x18);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(tlsBytes);
    }

    /// <summary>
    /// Wraps a DTLS payload in Eth+IPv4+UDP (port 443) using FrameStack.
    /// </summary>
    internal static byte[] WrapUdp(ReadOnlySpan<byte> dtlsBytes, ushort srcPort = 12345, ushort dstPort = 443)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dtlsBytes);
    }

    /// <summary>
    /// Builds a DTLS record header + body. Record header layout:
    /// content_type(1) + version(2) + epoch(2) + sequence_number(6) + length(2) + body.
    /// </summary>
    internal static byte[] BuildDtlsRecord(
        byte contentType, ushort version,
        ushort epoch, ulong sequenceNumber48, ReadOnlySpan<byte> body)
    {
        byte[] rec = new byte[13 + body.Length];
        rec[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(1, 2), version);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(3, 2), epoch);
        // 6-byte sequence number, big-endian
        for (int i = 0; i < 6; i++)
        {
            rec[5 + i] = (byte)((sequenceNumber48 >> (8 * (5 - i))) & 0xFF);
        }
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(11, 2), (ushort)body.Length);
        body.CopyTo(rec.AsSpan(13));
        return rec;
    }

    /// <summary>
    /// Builds a DTLS handshake message header (12 bytes: type + 3-byte length +
    /// 2-byte msg_seq + 3-byte fragment_offset + 3-byte fragment_length) and prepends it to body.
    /// </summary>
    internal static byte[] BuildDtlsHandshakeMessage(byte type, ushort msgSeq, ReadOnlySpan<byte> body)
    {
        byte[] msg = new byte[12 + body.Length];
        msg[0] = type;
        msg[1] = (byte)((body.Length >> 16) & 0xFF);
        msg[2] = (byte)((body.Length >> 8) & 0xFF);
        msg[3] = (byte)(body.Length & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), msgSeq);
        // fragment_offset = 0
        msg[6] = 0;
        msg[7] = 0;
        msg[8] = 0;
        // fragment_length = body length
        msg[9] = msg[1];
        msg[10] = msg[2];
        msg[11] = msg[3];
        body.CopyTo(msg.AsSpan(12));
        return msg;
    }
}
