// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The registration of SOME/IP: the settings with their defaults and ranges, the three ways to supply a configuration (file
/// setting, stream, object), what a broken configuration reports, and the guard against registering the protocol twice.
/// </summary>
internal sealed class SomeIpRegistrationTests
{
    private const string _Alpha = """{"schema_version":"1.0","services":[{"service_id":1,"major_version":1,"name":"Alpha","methods":[],"data_types":[]}]}""";

    private static byte[] _Frame(ushort serviceId = 1, ushort methodId = 1) =>
        FrameBuilderTestExtensions.WrapRawUdp(SomeIpBytes.Message(serviceId, methodId), 40000, 30490);

    #region Configuration sources

    [Test]
    public async Task AnEmptyConfigurationPathRegistersTheProtocolWithoutWarnings()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Frame());

        await Assert.That(s.Warnings.Count).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.service.name")).IsEqualTo(0);
    }

    [Test]
    public async Task AnUnreadableConfigurationGivesExactlyOneDistinctWarningAndNoNames()
    {
        string truncated = """{"schema_version": "1.0", "services": [""";
        using SomeIpTestStack missing = SomeIpTestStack.Create(null, ("someip.config_file", SettingValue.String("missing.json")));
        using SomeIpTestStack directory = SomeIpTestStack.Create(null, ("someip.config_file", SettingValue.String("folder")));
        using SomeIpTestStack broken = SomeIpTestStack.Create(truncated);

        string[] texts = new string[3];
        SomeIpTestStack[] stacks = [missing, directory, broken];
        for (int i = 0; i < stacks.Length; i++)
        {
            await Assert.That(stacks[i].Warnings.Count).IsEqualTo(1);
            texts[i] = stacks[i].Warnings[0].Message;
            Packet packet = stacks[i].Parse(_Frame());
            await Assert.That(stacks[i].Count(packet, "someip")).IsEqualTo(1);
            await Assert.That(stacks[i].Count(packet, "someip.service.name")).IsEqualTo(0);
        }

        await Assert.That(texts.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task AConfigurationFromAStreamNamesTheSameAsTheFileSetting()
    {
        using SomeIpTestStack fromFile = SomeIpTestStack.Create(_Alpha);
        Packet expected = fromFile.Parse(_Frame());

        using Stack fromStream = _BuildCustomStack(static builder =>
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(_Alpha));
            return SomeIpRegistration.Register(builder, stream);
        }, out IReadOnlyList<SettingsLoadWarning> warnings);
        Packet actual = ProtocolTestHelper.ParseFrame(fromStream, _Frame(), 0, Timestamp.FromMillis(0));

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(_StringsOf(fromStream, actual, "someip.service.name")).IsEquivalentTo(fromFile.Strings(expected, "someip.service.name"));
        await Assert.That(_StringsOf(fromStream, actual, "someip.service.name").Count).IsEqualTo(1);
    }

    [Test]
    public async Task AConfigurationFromAnObjectBehavesLikeTheStream()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(_Alpha));
        _ = SomeIpRegistration.TryLoadConfig(stream, out SomeIpConfig? config, out SettingsLoadWarning? loadWarning);
        await Assert.That(loadWarning).IsNull();
        await Assert.That(config).IsNotNull();

        using Stack stack = _BuildCustomStack(builder => SomeIpRegistration.Register(builder, config!), out IReadOnlyList<SettingsLoadWarning> warnings);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, _Frame(), 0, Timestamp.FromMillis(0));

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(_StringsOf(stack, packet, "someip.service.name")).IsEquivalentTo(_Items("Alpha"));
    }

    [Test]
    public async Task AStreamThatIsNotJsonGivesOneWarningAndTheProtocolStillRegisters()
    {
        using Stack stack = _BuildCustomStack(
            static builder =>
            {
                using MemoryStream stream = new(Encoding.UTF8.GetBytes("not json"));
                return SomeIpRegistration.Register(builder, stream);
            },
            out IReadOnlyList<SettingsLoadWarning> warnings);

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(stack.GetProtocolId(SomeIpProtocol.ProtocolName)).IsNotNull();
    }

    [Test]
    public async Task RegisteringSomeIpTwiceOnOneBuilderWarnsAndKeepsTheFirstRegistration()
    {
        IReadOnlyList<SettingsLoadWarning>? second = null;
        using Stack stack = _BuildCustomStack(
            builder =>
            {
                IReadOnlyList<SettingsLoadWarning> first = SomeIpRegistration.Register(builder);
                second = SomeIpRegistration.Register(builder);
                return first;
            },
            out IReadOnlyList<SettingsLoadWarning> firstWarnings);

        await Assert.That(firstWarnings.Count).IsEqualTo(0);
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Count).IsEqualTo(1);
        await Assert.That(second[0].Message).Contains("already registered");
    }

    [Test]
    public async Task ARefusedDocumentVersionStillRegistersTheProtocolWithoutNames()
    {
        const string futureVersion = """{"schema_version":"9.0","services":[{"service_id":1,"major_version":1,"name":"Alpha","methods":[],"data_types":[]}]}""";
        using SomeIpTestStack s = SomeIpTestStack.Create(futureVersion);
        Packet packet = s.Parse(_Frame());

        await Assert.That(s.Warnings.Count).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.service.name")).IsEqualTo(0);
    }

    #endregion

    #region Settings

    [Test]
    public async Task EverySettingExistsWithItsDocumentedDefault()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        ReadOnlySettingsManagerView settings = s.Stack.Settings;

        await Assert.That(settings.GetSettingsInGroup("someip").Count).IsEqualTo(23);
        await Assert.That(settings.GetStringSetting(SomeIpRegistration.ConfigFileSetting)).IsEqualTo(string.Empty);
        await Assert.That(settings.GetStringSetting(SomeIpRegistration.FieldPrefixSetting)).IsEqualTo(string.Empty);
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.DecodePayloadSetting)).IsTrue();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.ShowTypeMetadataSetting)).IsFalse();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.VerifyE2eCrcSetting)).IsFalse();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.MethodTimingSetting)).IsFalse();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.MethodTimingIgnoreInterfaceSetting)).IsFalse();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.LearnSdPortsSetting)).IsFalse();
        await Assert.That(settings.GetU64ArraySetting(SomeIpRegistration.LearnSdPortsIgnoreSetting)!.Length).IsEqualTo(0);
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.HeuristicSetting)).IsFalse();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.TcpResyncSetting)).IsTrue();
        await Assert.That(settings.GetBoolSetting(SomeIpRegistration.DetectDtlsSetting)).IsFalse();
    }

    [Test]
    public async Task EveryLimitIsASettingWithTheRangeAndTheSevenTextsOfItsDefinition()
    {
        await Assert.That(SomeIpLimits.All.Count).IsEqualTo(11);

        using SomeIpTestStack s = SomeIpTestStack.Create();
        foreach (SomeIpLimitDefinition limit in SomeIpLimits.All)
        {
            await Assert.That(limit.Key.StartsWith("someip.", StringComparison.Ordinal)).IsTrue();
            string[] texts =
            [
                limit.Texts.Title, limit.Texts.Description, limit.Texts.Reason, limit.Texts.Benefit,
                limit.Texts.SmallerValues, limit.Texts.LargerValues, limit.Texts.OffEffect,
            ];
            foreach (string text in texts)
            {
                await Assert.That(string.IsNullOrWhiteSpace(text)).IsFalse().Because(Invariant($"{limit.Key} has an empty text"));
            }

            await Assert.That(limit.Texts.Title.EndsWith('.')).IsFalse();
            await Assert.That(limit.Minimum).IsLessThanOrEqualTo(limit.Default);
            await Assert.That(limit.Default).IsLessThanOrEqualTo(limit.Maximum);

            ReadOnlySettingView? setting = s.Stack.Settings.GetSetting(limit.Key);
            await Assert.That(setting).IsNotNull().Because(Invariant($"{limit.Key} must be registered"));
            ReadOnlySettingView view = setting!.Value;
            await Assert.That(view.MinValue!.Value.TryGetAsU64(out ulong min)).IsTrue();
            await Assert.That(view.MaxValue!.Value.TryGetAsU64(out ulong max)).IsTrue();
            await Assert.That(min).IsEqualTo(limit.Minimum);
            await Assert.That(max).IsEqualTo(limit.Maximum);
            await Assert.That(view.Description).Contains(limit.Texts.Reason);
        }
    }

    [Test]
    [Arguments(0UL)]
    [Arguments(2UL * 1024 * 1024 * 1024)]
    public async Task APreloadedLimitOutsideTheRangeIsRefusedWithTheRangeNamedAndTheDefaultKept(ulong value)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.tp_max_message_size", SettingValue.U64(value)));

        await Assert.That(s.Warnings.Count).IsEqualTo(1);
        await Assert.That(s.Warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
        await Assert.That(s.Warnings[0].SettingName).IsEqualTo("someip.tp_max_message_size");
        await Assert.That(s.Warnings[0].Message).Contains("1024 to 1073741824");
        await Assert.That(s.Stack.Settings.GetU64Setting("someip.tp_max_message_size")).IsEqualTo(10UL * 1024 * 1024);
    }

    [Test]
    public async Task AllowedPreloadedValuesAreKeptWithoutWarningsAndReachTheParser()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(
            null,
            ("someip.max_messages_per_unit", SettingValue.U64(2)),
            ("someip.tp_max_buffered_bytes", SettingValue.U64(8UL * 1024 * 1024 * 1024)));

        Packet packet = s.Parse(FrameBuilderTestExtensions.WrapRawUdp(
            SomeIpBytes.Concat(SomeIpBytes.Message(1, 1), SomeIpBytes.Message(1, 2), SomeIpBytes.Message(1, 3)), 40000, 30490));

        await Assert.That(s.Warnings.Count).IsEqualTo(0);
        await Assert.That(s.Stack.Settings.GetU64Setting("someip.tp_max_buffered_bytes")).IsEqualTo(8UL * 1024 * 1024 * 1024);
        await Assert.That(s.Count(packet, "someip")).IsEqualTo(2);
        await Assert.That(s.Count(packet, "someip.error.unit_limit")).IsEqualTo(1);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds a stack that registers only the lean profiling set and then SOME/IP through the given registration, so the
    /// registration under test is the only one (the standard registration would already have registered SOME/IP).
    /// </summary>
    private static Stack _BuildCustomStack(
        Func<StackBuilder, IReadOnlyList<SettingsLoadWarning>> register,
        out IReadOnlyList<SettingsLoadWarning> warnings)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        _ = builder.RegisterFairProfilingProtocols();
        warnings = register(builder);
        return builder.Build();
    }

    private static List<string> _StringsOf(Stack stack, Packet packet, string fieldName)
    {
        List<string> values = [];
        FieldId id = stack.GetFieldId(fieldName)!.Value;
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id && field.Value.Data.TryGetAsString(out string? value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static T[] _Items<T>(params T[] values) => values;

    #endregion
}
