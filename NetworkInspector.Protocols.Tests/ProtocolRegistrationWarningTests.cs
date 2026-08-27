// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// <see cref="ProtocolRegistration.RegisterStandardProtocols"/> must return
/// PDU Transport and Signal Message warnings instead of discarding them.
/// </summary>
internal sealed class ProtocolRegistrationWarningTests
{
    #region Tests

    [Test]
    public async Task RegisterStandardProtocols_BadSignalMessageJson_ReturnsWarning()
    {
        string json = """
            {
              "messages": [{
                "name": "bad_std_msg",
                "ui_name": "Bad",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17401 }],
                "signals": [{
                  "name": "bad_std_msg.a",
                  "ui_name": "A",
                  "start_bit": 0,
                  "bit_length": 16,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_std_warn_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            using SettingsManager settings = new(dir);
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, "signal_message.json");
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());

            IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();

            await Assert.That(warnings.Any(w => w.Message.Contains("bad_std_msg", StringComparison.Ordinal))).IsTrue();

            using Stack stack = builder.Build();
            await Assert.That(stack.GetProtocolId("bad_std_msg").HasValue).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RegisterStandardProtocols_ClampedPduFieldSize_ReturnsWarning()
    {
        using SettingsManager settings = new();
        settings.PreloadValue("pdu_transport.id_field_size", 3UL);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());

        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();

        await Assert.That(warnings.Any(w =>
            w.Kind == SettingsLoadWarningKind.OutOfRange
            && w.SettingName == "pdu_transport.id_field_size")).IsTrue();
    }

    [Test]
    public async Task RegisterStandardProtocols_MissingPduConfigFile_ReturnsWarning()
    {
        using SettingsManager settings = new();
        settings.PreloadValue(PduTransportRegistration.ConfigFileSetting, "missing-pdu-config.json");
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());

        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();

        await Assert.That(warnings.Any(w =>
            w.Kind == SettingsLoadWarningKind.ExternalConfigUnavailable
            && w.SettingName == PduTransportRegistration.ConfigFileSetting)).IsTrue();
    }

    [Test]
    public async Task RegisterStandardProtocols_NoExternalConfig_ReturnsEmpty()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
        await Assert.That(warnings.Count).IsEqualTo(0);
        using Stack stack = builder.Build();
        await Assert.That(stack.GetProtocolId(PduTransportProtocol.ProtocolName).HasValue).IsTrue();
    }

    #endregion
}
