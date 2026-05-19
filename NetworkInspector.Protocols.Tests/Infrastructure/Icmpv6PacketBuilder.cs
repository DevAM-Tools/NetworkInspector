// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.FrameBuilder.Constants;
using NetworkInspector.FrameBuilder.Core;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Builds well-formed Ethernet + IPv6 + ICMPv6 frames for protocol tests.
/// Uses <see cref="FrameStack"/> for the Ethernet/IPv6 layers and patches
/// the ICMPv6 checksum (over the IPv6 pseudo-header per RFC 4443 §2.3)
/// in place via <see cref="ChecksumUtils.PseudoHeaderIPv6"/>. The ICMPv6
/// body (type/code/checksum/payload) is supplied by the caller — there is
/// no FrameBuilder layer for the wide range of ICMPv6 message bodies
/// (NDP, MLD, error reports, …).
/// <para>
/// This helper is the test-side equivalent of an "ICMPv6 message writer";
/// it lets every test focus on the message body bytes instead of duplicating
/// frame plumbing.
/// </para>
/// <para>Thread safety: stateless static methods, safe for concurrent use.</para>
/// </summary>
internal static class Icmpv6PacketBuilder
{
    private const int EthernetHeaderSize = 14; // bytes
    private const int Ipv6HeaderSize = 40;     // bytes
    private const int ChecksumOffset = 2;       // within the ICMPv6 header

