// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Tests for signal message configuration handling: value names whose keys denote the same number, and the allowed range of
/// the <c>signal_message.max_enum_values</c> setting.
/// </summary>
internal sealed class SignalMessageHardeningTests
{
    private static SignalMessageConfig _MessageWithValueNames(Dictionary<string, string> valueNames) => new()
    {
        Name = "enum_msg",
        UiName = "Enum message",
        ByteLength = 1,
        DispatchBindings = [new() { Table = "udp.port", Key = 1 }],
        Signals =
        [
            new()
            {
                Name = "s",
                UiName = "S",
                StartBit = 0,
                BitLength = 8,
                ByteOrder = "little_endian",
                ValueNames = valueNames,
            },
        ],
    };

    private static (CompiledSignalMessage[] Compiled, List<SettingsLoadWarning> Warnings) _Compile(SignalMessageConfig message)
    {
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [message] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        return (compiled, warnings);
    }

    #region Value names

    [Test]
    [Arguments("1", "01")]
    [Arguments("2", "+2")]
    [Arguments("0", "000")]
    public async Task ValueNamesKeysDenotingTheSameNumberAreReported(string first, string second)
    {
        (CompiledSignalMessage[] compiled, List<SettingsLoadWarning> warnings) = _Compile(
            _MessageWithValueNames(new Dictionary<string, string> { [first] = "A", [second] = "B" }));

        await Assert.That(compiled.Length).IsEqualTo(1);
        await Assert.That(compiled[0].StaticSignals.Length).IsEqualTo(0);
        await Assert.That(warnings.Any(w => w.Message.Contains("duplicate value_names key", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ValueNamesWithDistinctNumbersAreAccepted()
    {
        (CompiledSignalMessage[] compiled, List<SettingsLoadWarning> warnings) = _Compile(
            _MessageWithValueNames(new Dictionary<string, string> { ["1"] = "A", ["2"] = "B" }));

        await Assert.That(compiled[0].StaticSignals.Length).IsEqualTo(1);
        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    #endregion

    #region max_enum_values range

    [Test]
    public async Task MaxEnumValuesOfZeroIsRejectedByTheSettingAndTheDefaultStillLoadsTheMessages()
    {
        // The setting range and the compiler must agree: 0 is not a valid cap, so the setting refuses it (keeping the
        // default) instead of registering it and then discarding every message of the configuration.
        string json = """
            { "messages": [{
                "name": "enum_msg", "ui_name": "Enum message", "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 1 }],
                "signals": [{ "name": "enum_msg.s", "ui_name": "S", "start_bit": 0, "bit_length": 8, "byte_order": "little_endian",
                              "value_names": { "1": "ON" } }]
            }] }
            """;
        string directory = Path.Combine(Path.GetTempPath(), "ni_spdu_maxenum_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new(Path.GetTempPath());
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            settings.PreloadValue(SignalMessageRegistration.MaxEnumValuesSetting, 0UL);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());

            IReadOnlyList<SettingsLoadWarning> warnings = ProtocolRegistration.RegisterStandardProtocols(builder);

            await Assert.That(warnings.Any(w => w.Kind == SettingsLoadWarningKind.OutOfRange
                && w.SettingName == SignalMessageRegistration.MaxEnumValuesSetting)).IsTrue();
            await Assert.That(builder.GetProtocolId("enum_msg")).IsNotNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion
}
