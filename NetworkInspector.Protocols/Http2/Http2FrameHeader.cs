// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// HTTP/2 frame header (RFC 7540 Section 4.1).
/// 9-byte fixed header: Length(3) + Type(1) + Flags(1) + R(1 bit) + StreamId(31 bits).
/// </summary>
internal readonly struct Http2FrameHeader
{
    /// <summary>Size of the HTTP/2 frame header in bytes.</summary>
    public const int Size = 9;

    /// <summary>Payload length (24-bit, not including the 9-byte header).</summary>
    public int Length
    {
        get;
    }

    /// <summary>Frame type byte (DATA=0, HEADERS=1, etc.).</summary>
    public byte Type
    {
        get;
    }

    /// <summary>Frame flags byte (type-specific).</summary>
    public byte Flags
    {
        get;
    }

    /// <summary>Stream identifier (31-bit, high bit reserved and masked off).</summary>
    public uint StreamId
    {
        get;
    }

    private Http2FrameHeader(int length, byte type, byte flags, uint streamId)
    {
        Length = length;
        Type = type;
        Flags = flags;
        StreamId = streamId;
    }

    /// <summary>
    /// Tries to parse the 9-byte HTTP/2 frame header from the given span.
    /// </summary>
    /// <param name="data">At least 9 bytes of data.</param>
    /// <param name="header">Parsed header.</param>
    /// <returns><see langword="true"/> if parsed successfully.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out Http2FrameHeader header)
    {
        header = default;
        if (data.Length < Size)
        {
            return false;
        }

        // 3-byte big-endian length
        int length = (data[0] << 16) | (data[1] << 8) | data[2];
        byte type = data[3];
        byte flags = data[4];

        // 4-byte big-endian stream ID (mask off reserved bit)
        uint streamId = (uint)(
            (data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8]) & 0x7FFFFFFFU;

        header = new Http2FrameHeader(length, type, flags, streamId);
        return true;
    }

    /// <summary>
    /// Checks if this looks like a valid HTTP/2 frame type (0-9 are defined in RFC 7540).
    /// Types 10-255 are extensions (also valid but less common).
    /// </summary>
    public bool IsKnownFrameType() =>
        Type <= 9;
}
