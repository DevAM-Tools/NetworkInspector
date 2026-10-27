// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Simple Packet Block (SPB) — 12 bytes.
/// Minimal packet format without interface ID, timestamps, or options.
/// Only valid when a single interface is defined in the section; the reader always uses interface 0 of the section.
/// Fields are read as little-endian; byte-swapping applied externally.
/// There is no captured-length field: the stored length is the smaller of the original length and the interface's
/// snap length (see <c>PcapngPacketBlocks.TryReadSpb</c>), followed by padding to 4 bytes and the trailing block length.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct SimplePacketBlock
{
    /// <summary>Block type identifier. Always 0x00000003 for SPB.</summary>
    public uint BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public uint BlockTotalLength
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public uint OriginalPacketLength
    {
        get; init;
    }

    /// <summary>Fixed part size in bytes.</summary>
    internal const int Size = 12;

    /// <summary>Decodes the 12-byte fixed part; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out SimplePacketBlock value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new SimplePacketBlock
        {
            BlockType = BinaryPrimitives.ReadUInt32LittleEndian(h),                  // 0..4
            BlockTotalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),      // 4..8
            OriginalPacketLength = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),  // 8..12
        };
        return true;
    }
}
