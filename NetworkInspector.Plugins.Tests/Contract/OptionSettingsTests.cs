// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Options of sources and exporters are Core settings: registration into a per-request manager and the Core validation of
/// typed values. The tests prove that no second validation path exists next to the Core one.
/// </summary>
internal sealed class OptionSettingsTests
{
    #region Constants

    private static readonly string[] _TransportNames = ["udp", "tcp"];

    #endregion

    #region Registration

    [Test]
    public async Task SourceOptionsRegisterTheDocumentedSettings()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        Setting count = Expect.NotNull(manager.GetSetting("count"));
        Setting transport = Expect.NotNull(manager.GetSetting("transport"));
        Setting port = Expect.NotNull(manager.GetSetting("port"));
        Setting startCounter = Expect.NotNull(manager.GetSetting("startCounter"));

        await Assert.That(manager.SettingCount).IsEqualTo(4);
        await Assert.That(count.Type).IsEqualTo(SettingType.I64);
        await Assert.That(count.DefaultValue).IsEqualTo(SettingValue.I64(100));
        await Assert.That(count.MinValue).IsEqualTo(SettingValue.I64(0));
        await Assert.That(count.MaxValue).IsEqualTo(SettingValue.I64(int.MaxValue));
        await Assert.That(transport.Type).IsEqualTo(SettingType.Enum);
        await Assert.That(Expect.NotNull(transport.EnumMetadata).AllowedValues.Select(static value => value.Name).ToArray())
            .IsEquivalentTo(_TransportNames);
        await Assert.That(Expect.NotNull(manager.ReadOnly.GetEnumSetting("transport")).Name).IsEqualTo("udp");
        await Assert.That(port.DefaultValue).IsEqualTo(SettingValue.I64(40000));
        await Assert.That(port.MinValue).IsEqualTo(SettingValue.I64(1));
        await Assert.That(port.MaxValue).IsEqualTo(SettingValue.I64(65535));
        await Assert.That(startCounter.Type).IsEqualTo(SettingType.U64);
        await Assert.That(startCounter.DefaultValue).IsEqualTo(SettingValue.U64(0));
    }

    [Test]
    public async Task EveryOptionHasUiNameAndDescription()
    {
        using SettingsManager source = OptionManager.Create(SampleSource.RegisterOptions);
        using SettingsManager exporter = OptionManager.Create(SampleTextExporter.RegisterOptions);

        foreach (ReadOnlySettingView setting in source.ReadOnly.AllSettings.Concat(exporter.ReadOnly.AllSettings))
        {
            await Assert.That(setting.UiName).IsNotEmpty();
            await Assert.That(setting.Description).IsNotNull();
            await Assert.That(Expect.NotNull(setting.Description)).IsNotEmpty();
        }
    }

    [Test]
    public async Task ExporterOptionsRegisterIncludeHeader()
    {
        using SettingsManager manager = OptionManager.Create(SampleTextExporter.RegisterOptions);

        await Assert.That(manager.SettingCount).IsEqualTo(1);
        await Assert.That(manager.ReadOnly.GetBoolSetting("includeHeader")).IsTrue();
    }

    [Test]
    public async Task OptionsReturnTheirDefaultsThroughTheCoreAccessors()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);
        ReadOnlySettingsManagerView view = manager.ReadOnly;

        await Assert.That(view.GetI64Setting("count")).IsEqualTo(100L);
        await Assert.That(view.GetI64Setting("port")).IsEqualTo(40000L);
        await Assert.That(view.GetU64Setting("startCounter")).IsEqualTo(0UL);
    }

    [Test]
    public async Task RunningTheActionOnASecondManagerSucceeds()
    {
        using SettingsManager first = OptionManager.Create(SampleSource.RegisterOptions);
        using SettingsManager second = OptionManager.Create(SampleSource.RegisterOptions);

        // Each run creates new Setting instances, so the two managers are independent.
        OptionManager.Set(first, "count", SettingValue.I64(7));

        await Assert.That(first.ReadOnly.GetI64Setting("count")).IsEqualTo(7L);
        await Assert.That(second.ReadOnly.GetI64Setting("count")).IsEqualTo(100L);
    }

    [Test]
    public async Task RunningTheActionTwiceOnOneManagerThrowsDuplicateName()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        await Assert.That(() => SampleSource.RegisterOptions(new SettingsRegistrar(manager))).Throws<DuplicateNameSettingsException>();
    }

    [Test]
    public async Task RegistrationWithoutOptionsYieldsAnEmptyManager()
    {
        using SettingsManager manager = OptionManager.Create(static _ => { });

        await Assert.That(manager.SettingCount).IsEqualTo(0);
    }

    #endregion

    #region Values reach Create

    [Test]
    public async Task ValidValuesReachTheCreateDelegate()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);
        OptionManager.Set(manager, "count", SettingValue.I64(5));
        OptionManager.Set(manager, "transport", SettingValue.Enum("tcp", 1));

        bool created = SampleSource.Registration.Create(OptionManager.SourceRequest(manager), out IFrameSource? source, out PluginError? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        IFrameSource createdSource = Expect.NotNull(source);
        await Assert.That(createdSource.EstimatedFrameCount).IsEqualTo(5);
        await Assert.That(Expect.NotNull(createdSource.Description)).Contains("TCP");
        createdSource.Dispose();
    }

    [Test]
    [Arguments(1L)]
    [Arguments(65535L)]
    public async Task PortAcceptsTheInclusiveBounds(long port)
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        OptionManager.Set(manager, "port", SettingValue.I64(port));

        await Assert.That(manager.ReadOnly.GetI64Setting("port")).IsEqualTo(port);
    }

    [Test]
    [Arguments(0L)]
    [Arguments(65536L)]
    public async Task PortRefusesValuesOutsideTheBounds(long port)
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        await Assert.That(() => OptionManager.Set(manager, "port", SettingValue.I64(port))).Throws<ValidationSettingsException>();

        // The refused value must not change what readers see.
        await Assert.That(manager.ReadOnly.GetI64Setting("port")).IsEqualTo(40000L);
    }

    [Test]
    [Arguments(0L, true)]
    [Arguments(int.MaxValue, true)]
    [Arguments(-1L, false)]
    [Arguments(int.MaxValue + 1L, false)]
    public async Task CountBoundsAreInclusive(long count, bool accepted)
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        if (accepted)
        {
            OptionManager.Set(manager, "count", SettingValue.I64(count));
            await Assert.That(manager.ReadOnly.GetI64Setting("count")).IsEqualTo(count);
        }
        else
        {
            await Assert.That(() => OptionManager.Set(manager, "count", SettingValue.I64(count))).Throws<ValidationSettingsException>();
            await Assert.That(manager.ReadOnly.GetI64Setting("count")).IsEqualTo(100L);
        }
    }

    [Test]
    public async Task UnknownEnumValueIsRefused()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        await Assert.That(() => OptionManager.Set(manager, "transport", SettingValue.Enum("sctp", 2))).Throws<ValidationSettingsException>();
        await Assert.That(Expect.NotNull(manager.ReadOnly.GetEnumSetting("transport")).Name).IsEqualTo("udp");
    }

    [Test]
    public async Task WrongValueTypeIsRefused()
    {
        using SettingsManager manager = OptionManager.Create(SampleSource.RegisterOptions);

        await Assert.That(() => OptionManager.Set(manager, "count", SettingValue.String("5"))).Throws<TypeMismatchSettingsException>();
        await Assert.That(manager.ReadOnly.GetI64Setting("count")).IsEqualTo(100L);
    }

    #endregion
}
