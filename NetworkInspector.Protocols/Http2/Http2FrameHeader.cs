// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// HTTP/2 frame header (RFC 7540 Section 4.1).
/// 9-byte fixed header: Length(3) + Type(1) + Flags(1) + R(1 bit) + StreamId(31 bits).
/// </summary>
/// <param name="Length">The payload length in bytes (24-bit value, so 0 to 16,777,215); excludes the 9 header bytes. Not checked against
/// the connection's negotiated maximum frame size here.</param>
/// <param name="Type">The frame type octet.</param>
/// <param name="Flags">The raw flags octet.</param>
/// <param name="StreamId">The 31-bit stream identifier with the reserved bit already cleared.</param>
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
    /// <param name="data">At least 9 bytes of data; only the first 9 are read.</param>
    /// <param name="header">Parsed header; <see langword="default"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> if parsed successfully; <see langword="false"/> when fewer than 9 bytes are given.</returns>
    /// <remarks>
    /// Every bit pattern of 9 bytes is a parseable header, so success says nothing about plausibility: the caller
    /// validates the type and length before trusting the header.
    /// </remarks>
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

        // 4-byte big-endian stream ID. The top bit is reserved and must be ignored on receipt (RFC 7540 section 4.1),
        // so it is masked off after the shift (data[5] << 24 may set the int sign bit, which the mask clears).
        uint streamId = (uint)(
            (data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8]) & 0x7FFFFFFFU;

        header = new Http2FrameHeader(length, type, flags, streamId);
        return true;
    }

    #endregion
}
