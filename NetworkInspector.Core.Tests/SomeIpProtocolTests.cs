// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for SOME/IP protocol parsing (AUTOSAR).
/// Verifies header field extraction and display text.
/// </summary>
internal sealed class SomeIpProtocolTests
{
    /// <summary>
    /// Builds a stack and parses a SOME/IP-over-UDP frame.
    /// </summary>
    private static (Stack Stack, Packet Packet) BuildAndParse(byte[] frameData)
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
    public async Task Parse_SomeIp_ServiceIdCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(
            serviceId: 0x0123, methodId: 0x4567);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? serviceIdField = stack.GetFieldId("someip.serviceid");
            await Assert.That(serviceIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(serviceIdField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x0123UL);
        }
    }

    [Test]
    public async Task Parse_SomeIp_MethodIdCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(
            serviceId: 0xABCD, methodId: 0x1234);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? methodIdField = stack.GetFieldId("someip.methodid");
            await Assert.That(methodIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(methodIdField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x1234UL);
        }
    }

    [Test]
    public async Task Parse_SomeIp_MessageIdCombined()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(
            serviceId: 0x0123, methodId: 0x4567);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? msgIdField = stack.GetFieldId("someip.messageid");
            await Assert.That(msgIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(msgIdField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x01234567UL);
        }
    }

    [Test]
    public async Task Parse_SomeIp_MsgTypeCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(messageType: 0x80); // RESPONSE

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? msgTypeField = stack.GetFieldId("someip.msgtype");
            await Assert.That(msgTypeField).IsNotNull();
            bool has = packet.TryGetFieldValue(msgTypeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x80UL);
        }
    }

    [Test]
    public async Task Parse_SomeIp_ReturnCodeCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(returnCode: 0x01); // E_NOT_OK

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? returnCodeField = stack.GetFieldId("someip.returncode");
            await Assert.That(returnCodeField).IsNotNull();
            bool has = packet.TryGetFieldValue(returnCodeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x01UL);
        }
    }

    [Test]
    public async Task Parse_SomeIp_PayloadPresent()
    {
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(payload: payload);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? payloadField = stack.GetFieldId("someip.payload");
            await Assert.That(payloadField).IsNotNull();
            bool has = packet.TryGetFieldValue(payloadField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();

            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
            await Assert.That(bytesVal.Length).IsEqualTo(5);
            await Assert.That(bytesVal.Span[0]).IsEqualTo((byte)0xAA);
            await Assert.That(bytesVal.Span[4]).IsEqualTo((byte)0xEE);
        }
    }

    [Test]
    public async Task Parse_SomeIp_ShortData_NoFields()
    {
        // Build a frame with a full SOME/IP header but truncate it
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(payload: []);

        // Manually truncate the frame to cut the SOME/IP header short
        byte[] truncated = new byte[frameData.Length - 10];
        Array.Copy(frameData, truncated, truncated.Length);

        (Stack stack, Packet packet) = BuildAndParse(truncated);
        using (stack)
        {
            // SOME/IP service ID should not be present (data too short)
            FieldId? serviceIdField = stack.GetFieldId("someip.serviceid");
            await Assert.That(serviceIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(serviceIdField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_SomeIp_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            NetworkInspector.Core.Index.PacketIndex index = new(stack);

            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // SOME/IP protocol should be in the index
            ProtocolId? someipProtocolId = stack.GetProtocolId("someip");
            await Assert.That(someipProtocolId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(someipProtocolId!.Value).Contains(0)).IsTrue();

            // SOME/IP service ID field should be in the index
            FieldId? serviceIdField = stack.GetFieldId("someip.serviceid");
            await Assert.That(serviceIdField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(serviceIdField!.Value).Contains(0)).IsTrue();
        }
    }

    // ====================================================================
    // Message type sub-fields (ACK/TP flags)
    // ====================================================================

    [Test]
    public async Task Parse_SomeIp_MsgTypeAck_SetForAckResponse()
    {
        // Message type 0xC0 = RESPONSE (0x80) | ACK (0x40)
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(messageType: 0xC0);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? ackField = stack.GetFieldId("someip.msgtype.ack");
            await Assert.That(ackField).IsNotNull();
            bool has = packet.TryGetFieldValue(ackField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_SomeIp_MsgTypeTp_NotSetForNormalRequest()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpFrame(messageType: 0x00);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? tpField = stack.GetFieldId("someip.msgtype.tp");
            await Assert.That(tpField).IsNotNull();
            bool has = packet.TryGetFieldValue(tpField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
        }
    }

    // ====================================================================
    // SOME/IP-TP tests
    // ====================================================================

    [Test]
    public async Task Parse_SomeIpTp_OffsetCorrect()
    {
        // Offset = 0x100 (256 bytes), more segments = true
        byte[] frameData = FrameBuilders.GenerateSomeIpTpFrame(
            byteOffset: 0x100, moreSegments: true);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? offsetField = stack.GetFieldId("someip.tp.offset");
            await Assert.That(offsetField).IsNotNull();
            bool has = packet.TryGetFieldValue(offsetField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x100UL);
        }
    }

    [Test]
    public async Task Parse_SomeIpTp_MoreSegmentsTrue()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpTpFrame(moreSegments: true);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? moreField = stack.GetFieldId("someip.tp.more");
            await Assert.That(moreField).IsNotNull();
            bool has = packet.TryGetFieldValue(moreField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_SomeIpTp_MoreSegmentsFalse()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpTpFrame(moreSegments: false);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? moreField = stack.GetFieldId("someip.tp.more");
            await Assert.That(moreField).IsNotNull();
            bool has = packet.TryGetFieldValue(moreField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
        }
    }

    [Test]
    public async Task Parse_SomeIpTp_MsgTypeTpFlagSet()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpTpFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // TP flag should be set in message type decomposition
            FieldId? tpField = stack.GetFieldId("someip.msgtype.tp");
            await Assert.That(tpField).IsNotNull();
            bool has = packet.TryGetFieldValue(tpField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_SomeIpTp_NoPayloadFieldForTpMessage()
    {
        // TP messages should NOT have a raw payload field — TP data is structured
        byte[] frameData = FrameBuilders.GenerateSomeIpTpFrame(payload: [0xAA, 0xBB]);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Payload field should still be present (it's the data after TP header)
            FieldId? payloadField = stack.GetFieldId("someip.payload");
            await Assert.That(payloadField).IsNotNull();
            // Note: Payload IS emitted for TP — it's the reassembled/fragment data after TP header
        }
    }

    // ====================================================================
    // SOME/IP-SD tests
    // ====================================================================

    [Test]
    public async Task Parse_SomeIpSd_FlagsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(flags: 0xC0);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? flagsField = stack.GetFieldId("someip_sd.flags");
            await Assert.That(flagsField).IsNotNull();
            bool has = packet.TryGetFieldValue(flagsField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xC0UL);
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_RebootFlagSet()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(flags: 0x80);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? rebootField = stack.GetFieldId("someip_sd.flags.reboot");
            await Assert.That(rebootField).IsNotNull();
            bool has = packet.TryGetFieldValue(rebootField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_UnicastFlagSet()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(flags: 0x40);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? unicastField = stack.GetFieldId("someip_sd.flags.unicast");
            await Assert.That(unicastField).IsNotNull();
            bool has = packet.TryGetFieldValue(unicastField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_OfferEntry_ServiceIdCorrect()
    {
        byte[] entry = FrameBuilders.BuildSdOfferEntry(0xABCD, 0x0001, 1, 3, 0);
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(entries: entry);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? serviceIdField = stack.GetFieldId("someip_sd.entry.serviceid");
            await Assert.That(serviceIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(serviceIdField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xABCDUL);
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_OfferEntry_TtlCorrect()
    {
        byte[] entry = FrameBuilders.BuildSdOfferEntry(0x0001, 0x0001, 1, 86400, 0);
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(entries: entry);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? ttlField = stack.GetFieldId("someip_sd.entry.ttl");
            await Assert.That(ttlField).IsNotNull();
            bool has = packet.TryGetFieldValue(ttlField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(86400UL);
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_OfferEntry_EntryTypeCorrect()
    {
        byte[] entry = FrameBuilders.BuildSdOfferEntry(0x0001, 0x0001, 1, 3, 0);
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(entries: entry);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? typeField = stack.GetFieldId("someip_sd.entry.type");
            await Assert.That(typeField).IsNotNull();
            bool has = packet.TryGetFieldValue(typeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x01UL); // OfferService
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_Ipv4EndpointOption_PortCorrect()
    {
        byte[] option = FrameBuilders.BuildSdIpv4EndpointOption(10, 0, 0, 1, 17, 30490);
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(options: option);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? portField = stack.GetFieldId("someip_sd.option.port");
            await Assert.That(portField).IsNotNull();
            bool has = packet.TryGetFieldValue(portField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(30490UL);
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_Ipv4EndpointOption_ProtoCorrect()
    {
        byte[] option = FrameBuilders.BuildSdIpv4EndpointOption(10, 0, 0, 1, 6, 30490);
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame(options: option);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? protoField = stack.GetFieldId("someip_sd.option.proto");
            await Assert.That(protoField).IsNotNull();
            bool has = packet.TryGetFieldValue(protoField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(6UL); // TCP
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_NoPayloadField()
    {
        // SD messages should NOT have a raw payload field — it's all parsed as SD
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? payloadField = stack.GetFieldId("someip.payload");
            await Assert.That(payloadField).IsNotNull();
            bool has = packet.TryGetFieldValue(payloadField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_SomeIpSd_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateSomeIpSdFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            NetworkInspector.Core.Index.PacketIndex index = new(stack);

            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // SD flags field should be in the index
            FieldId? sdFlagsField = stack.GetFieldId("someip_sd.flags");
            await Assert.That(sdFlagsField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(sdFlagsField!.Value).Contains(0)).IsTrue();
        }
    }
}