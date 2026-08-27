// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Stream-based JSON load and registration warnings for signal messages.
/// </summary>
internal sealed class SignalMessageStreamTests
{
    #region Tests

    [Test]
    public async Task TryLoadConfig_ValidJson_ReturnsMessages()
    {
        using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
        bool ok = SignalMessageRegistration.TryLoadConfig(
            stream,
            out SignalMessagesConfig? config,
            out SettingsLoadWarning? warning);

        await Assert.That(ok).IsTrue();
        await Assert.That(warning).IsNull();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Messages.Length).IsEqualTo(1);
        await Assert.That(config.Messages[0].Name).IsEqualTo("stream_msg");
    }

    [Test]
    public async Task TryLoadConfig_MalformedJson_ReturnsWarning()
    {
        using MemoryStream stream = _Utf8Stream("{ not json");
        bool ok = SignalMessageRegistration.TryLoadConfig(
            stream,
            out SignalMessagesConfig? config,
            out SettingsLoadWarning? warning);

        await Assert.That(ok).IsFalse();
        await Assert.That(config).IsNull();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.SettingName).IsEqualTo(SignalMessageRegistration.ConfigFileSetting);
    }

    [Test]
    public async Task TryLoadConfig_NullStream_Throws()
    {
        await Assert.That(() => SignalMessageRegistration.TryLoadConfig(null!, out _, out _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_FromStream_RegistersMessage()
    {
        using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);

        await Assert.That(warnings.Count).IsEqualTo(0);
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId("stream_msg").HasValue).IsTrue();
    }

    [Test]
    public async Task Register_FromStream_Malformed_ReturnsWarning_DoesNotThrow()
    {
        using MemoryStream stream = _Utf8Stream("{");
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);

        await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId("stream_msg").HasValue).IsFalse();
    }

    [Test]
    public async Task Register_FromConfig_RegistersMessage()
    {
        using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
        bool ok = SignalMessageRegistration.TryLoadConfig(stream, out SignalMessagesConfig? config, out _);
        await Assert.That(ok).IsTrue();

        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, config!);

        await Assert.That(warnings.Count).IsEqualTo(0);
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId("stream_msg").HasValue).IsTrue();
    }

    [Test]
    public async Task Register_EmptyDispatchTable_ReturnsWarning()
    {
        string json = """
            {
              "messages": [{
                "name": "empty_table_msg",
                "ui_name": "EmptyTable",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "  ", "key": 1 }],
                "signals": [{
                  "name": "empty_table_msg.s",
                  "ui_name": "S",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        using MemoryStream stream = _Utf8Stream(json);
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);

        await Assert.That(warnings.Any(w => w.Message.Contains("table name is empty", StringComparison.Ordinal))).IsTrue();
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId("empty_table_msg").HasValue).IsTrue();
    }

    [Test]
    public async Task Register_FromStream_AfterStandardProtocols_AddsAdditionalMessage()
    {
        using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        _ = builder.RegisterStandardProtocols();
        IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);

        await Assert.That(warnings.Count).IsEqualTo(0);
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId("stream_msg").HasValue).IsTrue();
        await Assert.That(stack.GetProtocolId(PduTransportProtocol.ProtocolName).HasValue).IsTrue();
    }

    [Test]
    public async Task Register_FromStream_WithFileSetting_RegistersBothSources()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ni_sig_both_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, _FileOnlyMessageJson).ConfigureAwait(false);

            using SettingsManager settings = new(dir);
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder, stream);

            await Assert.That(warnings.Count).IsEqualTo(0);
            using Stack stack = builder.Build();
            await Assert.That(stack.GetProtocolId("file_only_msg").HasValue).IsTrue();
            await Assert.That(stack.GetProtocolId("stream_msg").HasValue).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_NullBuilder_Throws()
    {
        using MemoryStream stream = _Utf8Stream(_ValidOneMessageJson);
        await Assert.That(() => SignalMessageRegistration.Register(null!, stream))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_NullStream_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        await Assert.That(() => SignalMessageRegistration.Register(builder, (Stream)null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_NullConfig_Throws()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        await Assert.That(() => SignalMessageRegistration.Register(builder, (SignalMessagesConfig)null!))
            .Throws<ArgumentNullException>();
    }

    #endregion

    #region Helpers

    private const string _FileOnlyMessageJson = """
        {
          "messages": [{
            "name": "file_only_msg",
            "ui_name": "FileOnly",
            "byte_length": 1,
            "dispatch_bindings": [{ "table": "udp.port", "key": 17302 }],
            "signals": [{
              "name": "file_only_msg.s",
              "ui_name": "S",
              "start_bit": 0,
              "bit_length": 8,
              "byte_order": "little_endian"
            }]
          }]
        }
        """;

    private const string _ValidOneMessageJson = """
        {
          "messages": [{
            "name": "stream_msg",
            "ui_name": "Stream",
            "byte_length": 1,
            "dispatch_bindings": [{ "table": "udp.port", "key": 17301 }],
            "signals": [{
              "name": "stream_msg.s",
              "ui_name": "S",
              "start_bit": 0,
              "bit_length": 8,
              "byte_order": "little_endian"
            }]
          }]
        }
        """;

    private static MemoryStream _Utf8Stream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);

    #endregion
}
