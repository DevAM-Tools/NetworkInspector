// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the IPv6 dissector (Plan §3.1.3).
/// Exercises the fixed header plus one frame variant per supported extension
/// header (Hop-by-Hop, Routing, Destination Options, Fragment).
/// </summary>
/// <remarks>
/// <para>
/// Frames are emitted exclusively via the <see cref="FrameStack"/> API so the
/// extension chain itself goes through the production frame builder; no
/// hand-crafted byte arrays are used. Comparison goes through
/// <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], (string, string)[])"/>.
/// </para>
/// <para>
/// The fixed-header layer (<see cref="IPv6Layer"/>) does not currently expose
/// Traffic Class or Flow Label setters, so those two fields are pinned to the
/// expected zero value tshark also reports — that still catches a regression
/// where either side starts emitting a non-zero value silently.
/// </para>
/// <para>Thread safety: stateless; the shared parser stack is read-only.</para>
/// </remarks>
internal sealed class IPv6TsharkTests
{
    #region Frame builders

    // 2001:db8::1 / 2001:db8::2
    private static readonly byte[] _SrcAddrBytes =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _DstAddrBytes =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>Fixed Ethernet+IPv6+UDP frame; no extension headers.</summary>
    private static byte[] BuildPlainFrame(byte hopLimit = 64)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes), hopLimit: hopLimit);
        UdpLayer udp = new(12345, 53);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Frame with an IPv6 Hop-by-Hop extension header (PadN-only minimum form).</summary>
    private static byte[] BuildHopByHopFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes));
        IPv6HopByHopLayer hbh = new();
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        return FrameStack.Start(eth).Then(ip).Then(hbh).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Frame with an IPv6 Routing extension header (default zero-segment form).</summary>
    private static byte[] BuildRoutingFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes));
        IPv6RoutingLayer routing = new();
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xCA, 0xFE];
        return FrameStack.Start(eth).Then(ip).Then(routing).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Frame with an IPv6 Destination Options extension header.</summary>
    private static byte[] BuildDestinationOptionsFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes));
        IPv6DestinationOptionsLayer dst = new();
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xCA, 0xFE];
        return FrameStack.Start(eth).Then(ip).Then(dst).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Frame with an IPv6 Fragment extension header (single, complete fragment).</summary>
    private static byte[] BuildFragmentFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes));
        IPv6FragmentExtensionLayer frag = new(identification: 0x12345678u);
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        return FrameStack.Start(eth).Then(ip).Then(frag).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    #endregion

    #region Fixed-header coverage

    /// <summary>
    /// Verifies all fields the dissector exposes for a plain IPv6 frame.
    /// Traffic Class / Flow Label are produced as zero by both sides.
    /// </summary>
    [Test]
    public async Task IPv6_PlainFrame_AllFixedHeaderFieldsMatchTshark()
    {
        byte[] frame = BuildPlainFrame(hopLimit: 128);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ipv6.version", "ipv6.version"),
                ("ipv6.tclass", "ipv6.tclass"),
                ("ipv6.flow", "ipv6.flow"),
                ("ipv6.plen", "ipv6.plen"),
                ("ipv6.nxt", "ipv6.nxt"),
                ("ipv6.hlim", "ipv6.hlim"),
                ("ipv6.src", "ipv6.src"),
                ("ipv6.dst", "ipv6.dst")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Extension-header coverage

    /// <summary>NextHeader=0 (Hop-by-Hop) and src/dst stay symmetric.</summary>
    [Test]
    public async Task IPv6_HopByHopExtension_NextHeaderAndAddressesMatchTshark()
    {
        byte[] frame = BuildHopByHopFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ipv6.nxt", "ipv6.nxt"),
                ("ipv6.src", "ipv6.src"),
                ("ipv6.dst", "ipv6.dst"),
                ("ipv6.plen", "ipv6.plen")).ConfigureAwait(false);
        }
    }

    /// <summary>NextHeader=43 (Routing) and core fields stay symmetric.</summary>
    [Test]
    public async Task IPv6_RoutingExtension_NextHeaderAndAddressesMatchTshark()
    {
        byte[] frame = BuildRoutingFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ipv6.nxt", "ipv6.nxt"),
                ("ipv6.src", "ipv6.src"),
                ("ipv6.dst", "ipv6.dst"),
                ("ipv6.plen", "ipv6.plen")).ConfigureAwait(false);
        }
    }

    /// <summary>NextHeader=60 (Destination Options) and core fields stay symmetric.</summary>
    [Test]
    public async Task IPv6_DestinationOptionsExtension_NextHeaderAndAddressesMatchTshark()
    {
        byte[] frame = BuildDestinationOptionsFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ipv6.nxt", "ipv6.nxt"),
                ("ipv6.src", "ipv6.src"),
                ("ipv6.dst", "ipv6.dst"),
                ("ipv6.plen", "ipv6.plen")).ConfigureAwait(false);
        }
    }

    /// <summary>NextHeader=44 (Fragment) and core fields stay symmetric.</summary>
    [Test]
    public async Task IPv6_FragmentExtension_NextHeaderAndAddressesMatchTshark()
    {
        byte[] frame = BuildFragmentFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ipv6.nxt", "ipv6.nxt"),
                ("ipv6.src", "ipv6.src"),
                ("ipv6.dst", "ipv6.dst"),
                ("ipv6.plen", "ipv6.plen")).ConfigureAwait(false);
        }
    }

    #endregion
}
