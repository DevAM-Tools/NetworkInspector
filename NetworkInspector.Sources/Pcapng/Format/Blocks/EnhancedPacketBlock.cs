// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Enhanced Packet Block (EPB) — 28 bytes.
/// Standard packet format with interface ID, timestamps, and options.
/// Fields are read as little-endian; byte-swapping applied externally.
/// </summary>
[BinaryParsable]
internal readonly partial struct EnhancedPacketBlock
{
    /// <summary>Block type identifier. Always 0x00000006 for EPB.</summary>
    public U32LE BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public U32LE BlockTotalLength
    {
        get; init;
    }

    /// <summary>Zero-based interface ID referencing an IDB in the current section.</summary>
    public U32LE InterfaceId
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
