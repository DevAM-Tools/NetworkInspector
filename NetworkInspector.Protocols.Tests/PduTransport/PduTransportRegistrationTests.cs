// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.PduTransport;

/// <summary>
/// Stream-based JSON load and registration warnings for PDU Transport.
/// </summary>
internal sealed class PduTransportRegistrationTests
{
    #region Tests

    [Test]
    public async Task TryLoadConfig_ValidJson_ReturnsPdus()
    {
        using MemoryStream stream = _Utf8Stream("""{"pdus":[{"id":32,"name":"BenchPdu"}]}""");
        bool ok = PduTransportRegistration.TryLoadConfig(
            stream,
            out PduTransportConfig? config,
            out SettingsLoadWarning? warning);

        await Assert.That(ok).IsTrue();
        await Assert.That(warning).IsNull();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Pdus.Length).IsEqualTo(1);
        await Assert.That(config.Pdus[0].Id).IsEqualTo(32u);
        await Assert.That(config.Pdus[0].Name).IsEqualTo("BenchPdu");
    }

    [Test]
    public async Task TryLoadConfig_MalformedJson_ReturnsWarning()
    {
        using MemoryStream stream = _Utf8Stream("{ not json");
        bool ok = PduTransportRegistration.TryLoadConfig(
            stream,
            out PduTransportConfig? config,
            out SettingsLoadWarning? warning);

        await Assert.That(ok).IsFalse();
        await Assert.That(config).IsNull();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.SettingName).IsEqualTo(PduTransportRegistration.ConfigFileSetting);
    }

    [Test]
    public async Task TryLoadConfig_NullStream_Throws()
    {
        await Assert.That(() => PduTransportRegistration.TryLoadConfig(null!, out _, out _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_FromStream_SucceedsWithoutWarning()
    {
        using MemoryStream stream = _Utf8Stream("""{"pdus":[{"id":1,"name":"BrakeStatus"}]}""");
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, stream, warnings);

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(protocol.ConfigLoadWarning).IsNull();
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId(PduTransportProtocol.ProtocolName).HasValue).IsTrue();
    }

    [Test]
    public async Task Register_FromStream_Malformed_WarningNotSwallowed()
    {
        using MemoryStream stream = _Utf8Stream("{");
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, stream, warnings);

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(protocol.ConfigLoadWarning).IsNotNull();
    }

    [Test]
    public async Task Register_FromConfig_AppliesNames()
    {
        PduTransportConfig config = new()
        {
            Pdus =
            [
                new() { Id = 7, Name = "FromObject" },
            ],
        };

        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, config, warnings);

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(protocol.ConfigLoadWarning).IsNull();
    }

    [Test]
    public async Task Register_ClampedIdFieldSize_WarningReturnedToCaller()
    {
        using SettingsManager settings = new();
        settings.PreloadValue("pdu_transport.id_field_size", 3UL);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(warnings.Any(w => w.Kind == SettingsLoadWarningKind.OutOfRange)).IsTrue();
        await Assert.That(protocol.IdFieldSizeClampWarning).IsNotNull();
    }

    [Test]
    public async Task Register_ClampedLengthFieldSize_WarningReturnedToCaller()
    {
        using SettingsManager settings = new();
        settings.PreloadValue("pdu_transport.length_field_size", 8UL);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(warnings.Any(w =>
            w.Kind == SettingsLoadWarningKind.OutOfRange
            && w.SettingName == "pdu_transport.length_field_size")).IsTrue();
        await Assert.That(protocol.LengthFieldSizeClampWarning).IsNotNull();
    }

    [Test]
    public async Task TryLoadConfigFromStream_AfterRegisterFields_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        using MemoryStream stream = _Utf8Stream("""{"pdus":[]}""");
        await Assert.That(() => protocol.TryLoadConfigFromStream(stream, out _))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ApplyConfig_AfterRegisterFields_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(() => protocol.ApplyConfig(new()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ApplyConfig_Null_Throws()
    {
        PduTransportProtocol protocol = new();
        await Assert.That(() => protocol.ApplyConfig(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryLoadConfigFromStream_NullStream_Throws()
    {
        PduTransportProtocol protocol = new();
        await Assert.That(() => protocol.TryLoadConfigFromStream(null!, out _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AppendRegistrationWarnings_Null_Throws()
    {
        PduTransportProtocol protocol = new();
        await Assert.That(() => protocol.AppendRegistrationWarnings(null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_NullWarnings_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        await Assert.That(() => PduTransportRegistration.Register(builder, (ICollection<SettingsLoadWarning>)null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_NullBuilder_Throws()
    {
        List<SettingsLoadWarning> warnings = [];
        await Assert.That(() => PduTransportRegistration.Register(null!, warnings))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_NullStream_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        await Assert.That(() => PduTransportRegistration.Register(builder, (Stream)null!, warnings))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task StreamLoadFailure_StillAppliesFileSetting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ni_pdu_fileplusbad_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pdutr.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"pdus":[{"id":1,"name":"FromFile"}]}""").ConfigureAwait(false);

            using SettingsManager settings = new(dir);
            settings.PreloadValue(PduTransportRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            PduTransportProtocol protocol = new();
            using MemoryStream bad = _Utf8Stream("{");
            bool ok = protocol.TryLoadConfigFromStream(bad, out SettingsLoadWarning? loadWarning);
            await Assert.That(ok).IsFalse();
            await Assert.That(loadWarning).IsNotNull();

            ProtocolId id = builder.RegisterProtocol(protocol);
            protocol.RegisterFields(builder, id);

            await Assert.That(protocol.AdditionalConfigLoadWarning).IsNotNull();
            await Assert.That(protocol.AdditionalConfigLoadWarning!.Value.Kind)
                .IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);

            using Stack stack = builder.Build();
            Packet packet = _ParsePduDatagram(stack, _OnePdu(1, [0xAA]));
            await Assert.That(_TryGetString(stack, packet, "pdu_transport.name", out string? name)).IsTrue();
            await Assert.That(name).IsEqualTo("FromFile");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_FromStream_MergesOnTopOfFileSetting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ni_pdu_merge_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pdutr.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"pdus":[{"id":1,"name":"FromFile"}]}""").ConfigureAwait(false);

            using SettingsManager settings = new(dir);
            settings.PreloadValue(PduTransportRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            using MemoryStream extra = _Utf8Stream("""{"pdus":[{"id":1,"name":"FromStream"},{"id":2,"name":"Extra"}]}""");
            List<SettingsLoadWarning> warnings = [];
            _ = PduTransportRegistration.Register(builder, extra, warnings);
            await Assert.That(warnings.Count).IsEqualTo(0);

            using Stack stack = builder.Build();
            Packet overwritten = _ParsePduDatagram(stack, _OnePdu(1, [0xAA]));
            await Assert.That(_TryGetString(stack, overwritten, "pdu_transport.name", out string? name1)).IsTrue();
            await Assert.That(name1).IsEqualTo("FromStream");

            Packet extraPacket = _ParsePduDatagram(stack, _OnePdu(2, [0xBB]));
            await Assert.That(_TryGetString(stack, extraPacket, "pdu_transport.name", out string? name2)).IsTrue();
            await Assert.That(name2).IsEqualTo("Extra");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_FromStream_EmptyFileSetting_AppliesStreamNames()
    {
        using MemoryStream stream = _Utf8Stream("""{"pdus":[{"id":9,"name":"HandAdded"}]}""");
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        _ = PduTransportRegistration.Register(builder, stream, warnings);
        await Assert.That(warnings.Count).IsEqualTo(0);

        using Stack stack = builder.Build();
        Packet packet = _ParsePduDatagram(stack, _OnePdu(9, [0xCC]));
        await Assert.That(_TryGetString(stack, packet, "pdu_transport.name", out string? name)).IsTrue();
        await Assert.That(name).IsEqualTo("HandAdded");
    }

    [Test]
    public async Task Register_NullConfig_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        await Assert.That(() => PduTransportRegistration.Register(builder, (PduTransportConfig)null!, warnings))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_EmptyUdpDispatchPorts_NoPortWarning()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(protocol.UdpDispatchPortsWarning).IsNull();
        await Assert.That(warnings.Any(w => w.SettingName == PduTransportRegistration.UdpDispatchPortsSetting))
            .IsFalse();
    }

    [Test]
    public async Task Register_InvalidAndValidUdpDispatchPorts_WarningListsSkippedValues()
    {
        using SettingsManager settings = new();
        settings.PreloadValue(
            PduTransportRegistration.UdpDispatchPortsSetting,
            SettingValue.U64Array([47290UL, 0UL, 65536UL, 47290UL]));
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(protocol.UdpDispatchPortsWarning).IsNotNull();
        SettingsLoadWarning warning = protocol.UdpDispatchPortsWarning!.Value;
        await Assert.That(warning.Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
        await Assert.That(warning.SettingName).IsEqualTo(PduTransportRegistration.UdpDispatchPortsSetting);
        await Assert.That(warning.Message.Contains(": 0, 65536", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warning.Message.Contains("47290", StringComparison.Ordinal)).IsFalse();
        await Assert.That(warnings.Contains(warning)).IsTrue();
    }

    [Test]
    public async Task Register_ScalarU64PreloadOnUdpDispatchPorts_KeepsEmptyAndDoesNotThrow()
    {
        using SettingsManager settings = new();
        settings.PreloadValue(PduTransportRegistration.UdpDispatchPortsSetting, 47290UL);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<SettingsLoadWarning> warnings = [];
        PduTransportProtocol protocol = PduTransportRegistration.Register(builder, warnings);

        await Assert.That(protocol.UdpDispatchPortsWarning).IsNull();
        ulong[]? current = settings.GetU64ArraySetting(PduTransportRegistration.UdpDispatchPortsSetting);
        await Assert.That(current).IsNotNull();
        await Assert.That(current!.Length).IsEqualTo(0);
    }

    #endregion

    #region Helpers

    private static MemoryStream _Utf8Stream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);

    private static byte[] _OnePdu(uint id, byte[] payload)
    {
        byte[] datagram = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(0, 4), id);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(datagram.AsSpan(8));
        return datagram;
    }

    private static Packet _ParsePduDatagram(Stack stack, byte[] datagram)
    {
        ProtocolId pduId = stack.GetProtocolId(PduTransportProtocol.ProtocolName)!.Value;
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            datagram,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        return Packet.ParseFrame(new PacketId(0), stack, frame, pduId);
    }

    private static bool _TryGetString(Stack stack, Packet packet, string fieldName, out string? value)
    {
        value = null;
        FieldId? fieldId = stack.GetFieldId(fieldName);
        if (fieldId is null)
        {
            return false;
        }

        if (!packet.TryGetFieldValue(fieldId.Value, out FieldValue fv, materialize: true))
        {
            return false;
        }

        return fv.Data.TryGetAsString(out value);
    }

    #endregion
}
