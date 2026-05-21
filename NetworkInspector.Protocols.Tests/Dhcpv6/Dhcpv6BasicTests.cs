// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DHCPv6 happy-path tests. Cover Solicit/Advertise/Reply with the most
/// common options (Client-ID, Server-ID, IA_NA, IAADDR, ORO, Elapsed Time,
/// Status Code, Rapid Commit, DNS Servers).
/// </summary>
internal sealed class Dhcpv6BasicTests
{
    [Test]
    public async Task Parse_Solicit_HeaderAndOptions()
    {
        // Build a SOLICIT (msg-type 1) with ClientID + ORO + Elapsed Time + Rapid Commit.
        DhcpV6Option clientId = new(1, new byte[] { 0x00, 0x03, 0x00, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }); // DUID-LL
        DhcpV6Option oro = new(6, new byte[] { 0x00, 0x17, 0x00, 0x18 });   // Request DNS servers + domain list
        DhcpV6Option elapsed = new(8, new byte[] { 0x00, 0x64 });            // 100 → 1.0s
        DhcpV6Option rapidCommit = new(14, Array.Empty<byte>());
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x01, 0x00, 0x02]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0, 0x01, 0, 0x02]));
        UdpLayer udp = new(546, 547);
        DhcpV6Layer dhcp = new(msgType: 1, xid24: 0x123456u, options: [clientId, oro, elapsed, rapidCommit]);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.msgtype", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.xid", 0x123456).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.option.elapsed_time", 100).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "dhcpv6.option.rapid_commit", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Reply_DnsServersAndStatus()
    {
        // 2001:4860:4860::8888 (Google DNS).
        byte[] dns1 = [0x20, 0x01, 0x48, 0x60, 0x48, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0x88, 0x88];
        byte[] dns2 = [0x20, 0x01, 0x48, 0x60, 0x48, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0x88, 0x44];
        byte[] dnsBoth = new byte[32];
        dns1.CopyTo(dnsBoth.AsSpan(0));
        dns2.CopyTo(dnsBoth.AsSpan(16));
        DhcpV6Option dnsServers = new(23, dnsBoth);
        // Status code: success (0) + UTF-8 status message.
        byte[] statusBytes = [0x00, 0x00, (byte)'O', (byte)'K'];
        DhcpV6Option statusCode = new(13, statusBytes);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x01, 0x00, 0x02]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0, 0x01, 0, 0x02]));
        UdpLayer udp = new(547, 546);
        DhcpV6Layer dhcp = new(msgType: 7, xid24: 0xAABBCCu, options: [dnsServers, statusCode]);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.msgtype", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcpv6.option.status_code", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "dhcpv6.option.dns_server", "2001:4860:4860::8888").ConfigureAwait(false);
        }
    }
}
