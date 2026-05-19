// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DHCPv4 malformed-input tests. Cover an invalid magic cookie and a
/// truncated BOOTP fixed header.
/// Frames are constructed via the <see cref="FrameStack"/> directly.
/// </summary>
internal sealed class DhcpMalformedTests
{
    // 0.0.0.0 → 255.255.255.255 is the canonical DHCP DISCOVER pair.
    private static byte[] WrapUdp(ReadOnlySpan<byte> dhcpPayload)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(68, 67);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dhcpPayload);
    }

    [Test]
    public async Task Parse_TruncatedHeader_Rejected()
    {
        // Less than the 240-byte BOOTP header.
        byte[] payload = new byte[100];
        byte[] frame = WrapUdp(payload);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            // Parser must refuse to add any DHCP fields.
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dhcp.cookie").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_InvalidMagicCookie_Rejected()
    {
        byte[] payload = new byte[241];
        // Set op = 1 so the first byte is plausible, but leave the magic cookie at zero.
        payload[0] = 1;
        payload[240] = 0xFF;
        byte[] frame = WrapUdp(payload);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dhcp.cookie").ConfigureAwait(false);
        }
    }
}
