// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for SLL v1 (Linux Cooked Capture), SLL v2, and LLC+SNAP protocol parsing.
/// </summary>
internal sealed class LinkLayerProtocolTests
{
    // === SLL v1 Tests ===

    /// <summary>
    /// Builds a full stack and parses the given frame with the specified link type.
    /// </summary>
    private static (Stack Stack, Packet Packet) _BuildAndParse(byte[] frameData, LinkType linkType)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    /// <summary>Builds and parses an Ethernet frame (LinkType.Ethernet).</summary>
    private static (Stack Stack, Packet Packet) _BuildAndParseEthernet(byte[] frameData) =>
        _BuildAndParse(frameData, LinkType.Ethernet);

    [Test]
    public async Task Parse_SllFrame_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSllFrame(packetType: 0, etherType: 0x0800);

        (Stack stack, Packet packet) = _BuildAndParse(frameData, LinkType.LinuxSll);
        using (stack)
        {
            // Verify SLL protocol is detected
            ProtocolId? sllId = stack.GetProtocolId("sll");
            await Assert.That(sllId).IsNotNull();

            // Packet type = 0 (Unicast)
            FieldId? pktTypeId = stack.GetFieldId("sll.pkttype");
            await Assert.That(pktTypeId).IsNotNull();
            bool hasPktType = packet.TryGetFieldValue(pktTypeId!.Value, out FieldValue pktTypeVal);
            await Assert.That(hasPktType).IsTrue();
            pktTypeVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0UL);

            // ARPHRD type = 1 (Ethernet)
            FieldId? haTypeId = stack.GetFieldId("sll.hatype");
            bool hasHaType = packet.TryGetFieldValue(haTypeId!.Value, out FieldValue haTypeVal);
            await Assert.That(hasHaType).IsTrue();
            haTypeVal.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(1UL);

            // Address length = 6
            FieldId? haLenId = stack.GetFieldId("sll.halen");
            bool hasHaLen = packet.TryGetFieldValue(haLenId!.Value, out FieldValue haLenVal);
            await Assert.That(hasHaLen).IsTrue();
            haLenVal.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(6UL);

