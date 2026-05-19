// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Builds raw HTTP/2 frame bytes (RFC 7540 §4.1) and wraps them in
/// Eth+IPv4+TCP. Frame layout: 24-bit length, 8-bit type, 8-bit flags,
/// 32-bit (R + 31-bit stream-id), payload.
/// </summary>
internal static class Http2PayloadBuilder
{
    /// <summary>Frame type identifiers from RFC 7540 §11.2.</summary>
    internal static class FrameType
    {
        internal const byte Data = 0x0;
        internal const byte Headers = 0x1;
        internal const byte Priority = 0x2;
        internal const byte RstStream = 0x3;
        internal const byte Settings = 0x4;
        internal const byte PushPromise = 0x5;
        internal const byte Ping = 0x6;
        internal const byte Goaway = 0x7;
        internal const byte WindowUpdate = 0x8;
        internal const byte Continuation = 0x9;
    }

    /// <summary>Frame flag bit values.</summary>
    internal static class Flags
    {
        internal const byte EndStream = 0x01;
        internal const byte Ack = 0x01;
        internal const byte EndHeaders = 0x04;
        internal const byte Padded = 0x08;
        internal const byte Priority = 0x20;
    }

    /// <summary>
    /// Builds a single HTTP/2 frame from header parts plus payload.
    /// </summary>
    internal static byte[] BuildFrame(byte type, byte flags, uint streamId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0xFFFFFF)
        {
            throw new ArgumentException("Payload too large for HTTP/2 frame");
        }
        byte[] frame = new byte[9 + payload.Length];
        // 24-bit length, big-endian
        frame[0] = (byte)((payload.Length >> 16) & 0xFF);
        frame[1] = (byte)((payload.Length >> 8) & 0xFF);
        frame[2] = (byte)(payload.Length & 0xFF);
        frame[3] = type;
        frame[4] = flags;
        // R bit must be 0; mask top bit of stream id.
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5, 4), streamId & 0x7FFFFFFFu);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }

    /// <summary>
    /// Builds a SETTINGS frame body from id/value pairs (each 6 bytes).
    /// </summary>
    internal static byte[] BuildSettingsBody(params (ushort id, uint value)[] settings)
    {
        byte[] body = new byte[settings.Length * 6];
        for (int i = 0; i < settings.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6 * i, 2), settings[i].id);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(6 * i + 2, 4), settings[i].value);
        }
        return body;
    }

    /// <summary>Builds a PING frame body (always 8 opaque bytes).</summary>
    internal static byte[] BuildPingBody(ulong opaque)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, opaque);
        return body;
    }

    /// <summary>Builds a WINDOW_UPDATE frame body (4 bytes, R + 31-bit increment).</summary>
    internal static byte[] BuildWindowUpdateBody(uint increment)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, increment & 0x7FFFFFFFu);
        return body;
    }

    /// <summary>Builds a RST_STREAM frame body (4 bytes, error code).</summary>
    internal static byte[] BuildRstStreamBody(uint errorCode)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, errorCode);
        return body;
    }

    /// <summary>Builds a GOAWAY frame body (4 + 4 + variable debug data).</summary>
    internal static byte[] BuildGoawayBody(uint lastStreamId, uint errorCode, ReadOnlySpan<byte> debugData)
    {
        byte[] body = new byte[8 + debugData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), lastStreamId & 0x7FFFFFFFu);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), errorCode);
        debugData.CopyTo(body.AsSpan(8));
        return body;
    }

    /// <summary>
    /// Builds an HPACK-encoded header block consisting of a single
    /// "Indexed Header Field" entry referencing an entry from the static table.
    /// (RFC 7541 §6.1: top bit set, 7-bit prefix integer.)
    /// </summary>
    internal static byte[] BuildHpackIndexed(byte staticTableIndex)
    {
        if ((staticTableIndex & 0x80) != 0)
        {
            throw new ArgumentException("Index must fit in 7 bits");
        }
        return [(byte)(0x80 | staticTableIndex)];
    }

    /// <summary>
    /// Wraps raw HTTP/2 bytes in Eth+IPv4+TCP using port 8443 (which is the
    /// port HTTP/2 is registered at in this stack).
    /// </summary>
    internal static byte[] WrapTcp(ReadOnlySpan<byte> http2Bytes, ushort srcPort = 12345, ushort dstPort = 8443)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(srcPort, dstPort, seqNum: 1, ackNum: 0, flags: 0x18);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(http2Bytes);
    }
}
