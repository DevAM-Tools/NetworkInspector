// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Obsolete Packet Block (PB) — 28 bytes.
/// Deprecated in favour of the Enhanced Packet Block.
/// Notable difference: interface_id is 16 bits (vs 32 bits in EPB).
/// Fields are read as little-endian; byte-swapping applied externally.
/// </summary>
[BinaryParsable]
internal readonly partial struct ObsoletePacketBlock
{
    /// <summary>Block type identifier. Always 0x00000002 for PB.</summary>
    public U32LE BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public U32LE BlockTotalLength
    {
        get; init;
    }

    /// <summary>Zero-based interface ID (16 bits, unlike EPB's 32 bits).</summary>
    public U16LE InterfaceId
    {
        get; init;
    }

    /// <summary>Number of packets dropped between this and the previous packet.</summary>
    public U16LE DropsCount
    {
        get; init;
    }

    /// <summary>Upper 32 bits of the 64-bit timestamp.</summary>
    public U32LE TimestampHigh
    {
        get; init;
    }

    /// <summary>Lower 32 bits of the 64-bit timestamp.</summary>
    public U32LE TimestampLow
    {
        get; init;
    }

    /// <summary>Number of octets actually captured and stored in this block.</summary>
    public U32LE CapturedLength
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public U32LE OriginalLength
    {
        get; init;
    }
}
