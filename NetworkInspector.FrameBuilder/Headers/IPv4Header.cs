// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IPv4 header (20 bytes, no options).
/// Layout per RFC 791: VersionIhl(1), DscpEcn(1), TotalLength(2), Identification(2),
/// FlagsFragment(2), Ttl(1), Protocol(1), HeaderChecksum(2), SrcAddr(4), DstAddr(4).
/// </summary>
/// <remarks>
/// <para>TotalLength and HeaderChecksum are left at 0 and patched by <c>IPv4Layer</c>'s
/// <c>FixPhase.Length</c> / <c>FixPhase.OuterChecksum</c> post-fixes.</para>
/// <para>Options can be appended after this header by using <c>IPv4LayerWithOptions</c>.</para>
/// </remarks>
[BinaryWritable]
internal readonly partial struct IPv4Header
{
    /// <summary>Size of the base IPv4 header without options in bytes.</summary>
    internal const int Size = 20;

    /// <summary>Maximum IPv4 header size including options in bytes (IHL=15 × 4).</summary>
    internal const int MaxSize = 60;

    /// <summary>Version(4 bits) + IHL(4 bits). 0x45 = IPv4 with 20-byte header (no options).</summary>
    internal byte VersionIhl
    {
        get; init;
    }

    /// <summary>DSCP(6 bits) + ECN(2 bits). Usually 0.</summary>
    internal byte DscpEcn
    {
        get; init;
    }

    /// <summary>Total length including header and payload. Set to 0 for fixup.</summary>
    internal U16BE TotalLength
    {
        get; init;
    }

    /// <summary>Identification field for fragmentation.</summary>
    internal U16BE Identification
    {
        get; init;
    }

    /// <summary>Flags(3 bits) + Fragment Offset(13 bits). 0x4000 = Don't Fragment.</summary>
    internal U16BE FlagsFragment
    {
        get; init;
    }

    /// <summary>Time To Live (hop count).</summary>
    internal byte Ttl
    {
        get; init;
    }

    /// <summary>Next protocol number (6=TCP, 17=UDP, 1=ICMP).</summary>
    internal byte Protocol
    {
        get; init;
    }

    /// <summary>Header checksum. Set to 0 for fixup.</summary>
    internal U16BE HeaderChecksum
    {
        get; init;
    }

    /// <summary>Source IPv4 address.</summary>
    internal IPv4Address SrcAddr
    {
        get; init;
    }

    /// <summary>Destination IPv4 address.</summary>
    internal IPv4Address DstAddr
    {
        get; init;
    }

    /// <summary>
    /// Creates an IPv4 header with common defaults.
    /// TotalLength and HeaderChecksum are left at 0 and patched by <c>IPv4Layer</c>'s <c>FixPhase.Length</c> / <c>FixPhase.OuterChecksum</c> post-fixes.
    /// </summary>
    /// <param name="srcIp">Source address.</param>
    /// <param name="dstIp">Destination address.</param>
    /// <param name="protocol">Next protocol (use <see cref="IpProtocols"/>).</param>
    /// <param name="ttl">Time to live. Default: 64.</param>
    /// <param name="identification">Identification field. Default: 0.</param>
    /// <param name="dontFragment">Set the DF flag. Default: true.</param>
    /// <param name="moreFragments">Set the MF flag (more fragments follow). Default: false.</param>
    /// <param name="fragmentOffset">Fragment offset in 8-byte units (13 bits). Default: 0.</param>
    /// <param name="ihl">Internet Header Length in 32-bit words. Default: 5 (20 bytes, no options).</param>
    /// <param name="typeOfService">
    /// Type of Service byte (DSCP in bits 7–2, ECN in bits 1–0). Default: 0.
    /// Use <c>(byte)((dscp &lt;&lt; 2) | (ecn &amp; 0x3))</c> to compose from DSCP and ECN values.
    /// </param>
    /// <param name="reservedFlag">
    /// IPv4 Reserved flag (bit 15 of FlagsFragment, RFC 791 §3.1). Always 0 in
    /// conforming implementations; exposed for protocol-conformance / corruption tests.
    /// Default: false.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv4Header Create(
        IPv4Address srcIp,
        IPv4Address dstIp,
        byte protocol,
        byte ttl = 64,
        ushort identification = 0,
        bool dontFragment = true,
        bool moreFragments = false,
        ushort fragmentOffset = 0,
        byte ihl = 5,
        byte typeOfService = 0,
        bool reservedFlag = false)
    {
        return new IPv4Header
        {
            VersionIhl = (byte)(0x40 | (ihl & 0x0F)),
            DscpEcn = typeOfService,
            TotalLength = (ushort)0, // patched by IPv4Layer FixPhase.Length
            Identification = identification,
            // Encode Reserved (bit 15), DF (bit 14), MF (bit 13), and Fragment Offset (bits 12–0)
            FlagsFragment = (ushort)(
                (reservedFlag ? 0x8000 : 0) |
                (dontFragment ? 0x4000 : 0) |
                (moreFragments ? 0x2000 : 0) |
                (fragmentOffset & 0x1FFF)),
            Ttl = ttl,
            Protocol = protocol,
            HeaderChecksum = (ushort)0, // patched by IPv4Layer FixPhase.OuterChecksum
            SrcAddr = srcIp,
            DstAddr = dstIp,
        };
    }
}
