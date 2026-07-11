// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for ARP protocol parsing (RFC 826).
/// Verifies field extraction, display text, and edge cases.
/// </summary>
internal sealed class ArpProtocolTests
{
    /// <summary>
    /// Builds a full stack with standard protocols and parses the given frame data.
    /// </summary>
    private static (Stack Stack, Packet Packet) _BuildAndParse(byte[] frameData)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    [Test]
    public async Task Parse_ArpRequest_FieldsCorrect()
    {
        byte[] senderMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] senderIp = [192, 168, 1, 1];
        byte[] targetMac = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] targetIp = [192, 168, 1, 2];

        byte[] frameData = FrameBuilders.GenerateArpRequestFrame(
            senderMac, senderIp, targetMac, targetIp);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Verify ARP protocol is detected
            ProtocolId? arpId = stack.GetProtocolId("arp");
            await Assert.That(arpId).IsNotNull();

            // Verify field values
            FieldId? hwTypeId = stack.GetFieldId("arp.hw.type");
            await Assert.That(hwTypeId).IsNotNull();
            bool hasHwType = packet.TryGetFieldValue(hwTypeId!.Value, out FieldValue hwTypeValue);
            await Assert.That(hasHwType).IsTrue();
            hwTypeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL); // Ethernet

            FieldId? protoTypeId = stack.GetFieldId("arp.proto.type");
            bool hasProtoType = packet.TryGetFieldValue(protoTypeId!.Value, out FieldValue protoTypeValue);
            await Assert.That(hasProtoType).IsTrue();
            protoTypeValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0x0800UL); // IPv4

            FieldId? hwSizeId = stack.GetFieldId("arp.hw.size");
            bool hasHwSize = packet.TryGetFieldValue(hwSizeId!.Value, out FieldValue hwSizeValue);
            await Assert.That(hasHwSize).IsTrue();
            hwSizeValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(6UL);

            FieldId? protoSizeId = stack.GetFieldId("arp.proto.size");
            bool hasProtoSize = packet.TryGetFieldValue(protoSizeId!.Value, out FieldValue protoSizeValue);
            await Assert.That(hasProtoSize).IsTrue();
            protoSizeValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(4UL);

            FieldId? opcodeId = stack.GetFieldId("arp.opcode");
            bool hasOpcode = packet.TryGetFieldValue(opcodeId!.Value, out FieldValue opcodeValue);
            await Assert.That(hasOpcode).IsTrue();
            opcodeValue.Data.TryGetAsU64(out ulong u64Val5);
            await Assert.That(u64Val5).IsEqualTo(1UL); // Request

            FieldId? srcMacId = stack.GetFieldId("arp.src.hw_mac");
            bool hasSrcMac = packet.TryGetFieldValue(srcMacId!.Value, out FieldValue srcMacValue);
            await Assert.That(hasSrcMac).IsTrue();
            srcMacValue.Data.TryGetAsMacAddress(out MacAddress macVal);
            await Assert.That(macVal.Format()).IsEqualTo("00:11:22:33:44:55");

            FieldId? srcIpId = stack.GetFieldId("arp.src.proto_ipv4");
            bool hasSrcIp = packet.TryGetFieldValue(srcIpId!.Value, out FieldValue srcIpValue);
            await Assert.That(hasSrcIp).IsTrue();
            srcIpValue.Data.TryGetAsIPv4(out IPv4Address ipv4Val);
            await Assert.That(ipv4Val.Format()).IsEqualTo("192.168.1.1");

            FieldId? dstMacId = stack.GetFieldId("arp.dst.hw_mac");
            bool hasDstMac = packet.TryGetFieldValue(dstMacId!.Value, out FieldValue dstMacValue);
            await Assert.That(hasDstMac).IsTrue();
            dstMacValue.Data.TryGetAsMacAddress(out MacAddress macVal2);
            await Assert.That(macVal2.Format()).IsEqualTo("00:00:00:00:00:00");

            FieldId? dstIpId = stack.GetFieldId("arp.dst.proto_ipv4");
            bool hasDstIp = packet.TryGetFieldValue(dstIpId!.Value, out FieldValue dstIpValue);
            await Assert.That(hasDstIp).IsTrue();
            dstIpValue.Data.TryGetAsIPv4(out IPv4Address ipv4Val2);
            await Assert.That(ipv4Val2.Format()).IsEqualTo("192.168.1.2");
        }
    }

    [Test]
    public async Task Parse_ArpReply_OpcodeCorrect()
    {
        byte[] senderMac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        byte[] senderIp = [10, 0, 0, 1];
        byte[] targetMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] targetIp = [10, 0, 0, 2];

        byte[] frameData = FrameBuilders.GenerateArpReplyFrame(
            senderMac, senderIp, targetMac, targetIp);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? opcodeId = stack.GetFieldId("arp.opcode");
            bool hasOpcode = packet.TryGetFieldValue(opcodeId!.Value, out FieldValue opcodeValue);
            await Assert.That(hasOpcode).IsTrue();
            opcodeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(2UL); // Reply
        }
    }

    [Test]
    public async Task Parse_ArpRequest_ShortData_DoesNotCrash()
    {
        // 14-byte Ethernet header + only 10 bytes of ARP (too short, needs 28)
        byte[] shortFrame = new byte[24];
        // Ethernet: dst=broadcast, src=zeros, type=ARP
        shortFrame[0] = 0xFF;
        shortFrame[1] = 0xFF;
        shortFrame[2] = 0xFF;
        shortFrame[3] = 0xFF;
        shortFrame[4] = 0xFF;
        shortFrame[5] = 0xFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(12), 0x0806);

        (Stack stack, Packet packet) = _BuildAndParse(shortFrame);
        using (stack)
        {
            // Should not crash; ARP fields may not be present due to insufficient data
            await Assert.That(packet.IsFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ArpFrame_IndexPresence()
    {
        byte[] senderMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] senderIp = [192, 168, 1, 1];
        byte[] targetMac = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] targetIp = [192, 168, 1, 2];

        byte[] frameData = FrameBuilders.GenerateArpRequestFrame(
            senderMac, senderIp, targetMac, targetIp);

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0), Timestamp.FromSecs(0), frameData,
                LinkType.Ethernet, FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            NetworkInspector.Core.Index.PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // ARP protocol should be present in the index
            ProtocolId? arpId = stack.GetProtocolId("arp");
            await Assert.That(arpId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(arpId!.Value).Contains(0)).IsTrue();

            // ARP fields should be present (all share "arp" index group)
            FieldId? opcodeId = stack.GetFieldId("arp.opcode");
            await Assert.That(opcodeId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(opcodeId!.Value).Contains(0)).IsTrue();
        }
    }
}
