// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Interface Description Block (IDB) — 16 bytes.
/// Describes a capture interface. Fields are read as little-endian;
/// byte-swapping is applied externally based on the enclosing section's byte order.
/// </summary>
[BinaryParsable]
internal readonly partial struct InterfaceDescriptionBlock
{
    /// <summary>Block type identifier. Always 0x00000001 for IDB.</summary>
    public U32LE BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public U32LE BlockTotalLength
    {
        get; init;
    }

    /// <summary>Link-layer type code (see IANA LINKTYPE registry).</summary>
    public U16LE LinkType
    {
        get; init;
    }

    /// <summary>Reserved field, must be zero.</summary>
    public U16LE Reserved
    {
        get; init;
    }

    /// <summary>Snapshot length — maximum number of octets captured from each packet.</summary>
    public U32LE SnapLength
    {
        get; init;
    }
}
