// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>Settings, length-check, and enum CustomText coverage for Signal message protocols.</summary>
internal sealed class SignalMessageProtocolBehaviorTests
{
    [Test]
    public async Task Parse_ShortBuffer_ReturnsInsufficientData()
    {
        string json = """
            {
              "messages": [{
                "name": "short_buf",
                "ui_name": "Short",
                "byte_length": 4,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17001 }],
                "signals": [{
                  "name": "short_buf.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_short_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            // 1-byte UDP payload — RequiredByteLength for 16-bit LE at bit 0 is 2
            byte[] frame = FrameStack
                .Start(AutomotiveEthUdpFrames.TestEthernet())
                .Then(AutomotiveEthUdpFrames.TestIpv4())
                .Then(new UdpLayer(15000, 17001))
                .CreateWithFixedValues()
                .EmitFrame([0x01]);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                FieldId? signalField = stack.GetFieldId("short_buf.a");
                await Assert.That(signalField).IsNotNull();
                bool has = packet.TryGetFieldValue(signalField!.Value, out _, materialize: true);
                await Assert.That(has).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_ContainerIsLazy_SignalsAppearOnMaterialize()
    {
        SignalMessageLayout layout = new()
        {
            Name = "lazy_sig",
            UiName = "Lazy Sig",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding { Table = UdpProtocol.PortTableName, Key = 17004UL }),
            Mux = null,
            MuxGroups = [],
        };

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer spdu = new(layout, vals);
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17004))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_lazy_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                // Message container is pending until materialization.
                await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();

                FieldId? containerId = stack.GetFieldId("lazy_sig");
                await Assert.That(containerId).IsNotNull();
                bool containerFound = packet.TryGetFieldValue(
                    containerId!.Value, out FieldValue containerValue, materialize: false);
                await Assert.That(containerFound).IsTrue();
                await Assert.That(containerValue.Type).IsEqualTo(FieldType.Bytes);

                // Signal fields do not exist until the container populator runs.
                FieldId? signalId = stack.GetFieldId("lazy_sig.EngineRpm");
                await Assert.That(signalId).IsNotNull();
                bool signalBefore = packet.TryGetFieldValue(
                    signalId!.Value, out _, materialize: false);
                await Assert.That(signalBefore).IsFalse();

                // Materializing the tree builds signals under the container.
                await ProtocolTestHelper.AssertF64Field(stack, packet, "lazy_sig.EngineRpm", 125.0)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ShowRaw_AppendsRawChildOnMaterialize()
    {
        SignalMessageLayout layout = new()
        {
            Name = "raw_child",
            UiName = "Raw Child",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding { Table = UdpProtocol.PortTableName, Key = 17002 }),
            Mux = null,
            MuxGroups = [],
        };

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer spdu = new(layout, vals);
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17002))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_raw_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm =>
                {
                    sm.PreloadValue("signal_message.config_file", path);
                    sm.PreloadValue("signal_message.show_raw", true);
                });

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "raw_child.EngineRpm", 125.0).ConfigureAwait(false);
                await ProtocolTestHelper.AssertU64Field(stack, packet, "raw_child.EngineRpm.raw", 100).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ShowEnum_AppendsEnumChildOnMaterialize()
    {
        ImmutableDictionary<ulong, string> names = ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<ulong, string>(0, "Off"),
                new KeyValuePair<ulong, string>(1, "On"),
            ]);

        SignalMessageLayout layout = new()
        {
            Name = "enum_child",
            UiName = "Enum Child",
            ByteLength = 1,
            Signals = ImmutableArray.Create(
                new SignalSpec
                {
                    Name = "state",
                    UiName = "State",
                    StartBit = 0,
                    BitLength = 8,
                    Endian = SignalEndian.Little,
                    Factor = 1.0,
                    Offset = 0.0,
                    Unit = string.Empty,
                    ValueNames = names,
                }),
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding { Table = UdpProtocol.PortTableName, Key = 17003UL }),
            Mux = null,
            MuxGroups = [],
        };

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).SetRaw("state", 1);
        SignalMessageLayer spdu = new(layout, vals);
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17003))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_enum_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm =>
                {
                    sm.PreloadValue("signal_message.config_file", path);
                    sm.PreloadValue("signal_message.show_enum", true);
                });

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "enum_child.state", 1.0).ConfigureAwait(false);
                await ProtocolTestHelper.AssertStringField(stack, packet, "enum_child.state.enum", "On")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_UsesJsonDescriptionOrDefault()
    {
        string json = """
            {
              "messages": [
                {
                  "name": "desc_custom",
                  "ui_name": "Custom",
                  "description": "Engine status frame",
                  "byte_length": 2,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17010 }],
                  "signals": [{
                    "name": "desc_custom.a",
                    "ui_name": "A",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                },
                {
                  "name": "desc_default",
                  "ui_name": "Default",
                  "byte_length": 2,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17011 }],
                  "signals": [{
                    "name": "desc_default.b",
                    "ui_name": "B",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                }
              ]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_desc_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            _ = SignalMessageRegistration.Register(builder);
            Stack stack = builder.Build();

            using (stack)
            {
                ProtocolId? customId = stack.GetProtocolId("desc_custom");
                await Assert.That(customId).IsNotNull();
                ProtocolInfo? custom = stack.GetProtocol(customId!.Value);
                await Assert.That(custom).IsNotNull();
                await Assert.That(custom!.Description).IsEqualTo("Engine status frame");

                ProtocolId? defaultId = stack.GetProtocolId("desc_default");
                await Assert.That(defaultId).IsNotNull();
                ProtocolInfo? fallback = stack.GetProtocol(defaultId!.Value);
                await Assert.That(fallback).IsNotNull();
                await Assert.That(fallback!.Description)
                    .IsEqualTo(SignalMessageCompiler.DefaultMessageDescription);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RegisterFields_SignalFieldTypeIsF64()
    {
        string json = """
            {
              "messages": [{
                "name": "ftype",
                "ui_name": "FType",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17020 }],
                "signals": [{
                  "name": "ftype.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_ftype_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            _ = SignalMessageRegistration.Register(builder);
            using Stack stack = builder.Build();

            FieldId? id = stack.GetFieldId("ftype.a");
            await Assert.That(id).IsNotNull();
            FieldInfo? info = stack.GetField(id!.Value);
            await Assert.That(info!.FieldType).IsEqualTo(FieldType.F64);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RegisterFields_UsesJsonNameAndUiNameAsIs()
    {
        string json = """
            {
              "messages": [{
                "name": "copy_as_is",
                "ui_name": "Copy As Is",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17028 }],
                "signals": [{
                  "name": "already.qualified.rpm",
                  "ui_name": "Engine RPM",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_copy_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            _ = SignalMessageRegistration.Register(builder);
            using Stack stack = builder.Build();

            FieldId? id = stack.GetFieldId("already.qualified.rpm");
            await Assert.That(id).IsNotNull();
            FieldInfo? info = stack.GetField(id!.Value);
            await Assert.That(info!.UiName).IsEqualTo("Engine RPM");
            await Assert.That(stack.GetFieldId("copy_as_is.already.qualified.rpm")).IsNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_PayloadLongerThanRequired_ParsesRequiredPrefix()
    {
        string json = """
            {
              "messages": [{
                "name": "short_msg",
                "ui_name": "Short Msg",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17021 }],
                "signals": [{
                  "name": "short_msg.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian",
                  "factor": 1,
                  "offset": 0
                }]
              }]
            }
            """;

        byte[] extraPayload = [0x64, 0x00, 0xDE, 0xAD];
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17021))
            .Then(SignalMessageLayer.FromRawBytes(extraPayload))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_left_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "short_msg.a", 100.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_UnmatchedMux_AppendsMuxWithoutGroupSignals()
    {
        string json = """
            {
              "messages": [{
                "name": "mux_miss",
                "ui_name": "Mux Miss",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17022 }],
                "mux_signal": {
                  "name": "mux_miss.mux",
                  "ui_name": "Mux",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian"
                },
                "mux_groups": [{
                  "mux_value": 0,
                  "signals": [{
                    "name": "mux_miss.only0",
                    "ui_name": "Only0",
                    "start_bit": 8,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }]
                }]
              }]
            }
            """;

        byte[] payload = [0x07, 0xAA];
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17022))
            .Then(SignalMessageLayer.FromRawBytes(payload))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_muxmiss_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertU64Field(stack, packet, "mux_miss.mux.value", 7).ConfigureAwait(false);
                FieldId? groupId = stack.GetFieldId("mux_miss.only0");
                await Assert.That(groupId).IsNotNull();
                bool present = packet.TryGetFieldValue(groupId!.Value, out _, materialize: true);
                await Assert.That(present).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_CustomText_IncludesUnitAndEnum()
    {
        string json = """
            {
              "messages": [{
                "name": "txt",
                "ui_name": "Txt",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17023 }],
                "signals": [{
                  "name": "txt.st",
                  "ui_name": "State",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian",
                  "unit": "rpm",
                  "value_names": { "1": "On" }
                }]
              }]
            }
            """;

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17023))
            .Then(SignalMessageLayer.FromRawBytes((byte[])[0x01]))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_txt_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertDisplayText(stack, packet, "txt.st", "State: 1.00 rpm (1) [On]")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_CustomText_UnitWithoutEnum()
    {
        string json = """
            {
              "messages": [{
                "name": "txtu",
                "ui_name": "TxtU",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17024 }],
                "signals": [{
                  "name": "txtu.v",
                  "ui_name": "Val",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian",
                  "unit": "V"
                }]
              }]
            }
            """;

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17024))
            .Then(SignalMessageLayer.FromRawBytes((byte[])[0x02]))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_txtu_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertDisplayText(stack, packet, "txtu.v", "Val: 2.00 V (2)")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_CustomText_WideSignal_FormatsOnMaterialize()
    {
        string json = """
            {
              "messages": [{
                "name": "txtw",
                "ui_name": "TxtW",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17029 }],
                "signals": [{
                  "name": "txtw.v",
                  "ui_name": "Wide",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17029))
            .Then(SignalMessageLayer.FromRawBytes((byte[])[0x64, 0x00]))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_txtw_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertDisplayText(stack, packet, "txtw.v", "Wide: 100.00 (100)")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_CustomText_PrecomputedEnumMiss_OmitsBrackets()
    {
        string json = """
            {
              "messages": [{
                "name": "txtm",
                "ui_name": "TxtM",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17030 }],
                "signals": [{
                  "name": "txtm.st",
                  "ui_name": "State",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian",
                  "value_names": { "1": "On" }
                }]
              }]
            }
            """;

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17030))
            .Then(SignalMessageLayer.FromRawBytes((byte[])[0x00]))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_txtm_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertDisplayText(stack, packet, "txtm.st", "State: 0.00 (0)")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Ctor_EmptyName_Throws()
    {
        CompiledSignalMessage compiled = new(
            "",
            "U",
            "D",
            1,
            [],
            [],
            null,
            []);
        await Assert.That(() => new SignalMessageProtocol(
            compiled,
            new SignalMessageCompileSettings(false, false, 8))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Register_MaxEnumValuesZero_ReturnsWarning()
    {
        string json = """{ "messages": [] }""";
        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_max0_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            settings.PreloadValue(SignalMessageRegistration.MaxEnumValuesSetting, 0UL);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w => w.Kind == SettingsLoadWarningKind.OutOfRange)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AppendBuildDiagnosticsWarnings_MapsMissingDispatchTable()
    {
        string json = """
            {
              "messages": [{
                "name": "need_tbl",
                "ui_name": "Need",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "does.not.exist", "key": 1 }],
                "signals": [{
                  "name": "need_tbl.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_diag_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            List<SettingsLoadWarning> warnings = [.. SignalMessageRegistration.Register(builder)];
            using Stack stack = builder.Build();
            SignalMessageRegistration.AppendBuildDiagnosticsWarnings(stack, warnings);
            await Assert.That(warnings.Any(w =>
                w.Message.Contains("does.not.exist", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_WideMux_UsesLinearGroupScan()
    {
        string json = """
            {
              "messages": [{
                "name": "wide_mux",
                "ui_name": "Wide",
                "byte_length": 3,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17025 }],
                "mux_signal": {
                  "name": "wide_mux.mux",
                  "ui_name": "Mux",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                },
                "mux_groups": [{
                  "mux_value": 1,
                  "signals": [{
                    "name": "wide_mux.w",
                    "ui_name": "W",
                    "start_bit": 16,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }]
                }]
              }]
            }
            """;

        byte[] payload = [0x01, 0x00, 0x2A];
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17025))
            .Then(SignalMessageLayer.FromRawBytes(payload))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_wide_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "wide_mux.w", 42.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Parse_WideMuxUnmatched_OmitsGroupSignals()
    {
        string json = """
            {
              "messages": [{
                "name": "wide_miss",
                "ui_name": "Wide Miss",
                "byte_length": 3,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17026 }],
                "mux_signal": {
                  "name": "wide_miss.mux",
                  "ui_name": "Mux",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                },
                "mux_groups": [{
                  "mux_value": 1,
                  "signals": [{
                    "name": "wide_miss.w",
                    "ui_name": "W",
                    "start_bit": 16,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }]
                }]
              }]
            }
            """;

        byte[] payload = [0x02, 0x00, 0x2A];
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, 17026))
            .Then(SignalMessageLayer.FromRawBytes(payload))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_widemiss_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                FieldId? groupId = stack.GetFieldId("wide_miss.w");
                bool present = packet.TryGetFieldValue(groupId!.Value, out _, materialize: true);
                await Assert.That(present).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_DuplicateProtocolName_ReturnsWarning()
    {
        string json = """
            {
              "messages": [{
                "name": "dup_sm",
                "ui_name": "Dup",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17027 }],
                "signals": [{
                  "name": "dup_sm.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_dupname_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            builder.RegisterProtocol(new NamedStubProtocol("dup_sm"));
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w =>
                w.Message.Contains("dup_sm", StringComparison.Ordinal)
                && w.Message.Contains("already registered", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NamedStubProtocol : IProtocol
    {
        public NamedStubProtocol(string name) => Name = name;

        public string Name { get; }

        public string UiName => Name;

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => 0;
    }
}