            // EtherType = 0x0800
            FieldId? etypeId = stack.GetFieldId("sll.etype");
            bool hasEtype = packet.TryGetFieldValue(etypeId!.Value, out FieldValue etypeVal);
            await Assert.That(hasEtype).IsTrue();
            etypeVal.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(0x0800UL);
        }
    }

    [Test]
    public async Task Parse_SllFrame_DispatchesToIpv4()
    {
        byte[] frameData = FrameBuilders.GenerateSllFrame(etherType: 0x0800);

        (Stack stack, Packet packet) = _BuildAndParse(frameData, LinkType.LinuxSll);
        using (stack)
        {
            // IPv4 should be parsed after SLL
            FieldId? ipSrcId = stack.GetFieldId("ip.src");
            await Assert.That(ipSrcId).IsNotNull();
            bool hasIpSrc = packet.TryGetFieldValue(ipSrcId!.Value, out _);
            await Assert.That(hasIpSrc).IsTrue();

            // UDP should also be parsed
            FieldId? udpSrcPortId = stack.GetFieldId("udp.srcport");
            bool hasUdpSrcPort = packet.TryGetFieldValue(udpSrcPortId!.Value, out FieldValue udpSrcPortVal);
            await Assert.That(hasUdpSrcPort).IsTrue();
            udpSrcPortVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(5000UL);
        }
    }

    [Test]
    public async Task Parse_SllFrame_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateSllFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.LinuxSll,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            FieldId? pktTypeId = stack.GetFieldId("sll.pkttype");
            await Assert.That(index.GetFieldBitmap(pktTypeId!.Value).Contains(0)).IsTrue();
        }
    }

    // === SLL v2 Tests ===

    [Test]
    public async Task Parse_Sll2Frame_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSll2Frame(
            etherType: 0x0800, packetType: 0, interfaceIndex: 42);

        (Stack stack, Packet packet) = _BuildAndParse(frameData, LinkType.LinuxSll2);
        using (stack)
        {
            // Verify SLL2 protocol is detected
            ProtocolId? sll2Id = stack.GetProtocolId("sll2");
            await Assert.That(sll2Id).IsNotNull();

            // EtherType
            FieldId? etypeId = stack.GetFieldId("sll2.etype");
            bool hasEtype = packet.TryGetFieldValue(etypeId!.Value, out FieldValue etypeVal);
            await Assert.That(hasEtype).IsTrue();
            etypeVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x0800UL);

            // Interface index
            FieldId? ifIndexId = stack.GetFieldId("sll2.if_index");
            bool hasIfIndex = packet.TryGetFieldValue(ifIndexId!.Value, out FieldValue ifIndexVal);
            await Assert.That(hasIfIndex).IsTrue();
            ifIndexVal.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(42UL);

            // Packet type
            FieldId? pktTypeId = stack.GetFieldId("sll2.pkttype");
            bool hasPktType = packet.TryGetFieldValue(pktTypeId!.Value, out FieldValue pktTypeVal);
            await Assert.That(hasPktType).IsTrue();
            pktTypeVal.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(0UL);
        }
    }

    [Test]
    public async Task Parse_Sll2Frame_DispatchesToIpv4()
    {
        byte[] frameData = FrameBuilders.GenerateSll2Frame(etherType: 0x0800);

        (Stack stack, Packet packet) = _BuildAndParse(frameData, LinkType.LinuxSll2);
        using (stack)
        {
            FieldId? ipSrcId = stack.GetFieldId("ip.src");
            bool hasIpSrc = packet.TryGetFieldValue(ipSrcId!.Value, out _);
            await Assert.That(hasIpSrc).IsTrue();
        }
    }

    // === LLC + SNAP Tests ===

    [Test]
    public async Task Parse_LlcSnapFrame_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateLlcSnapFrame();

        (Stack stack, Packet packet) = _BuildAndParseEthernet(frameData);
        using (stack)
        {
            // Verify LLC protocol is detected
            ProtocolId? llcId = stack.GetProtocolId("llc");
            await Assert.That(llcId).IsNotNull();

            // DSAP = 0xAA (SNAP)
            FieldId? dsapId = stack.GetFieldId("llc.dsap");
            await Assert.That(dsapId).IsNotNull();
            bool hasDsap = packet.TryGetFieldValue(dsapId!.Value, out FieldValue dsapVal);
            await Assert.That(hasDsap).IsTrue();
            dsapVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xAAUL);

            // SSAP = 0xAA (SNAP)
            FieldId? ssapId = stack.GetFieldId("llc.ssap");
            bool hasSsap = packet.TryGetFieldValue(ssapId!.Value, out FieldValue ssapVal);
            await Assert.That(hasSsap).IsTrue();
            ssapVal.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0xAAUL);

            // Control = 0x03 (UI)
            FieldId? controlId = stack.GetFieldId("llc.control");
            bool hasControl = packet.TryGetFieldValue(controlId!.Value, out FieldValue controlVal);
            await Assert.That(hasControl).IsTrue();
            controlVal.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(0x03UL);
        }
    }

    [Test]
    public async Task Parse_LlcSnapFrame_SnapFieldsPresent()
    {
        byte[] frameData = FrameBuilders.GenerateLlcSnapFrame();

        (Stack stack, Packet packet) = _BuildAndParseEthernet(frameData);
        using (stack)
        {
            // SNAP Type = 0x0800 (IPv4)
            FieldId? snapTypeId = stack.GetFieldId("llc.type");
            await Assert.That(snapTypeId).IsNotNull();
            bool hasSnapType = packet.TryGetFieldValue(snapTypeId!.Value, out FieldValue snapTypeVal);
            await Assert.That(hasSnapType).IsTrue();
            snapTypeVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x0800UL);
        }
    }

    [Test]
    public async Task Parse_LlcSnapFrame_DispatchesToIpv4()
    {
        byte[] frameData = FrameBuilders.GenerateLlcSnapFrame();

        (Stack stack, Packet packet) = _BuildAndParseEthernet(frameData);
        using (stack)
        {
            // IPv4 should be dispatched after LLC SNAP
            FieldId? ipSrcId = stack.GetFieldId("ip.src");
            bool hasIpSrc = packet.TryGetFieldValue(ipSrcId!.Value, out _);
            await Assert.That(hasIpSrc).IsTrue();

            // UDP should also be parsed
            FieldId? udpSrcPortId = stack.GetFieldId("udp.srcport");
            bool hasUdpSrcPort = packet.TryGetFieldValue(udpSrcPortId!.Value, out FieldValue udpSrcPortVal);
            await Assert.That(hasUdpSrcPort).IsTrue();
            udpSrcPortVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(5000UL);
        }
    }

    [Test]
    public async Task Parse_LlcSnapFrame_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateLlcSnapFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            FieldId? dsapId = stack.GetFieldId("llc.dsap");
            await Assert.That(index.GetFieldBitmap(dsapId!.Value).Contains(0)).IsTrue();
        }
    }
}
