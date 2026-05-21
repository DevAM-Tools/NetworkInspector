// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Legacy PCAP packet header — 16 bytes.
/// Precedes each captured packet in a legacy .pcap file.
/// Fields are read as little-endian; byte-swapping applied
/// based on the global header's magic number.
/// </summary>
[BinaryParsable]
internal readonly partial struct PcapPacketHeader
{
    /// <summary>Timestamp seconds since epoch.</summary>
    public U32LE TsSec
    {
        get; init;
    }

    /// <summary>
    /// Timestamp fractional part — microseconds or nanoseconds
    /// depending on the global header magic number.
    /// </summary>
    public U32LE TsFrac
    {
        get; init;
    }

    /// <summary>Number of octets captured and saved in the file.</summary>
    public U32LE InclLen
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public U32LE OrigLen
    {
        get; init;
    }
}
