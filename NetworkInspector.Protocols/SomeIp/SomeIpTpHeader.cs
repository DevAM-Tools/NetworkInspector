// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP Transport Protocol (TP) header (4 bytes).
/// When the TP flag (bit 5) is set in the SOME/IP message type, the first
/// 4 bytes of the payload contain the TP header.
/// <code>
/// | Bits 31-4   | Bits 3-1  | Bit 0         |
/// | Offset (28) | Reserved  | More Segments |
/// </code>
/// The offset value represents the byte position in the reassembled message
/// (the upper 28 bits of the raw 32-bit value, i.e., the raw value with
/// lower 4 bits masked off, giving 16-byte granularity).
/// </summary>
internal readonly struct SomeIpTpHeader
{
    /// <summary>Size of the SOME/IP-TP header in bytes.</summary>
    internal const int Size = 4;

    /// <summary>Mask for the 28-bit offset field (upper 28 bits).</summary>
    private const uint OffsetMask = 0xFFFF_FFF0;

    /// <summary>Mask for the More Segments flag (bit 0).</summary>
    private const uint MoreMask = 0x0000_0001;

    /// <summary>Mask for the reserved bits (bits 1-3).</summary>
    private const uint ReservedMask = 0x0000_000E;

    /// <summary>
    /// Byte offset in the reassembled message. This is the raw upper 28 bits
    /// masked directly — the value equals the byte offset (16-byte granularity
    /// is encoded in the wire format).
    /// </summary>
    internal uint ByteOffset
    {
        get;
    }

    /// <summary>True if more segments follow this one.</summary>
    internal bool MoreSegments
    {
        get;
    }

    /// <summary>Reserved bits (should be 0 in valid packets).</summary>
    internal byte Reserved
    {
        get;
    }

    private SomeIpTpHeader(uint byteOffset, bool moreSegments, byte reserved)
    {
        ByteOffset = byteOffset;
        MoreSegments = moreSegments;
        Reserved = reserved;
    }

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
        uint byteOffset = raw & OffsetMask;
        bool more = (raw & MoreMask) != 0;
        byte reserved = (byte)((raw & ReservedMask) >> 1);

        header = new SomeIpTpHeader(byteOffset, more, reserved);
        return true;
    }
}
