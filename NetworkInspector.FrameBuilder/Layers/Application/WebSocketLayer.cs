// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// WebSocket frame encoder (RFC 6455 §5.2).  A <b>stream producer</b>, not a stack
/// layer: it cannot be appended with <c>Then(...)</c> because a WebSocket frame is a
/// byte stream carried by TCP, not a fixed header.  Hand it to
/// <see cref="TcpConnection{TOld,TTail}.WriteFromClient{TProducer}"/> /
/// <c>WriteFromServer</c>, or call <see cref="WriteStream"/> yourself.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IStreamProducer"/> — <see cref="WriteStream"/> writes the
///   complete RFC 6455 frame (header + masked/unmasked payload) into the supplied
///   <see cref="IBufferWriter{T}"/>.</item>
/// </list>
/// <para><b>Frame layout (RFC 6455 §5.2):</b></para>
/// <code>
/// Byte 0: FIN(1) RSV1(1) RSV2(1) RSV3(1) Opcode(4)
/// Byte 1: MASK(1) Payload len(7)   [0–125 → length, 126 → 16-bit ext, 127 → 64-bit ext]
/// [2 bytes]  extended payload length (only if base length == 126)
/// [8 bytes]  extended payload length (only if base length == 127)
/// [4 bytes]  masking key            (only if MASK == 1)
/// [N bytes]  payload data           (XOR'd with masking key if MASK == 1)
/// </code>
/// <para><b>Allocation strategy:</b> masking writes directly into the writer span;
/// no intermediate buffer.</para>
/// <para><b>Thread safety:</b> instances are immutable after construction.
/// <see cref="WriteStream"/> may be called concurrently on the same instance
/// with distinct writers.</para>
/// </remarks>
public readonly struct WebSocketLayer : IStreamProducer
{
    /// <summary>The RFC 6455 base payload-length sentinel indicating 16-bit extended length.</summary>
    private const byte _Len16Sentinel = 126;

    /// <summary>The RFC 6455 base payload-length sentinel indicating 64-bit extended length.</summary>
    private const byte _Len64Sentinel = 127;

    /// <summary>Caller-owned payload view; referenced, not copied.</summary>
    private readonly ReadOnlyMemory<byte> _Payload;
    private readonly byte _Opcode;
    private readonly WebSocketFrameOptions _Options;

    /// <summary>
    /// Creates a <see cref="WebSocketLayer"/> that encodes a single RFC 6455 frame.
    /// </summary>
    /// <param name="payload">
    /// Application data to carry in the frame payload.  Referenced, not copied: it must
    /// stay unchanged until <see cref="WriteStream"/> has run.
    /// </param>
    /// <param name="opcode">
    /// RFC 6455 opcode; use constants from <see cref="WebSocketOpcode"/>
    /// (e.g. <see cref="WebSocketOpcode.Text"/>, <see cref="WebSocketOpcode.Binary"/>).
    /// </param>
    /// <param name="options">
    /// Frame-level options (FIN, RSV bits, masking key).
    /// Default encodes a single unmasked FIN frame — typical for server→client frames.
    /// For client→server frames, supply a non-<see langword="null"/>
    /// <see cref="WebSocketFrameOptions.MaskingKey"/>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketLayer(ReadOnlyMemory<byte> payload, byte opcode, WebSocketFrameOptions options = default)
    {
        _Payload = payload;
        _Opcode = opcode;
        _Options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the complete RFC 6455 frame (2-byte header + optional extended length +
    /// optional masking key + payload) into <paramref name="writer"/>.  The masked
    /// payload is XORed straight into the writer's span.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStream(IBufferWriter<byte> writer)
    {
        ReadOnlySpan<byte> payload = _Payload.Span;
        int payloadLen = payload.Length;
        bool masked = _Options.MaskingKey.HasValue;

        // --- Encode byte 0: FIN + RSV1/2/3 + opcode ---
        byte b0 = _Opcode;
        if (_Options.Fin)
        {
            b0 |= 0x80;
        }
        if (_Options.Rsv1)
        {
            b0 |= 0x40;
        }
        if (_Options.Rsv2)
        {
            b0 |= 0x20;
        }
        if (_Options.Rsv3)
        {
            b0 |= 0x10;
        }

        // --- Encode byte 1: MASK + base payload length ---
        byte maskBit = masked ? (byte)0x80 : (byte)0x00;
        byte b1;
        int extLenBytes;
        if (payloadLen <= 125)
        {
            b1 = (byte)(maskBit | (byte)payloadLen);
            extLenBytes = 0;
        }
        else if (payloadLen <= 0xFFFF)
        {
            b1 = (byte)(maskBit | _Len16Sentinel);
            extLenBytes = 2;
        }
        else
        {
            b1 = (byte)(maskBit | _Len64Sentinel);
            extLenBytes = 8;
        }

        int maskKeyBytes = masked ? 4 : 0;
        int headerBytes = 2 + extLenBytes + maskKeyBytes;
        Span<byte> header = writer.GetSpan(headerBytes);
        header[0] = b0;
        header[1] = b1;

        int offset = 2;
        if (extLenBytes == 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header[offset..], (ushort)payloadLen);
            offset += 2;
        }
        else if (extLenBytes == 8)
        {
            BinaryPrimitives.WriteUInt64BigEndian(header[offset..], (ulong)payloadLen);
            offset += 8;
        }

        if (masked)
        {
            uint key = _Options.MaskingKey!.Value;
            header[offset] = (byte)(key >> 24);
            header[offset + 1] = (byte)(key >> 16);
            header[offset + 2] = (byte)(key >> 8);
            header[offset + 3] = (byte)key;
        }

        writer.Advance(headerBytes);

        if (payloadLen == 0)
        {
            return;
        }

        if (!masked)
        {
            // Unmasked path: write payload directly.
            Span<byte> dst = writer.GetSpan(payloadLen);
            payload.CopyTo(dst);
            writer.Advance(payloadLen);
            return;
        }

        // Masked path: mask straight into the writer's span.  Source and destination
        // never alias (the payload is the caller's memory, the span belongs to the
        // writer), so one pass suffices.
        uint maskKey = _Options.MaskingKey!.Value;
        Span<byte> maskedDst = writer.GetSpan(payloadLen);
        _ApplyMask(payload, maskedDst[..payloadLen], maskKey);
        writer.Advance(payloadLen);
    }

    /// <summary>
    /// XOR-applies the 4-byte repeating masking key from <paramref name="src"/>
    /// into <paramref name="dst"/> (RFC 6455 §5.3).
    /// </summary>
    /// <remarks>
    /// Algorithm: for each byte i, <c>dst[i] = src[i] ^ key[(i % 4)]</c>.
    /// The key bytes are extracted in network byte order (most-significant byte first),
    /// so key[0] = bits 31-24, key[1] = bits 23-16, etc.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ApplyMask(ReadOnlySpan<byte> src, Span<byte> dst, uint key)
    {
        byte k0 = (byte)(key >> 24);
        byte k1 = (byte)(key >> 16);
        byte k2 = (byte)(key >> 8);
        byte k3 = (byte)key;

        int i = 0;
        // Process 4 bytes at a time for performance.
        int bulk = src.Length & ~3;
        for (; i < bulk; i += 4)
        {
            dst[i] = (byte)(src[i] ^ k0);
            dst[i + 1] = (byte)(src[i + 1] ^ k1);
            dst[i + 2] = (byte)(src[i + 2] ^ k2);
            dst[i + 3] = (byte)(src[i + 3] ^ k3);
        }
        // Remaining bytes (0–3).
        ReadOnlySpan<byte> keys = [k0, k1, k2, k3];
        for (; i < src.Length; i++)
        {
            dst[i] = (byte)(src[i] ^ keys[i & 3]);
        }
    }
}