    private static readonly MacAddress _DefaultDstMac = MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]);
    private static readonly MacAddress _DefaultSrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly byte[] _DefaultSrcIp = [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _DefaultDstIp = [0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];

    /// <summary>
    /// Builds an Eth+IPv6+ICMPv6 frame. The ICMPv6 header (4 bytes:
    /// type/code/checksum) is generated from <paramref name="type"/> and
    /// <paramref name="code"/>; the checksum is computed and patched in
    /// after the body is appended. <paramref name="body"/> is everything
    /// after byte 4 of the ICMPv6 packet.
    /// </summary>
    /// <param name="type">ICMPv6 type field.</param>
    /// <param name="code">ICMPv6 code field.</param>
    /// <param name="body">Bytes following the 4-byte ICMPv6 header.</param>
    /// <param name="srcIp">Source IPv6 address (16 bytes). Defaults to fe80::1.</param>
    /// <param name="dstIp">Destination IPv6 address (16 bytes). Defaults to ff02::1.</param>
    /// <returns>The complete frame ready to be parsed or written to PCAPNG.</returns>
    internal static byte[] Build(
        byte type,
        byte code,
        ReadOnlySpan<byte> body,
        byte[]? srcIp = null,
        byte[]? dstIp = null)
    {
        srcIp ??= _DefaultSrcIp;
        dstIp ??= _DefaultDstIp;

        // 4-byte ICMPv6 header + body becomes the IPv6 payload.
        int icmpLen = 4 + body.Length;
        byte[] icmpPacket = new byte[icmpLen];
        icmpPacket[0] = type;
        icmpPacket[1] = code;
        // Checksum stays 0 here — patched after the frame is built.
        body.CopyTo(icmpPacket.AsSpan(4));

        // Build via FrameStack with explicit nextHeader=58 (ICMPv6).
        EthernetLayer eth = new(_DefaultDstMac, _DefaultSrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(srcIp), IPv6Address.FromBytes(dstIp), nextHeader: 58 /* ICMPv6 */);

        byte[] buffer = FrameStack.Start(eth).Then(ip).CreateWithFixedValues().EmitFrame(icmpPacket);
        int len = buffer.Length;

        // Patch ICMPv6 checksum over IPv6 pseudo-header (RFC 4443 §2.3).
        int icmpOffset = EthernetHeaderSize + Ipv6HeaderSize;
        Span<byte> bufferSpan = buffer.AsSpan(0, len);
        // Zero checksum field before computing
        bufferSpan[icmpOffset + ChecksumOffset] = 0;
        bufferSpan[icmpOffset + ChecksumOffset + 1] = 0;
        ReadOnlySpan<byte> segment = bufferSpan[icmpOffset..];
        ushort checksum = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.IcmpV6, segment);
        BinaryPrimitives.WriteUInt16BigEndian(bufferSpan.Slice(icmpOffset + ChecksumOffset, 2), checksum);

        return buffer;
    }

    /// <summary>
    /// Builds an Eth+IPv6+ICMPv6 frame with a deliberately invalid checksum.
    /// Used by malformed-packet tests to exercise checksum-validation paths.
    /// </summary>
    internal static byte[] BuildWithRawChecksum(
        byte type,
        byte code,
        ushort rawChecksum,
        ReadOnlySpan<byte> body)
    {
        int icmpLen = 4 + body.Length;
        byte[] icmpPacket = new byte[icmpLen];
        icmpPacket[0] = type;
        icmpPacket[1] = code;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(ChecksumOffset, 2), rawChecksum);
        body.CopyTo(icmpPacket.AsSpan(4));

        EthernetLayer eth = new(_DefaultDstMac, _DefaultSrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_DefaultSrcIp), IPv6Address.FromBytes(_DefaultDstIp), nextHeader: 58);

        return FrameStack.Start(eth).Then(ip).CreateWithFixedValues().EmitFrame(icmpPacket);
    }

    #region NDP message body builders

    /// <summary>
    /// Builds a Router Advertisement body (RFC 4861 §4.2): cur-hop-limit,
    /// flags (M/O), router lifetime, reachable time, retrans timer, options.
    /// </summary>
    internal static byte[] BuildRouterAdvertisementBody(
        byte curHopLimit,
        bool managed,
        bool other,
        ushort routerLifetimeSec,
        uint reachableTimeMs,
        uint retransTimerMs,
        ReadOnlySpan<byte> options)
    {
        byte[] body = new byte[12 + options.Length];
        body[0] = curHopLimit;
        byte flags = 0;
        if (managed)
        {
            flags |= 0x80;
        }
        if (other)
        {
            flags |= 0x40;
        }
        body[1] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), routerLifetimeSec);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), reachableTimeMs);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), retransTimerMs);
        options.CopyTo(body.AsSpan(12));
        return body;
    }

    /// <summary>
    /// Builds a Router Solicitation body (RFC 4861 §4.1): 4 reserved bytes + options.
    /// </summary>
    internal static byte[] BuildRouterSolicitationBody(ReadOnlySpan<byte> options)
    {
        byte[] body = new byte[4 + options.Length];
        options.CopyTo(body.AsSpan(4));
        return body;
    }

    /// <summary>
    /// Builds a Neighbor Solicitation body (RFC 4861 §4.3): 4 reserved bytes,
    /// target IPv6 address (16 bytes), options.
    /// </summary>
    internal static byte[] BuildNeighborSolicitationBody(ReadOnlySpan<byte> targetIp, ReadOnlySpan<byte> options)
    {
        byte[] body = new byte[20 + options.Length];
        targetIp.CopyTo(body.AsSpan(4, 16));
        options.CopyTo(body.AsSpan(20));
        return body;
    }

    /// <summary>
    /// Builds a Neighbor Advertisement body (RFC 4861 §4.4): 4 flag bytes
    /// (R/S/O), target IPv6 address (16 bytes), options.
    /// </summary>
    internal static byte[] BuildNeighborAdvertisementBody(
        bool router, bool solicited, bool overrideFlag,
        ReadOnlySpan<byte> targetIp, ReadOnlySpan<byte> options)
    {
        byte[] body = new byte[20 + options.Length];
        uint flags = 0;
        if (router)
        {
            flags |= 0x80000000u;
        }
        if (solicited)
        {
            flags |= 0x40000000u;
        }
        if (overrideFlag)
        {
            flags |= 0x20000000u;
        }
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), flags);
        targetIp.CopyTo(body.AsSpan(4, 16));
        options.CopyTo(body.AsSpan(20));
        return body;
    }

    /// <summary>
    /// Builds a Redirect body (RFC 4861 §4.5): 4 reserved bytes, target IPv6 address,
    /// destination IPv6 address, options.
    /// </summary>
    internal static byte[] BuildRedirectBody(
        ReadOnlySpan<byte> targetIp, ReadOnlySpan<byte> destinationIp, ReadOnlySpan<byte> options)
    {
        byte[] body = new byte[36 + options.Length];
        targetIp.CopyTo(body.AsSpan(4, 16));
        destinationIp.CopyTo(body.AsSpan(20, 16));
        options.CopyTo(body.AsSpan(36));
        return body;
    }

    #endregion

    #region NDP option builders

    /// <summary>
    /// Builds a Source/Target Link-Layer Address option (RFC 4861 §4.6.1).
    /// Type 1 = source, type 2 = target. Length 1 (= 8 bytes total) for Ethernet MACs.
    /// </summary>
    internal static byte[] BuildLinkLayerAddressOption(byte optType, ReadOnlySpan<byte> mac6)
    {
        byte[] opt = new byte[8];
        opt[0] = optType;
        opt[1] = 1; // length in 8-byte units
        mac6.CopyTo(opt.AsSpan(2, 6));
        return opt;
    }

    /// <summary>
    /// Builds a Prefix Information option (RFC 4861 §4.6.2). Always 32 bytes.
    /// </summary>
    internal static byte[] BuildPrefixInformationOption(
        byte prefixLength,
        bool onLink,
        bool autonomous,
        uint validLifetimeSec,
        uint preferredLifetimeSec,
        ReadOnlySpan<byte> prefix)
    {
        byte[] opt = new byte[32];
        opt[0] = 3; // type
        opt[1] = 4; // length in 8-byte units (32 bytes)
        opt[2] = prefixLength;
        byte flags = 0;
        if (onLink)
        {
            flags |= 0x80;
        }
        if (autonomous)
        {
            flags |= 0x40;
        }
        opt[3] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(4, 4), validLifetimeSec);
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(8, 4), preferredLifetimeSec);
        // bytes 12..15 reserved
        prefix.CopyTo(opt.AsSpan(16, 16));
        return opt;
    }

    /// <summary>
    /// Builds an MTU option (RFC 4861 §4.6.4). Always 8 bytes.
    /// </summary>
    internal static byte[] BuildMtuOption(uint mtuBytes)
    {
        byte[] opt = new byte[8];
        opt[0] = 5; // type
        opt[1] = 1; // length in 8-byte units
        // bytes 2..3 reserved
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(4, 4), mtuBytes);
        return opt;
    }

    #endregion
}
