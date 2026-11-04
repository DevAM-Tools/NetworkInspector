// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Checks the schemas and examples that the package ships: the schema files are well-formed, every example loads through its
/// protocol's registration without a warning, a frame for a configured item decodes to a named field, and the examples
/// together show every mechanism of the configuration they belong to (a new mechanism without an example fails).
/// </summary>
internal sealed class ShippedConfigTests
{
    private const ushort _PduDispatchPort = 47290;
    private const ushort _MultiplexedPort = 47300;

    // Engine status payload: rpm 1000 (raw 4000 = 0x0FA0, little endian), coolant 90 degC (raw 130), state Running in the
    // low nibble of byte 3, torque 100.0 Nm (raw 1000 = 0x03E8, big endian at byte 4).
    private static readonly byte[] _EngineStatusPayload = [0xA0, 0x0F, 0x82, 0x02, 0x03, 0xE8, 0x00, 0x00];

    private static byte[] _UdpFrame(ushort sourcePort, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = AutomotiveEthUdpFrames.TestEthernet();
        IPv4Layer ip = AutomotiveEthUdpFrames.TestIpv4();
        UdpLayer udp = new(sourcePort, destinationPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _PduFrame(ushort pduId, ReadOnlySpan<byte> payload)
    {
        // The shipped profile sets two-byte id and length fields.
        byte[] pdu = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(pdu, pduId);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(pdu.AsSpan(4));
        return _UdpFrame(15002, _PduDispatchPort, pdu);
    }

    private static Packet _Parse(Stack stack, byte[] frame) =>
        ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, Timestamp.FromSecs(0));

    #region Schemas

    [Test]
    [Arguments("pdu-transport-config.schema.json")]
    [Arguments("signal-message-config.schema.json")]
    [Arguments("someip-config.schema.json")]
    public async Task SchemaIsJsonWithAnIdThatEndsWithItsFileName(string fileName)
    {
        using JsonDocument document = JsonDocument.Parse(ShippedConfigFixture.ReadSchemaText(fileName));

        string id = document.RootElement.GetProperty("$id").GetString()!;

        await Assert.That(id.EndsWith(fileName, StringComparison.Ordinal)).IsTrue();
        await Assert.That(document.RootElement.GetProperty("$schema").GetString()).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
    }

    [Test]
    public async Task SomeIpSchemaDefinesTheSevenTypeCategories()
    {
        using JsonDocument document = JsonDocument.Parse(ShippedConfigFixture.ReadSchemaText("someip-config.schema.json"));

        JsonElement variants = document.RootElement.GetProperty("$defs").GetProperty("data_type").GetProperty("oneOf");
        List<string> categories = [];
        foreach (JsonElement variant in variants.EnumerateArray())
        {
            string reference = variant.GetProperty("$ref").GetString()!;
            JsonElement definition = document.RootElement.GetProperty("$defs").GetProperty(reference[(reference.LastIndexOf('/') + 1)..]);
            categories.Add(definition.GetProperty("properties").GetProperty("category").GetProperty("const").GetString()!);
        }

        await Assert.That(categories.Count).IsEqualTo(7);
        await Assert.That(categories.Contains("base_type", StringComparer.Ordinal)).IsTrue();
        await Assert.That(categories.Contains("tlv_structure", StringComparer.Ordinal)).IsTrue();
        await Assert.That(categories.Contains("alias", StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NegativeSamplesAreShippedOnlyWithTheTestProject()
    {
        // The package ships schemas and examples; the negative samples sit in the test project's Fixtures folder.
        foreach (string name in new[] { "unknown_primitive.json", "unknown_category.json", "unknown_property.json" })
        {
            await Assert.That(File.Exists(Path.Combine(ShippedConfigFixture.FixturesRoot, "someip", "negative", name))).IsTrue();
            await Assert.That(ShippedConfigFixture.AllExamplePaths().Any(path => Path.GetFileName(path) == name)).IsFalse();
        }
    }

    [Test]
    public async Task EveryShippedExampleIsWellFormedJson()
    {
        IReadOnlyList<string> examples = ShippedConfigFixture.AllExamplePaths();

        await Assert.That(examples.Count).IsGreaterThanOrEqualTo(10);
        foreach (string path in examples)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            await Assert.That(document.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
    }

    #endregion

    #region PDU Transport and Signal Message examples

    [Test]
    public async Task PduTransportExampleLoadsWithoutWarningAndNamesThreePdus()
    {
        using FileStream stream = ShippedConfigFixture.ReadExample("pdu-transport", "pdu_names.json");

        bool loaded = PduTransportRegistration.TryLoadConfig(stream, out PduTransportConfig? config, out SettingsLoadWarning? warning);

        await Assert.That(loaded).IsTrue();
        await Assert.That(warning).IsNull();
        await Assert.That(config!.Pdus.Length).IsEqualTo(3);
        await Assert.That(config.Pdus.Any(pdu => pdu.Comment is not null)).IsTrue();
    }

    [Test]
    [Arguments("can_and_pdu_bindings.json")]
    [Arguments("multiplexed.json")]
    public async Task SignalMessageExampleRegistersWithoutAWarning(string fileName)
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> standard = builder.RegisterStandardProtocols();
        using FileStream stream = ShippedConfigFixture.ReadExample("signal-message", fileName);

        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);
        using Stack stack = builder.Build();
        List<SettingsLoadWarning> unresolved = [];
        SignalMessageRegistration.AppendBuildDiagnosticsWarnings(stack, unresolved);

        await Assert.That(standard.Count).IsEqualTo(0);
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(unresolved.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SignalMessageExamplesTogetherShowEveryMechanism()
    {
        List<SignalFieldConfig> signals = [];
        List<MuxGroupConfig> groups = [];
        HashSet<string> tables = new(StringComparer.Ordinal);
        bool mux = false;
        foreach (string name in new[] { "can_and_pdu_bindings.json", "multiplexed.json" })
        {
            using FileStream stream = ShippedConfigFixture.ReadExample("signal-message", name);
            _ = SignalMessageRegistration.TryLoadConfig(stream, out SignalMessagesConfig? config, out _);
            foreach (SignalMessageConfig message in config!.Messages)
            {
                signals.AddRange(message.Signals);
                groups.AddRange(message.MuxGroups);
                mux |= message.MuxSignal is not null;
                foreach (NetworkInspector.Protocols.SignalMessage.DispatchBinding binding in message.DispatchBindings)
                {
                    _ = tables.Add(binding.Table);
                }
            }
        }

        foreach (MuxGroupConfig group in groups)
        {
            signals.AddRange(group.Signals);
        }

        await Assert.That(signals.Any(s => s.ByteOrder == "big_endian")).IsTrue();
        await Assert.That(signals.Any(s => s.ByteOrder == "little_endian")).IsTrue();
        await Assert.That(signals.Any(s => s.Factor != 1.0 && s.Offset == 0)).IsTrue();
        await Assert.That(signals.Any(s => s.Offset != 0)).IsTrue();
        await Assert.That(signals.Any(s => s.Unit.Length > 0)).IsTrue();
        await Assert.That(signals.Any(s => s.Min is not null && s.Max is not null)).IsTrue();
        await Assert.That(signals.Any(s => s.ValueNames is { Count: > 0 })).IsTrue();
        await Assert.That(tables.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(mux).IsTrue();
        await Assert.That(groups.Count).IsEqualTo(2);
    }

    #endregion

    #region Profile folder and decoded frames

    [Test]
    public async Task ProfileFolderBuildsAStackThatDecodesPduTransportAndSignalMessage()
    {
        string profile = ShippedConfigFixture.ProfileDirectory("pdu_transport.json", "signal_message.json");
        try
        {
            using SettingsManager settings = new(profile);
            IReadOnlyList<SettingsLoadWarning> loadWarnings = settings.Load();
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
            using Stack stack = builder.Build();

            Packet packet = _Parse(stack, _PduFrame(4097, _EngineStatusPayload));

            await Assert.That(loadWarnings.Count).IsEqualTo(0);
            await Assert.That(warnings.Count).IsEqualTo(0);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "pdu_transport.id", 4097);
            await ProtocolTestHelper.AssertStringField(stack, packet, "pdu_transport.name", "EngineStatus");
            await ProtocolTestHelper.AssertF64FieldApprox(stack, packet, "engine_status.rpm", 1000.0, 1e-9);
            await ProtocolTestHelper.AssertF64FieldApprox(stack, packet, "engine_status.coolant_temp", 90.0, 1e-9);
            await ProtocolTestHelper.AssertF64FieldApprox(stack, packet, "engine_status.torque", 100.0, 1e-9);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }


    [Test]
    public async Task ProfileFolderBuildsAStackThatDecodesSomeIpWithNamesAndTheProfileLimit()
    {
        string profile = ShippedConfigFixture.ProfileDirectory("someip.json");
        try
        {
            using SettingsManager settings = new(profile);
            IReadOnlyList<SettingsLoadWarning> loadWarnings = settings.Load();
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
            using Stack stack = builder.Build();

            // The full example names service 62002 (0xF232) version 2 "ClimateControl" and method 1 "SetTemperature".
            byte[] message = SomeIpBytes.Message(0xF232, 0x0001, [0, 0], clientId: 1, interfaceVersion: 2);
            Packet packet = _Parse(stack, _UdpFrame(40000, 30490, message));

            await Assert.That(loadWarnings.Count).IsEqualTo(0);

            // The profile copies the full example, which lists tls_ports and dtls_ports; the shipped stack cannot decrypt and says
            // so once per list. Nothing else may be reported.
            await Assert.That(warnings.Count).IsEqualTo(2);
            await Assert.That(warnings.All(static w => w.Message.Contains("do not decrypt", StringComparison.Ordinal))).IsTrue();
            await ProtocolTestHelper.AssertStringField(stack, packet, "someip.service.name", "Climate Control");
            await ProtocolTestHelper.AssertStringField(stack, packet, "someip.method.name", "Set Temperature");
            await ProtocolTestHelper.AssertStringField(stack, packet, "someip.client.name", "CockpitECU");
            await Assert.That(stack.Settings.GetU64Setting("someip.tp_max_message_size")).IsEqualTo(33554432UL);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Test]
    [MethodDataSource(nameof(SomeIpExampleNames))]
    public async Task EverySomeIpExampleRegistersWithOnlyThePlaintextWarningsAndNamesItsFirstService(string fileName)
    {
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        _ = builder.RegisterFairProfilingProtocols();

        using FileStream definition = ShippedConfigFixture.ReadExample("someip", fileName);
        _ = SomeIpRegistration.TryLoadConfig(definition, out SomeIpConfig? config, out SettingsLoadWarning? loadWarning);
        await Assert.That(loadWarning).IsNull();
        SomeIpServiceConfig first = config!.Services[0];

        using FileStream stream = ShippedConfigFixture.ReadExample("someip", fileName);
        IReadOnlyList<SettingsLoadWarning> warnings = SomeIpRegistration.Register(builder, stream);
        using Stack stack = builder.Build();

        byte[] message = SomeIpBytes.Message((ushort)first.ServiceId, 0x0001, interfaceVersion: (byte)first.MajorVersion);
        Packet packet = _Parse(stack, _UdpFrame(40000, 30490, message));

        // The shipped TLS and DTLS parsers do not decrypt, so an example that lists tls_ports or dtls_ports is told so, once per
        // list; any other warning is a defect of the example.
        int expectedWarnings = 0;
        if (config.TlsPorts.Length > 0)
        {
            expectedWarnings++;
        }

        if (config.DtlsPorts.Length > 0)
        {
            expectedWarnings++;
        }

        await Assert.That(warnings.Count).IsEqualTo(expectedWarnings);
        await Assert.That(warnings.All(static w => w.Message.Contains("do not decrypt", StringComparison.Ordinal))).IsTrue();
        await ProtocolTestHelper.AssertStringField(stack, packet, "someip.service.name", first.UiName ?? first.Name);
    }

    public static IEnumerable<Func<string>> SomeIpExampleNames() =>
        Directory.EnumerateFiles(Path.Combine(ShippedConfigFixture.SchemasRoot, "examples", "someip"), "*.json")
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .Select(static name => (Func<string>)(() => name));
    [Test]
    public async Task MultiplexedExampleDecodesTheSelectedGroup()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        _ = builder.RegisterStandardProtocols();
        using FileStream stream = ShippedConfigFixture.ReadExample("signal-message", "multiplexed.json");
        _ = SignalMessageRegistration.Register(builder, stream);
        using Stack stack = builder.Build();

        // heartbeat 7, page 1 (the lamp group), lamp level 80 %.
        Packet packet = _Parse(stack, _UdpFrame(15003, _MultiplexedPort, [0x07, 0x01, 0x50, 0x00]));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, packet, "body_control.heartbeat", 7.0, 1e-9);
        await ProtocolTestHelper.AssertF64FieldApprox(stack, packet, "body_control.lamp_level", 80.0, 1e-9);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "body_control.door_state");
    }

    [Test]
    public async Task ProfileExamplesNameTheFilesTheFixtureCopies()
    {
        string profile = ShippedConfigFixture.ProfileDirectory();
        try
        {
            foreach (string group in new[] { "pdu_transport.json", "signal_message.json", "someip.json" })
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, group)));
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.EndsWith(".config_file", StringComparison.Ordinal))
                    {
                        await Assert.That(File.Exists(Path.Combine(profile, property.Value.GetString()!))).IsTrue();
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    #endregion
}
