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
[BinaryParsable]
internal readonly partial struct SimplePacketBlock
{
    /// <summary>Block type identifier. Always 0x00000003 for SPB.</summary>
    public U32LE BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public U32LE BlockTotalLength
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public U32LE OriginalPacketLength
    {
        get; init;
    }
}
