// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Legacy PCAP global header — 24 bytes.
/// Appears once at the start of a legacy .pcap file.
/// Fields are read as little-endian; byte-swapping applied
/// if magic matches a swapped variant.
/// </summary>
[BinaryParsable]
internal readonly partial struct PcapGlobalHeader
{
    /// <summary>Magic number identifying format and byte order.</summary>
    public U32LE Magic
    {
        get; init;
    }

    /// <summary>Major PCAP version (typically 2).</summary>
    public U16LE VersionMajor
    {
        get; init;
    }

    /// <summary>Minor PCAP version (typically 4).</summary>
    public U16LE VersionMinor
    {
        get; init;
    }

    /// <summary>GMT to local correction (usually 0).</summary>
    public U32LE ThisZone
    {
        get; init;
    }

    /// <summary>Accuracy of timestamps (usually 0).</summary>
    public U32LE SigFigs
    {
        get; init;
    }

    /// <summary>Snapshot length — maximum number of octets captured.</summary>
    public U32LE SnapLen
    {
        get; init;
    }

    /// <summary>Link-layer type code (see IANA LINKTYPE registry).</summary>
    public U32LE Network
    {
        get; init;
    }
}
