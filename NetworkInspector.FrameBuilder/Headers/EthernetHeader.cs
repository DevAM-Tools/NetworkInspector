// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Ethernet II frame header (14 bytes).
/// Layout: DstMac(6) + SrcMac(6) + EtherType(2 BE).
/// </summary>
[BinaryWritable]
internal readonly partial struct EthernetHeader
{
    /// <summary>Size of the Ethernet header in bytes.</summary>
    internal const int Size = 14;

    /// <summary>Destination MAC address.</summary>
    internal MacAddress DstMac
    {
        get; init;
    }

    /// <summary>Source MAC address.</summary>
    internal MacAddress SrcMac
    {
        get; init;
    }

    /// <summary>
    /// EtherType field identifying the next protocol.
    /// Common values: 0x0800 (IPv4), 0x86DD (IPv6), 0x0806 (ARP), 0x8100 (VLAN).
    /// </summary>
    internal U16BE EtherType
    {
        get; init;
    }
}
