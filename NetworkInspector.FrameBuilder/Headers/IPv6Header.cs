// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IPv6 header (40 bytes, fixed size).
/// Layout per RFC 8200: VersionClassFlow(4), PayloadLength(2), NextHeader(1),
/// HopLimit(1), SrcAddr(16), DstAddr(16).
/// </summary>
/// <remarks>
/// <para>PayloadLength is left at 0 and patched by <c>IPv6Layer</c>'s <c>FixPhase.Length</c> post-fix.</para>
/// <para>Extension headers (Hop-by-Hop, Routing, Destination Options, Fragment) can be appended after this
/// header by chaining the corresponding extension-header layers.</para>
/// </remarks>
[BinaryWritable]
internal readonly partial struct IPv6Header
{
    /// <summary>Size of the IPv6 header in bytes.</summary>
    internal const int Size = 40;

    /// <summary>
    /// Version(4 bits) + Traffic Class(8 bits) + Flow Label(20 bits).
    /// Use <see cref="MakeVersionClassFlow"/> to construct.
    /// </summary>
    internal U32BE VersionClassFlow
    {
        get; init;
    }

    /// <summary>Length of the payload after this header. Set to 0 for fixup.</summary>
    internal U16BE PayloadLength
    {
        get; init;
    }

    /// <summary>Next header type (6=TCP, 17=UDP, 58=ICMPv6, 0=HopByHop, 43=Routing, 44=Fragment).</summary>
    internal byte NextHeader
    {
        get; init;
    }

    /// <summary>Hop limit (equivalent to IPv4 TTL).</summary>
    internal byte HopLimit
    {
        get; init;
    }

    /// <summary>Source IPv6 address.</summary>
    internal IPv6Address SrcAddr
    {
        get; init;
    }

    /// <summary>Destination IPv6 address.</summary>
    internal IPv6Address DstAddr
    {
        get; init;
    }

    /// <summary>
    /// Constructs the 32-bit Version+TrafficClass+FlowLabel field.
    /// </summary>
    /// <param name="trafficClass">Traffic class (0–255). Default: 0.</param>
    /// <param name="flowLabel">Flow label (0–0xFFFFF). Default: 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint MakeVersionClassFlow(byte trafficClass = 0, uint flowLabel = 0)
        // Version = 6 (4 bits) | TC (8 bits) | Flow Label (20 bits)
        => (6u << 28) | ((uint)trafficClass << 20) | (flowLabel & 0x000F_FFFF);

    /// <summary>
    /// Creates an IPv6 header with common defaults.
    /// PayloadLength is left at 0 and patched by the <c>IPv6Layer</c> <c>FixPhase.Length</c> post-fix.
    /// </summary>
    /// <param name="srcIp">Source IPv6 address.</param>
    /// <param name="dstIp">Destination IPv6 address.</param>
    /// <param name="nextHeader">Next header protocol number.</param>
    /// <param name="hopLimit">Hop limit. Default: 64.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Header Create(
        IPv6Address srcIp,
        IPv6Address dstIp,
        byte nextHeader,
        byte hopLimit = 64)
    {
        return new IPv6Header
        {
            VersionClassFlow = MakeVersionClassFlow(),
            PayloadLength = (ushort)0, // patched by IPv6Layer FixPhase.Length
            NextHeader = nextHeader,
            HopLimit = hopLimit,
            SrcAddr = srcIp,
            DstAddr = dstIp,
        };
    }
}
