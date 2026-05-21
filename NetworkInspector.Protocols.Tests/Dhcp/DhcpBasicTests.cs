// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DHCPv4 happy-path tests. Cover Discover/Offer flows with the most common
/// option codes (53 message type, 50 requested IP, 51 lease time, 54 server id,
/// 1 subnet, 3 router, 6 DNS).
/// </summary>
internal sealed class DhcpBasicTests
{
    [Test]
    public async Task Parse_Discover_HeaderAndMessageType()
    {
        DhcpV4Option msgType = new(53, (byte[])[1]); // Discover
        DhcpV4Option reqIp = new(50, (byte[])[192, 168, 1, 50]);
        DhcpV4Option paramReq = new(55, (byte[])[1, 3, 6, 51]);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(68, 67);
        DhcpV4Layer dhcp = new(op: 1, xid: 0x12345678u, options: [msgType, reqIp, paramReq], flags: 0x8000);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.id", 0x12345678).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.cookie", 0x63825363).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "dhcp.flags.bc", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.option.dhcp", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.option.requested_ip", "192.168.1.50").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Offer_AddressesAndOptions()
    {
        DhcpV4Option msgType = new(53, (byte[])[2]); // Offer
        DhcpV4Option subnet = new(1, (byte[])[255, 255, 255, 0]);
        DhcpV4Option router = new(3, (byte[])[192, 168, 1, 1]);
        DhcpV4Option dns = new(6, (byte[])[8, 8, 8, 8, 1, 1, 1, 1]);
        DhcpV4Option leaseTime = new(51, (byte[])[0x00, 0x00, 0x0E, 0x10]); // 3600
        DhcpV4Option serverId = new(54, (byte[])[192, 168, 1, 1]);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(67, 68);
        DhcpV4Layer dhcp = new(
            op: 2, xid: 0xCAFEBABEu,
            yiaddr: new IPv4Address(0xC0A80132),
            siaddr: new IPv4Address(0xC0A80101),
            options: [msgType, subnet, router, dns, leaseTime, serverId]);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.type", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.option.dhcp", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.ip.your", "192.168.1.50").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.ip.server", "192.168.1.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.option.subnet_mask", "255.255.255.0").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.option.router", "192.168.1.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dhcp.option.dhcp_server_id", "192.168.1.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.option.lease_time", 3600).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Discover_HostName_Option()
    {
        DhcpV4Option msgType = new(53, (byte[])[1]);
        byte[] hostBytes = Encoding.ASCII.GetBytes("test-host");
        DhcpV4Option hostName = new(12, hostBytes);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(68, 67);
        DhcpV4Layer dhcp = new(op: 1, xid: 0x11111111u, options: [msgType, hostName]);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dhcp.option.hostname", "test-host").ConfigureAwait(false);
        }
    }

    #region Flags display text

    [Test]
    public async Task Parse_Discover_FlagsDisplayText_Broadcast()
    {
        DhcpV4Option msgType = new(53, (byte[])[1]);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(68, 67);
        DhcpV4Layer dhcp = new(op: 1, xid: 0x12345678u, options: [msgType], flags: 0x8000);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "dhcp.flags", "0x8000 [Broadcast]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Discover_FlagsDisplayText_None()
    {
        DhcpV4Option msgType = new(53, (byte[])[1]);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(68, 67);
        DhcpV4Layer dhcp = new(op: 1, xid: 0x12345678u, options: [msgType], flags: 0x0000);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dhcp).CreateWithFixedValues().EmitFrame([]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "dhcp.flags", "0x0000 [None]").ConfigureAwait(false);
        }
    }

    #endregion
}
