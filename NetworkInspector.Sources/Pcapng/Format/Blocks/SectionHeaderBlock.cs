// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Section Header Block (SHB) — 24 bytes.
/// Marks the start of a new section. Fields are read as little-endian;
/// if <see cref="ByteOrderMagic"/> equals <see cref="PcapConstants.PcapngSwappedMagic"/>,
/// all fields must be swapped to the correct byte order.
/// </summary>
[BinaryParsable]
internal readonly partial struct SectionHeaderBlock
{
    /// <summary>Block type identifier. Always 0x0A0D0D0A for SHB (palindromic).</summary>
    public U32LE BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including this field and trailing copy).</summary>
    public U32LE BlockTotalLength
    {
        get; init;
    }

    /// <summary>Byte-order magic: 0x1A2B3C4D (native) or 0x4D3C2B1A (swapped).</summary>
    public U32LE ByteOrderMagic
    {
        get; init;
    }

    /// <summary>Major version number (1).</summary>
    public U16LE MajorVersion
    {
        get; init;
    }

    /// <summary>Minor version number (0).</summary>
    public U16LE MinorVersion
    {
        get; init;
    }

    /// <summary>Section length in bytes, or -1 (0xFFFFFFFFFFFFFFFF) if unspecified.</summary>
    public I64LE SectionLength
    {
        get; init;
    }
}
