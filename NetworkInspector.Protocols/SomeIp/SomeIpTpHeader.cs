// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP Transport Protocol (TP) header (4 bytes).
/// When the TP flag (bit 5, 0x20) is set in the SOME/IP message type, the first
/// 4 bytes of the payload contain the TP header.
/// </summary>
/// <remarks>
/// Wire layout of the big-endian 32-bit word: bits 31..4 hold the offset, bits 3..1 are reserved, bit 0 is the
/// More Segments flag. The offset is transmitted in units of 16 bytes; because it sits in the upper 28 bits, masking
/// the word with <c>0xFFFFFFF0</c> yields the offset in bytes directly (value times 16) without a shift.
/// Thread safety: immutable value type.
/// </remarks>
/// <param name="ByteOffset">Offset of this segment inside the reassembled payload, in bytes (always a multiple of 16).</param>
/// <param name="MoreSegments"><see langword="true"/> when further segments follow this one.</param>
/// <param name="Reserved">The three reserved bits as a number 0..7 (should be 0 on the wire; not enforced).</param>
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

    /// <summary>Attempts to parse a 4-byte SOME/IP-TP header from the start of <paramref name="data"/>.</summary>
    /// <param name="data">Payload bytes starting at the TP header; may be longer than the header.</param>
    /// <param name="header">The decoded header, or <see langword="default"/> when too short.</param>
    /// <returns><see langword="true"/> when at least <see cref="Size"/> bytes were available.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, out SomeIpTpHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        uint raw = BinaryPrimitives.ReadUInt32BigEndian(data);

        // Byte offset = upper 28 bits (raw & 0xFFFFFFF0). The wire value counts 16-byte units and
        // occupies bits 31..4, so masking out the low nibble already equals unit count * 16 (bytes):
        // no shift is needed, and the result cannot overflow a uint.
        uint byteOffset = raw & _OffsetMask;
        bool more = (raw & _MoreMask) != 0;
        byte reserved = (byte)((raw & _ReservedMask) >> 1);

        header = new SomeIpTpHeader(byteOffset, more, reserved);
        return true;
    }

    #endregion
}
