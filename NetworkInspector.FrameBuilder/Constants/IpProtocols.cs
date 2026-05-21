// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Constants;

/// <summary>
/// Standard IPv4 protocol numbers assigned by IANA.
/// </summary>
public static class IpProtocols
{
    /// <summary>IPv6 Hop-by-Hop Options extension header (0).</summary>
    public const byte IPv6HopByHop = 0;

    /// <summary>Internet Control Message Protocol (1).</summary>
    public const byte Icmp = 1;

    /// <summary>Transmission Control Protocol (6).</summary>
    public const byte Tcp = 6;

    /// <summary>User Datagram Protocol (17).</summary>
    public const byte Udp = 17;

    /// <summary>IPv6 Routing extension header (43).</summary>
    public const byte IPv6Routing = 43;

    /// <summary>IPv6 Fragment extension header (44).</summary>
    public const byte IPv6Fragment = 44;

    /// <summary>Internet Control Message Protocol for IPv6 (58).</summary>
    public const byte IcmpV6 = 58;

    /// <summary>IPv6 No Next Header marker (59).</summary>
    public const byte IPv6NoNextHeader = 59;

    /// <summary>IPv6 Destination Options extension header (60).</summary>
    public const byte IPv6DestinationOptions = 60;
}
