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
internal readonly struct IPv6Header
{
    /// <summary>Size of the IPv6 header in bytes.</summary>
    internal const int Size = 40;

    /// <summary>
    /// Version(4 bits) + Traffic Class(8 bits) + Flow Label(20 bits).
    /// Use <see cref="MakeVersionClassFlow"/> to construct.
    /// </summary>
    internal uint VersionClassFlow
    {
        get; init;
    }

    /// <summary>Length of the payload after this header. Set to 0 for fixup.</summary>
    internal ushort PayloadLength
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

    /// <summary>Writes the 40 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset stores on an exact-size slice, so the JIT drops the
    /// per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        // One length check up front; a short destination is an expected failure, not an exception.
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        // Exact-size slice: the fixed-offset stores below need no further bounds checks after JIT.
        Span<byte> d = destination[..Size];
        BinaryPrimitives.WriteUInt32BigEndian(d, VersionClassFlow);    // 0..4
        BinaryPrimitives.WriteUInt16BigEndian(d[4..], PayloadLength);  // 4..6
        d[6] = NextHeader;                                             // 6
        d[7] = HopLimit;                                               // 7
        _ = SrcAddr.ToBytes(d[8..]);                                   // 8..24 (the slice is exact, so the count is always 16)
        _ = DstAddr.ToBytes(d[24..]);                                  // 24..40
        bytesWritten = Size;
        return true;
    }
}
