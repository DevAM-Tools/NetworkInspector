// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for ARP protocol parsing (RFC 826).
/// Uses FrameBuilder to construct request and reply frames.
/// </summary>
internal sealed class ArpBasicTests
{
    /// <summary>Creates an Ethernet + ARP Request frame.</summary>
    private static byte[] BuildArpRequestFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // broadcast
        MacAddress srcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress targetMac = MacAddress.FromBytes([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]); // unknown
        // ARP Request: opcode 1
        // sender: 00:11:22:33:44:55 / 192.168.1.100
        // target: 00:00:00:00:00:00 / 192.168.1.1
        EthernetLayer eth = new(dstMac, srcMac);
        ArpLayer arp = new(1, srcMac, new IPv4Address(0xC0A80164), targetMac, new IPv4Address(0xC0A80101));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Creates an Ethernet + ARP Reply frame.</summary>
    private static byte[] BuildArpReplyFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        // ARP Reply: opcode 2
        EthernetLayer eth = new(dstMac, srcMac);
        ArpLayer arp = new(2, srcMac, new IPv4Address(0xC0A80101), dstMac, new IPv4Address(0xC0A80164));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    [Test]
    public async Task Parse_ArpRequest_Opcode()
    {
        byte[] frame = BuildArpRequestFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.opcode", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ArpReply_Opcode()
    {
        byte[] frame = BuildArpReplyFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.opcode", 2).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ArpRequest_EtherType()
    {
        byte[] frame = BuildArpRequestFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // EtherType 0x0806 = ARP
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0806).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ArpRequest_HardwareType()
    {
        byte[] frame = BuildArpRequestFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Hardware type 1 = Ethernet
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.hw.type", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task TsharkCrossValidation_ArpOpcode()
    {
        byte[] frame = BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.opcode");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("1");
    }

    [Test]
    public async Task TsharkCrossValidation_SenderProtocolAddress()
    {
        // ARP request sender IP: 192.168.1.100
        byte[] frame = BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.src.proto_ipv4");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("192.168.1.100");
    }

    [Test]
    public async Task TsharkCrossValidation_TargetProtocolAddress()
    {
        // ARP request target IP: 192.168.1.1
        byte[] frame = BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.dst.proto_ipv4");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("192.168.1.1");
    }
}
