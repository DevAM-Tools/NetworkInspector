// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// TLS record layer for the <see cref="FrameStack"/> API.
/// Produces a complete TLS record (5-byte header + body).
/// </summary>
/// <remarks>
/// <para>TLS record wire format (RFC 5246 §6.2 / RFC 8446 §5.1):</para>
/// <code>
/// Byte  0:   Content type
/// Bytes 1-2: Protocol version (big-endian)
/// Bytes 3-4: Length (big-endian, counts body bytes only)
/// Bytes 5+:  Body
/// </code>
/// <para>Use <see cref="BuildRecord"/> to create instances, or the static handshake helpers
/// (<see cref="BuildHandshakeMessage"/>, <see cref="BuildClientHelloBody"/>, etc.)
/// to construct the body bytes before passing them to <see cref="BuildRecord"/>.</para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct TlsRecordLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    private readonly ReadOnlyMemory<byte> _Record;

    /// <summary>Creates a <see cref="TlsRecordLayer"/> from pre-built record bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TlsRecordLayer(ReadOnlyMemory<byte> record)
    {
        _Record = record;
    }

    /// <summary>TLS 1.0 version code.</summary>
    public const ushort Tls10 = 0x0301;

    /// <summary>TLS 1.2 version code.</summary>
    public const ushort Tls12 = 0x0303;

    /// <summary>TLS 1.3 version code.</summary>
    public const ushort Tls13 = 0x0304;

    /// <summary>
    /// Builds a TLS record (5-byte header + body).
    /// </summary>
    /// <param name="contentType">Record content type (see <see cref="TlsContentType"/>).</param>
    /// <param name="version">TLS version (e.g. <see cref="Tls12"/>).</param>
    /// <param name="body">Record body bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="body"/> length exceeds the 16-bit TLS record length field (65535 bytes).
    /// </exception>
    public static TlsRecordLayer BuildRecord(byte contentType, ushort version, ReadOnlySpan<byte> body)
    {
        if (body.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                "TLS record body must not exceed 65535 bytes.");
        }
        byte[] rec = new byte[5 + body.Length];
        rec[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(1, 2), version);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(3, 2), (ushort)body.Length);
        body.CopyTo(rec.AsSpan(5));
        return new TlsRecordLayer(rec);
    }

    /// <summary>
    /// Wraps a handshake message body in a 4-byte handshake header (type + 3-byte length).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="body"/> length exceeds the 24-bit handshake length field (16777215 bytes).
    /// </exception>
    public static byte[] BuildHandshakeMessage(byte type, ReadOnlySpan<byte> body)
    {
        if (body.Length > 0x00FFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                "TLS handshake message body must not exceed 16777215 bytes.");
        }
        byte[] msg = new byte[4 + body.Length];
        msg[0] = type;
        msg[1] = (byte)((body.Length >> 16) & 0xFF);
        msg[2] = (byte)((body.Length >> 8) & 0xFF);
        msg[3] = (byte)(body.Length & 0xFF);
        body.CopyTo(msg.AsSpan(4));
        return msg;
    }

    /// <summary>
    /// Builds a minimal TLS ClientHello body (RFC 5246 §7.4.1.2 / RFC 8446 §4.1.2).
    /// </summary>
    /// <param name="legacyVersion">Advertised legacy version (typically TLS 1.2 = 0x0303).</param>
    /// <param name="random32">32-byte random field.</param>
    /// <param name="sessionId">Session ID (0..32 bytes).</param>
    /// <param name="cipherSuites">Cipher suite values to advertise.</param>
    /// <param name="compressionMethods">Compression methods (normally [0] = null).</param>
    /// <param name="extensionsConcatenated">Concatenated, pre-encoded extension bytes.</param>
    public static byte[] BuildClientHelloBody(
        ushort legacyVersion,
        ReadOnlySpan<byte> random32,
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<ushort> cipherSuites,
        ReadOnlySpan<byte> compressionMethods,
        ReadOnlySpan<byte> extensionsConcatenated)
    {
        if (random32.Length != 32)
        {
            throw new ArgumentException("Random must be 32 bytes.", nameof(random32));
        }
        int ciphersBytes = cipherSuites.Length * 2;
        if (ciphersBytes > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(cipherSuites), cipherSuites.Length,
                "Cipher suite list must not exceed 32767 entries (65534 bytes).");
        }
        if (extensionsConcatenated.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(extensionsConcatenated), extensionsConcatenated.Length,
                "Extensions block must not exceed 65535 bytes.");
        }
        int total =
            2 + 32
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
    /// Builds a minimal TLS ServerHello body (RFC 5246 §7.4.1.3).
    /// </summary>
    /// <param name="legacyVersion">Advertised legacy version.</param>
    /// <param name="random32">32-byte random field.</param>
    /// <param name="sessionId">Session ID (0..32 bytes).</param>
    /// <param name="cipherSuite">Selected cipher suite code.</param>
    /// <param name="compressionMethod">Selected compression method (normally 0).</param>
    /// <param name="extensionsConcatenated">Concatenated, pre-encoded extension bytes.</param>
    public static byte[] BuildServerHelloBody(
        ushort legacyVersion,
        ReadOnlySpan<byte> random32,
        ReadOnlySpan<byte> sessionId,
        ushort cipherSuite,
        byte compressionMethod,
        ReadOnlySpan<byte> extensionsConcatenated)
    {
        if (random32.Length != 32)
        {
            throw new ArgumentException("Random must be 32 bytes.", nameof(random32));
        }
        if (extensionsConcatenated.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(extensionsConcatenated), extensionsConcatenated.Length,
                "Extensions block must not exceed 65535 bytes.");
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

    /// <summary>Builds a TLS extension with a 4-byte type+length prefix.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="data"/> length exceeds 65535 bytes.</exception>
    public static byte[] BuildExtension(ushort type, ReadOnlySpan<byte> data)
    {
        if (data.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length,
                "TLS extension data must not exceed 65535 bytes.");
        }
        byte[] ext = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(2, 2), (ushort)data.Length);
        data.CopyTo(ext.AsSpan(4));
        return ext;
    }

    /// <summary>Builds the body of an SNI extension (RFC 6066) for one host name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="hostName"/> exceeds 65535 bytes when ASCII-encoded.
    /// </exception>
    public static byte[] BuildSniExtensionBody(string hostName)
    {
        int nameLen = Encoding.ASCII.GetByteCount(hostName);
        if (nameLen > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(hostName), nameLen,
                "SNI host name must not exceed 65535 bytes.");
        }
        byte[] body = new byte[2 + 1 + 2 + nameLen];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), (ushort)(3 + nameLen));
        body[2] = 0; // host_name type
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3, 2), (ushort)nameLen);
        Encoding.ASCII.GetBytes(hostName, body.AsSpan(5, nameLen));
        return body;
    }

    /// <summary>Builds the body of an ALPN extension (RFC 7301) for the given protocol list.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any protocol name exceeds 255 bytes (ALPN 1-byte length prefix).
    /// </exception>
    public static byte[] BuildAlpnExtensionBody(params string[] protocols)
    {
        int inner = 0;
        foreach (string p in protocols)
        {
            int n = Encoding.ASCII.GetByteCount(p);
            if (n > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(protocols), n,
                    "ALPN protocol name must not exceed 255 bytes.");
            }
            inner += 1 + n;
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

    /// <summary>Builds a supported_versions extension body for ClientHello (RFC 8446 §4.2.1).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the version list is too long to fit in the 1-byte length prefix (max 127 versions).
    /// </exception>
    public static byte[] BuildSupportedVersionsExtensionBody(params ushort[] versions)
    {
        int innerInt = versions.Length * 2;
        if (innerInt > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(versions), versions.Length,
                "supported_versions list must not exceed 127 entries (255 bytes).");
        }
        byte inner = (byte)innerInt;
        byte[] body = new byte[1 + inner];
        body[0] = inner;
        for (int i = 0; i < versions.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1 + 2 * i, 2), versions[i]);
        }
        return body;
    }

    /// <summary>Builds a TLS Alert body (level + description per RFC 5246 §7.2).</summary>
    public static byte[] BuildAlertBody(byte level, byte description) => [level, description];

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Record.Length;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
        => _Record.Span.CopyTo(dst);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }
}

