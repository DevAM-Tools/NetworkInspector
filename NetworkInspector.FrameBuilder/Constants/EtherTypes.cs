// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Constants;

/// <summary>
/// Standard EtherType values for the Ethernet II Type field.
/// </summary>
public static class EtherTypes
{
    /// <summary>Internet Protocol version 4 (0x0800).</summary>
    public const ushort IPv4 = 0x0800;

    /// <summary>Address Resolution Protocol (0x0806).</summary>
    public const ushort Arp = 0x0806;

    /// <summary>IEEE 802.1Q VLAN tagging (0x8100).</summary>
    public const ushort VlanTagged = 0x8100;

    /// <summary>IEEE 802.1ad QinQ double tagging (0x88A8).</summary>
    public const ushort QinQ = 0x88A8;

    /// <summary>Internet Protocol version 6 (0x86DD).</summary>
    public const ushort IPv6 = 0x86DD;
}
