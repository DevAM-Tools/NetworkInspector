// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Enhanced Packet Block (EPB) — 28 bytes.
/// Standard packet format with interface ID, timestamps, and options.
/// Fields are read as little-endian; byte-swapping applied externally.
/// The 28 bytes are the fixed part only: the packet data, its padding to 4 bytes, the options, and the trailing
/// block length follow.
/// </summary>
/// <remarks>
/// Layout reference only: the production readers do not parse this struct. <c>PcapngPacketBlocks.TryReadEpb</c> reads
/// the same fields directly with an endian-aware reader because EPB parsing is the per-packet hot path (every scanned
/// packet and every <c>FrameById</c>), so direct reads replace the generic struct parse there.
/// The struct documents the on-disk layout and is covered by a parity test, so it must stay in step with that code.
/// </remarks>
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
