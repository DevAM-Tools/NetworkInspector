// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Section Header Block (SHB) — 24 bytes.
/// Marks the start of a new section. Fields are read as little-endian;
/// if <see cref="ByteOrderMagic"/> equals <see cref="PcapConstants.PcapngSwappedMagic"/>,
/// all fields must be swapped to the correct byte order. The 24 bytes are the fixed part only; the section options
/// and the trailing block length follow. Each section carries its own byte order, so a file can mix sections of
/// both byte orders.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct SectionHeaderBlock
{
    /// <summary>Block type identifier. Always 0x0A0D0D0A for SHB (palindromic).</summary>
    public uint BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including this field and trailing copy).</summary>
    public uint BlockTotalLength
    {
        get; init;
    }

    /// <summary>Byte-order magic: 0x1A2B3C4D (native) or 0x4D3C2B1A (swapped).</summary>
    public uint ByteOrderMagic
    {
        get; init;
    }

    /// <summary>Major version number (1).</summary>
    public ushort MajorVersion
    {
        get; init;
    }

    /// <summary>Minor version number (0).</summary>
    public ushort MinorVersion
    {
        get; init;
    }

    /// <summary>Section length in bytes, or -1 (0xFFFFFFFFFFFFFFFF) if unspecified.</summary>
    public long SectionLength
    {
        get; init;
    }

    /// <summary>Fixed part size in bytes.</summary>
    internal const int Size = 24;

    /// <summary>Decodes the 24-byte fixed part; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out SectionHeaderBlock value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new SectionHeaderBlock
        {
            BlockType = BinaryPrimitives.ReadUInt32LittleEndian(h),             // 0..4
            BlockTotalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), // 4..8
            ByteOrderMagic = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),   // 8..12
            MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]),    // 12..14
            MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]),    // 14..16
            SectionLength = BinaryPrimitives.ReadInt64LittleEndian(h[16..]),    // 16..24 (signed: -1 = unknown)
        };
        return true;
    }
}
