// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the UDP dissector (Plan §3.1.5).
/// Pins source port, destination port, length and checksum across both
/// IPv4 and IPv6 carriers — the checksum path differs because the
/// pseudo-header is different.
/// </summary>
/// <remarks>
/// <para>
/// Frames are emitted via the <see cref="FrameStack"/> API; the UDP layer
/// computes its checksum from the carrier-published pseudo-header during the
/// post-fix phase, so verifying <c>udp.checksum</c> end-to-end implicitly
/// pins the IPv4/IPv6 pseudo-header construction as well.
/// </para>
/// <para>Thread safety: stateless tests over the shared parser stack.</para>
/// </remarks>
internal sealed class UdpTsharkTests
{
    #region Frame builders

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static readonly byte[] _Ipv6Src =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _Ipv6Dst =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    /// <summary>Eth + IPv4 + UDP carrier with a fixed payload.</summary>
    private static byte[] _BuildIPv4UdpFrame(ushort srcPort = 12345, ushort dstPort = 53, int payloadLength = 8)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xAC100164), new IPv4Address(0xAC100101));
        UdpLayer udp = new(srcPort, dstPort);
        byte[] payload = new byte[payloadLength];
        for (int i = 0; i < payloadLength; i++)
        {
            payload[i] = (byte)(i + 1);
        }
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Eth + IPv6 + UDP carrier with a fixed payload.</summary>
    private static byte[] _BuildIPv6UdpFrame(ushort srcPort = 49152, ushort dstPort = 53, int payloadLength = 8)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_Ipv6Src), IPv6Address.FromBytes(_Ipv6Dst));
        UdpLayer udp = new(srcPort, dstPort);
        byte[] payload = new byte[payloadLength];
        for (int i = 0; i < payloadLength; i++)
        {
            payload[i] = (byte)(0xA0 + i);
        }
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    #endregion

    #region IPv4 carrier

    [Test]
    public async Task Udp_OverIPv4_AllFieldsMatchTshark()
    {
        byte[] frame = _BuildIPv4UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("udp.srcport", "udp.srcport"),
                ("udp.dstport", "udp.dstport"),
                ("udp.length", "udp.length"),
                ("udp.checksum", "udp.checksum")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Edge case: empty UDP payload — header alone yields <c>udp.length</c>=8.
    /// Pins the length-field encoding.
    /// </summary>
    [Test]
    public async Task Udp_OverIPv4_EmptyPayload_LengthMatchesTshark()
    {
        byte[] frame = _BuildIPv4UdpFrame(payloadLength: 0);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("udp.srcport", "udp.srcport"),
                ("udp.dstport", "udp.dstport"),
                ("udp.length", "udp.length"),
                ("udp.checksum", "udp.checksum")).ConfigureAwait(false);
        }
    }

    #endregion

    #region IPv6 carrier

    [Test]
    public async Task Udp_OverIPv6_AllFieldsMatchTshark()
    {
        byte[] frame = _BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("udp.srcport", "udp.srcport"),
                ("udp.dstport", "udp.dstport"),
                ("udp.length", "udp.length"),
                ("udp.checksum", "udp.checksum")).ConfigureAwait(false);
        }
    }

    #endregion
}
