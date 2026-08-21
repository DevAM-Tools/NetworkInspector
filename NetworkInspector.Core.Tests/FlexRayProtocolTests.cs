// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for FlexRay protocol parsing (ISO 17458-2, LINKTYPE_FLEXRAY format).
/// </summary>
internal sealed class FlexRayProtocolTests
{
    /// <summary>
    /// Builds a stack and parses a FlexRay frame (link type 210).
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
            LinkType.Flexray,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    [Test]
    public async Task Parse_FlexRay_FrameIdCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(frameId: 42);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? frameIdField = stack.GetFieldId("flexray.frame_id");
            await Assert.That(frameIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(frameIdField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(42UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_FrameId_11Bit_MaxValue()
    {
        // 11-bit max frame ID = 2047 (0x7FF)
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(frameId: 2047);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? frameIdField = stack.GetFieldId("flexray.frame_id");
            await Assert.That(frameIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(frameIdField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(2047UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_CycleCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(cycle: 7);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? cycleField = stack.GetFieldId("flexray.cycle");
            await Assert.That(cycleField).IsNotNull();
            bool has = packet.TryGetFieldValue(cycleField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(7UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_CycleMax63()
    {
        // 6-bit cycle count max = 63
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(cycle: 63);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? cycleField = stack.GetFieldId("flexray.cycle");
            await Assert.That(cycleField).IsNotNull();
            bool has = packet.TryGetFieldValue(cycleField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(63UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_PayloadLengthCorrect()
    {
        byte[] payload = new byte[16];
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lengthField = stack.GetFieldId("flexray.payload_length");
            await Assert.That(lengthField).IsNotNull();
            bool has = packet.TryGetFieldValue(lengthField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(16UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_ChannelA()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(channelB: false);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? channelField = stack.GetFieldId("flexray.channel");
            await Assert.That(channelField).IsNotNull();
            bool has = packet.TryGetFieldValue(channelField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("Channel A");
        }
    }

    [Test]
    public async Task Parse_FlexRay_ChannelB()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(channelB: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? channelField = stack.GetFieldId("flexray.channel");
            await Assert.That(channelField).IsNotNull();
            bool has = packet.TryGetFieldValue(channelField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("Channel B");
        }
    }

    [Test]
    public async Task Parse_FlexRay_NullFrameIndicator_NotNull()
    {
        // NFI=true means NOT a null frame
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(nfi: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? nfiField = stack.GetFieldId("flexray.nfi");
            await Assert.That(nfiField).IsNotNull();
            bool has = packet.TryGetFieldValue(nfiField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_NullFrameIndicator_IsNull()
    {
        // NFI=false means it IS a null frame
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(nfi: false);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? nfiField = stack.GetFieldId("flexray.nfi");
            await Assert.That(nfiField).IsNotNull();
            bool has = packet.TryGetFieldValue(nfiField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
        }
    }

    [Test]
    public async Task Parse_FlexRay_SyncFrameIndicator()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(sfi: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? sfiField = stack.GetFieldId("flexray.sfi");
            await Assert.That(sfiField).IsNotNull();
            bool has = packet.TryGetFieldValue(sfiField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_StartupFrameIndicator()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(stfi: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? stfiField = stack.GetFieldId("flexray.stfi");
            await Assert.That(stfiField).IsNotNull();
            bool has = packet.TryGetFieldValue(stfiField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_PayloadPreambleIndicator()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(ppi: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? ppiField = stack.GetFieldId("flexray.ppi");
            await Assert.That(ppiField).IsNotNull();
            bool has = packet.TryGetFieldValue(ppiField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_HeaderCrc()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(headerCrc: 0x5A3);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? hcrcField = stack.GetFieldId("flexray.hcrc");
            await Assert.That(hcrcField).IsNotNull();
            bool has = packet.TryGetFieldValue(hcrcField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x5A3UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlags_FcrcErr()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(errorFlags: 0x10);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? fcrcField = stack.GetFieldId("flexray.fcrc_err");
            await Assert.That(fcrcField).IsNotNull();
            bool has = packet.TryGetFieldValue(fcrcField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlags_AllClear()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(errorFlags: 0x00);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? fcrcField = stack.GetFieldId("flexray.fcrc_err");
            FieldId? hcrcField = stack.GetFieldId("flexray.hcrc_err");
            FieldId? fesField = stack.GetFieldId("flexray.fes_err");
            FieldId? codField = stack.GetFieldId("flexray.cod_err");
            FieldId? tssField = stack.GetFieldId("flexray.tss_viol");

            await Assert.That(fcrcField).IsNotNull();

            // All error flags should be false
            packet.TryGetFieldValue(fcrcField!.Value, out FieldValue v1, materialize: true); // materialize: true — need complete field tree for assertion
            v1.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
            packet.TryGetFieldValue(hcrcField!.Value, out FieldValue v2, materialize: true); // materialize: true — need complete field tree for assertion
            v2.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsFalse();
            packet.TryGetFieldValue(fesField!.Value, out FieldValue v3, materialize: true); // materialize: true — need complete field tree for assertion
            v3.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal3).IsFalse();
            packet.TryGetFieldValue(codField!.Value, out FieldValue v4, materialize: true); // materialize: true — need complete field tree for assertion
            v4.Data.TryGetAsBool(out bool boolVal4);
            await Assert.That(boolVal4).IsFalse();
            packet.TryGetFieldValue(tssField!.Value, out FieldValue v5, materialize: true); // materialize: true — need complete field tree for assertion
            v5.Data.TryGetAsBool(out bool boolVal5);
            await Assert.That(boolVal5).IsFalse();
        }
    }

    [Test]
    public async Task Parse_FlexRay_AllIndicatorsSet()
    {
        // Frame with all indicator bits set and a specific frame ID
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(
            frameId: 100,
            cycle: 15,
            nfi: true,
            sfi: true,
            stfi: true,
            ppi: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Verify frame ID is still correct when indicators are set
            FieldId? frameIdField = stack.GetFieldId("flexray.frame_id");
            packet.TryGetFieldValue(frameIdField!.Value, out FieldValue fidValue, materialize: true); // materialize: true — need complete field tree for assertion
            fidValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(100UL);

            // All indicators true
            FieldId? nfiField = stack.GetFieldId("flexray.nfi");
            packet.TryGetFieldValue(nfiField!.Value, out FieldValue nfiValue, materialize: true); // materialize: true — need complete field tree for assertion
            nfiValue.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            FieldId? sfiField = stack.GetFieldId("flexray.sfi");
            packet.TryGetFieldValue(sfiField!.Value, out FieldValue sfiValue, materialize: true); // materialize: true — need complete field tree for assertion
            sfiValue.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();

            FieldId? stfiField = stack.GetFieldId("flexray.stfi");
            packet.TryGetFieldValue(stfiField!.Value, out FieldValue stfiValue, materialize: true); // materialize: true — need complete field tree for assertion
            stfiValue.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal3).IsTrue();

            FieldId? ppiField = stack.GetFieldId("flexray.ppi");
            packet.TryGetFieldValue(ppiField!.Value, out FieldValue ppiValue, materialize: true); // materialize: true — need complete field tree for assertion
            ppiValue.Data.TryGetAsBool(out bool boolVal4);
            await Assert.That(boolVal4).IsTrue();

            // Cycle correct
            FieldId? cycleField = stack.GetFieldId("flexray.cycle");
            packet.TryGetFieldValue(cycleField!.Value, out FieldValue cycleValue, materialize: true); // materialize: true — need complete field tree for assertion
            cycleValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(15UL);
        }
    }

    [Test]
    public async Task Parse_FlexRay_FlagsContainer_DisplayText_None()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(nfi: false, sfi: false, stfi: false, ppi: false);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? flagsField = stack.GetFieldId("flexray.flags");
            await Assert.That(flagsField).IsNotNull();

            string? flagsDisplayText = _FindCustomText(packet.RootField(), flagsField!.Value);
            await Assert.That(flagsDisplayText).IsEqualTo("[None]");
        }
    }

    [Test]
    public async Task Parse_FlexRay_FlagsContainer_DisplayText_Nfi()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(nfi: true, sfi: false, stfi: false, ppi: false);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? flagsField = stack.GetFieldId("flexray.flags");
            await Assert.That(flagsField).IsNotNull();

            string? flagsDisplayText = _FindCustomText(packet.RootField(), flagsField!.Value);
            await Assert.That(flagsDisplayText).IsEqualTo("[NFI]");
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlagsContainer_DisplayText_None()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(errorFlags: 0x00);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? errFlagsField = stack.GetFieldId("flexray.err_flags");
            await Assert.That(errFlagsField).IsNotNull();

            string? errFlagsDisplayText = _FindCustomText(packet.RootField(), errFlagsField!.Value);
            await Assert.That(errFlagsDisplayText).IsEqualTo("[None]");
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlagsContainer_DisplayText_FcrcErr()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame(errorFlags: 0x10);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? errFlagsField = stack.GetFieldId("flexray.err_flags");
            await Assert.That(errFlagsField).IsNotNull();

            string? errFlagsDisplayText = _FindCustomText(packet.RootField(), errFlagsField!.Value);
            await Assert.That(errFlagsDisplayText).IsEqualTo("[FCRC_ERR]");
        }
    }

    [Test]
    public async Task Parse_FlexRay_ShortData_NoFields()
    {
        byte[] shortFrame = [0x01, 0x00, 0x00]; // Only 3 bytes, need 7

        (Stack stack, Packet packet) = _BuildAndParse(shortFrame);
        using (stack)
        {
            FieldId? frameIdField = stack.GetFieldId("flexray.frame_id");
            await Assert.That(frameIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(frameIdField!.Value, out _, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_FlexRay_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateFlexRayFrame();

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
                LinkType.Flexray,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            ProtocolId? flexrayId = stack.GetProtocolId("flexray");
            await Assert.That(flexrayId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(flexrayId!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FlexRay_DispatchKey_IncludesSlotChannelCycle()
    {
        const ushort frameId = 100;
        const byte cycle = 15;
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frameData = FlexRayLinkTypeFrame.BuildFrame(
            channelB: true,
            frameId,
            cycle,
            headerCrc: 0,
            payload);

        ulong expectedKey = FlexRayLinkTypeFrame.EncodeDispatchKey(frameId, channelB: true, cycle);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_flexray_dispatch_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_message.json");
        try
        {
            string json = $$"""
                {
                  "messages": [{
                    "name": "fr_dispatch_probe",
                    "ui_name": "FR Dispatch Probe",
                    "byte_length": 4,
                    "dispatch_bindings": [{ "table": "flexray.id", "key": {{expectedKey}} }],
                    "signals": [{
                      "name": "fr_dispatch_probe.Probe",
                      "ui_name": "Probe",
                      "start_bit": 0,
                      "bit_length": 16,
                      "byte_order": "little_endian"
                    }]
                  }]
                }
                """;
            File.WriteAllText(jsonPath, json);

            using SettingsManager settingsManager = new();
            settingsManager.PreloadValue("signal_message.config_file", jsonPath);
            StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
            ProtocolRegistration.RegisterStandardProtocols(builder);
            Stack stack = builder.Build();
            using (stack)
            {
                Frame frame = Frame.Create(
                    new FrameId(0),
                    Timestamp.FromSecs(0),
                    frameData,
                    LinkType.Flexray,
                    FrameInterfaceId.Invalid,
                    stack.FrameInterfaceRegistry).Value;

                Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

                FieldId? probeField = stack.GetFieldId("fr_dispatch_probe.Probe");
                await Assert.That(probeField).IsNotNull();
                bool has = packet.TryGetFieldValue(probeField!.Value, out FieldValue probeValue, materialize: true);
                await Assert.That(has).IsTrue();
                // Signal fields store the physical F64 value (factor=1 → same magnitude as raw).
                await Assert.That(probeValue.Data.TryGetAsF64(out double probePhys)).IsTrue();
                // LE u16 from payload [0x01, 0x02] = 0x0201
                await Assert.That(probePhys).IsEqualTo(513.0);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }

    /// <summary>Depth-first search for the first field with <paramref name="fieldId"/>;
    /// returns its <c>CustomText.ToString()</c> or <see langword="null"/> when not present.</summary>
    private static string? _FindCustomText(Field field, FieldId fieldId)
    {
        if (field.FieldId == fieldId && !field.CustomText.IsNull)
        {
            return field.CustomText.ToString();
        }

        foreach (Field child in field.Children(materialize: true)) // materialize: true — navigate/populate children including lazy
        {
            string? result = _FindCustomText(child, fieldId);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
