// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the IPv4 dissector (Plan §3.1.4).
/// Covers the full set of fields the dissector publishes for both the plain
/// 20-byte header and the variable-size <see cref="IPv4LayerWithOptions"/>
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Frames are emitted via <see cref="FrameStack"/>. Field comparison goes
/// through <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], (string, string)[])"/>;
/// the equivalence helper normalises hex/decimal renderings and IPv4 textual
/// formats before comparing.
/// </para>
/// <para>
/// Fragmentation flags / offset are exercised across three frames to cover
/// the orthogonal MF/DF/OFFSET combinations the dissector cares about.
/// </para>
/// <para>Thread safety: stateless tests over the shared parser stack.</para>
/// </remarks>
internal sealed class IPv4TsharkTests
{
    #region Frame builders

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>
    /// Standard Eth+IPv4+UDP frame with explicit identification, TTL and
    /// don't-fragment so every dissector field has a concrete expected value.
    /// </summary>
    private static byte[] BuildPlainFrame(byte ttl = 64, ushort identification = 0xABCD, bool dontFragment = true)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(
            new IPv4Address(0xC0A80101),  // 192.168.1.1
            new IPv4Address(0xC0A80102),  // 192.168.1.2
            ttl: ttl,
            identification: identification,
            dontFragment: dontFragment);
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Frame using <see cref="IPv4LayerWithOptions"/> with a single
    /// "Router Alert" option (RFC 2113: type=148 / 0x94, len=4, value=0x0000).
    /// The option is exactly four bytes so no padding is needed and the IHL
    /// field becomes 6 (24 bytes header).
    /// </summary>
    private static byte[] BuildOptionsFrame()
    {
        byte[] routerAlert = [0x94, 0x04, 0x00, 0x00];
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4LayerWithOptions ip = new(
            new IPv4Address(0xC0A80101),
            new IPv4Address(0xC0A80102),
            options: routerAlert,
            ttl: 64,
            identification: 0x0001);
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xCA, 0xFE];
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    #endregion

    #region Plain header coverage

    /// <summary>
    /// Full field-set verification for the standard 20-byte IPv4 header.
    /// Pins all dissector outputs the layer can reproduce: version, hdr_len,
    /// dsfield (== tos when DSCP/ECN are zero), len, id, df flag, mf flag,
    /// frag_offset, ttl, proto, checksum, src and dst.
    /// </summary>
    [Test]
    public async Task IPv4_PlainFrame_AllFieldsMatchTshark()
    {
        byte[] frame = BuildPlainFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ip.version", "ip.version"),
                ("ip.hdr_len", "ip.hdr_len"),
                ("ip.dscp", "ip.dsfield.dscp"),
                ("ip.ecn", "ip.dsfield.ecn"),
                ("ip.len", "ip.len"),
                ("ip.id", "ip.id"),
                ("ip.flags.df", "ip.flags.df"),
                ("ip.flags.mf", "ip.flags.mf"),
                ("ip.frag_offset", "ip.frag_offset"),
                ("ip.ttl", "ip.ttl"),
                ("ip.proto", "ip.proto"),
                ("ip.checksum", "ip.checksum"),
                ("ip.src", "ip.src"),
                ("ip.dst", "ip.dst")).ConfigureAwait(false);
        }
    }

    /// <summary>DontFragment cleared — pins MF/DF/offset orthogonally to the default frame.</summary>
    [Test]
    public async Task IPv4_DontFragmentCleared_FlagFieldsMatchTshark()
    {
        byte[] frame = BuildPlainFrame(dontFragment: false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ip.flags.df", "ip.flags.df"),
                ("ip.flags.mf", "ip.flags.mf"),
                ("ip.frag_offset", "ip.frag_offset")).ConfigureAwait(false);
        }
    }

    /// <summary>Non-default TTL and identification values to exercise the encoders.</summary>
    [Test]
    public async Task IPv4_NonDefaultTtlAndId_FieldsMatchTshark()
    {
        byte[] frame = BuildPlainFrame(ttl: 128, identification: 0x4242);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ip.ttl", "ip.ttl"),
                ("ip.id", "ip.id")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Options-header coverage

    /// <summary>
    /// Frame produced via <see cref="IPv4LayerWithOptions"/> — header length
    /// jumps to 24 bytes, total length grows accordingly, and the checksum
    /// must still match tshark.
    /// </summary>
    [Test]
    public async Task IPv4_WithOptions_AllFieldsMatchTshark()
    {
        byte[] frame = BuildOptionsFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("ip.version", "ip.version"),
                ("ip.hdr_len", "ip.hdr_len"),
                ("ip.len", "ip.len"),
                ("ip.id", "ip.id"),
                ("ip.ttl", "ip.ttl"),
                ("ip.proto", "ip.proto"),
                ("ip.checksum", "ip.checksum"),
                ("ip.src", "ip.src"),
                ("ip.dst", "ip.dst")).ConfigureAwait(false);
        }
    }

    #endregion
}