/// <summary>TLS/DTLS record content types (RFC 5246 §6.2 / RFC 8446 §5.1).</summary>
/// <remarks><b>Thread safety:</b> static constants; safe for concurrent use.</remarks>
public static class TlsContentType
{
    /// <summary>ChangeCipherSpec (20).</summary>
    public const byte ChangeCipherSpec = 20;

    /// <summary>Alert (21).</summary>
    public const byte Alert = 21;

    /// <summary>Handshake (22).</summary>
    public const byte Handshake = 22;

    /// <summary>ApplicationData (23).</summary>
    public const byte ApplicationData = 23;
}

/// <summary>TLS/DTLS handshake message types (RFC 5246 §7.4 / RFC 8446 §4).</summary>
/// <remarks><b>Thread safety:</b> static constants; safe for concurrent use.</remarks>
public static class TlsHandshakeType
{
    /// <summary>ClientHello (1).</summary>
    public const byte ClientHello = 1;

    /// <summary>ServerHello (2).</summary>
    public const byte ServerHello = 2;

    /// <summary>Certificate (11).</summary>
    public const byte Certificate = 11;

    /// <summary>ServerKeyExchange (12).</summary>
    public const byte ServerKeyExchange = 12;

    /// <summary>ServerHelloDone (14).</summary>
    public const byte ServerHelloDone = 14;

    /// <summary>Finished (20).</summary>
    public const byte Finished = 20;
}

/// <summary>Common TLS extension type identifiers (IANA TLS extension registry).</summary>
/// <remarks><b>Thread safety:</b> static constants; safe for concurrent use.</remarks>
public static class TlsExtensionType
{
    /// <summary>server_name (0).</summary>
    public const ushort ServerName = 0;

    /// <summary>supported_groups (10).</summary>
    public const ushort SupportedGroups = 10;

    /// <summary>signature_algorithms (13).</summary>
    public const ushort SignatureAlgorithms = 13;

    /// <summary>application_layer_protocol_negotiation (16).</summary>
    public const ushort Alpn = 16;

    /// <summary>supported_versions (43).</summary>
    public const ushort SupportedVersions = 43;

    /// <summary>key_share (51).</summary>
    public const ushort KeyShare = 51;
}
