// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// DTLS record layer for the <see cref="FrameStack"/> API.
/// Produces a complete DTLS record (13-byte header + body).
/// </summary>
/// <remarks>
/// <para>DTLS 1.2 record wire format (RFC 6347 §4.1):</para>
/// <code>
/// Byte  0:    Content type
/// Bytes 1-2:  Protocol version (big-endian)
/// Bytes 3-4:  Epoch (big-endian)
/// Bytes 5-10: Sequence number (48-bit big-endian)
/// Bytes 11-12: Length (big-endian, counts body bytes only)
/// Bytes 13+:  Body
/// </code>
/// <para>Use <see cref="BuildRecord"/> to create instances, or the static handshake helper
/// <see cref="BuildHandshakeMessage"/> to construct the body bytes before passing them
/// to <see cref="BuildRecord"/>.</para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct DtlsRecordLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    private readonly ReadOnlyMemory<byte> _Record;

    /// <summary>Creates a <see cref="DtlsRecordLayer"/> from pre-built record bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DtlsRecordLayer(ReadOnlyMemory<byte> record)
    {
        _Record = record;
    }

    /// <summary>DTLS 1.0 version code (maps to TLS 1.1 wire value).</summary>
    public const ushort Dtls10 = 0xFEFF;

    /// <summary>DTLS 1.2 version code (maps to TLS 1.2 wire value).</summary>
    public const ushort Dtls12 = 0xFEFD;

    /// <summary>
    /// Builds a DTLS record (13-byte header + body).
    /// </summary>
    /// <param name="contentType">Content type (see <see cref="TlsContentType"/>).</param>
    /// <param name="version">DTLS version (e.g. <see cref="Dtls12"/>).</param>
    /// <param name="epoch">DTLS epoch counter.</param>
    /// <param name="sequenceNumber48">48-bit sequence number.</param>
    /// <param name="body">Record body bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="body"/> length exceeds 65535 bytes, or when
    /// <paramref name="sequenceNumber48"/> exceeds the 48-bit maximum (0xFFFFFFFFFFFF).
    /// </exception>
    public static DtlsRecordLayer BuildRecord(
        byte contentType,
        ushort version,
        ushort epoch,
        ulong sequenceNumber48,
        ReadOnlySpan<byte> body)
    {
        if (body.Length > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                "DTLS record body must not exceed 65535 bytes.");
        }
        if (sequenceNumber48 > 0x0000_FFFF_FFFF_FFFFul)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber48), sequenceNumber48,
                "DTLS sequence number must fit in 48 bits (max 0xFFFFFFFFFFFF).");
        }
        byte[] rec = new byte[13 + body.Length];
        rec[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(1, 2), version);
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(3, 2), epoch);
        // 6-byte sequence number, big-endian (bits 47..0).
        for (int i = 0; i < 6; i++)
        {
            rec[5 + i] = (byte)((sequenceNumber48 >> (8 * (5 - i))) & 0xFF);
        }
        BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(11, 2), (ushort)body.Length);
        body.CopyTo(rec.AsSpan(13));
        return new DtlsRecordLayer(rec);
    }

    /// <summary>
    /// Builds a DTLS handshake message header (12 bytes: type + 3-byte body-length +
    /// 2-byte msg_seq + 3-byte fragment_offset + 3-byte fragment_length) prepended to body.
    /// Fragment offset is always 0; fragment length equals body length (unfragmented message).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="body"/> length exceeds the 24-bit handshake length field (16777215 bytes).
    /// </exception>
    public static byte[] BuildHandshakeMessage(byte type, ushort msgSeq, ReadOnlySpan<byte> body)
    {
        if (body.Length > 0x00FFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                "DTLS handshake message body must not exceed 16777215 bytes.");
        }
        byte[] msg = new byte[12 + body.Length];
        msg[0] = type;
        msg[1] = (byte)((body.Length >> 16) & 0xFF);
        msg[2] = (byte)((body.Length >> 8) & 0xFF);
        msg[3] = (byte)(body.Length & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), msgSeq);
        // fragment_offset = 0 (bytes 6-8)
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
