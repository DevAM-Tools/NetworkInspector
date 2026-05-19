// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for ICMP protocol parsing (RFC 792).
/// Uses FrameBuilder echo request/reply frames.
/// </summary>
internal sealed class IcmpBasicTests
{
    [Test]
    public async Task Parse_IcmpEchoRequest_Type()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4EchoLayer icmp = new(identifier: 0x0001, sequenceNumber: 0x0001);
        byte[] data = [0xAB, 0xCD, 0xEF, 0x01];
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(data);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // ICMP type 8 = Echo Request
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.type", 8).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IcmpEchoReply_Type()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80102), new IPv4Address(0xC0A80101));
        IcmpV4EchoLayer icmp = new(type: IcmpV4EchoLayer.TypeEchoReply);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // ICMP type 0 = Echo Reply
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.type", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IcmpEchoRequest_Code()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4EchoLayer icmp = new(type: IcmpV4EchoLayer.TypeEchoRequest);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Code 0 for echo request
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.code", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task TsharkCrossValidation_IcmpType()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4EchoLayer icmp = new(type: IcmpV4EchoLayer.TypeEchoRequest);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);

        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "icmp.type");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("8");
    }
}
