// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// ICMPv6 NDP tests (RFC 4861) — Router Solicitation/Advertisement,
/// Neighbor Solicitation/Advertisement, Redirect, plus the common NDP
/// options (Source/Target Link-Layer Address, Prefix Information, MTU).
/// Frames are constructed via the typed NDP <see cref="FrameStack"/> layers.
/// </summary>
internal sealed class Icmpv6NdpTests
{
    private static readonly byte[] _TargetIp = [
        0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0x01];

    private static readonly byte[] _Prefix = [
        0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[] _SrcLinkAddr = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    // Default link-local addresses used for all NDP frames in this fixture.
    private static readonly EthernetLayer _Eth = new(
        MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
        MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
    private static readonly IPv6Layer _Ip = new(
        IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
        IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));

    /// <summary>Builds an NDP option: Source/Target Link-Layer Address (RFC 4861 §4.6.1).</summary>
    private static byte[] BuildLinkLayerAddressOption(byte optType, ReadOnlySpan<byte> mac6)
    {
        byte[] opt = new byte[8];
        opt[0] = optType;
        opt[1] = 1; // length in 8-byte units
        mac6.CopyTo(opt.AsSpan(2, 6));
        return opt;
    }

    /// <summary>Builds an NDP option: Prefix Information (RFC 4861 §4.6.2). Always 32 bytes.</summary>
    private static byte[] BuildPrefixInformationOption(
        byte prefixLength, bool onLink, bool autonomous,
        uint validLifetimeSec, uint preferredLifetimeSec,
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
        prefix.CopyTo(opt.AsSpan(16, 16));
        return opt;
    }

    /// <summary>Builds an NDP option: MTU (RFC 4861 §4.6.4). Always 8 bytes.</summary>
    private static byte[] BuildMtuOption(uint mtuBytes)
    {
        byte[] opt = new byte[8];
        opt[0] = 5; // type
        opt[1] = 1; // length in 8-byte units
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(4, 4), mtuBytes);
        return opt;
    }

    [Test]
    public async Task Parse_RouterSolicitation_TypeOnly()
    {
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(new IcmpV6RouterSolicitationLayer()).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 133).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_AllScalarFields()
    {
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: true, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 30_000, retransTimerMs: 1000);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 134).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.cur_hop_limit", 64).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.managed", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.other", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.router_lifetime", 1800).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.reachable_time", 30_000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.retrans_timer", 1000).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_WithSourceLinkAddrOption()
    {
        byte[] opt = BuildLinkLayerAddressOption(optType: 1 /* source */, _SrcLinkAddr);
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: false, other: true,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame(opt);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.len", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "icmpv6.nd.opt.linkaddr", "00:11:22:33:44:55").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_WithMtuOption()
    {
        byte[] opt = BuildMtuOption(mtuBytes: 1500);
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: false, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame(opt);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.type", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.mtu", 1500).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_WithPrefixInformationOption()
    {
        byte[] opt = BuildPrefixInformationOption(
            prefixLength: 64,
            onLink: true,
            autonomous: true,
            validLifetimeSec: 86_400,
            preferredLifetimeSec: 14_400,
            prefix: _Prefix);
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: false, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame(opt);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.type", 3).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.prefix.length", 64).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.opt.prefix.flag.onlink", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.opt.prefix.flag.auto", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.prefix.valid_lifetime", 86_400).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.prefix.preferred_lifetime", 14_400).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.nd.opt.prefix", "2001:db8::").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NeighborSolicitation_TargetAddress()
    {
        IcmpV6NeighborSolicitationLayer ns = new(IPv6Address.FromBytes(_TargetIp));
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ns).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 135).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.nd.target_address", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NeighborAdvertisement_FlagsAndTarget()
    {
        byte[] opt = BuildLinkLayerAddressOption(optType: 2 /* target */, _SrcLinkAddr);
        IcmpV6NeighborAdvertisementLayer na = new(
            IPv6Address.FromBytes(_TargetIp), router: true, solicited: true, overrideFlag: false);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(na).CreateWithFixedValues().EmitFrame(opt);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 136).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.na.flag.router", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.na.flag.solicited", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.na.flag.override", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.nd.target_address", "2001:db8::1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.opt.type", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "icmpv6.nd.opt.linkaddr", "00:11:22:33:44:55").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Redirect_TargetAndDestination()
    {
        byte[] dst = [
            0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0x42];
        // Redirect body: 4 reserved + 16 target + 16 destination (RFC 4861 §4.5).
        byte[] redirectBody = new byte[36];
        _TargetIp.CopyTo(redirectBody.AsSpan(4, 16));
        dst.CopyTo(redirectBody.AsSpan(20, 16));
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(new IcmpV6Layer(137, 0)).CreateWithFixedValues().EmitFrame(redirectBody);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 137).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.nd.target_address", "2001:db8::1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.nd.redirect.dst", "2001:db8::42").ConfigureAwait(false);
        }
    }

    #region Flags display text

    [Test]
    public async Task Parse_RouterAdvertisement_FlagsDisplayText_ManagedAndOther()
    {
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: true, other: true,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ra.flags", "[M, O]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_FlagsDisplayText_ManagedOnly()
    {
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: true, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ra.flags", "[M]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RouterAdvertisement_FlagsDisplayText_None()
    {
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: false, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(ra).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ra.flags", "[None]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NeighborAdvertisement_FlagsDisplayText_RouterAndSolicited()
    {
        IcmpV6NeighborAdvertisementLayer na = new(
            IPv6Address.FromBytes(_TargetIp), router: true, solicited: true, overrideFlag: false);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(na).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.na.flags", "[R, S]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NeighborAdvertisement_FlagsDisplayText_AllSet()
    {
        IcmpV6NeighborAdvertisementLayer na = new(
            IPv6Address.FromBytes(_TargetIp), router: true, solicited: true, overrideFlag: true);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(na).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.na.flags", "[R, S, O]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NeighborAdvertisement_FlagsDisplayText_None()
    {
        IcmpV6NeighborAdvertisementLayer na = new(
            IPv6Address.FromBytes(_TargetIp), router: false, solicited: false, overrideFlag: false);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(na).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.na.flags", "[None]").ConfigureAwait(false);
        }
    }

    #endregion
}
