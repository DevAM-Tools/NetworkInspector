// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DHCPv6 malformed-input tests. Cover a truncated header and a truncated
/// option value (length declared larger than remaining bytes).
/// Frames are constructed via the <see cref="FrameStack"/> directly.
/// </summary>
internal sealed class Dhcpv6MalformedTests
{
    // fe80::1 → ff02::1:2 is the canonical link-local DHCPv6 conversation pair.
    private static byte[] WrapUdp(ReadOnlySpan<byte> dhcpv6Payload)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x01, 0x00, 0x02]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0, 0x01, 0, 0x02]));
        UdpLayer udp = new(546, 547);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dhcpv6Payload);
    }

    [Test]
    public async Task Parse_TruncatedHeader_Rejected()
    {
        // Less than the 4-byte client/server header.
        byte[] payload = [0x01, 0x00];
        byte[] frame = WrapUdp(payload);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dhcpv6.msgtype").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_OptionLengthExceedsBuffer_Stops()
    {
        // Header: SOLICIT msg-type with xid 0; option claims 0xFFFF length but no data.
        byte[] payload = [0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0xFF, 0xFF];
        byte[] frame = WrapUdp(payload);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            // Header is parsed but the option must be rejected.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.msgtype", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dhcpv6.option.code").ConfigureAwait(false);
        }
    }
}
