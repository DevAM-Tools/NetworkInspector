// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP Transport Protocol (TP) header (4 bytes).
/// When the TP flag (bit 5) is set in the SOME/IP message type, the first
/// 4 bytes of the payload contain the TP header.
/// </summary>
internal readonly record struct SomeIpTpHeader(uint ByteOffset, bool MoreSegments, byte Reserved)
{
    #region Constants

    /// <summary>Size of the SOME/IP-TP header in bytes.</summary>
    internal const int Size = 4;

    /// <summary>Mask for the 28-bit offset field (upper 28 bits).</summary>
    private const uint _OffsetMask = 0xFFFF_FFF0;

    /// <summary>Mask for the More Segments flag (bit 0).</summary>
    private const uint _MoreMask = 0x0000_0001;

    /// <summary>Mask for the reserved bits (bits 1-3).</summary>
    private const uint _ReservedMask = 0x0000_000E;

    #endregion

    #region Parsing

    /// <summary>Attempts to parse a 4-byte SOME/IP-TP header.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> data, out SomeIpTpHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        uint raw = BinaryPrimitives.ReadUInt32BigEndian(data);

        // Byte offset = upper 28 bits (raw & 0xFFFFFFF0). The wire encoding
        // stores the value such that masking gives the byte offset directly.
        uint byteOffset = raw & _OffsetMask;
        bool more = (raw & _MoreMask) != 0;
        byte reserved = (byte)((raw & _ReservedMask) >> 1);

        header = new SomeIpTpHeader(byteOffset, more, reserved);
        return true;
    }

    #endregion
}
