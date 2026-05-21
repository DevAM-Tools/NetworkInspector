// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// HTTP/2 frame (RFC 7540 §4.1) for the <see cref="FrameStack"/> API.
/// Produces a complete 9-byte frame header plus the supplied body.
/// </summary>
/// <remarks>
/// <para>HTTP/2 frame wire format (RFC 7540 §4.1):</para>
/// <code>
/// Bytes 0-2: Length (24-bit big-endian, counts body bytes only)
/// Byte  3:   Type
/// Byte  4:   Flags
/// Bytes 5-8: R(1 bit, 0) + Stream Identifier (31 bits)
/// Bytes 9+:  Frame body
/// </code>
/// <para>Use the static factory method <see cref="BuildFrame"/> to create instances.</para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct Http2Layer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    private readonly ReadOnlyMemory<byte> _Frame;

    /// <summary>Creates an <see cref="Http2Layer"/> directly from pre-built HTTP/2 frame bytes.</summary>
    /// <param name="frame">Complete HTTP/2 frame (header + body).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Http2Layer(ReadOnlyMemory<byte> frame)
    {
        _Frame = frame;
    }

    /// <summary>Builds an HTTP/2 frame with the given header fields and body.</summary>
    /// <param name="type">Frame type (see <see cref="Http2FrameType"/>).</param>
    /// <param name="flags">Frame flags (see <see cref="Http2FrameFlags"/>).</param>
    /// <param name="streamId">Stream identifier (31 bits; the R bit is always 0).</param>
    /// <param name="body">Frame body bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="body"/> exceeds 2^24-1 bytes.</exception>
    public static Http2Layer BuildFrame(byte type, byte flags, uint streamId, ReadOnlySpan<byte> body)
    {
        if (body.Length > 0xFFFFFF)
        {
            throw new ArgumentException("Body too large for an HTTP/2 frame (max 2^24-1 bytes).", nameof(body));
        }
        byte[] frame = new byte[9 + body.Length];
        frame[0] = (byte)((body.Length >> 16) & 0xFF);
        frame[1] = (byte)((body.Length >> 8) & 0xFF);
        frame[2] = (byte)(body.Length & 0xFF);
        frame[3] = type;
        frame[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5, 4), streamId & 0x7FFFFFFFu);
        body.CopyTo(frame.AsSpan(9));
        return new Http2Layer(frame);
    }

    /// <summary>Builds a SETTINGS frame body from identifier/value pairs (each 6 bytes per RFC 7540 §6.5).</summary>
    public static byte[] BuildSettingsBody(params (ushort id, uint value)[] settings)
    {
        byte[] body = new byte[settings.Length * 6];
        for (int i = 0; i < settings.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6 * i, 2), settings[i].id);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(6 * i + 2, 4), settings[i].value);
        }
        return body;
    }

    /// <summary>Builds a PING frame body (always 8 opaque bytes per RFC 7540 §6.7).</summary>
    public static byte[] BuildPingBody(ulong opaque)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, opaque);
        return body;
    }

    /// <summary>Builds a WINDOW_UPDATE frame body (R bit + 31-bit increment per RFC 7540 §6.9).</summary>
    public static byte[] BuildWindowUpdateBody(uint increment)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, increment & 0x7FFFFFFFu);
        return body;
    }

    /// <summary>Builds a RST_STREAM frame body (4-byte error code per RFC 7540 §6.4).</summary>
    public static byte[] BuildRstStreamBody(uint errorCode)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, errorCode);
        return body;
    }

    /// <summary>
    /// Builds a GOAWAY frame body (last-stream-id + error-code + optional debug data per RFC 7540 §6.8).
    /// </summary>
    public static byte[] BuildGoawayBody(uint lastStreamId, uint errorCode, ReadOnlySpan<byte> debugData = default)
    {
        byte[] body = new byte[8 + debugData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), lastStreamId & 0x7FFFFFFFu);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), errorCode);
        debugData.CopyTo(body.AsSpan(8));
        return body;
    }

    /// <summary>
    /// Builds an HPACK-encoded single Indexed Header Field entry referencing the static table (RFC 7541 §6.1).
    /// </summary>
    /// <param name="staticTableIndex">Static table index (1..61); must fit in 7 bits.</param>
    public static byte[] BuildHpackIndexed(byte staticTableIndex)
    {
        if ((staticTableIndex & 0x80) != 0)
        {
            throw new ArgumentException("Static table index must fit in 7 bits (1..127).", nameof(staticTableIndex));
        }
        return [(byte)(0x80 | staticTableIndex)];
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Frame.Length;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
        => _Frame.Span.CopyTo(dst);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }
}

/// <summary>HTTP/2 frame type identifiers (RFC 7540 §11.2).</summary>
/// <remarks><b>Thread safety:</b> static constants; safe for concurrent use.</remarks>
public static class Http2FrameType
{
    /// <summary>DATA frame (0x0).</summary>
    public const byte Data = 0x0;

    /// <summary>HEADERS frame (0x1).</summary>
    public const byte Headers = 0x1;

    /// <summary>PRIORITY frame (0x2).</summary>
    public const byte Priority = 0x2;

    /// <summary>RST_STREAM frame (0x3).</summary>
    public const byte RstStream = 0x3;

    /// <summary>SETTINGS frame (0x4).</summary>
    public const byte Settings = 0x4;

    /// <summary>PUSH_PROMISE frame (0x5).</summary>
    public const byte PushPromise = 0x5;

    /// <summary>PING frame (0x6).</summary>
    public const byte Ping = 0x6;

    /// <summary>GOAWAY frame (0x7).</summary>
    public const byte Goaway = 0x7;

    /// <summary>WINDOW_UPDATE frame (0x8).</summary>
    public const byte WindowUpdate = 0x8;

    /// <summary>CONTINUATION frame (0x9).</summary>
    public const byte Continuation = 0x9;
}

/// <summary>Common HTTP/2 frame flag bit values (RFC 7540 §6).</summary>
/// <remarks><b>Thread safety:</b> static constants; safe for concurrent use.</remarks>
public static class Http2FrameFlags
{
    /// <summary>END_STREAM / ACK (0x01).</summary>
    public const byte EndStreamOrAck = 0x01;

    /// <summary>END_HEADERS (0x04).</summary>
    public const byte EndHeaders = 0x04;

    /// <summary>PADDED (0x08).</summary>
    public const byte Padded = 0x08;

    /// <summary>PRIORITY (0x20).</summary>
    public const byte Priority = 0x20;
}
