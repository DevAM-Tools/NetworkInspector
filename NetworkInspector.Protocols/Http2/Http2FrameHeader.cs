// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// HTTP/2 frame header (RFC 7540 Section 4.1).
/// 9-byte fixed header: Length(3) + Type(1) + Flags(1) + R(1 bit) + StreamId(31 bits).
/// </summary>
internal readonly record struct Http2FrameHeader(int Length, byte Type, byte Flags, uint StreamId)
{
    #region Constants

    /// <summary>Size of the HTTP/2 frame header in bytes.</summary>
    public const int Size = 9;

    #endregion

    #region Parsing

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

    #endregion

    #region Validation

    /// <summary>
    /// Checks if this looks like a valid HTTP/2 frame type (0-9 are defined in RFC 7540).
    /// Types 10-255 are extensions (also valid but less common).
    /// </summary>
    public bool IsKnownFrameType() => Type <= 9;

    #endregion
}
