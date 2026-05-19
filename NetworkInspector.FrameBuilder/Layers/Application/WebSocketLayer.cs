// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// WebSocket application-layer frame encoder for the <see cref="FrameStack"/> API
/// (RFC 6455 §5.2).
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — terminal payload carrier; nothing chains
///   underneath it.</item>
///   <item><see cref="IStreamProducer"/> — <see cref="WriteStream"/> writes the
///   complete RFC 6455 frame (header + masked/unmasked payload) into the supplied
///   <see cref="IBufferWriter{T}"/>.  Used via
///   <c>TcpConnection.WriteFromClient&lt;WebSocketLayer&gt;(...)</c>.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — needs no transport pseudo-header.</item>
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
/// <para><b>Allocation strategy:</b></para>
/// <list type="bullet">
///   <item>For payloads ≤ 125 bytes the masking key application uses a
///   fixed-size <c>Span&lt;byte&gt;</c> scratch on the stack —
///   <b>zero heap allocations</b>.</item>
///   <item>For payloads larger than 125 bytes an
///   <see cref="ArrayPool{T}"/> rental is used (returned before
///   <see cref="WriteStream"/> returns).</item>
/// </list>
/// <para><b>Thread safety:</b> instances are immutable after construction.
/// <see cref="WriteStream"/> may be called concurrently on the same instance
/// without external synchronisation; each call uses per-call stack or pool
/// storage for masking.</para>
/// </remarks>
public readonly struct WebSocketLayer : IStatelessLayer, IPayloadLayer, IStreamProducer, IPseudoHeaderIndependent
{
    /// <summary>The RFC 6455 base payload-length sentinel indicating 16-bit extended length.</summary>
    private const byte Len16Sentinel = 126;

    /// <summary>The RFC 6455 base payload-length sentinel indicating 64-bit extended length.</summary>
    private const byte Len64Sentinel = 127;

    /// <summary>Maximum payload length that can be inlined on the stack for masking (avoids heap allocation).</summary>
    private const int StackMaskThreshold = 125;

    private readonly ReadOnlyMemory<byte> _Payload;
    private readonly byte _Opcode;
    private readonly WebSocketFrameOptions _Options;

    /// <summary>
    /// Creates a <see cref="WebSocketLayer"/> that encodes a single RFC 6455 frame.
    /// </summary>
    /// <param name="payload">Application data to carry in the frame payload.</param>
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
    /// <remarks>Zero — the frame header and payload are both written by <see cref="WriteStream"/>.</remarks>
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 0;
    }

    /// <inheritdoc />
    /// <remarks>No-op: the entire frame is written by <see cref="WriteStream"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // Nothing to write; WriteStream handles the complete frame.
    }

    /// <inheritdoc />
    /// <remarks>No post-fix phases required; all fields are written in <see cref="WriteStream"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix operations needed for WebSocket frames.
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the complete RFC 6455 frame (2-byte header + optional extended length +
    /// optional masking key + payload) into <paramref name="writer"/>.
    /// For payloads ≤ 125 bytes the masked copy is performed on a stack-allocated
    /// buffer; larger payloads use an <see cref="ArrayPool{T}"/> rental.
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
            b1 = (byte)(maskBit | Len16Sentinel);
            extLenBytes = 2;
        }
        else
        {
            b1 = (byte)(maskBit | Len64Sentinel);
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

        // Masked path: XOR each byte with the repeating 4-byte masking key.
        // Algorithm: apply the masking key byte-by-byte with index modulo 4.
        // For small payloads use a stack buffer; for large payloads rent from the pool.
        uint maskKey = _Options.MaskingKey!.Value;
        Span<byte> dst2 = writer.GetSpan(payloadLen);

        if (payloadLen <= StackMaskThreshold)
        {
            // Stack-allocated copy: zero heap allocations.
            Span<byte> scratch = stackalloc byte[StackMaskThreshold];
            ApplyMask(payload, scratch[..payloadLen], maskKey);
            scratch[..payloadLen].CopyTo(dst2);
        }
        else
        {
            // Pool-based copy for larger payloads.
            byte[] rental = ArrayPool<byte>.Shared.Rent(payloadLen);
            try
            {
                ApplyMask(payload, rental.AsSpan(0, payloadLen), maskKey);
                rental.AsSpan(0, payloadLen).CopyTo(dst2);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rental);
            }
        }

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
    private static void ApplyMask(ReadOnlySpan<byte> src, Span<byte> dst, uint key)
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
