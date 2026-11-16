// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// The command line seeds the limit options of a source and of an output format from the limits stored in the profile, lets an
/// inline option override them, and reports a stored value that the option refuses.
/// </summary>
internal sealed class LimitOptionTests
{
    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    #region Helpers

    /// <summary>Builds a text set that every definition below shares; the sentences end with a period as the definition requires.</summary>
    private static LimitTexts _Texts() => new(
        Title: "Largest demo size",
        Description: "The largest size of one demo item.",
        Reason: "A hostile input could otherwise use all memory.",
        Benefit: "A broken input is refused with a clear message.",
        SmallerValues: "Items above the value are refused: 1 MiB refuses a 2 MiB item.",
        LargerValues: "More memory may be used per item: 1 GiB lets a 900 MiB item through.",
        OffEffect: "Every item is accepted and a hostile input can use all memory.");

    /// <summary>The limit as the profile stores it: any value from 1 byte up.</summary>
    private static readonly LimitDefinition _StoredDefinition = new(
        "demo.maxSize", LimitUnits.Bytes, 128 * _MiB, 1, 1L << 40, _Texts());

    /// <summary>The same limit as the plugin's option declares it: a narrower range, as another plugin version might.</summary>
    private static readonly LimitDefinition _OptionDefinition = new(
        "demo.maxSize", LimitUnits.Bytes, 128 * _MiB, _MiB, 1L << 30, _Texts());

    /// <summary>The profile settings with the stored limit, as the session of a run would hold them.</summary>
    private static SettingsManager _Profile(long? stored)
    {
        SettingsManager profile = new();
        profile.RegisterSetting(Setting.FromLimit(_StoredDefinition));
        if (stored is { } value)
        {
            Setting setting = profile.GetSetting("demo.maxSize") ?? throw new InvalidOperationException("not registered");
            setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(value)));
            profile.ApplyChanges();
        }

        return profile;
    }

    /// <summary>A registry with the demo plugin, which records the limit its source and its format receive.</summary>
    private sealed class DemoRegistry : IDisposable
    {
        private readonly string _Folder = Directory.CreateTempSubdirectory("ni-limit-registry-").FullName;

        internal DemoRegistry()
        {
            Registry = new();
            new PluginLoader(new PluginLoaderOptions(Path.Combine(_Folder, "cache.json"), [Plugin]))
                .Load(PluginTier.Core, [], Registry, CancellationToken.None);
        }

        internal DemoPlugin Plugin { get; } = new();

        internal PluginRegistry Registry { get; }

        public void Dispose()
        {
            Registry.Dispose();
            try
            {
                Directory.Delete(_Folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp folder that cannot be removed is left to the operating system; the test result does not depend on it.
            }
        }
    }

    /// <summary>A plugin with one source kind and one packet format that both declare the limit as the option <c>maxSize</c>.</summary>
    private sealed class DemoPlugin : IPlugin
    {
        internal LimitValue? SeenBySource { get; private set; }

        internal LimitValue? SeenByFormat { get; private set; }

        public PluginInfo Info { get; } = new("Test.LimitDemo", "1.0.0", "MIT", "Limit demo plugin.", []);

        public void Configure(IPluginBuilder builder)
        {
            builder.AddSource(new SourceRegistration("LimitDemo", "Limit demo", "A source that creates nothing.", [], false,
                static registrar => registrar.RegisterLimitOption(_OptionDefinition), _TryCreateSource));
            builder.AddPacketExporter(new ExporterRegistration<IPacketListener>("LimitDemo", "Limit demo", "Writes nothing.", ".limitdemo",
                static registrar => registrar.RegisterLimitOption(_OptionDefinition),
                ExporterCreation.ForStreams<IPacketListener>(_TryCreateFormat)));
        }

        public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        private bool _TryCreateSource(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
            [NotNullWhen(false)] out PluginError? error)
        {
            SeenBySource = request.Options.GetLimit("maxSize");
            source = null;
            error = new PluginError(PluginErrorCode.Failed, "The demo source creates nothing.");
            return false;
        }

        private bool _TryCreateFormat(StreamExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
            [NotNullWhen(false)] out PluginError? error)
        {
            SeenByFormat = request.Options.GetLimit("maxSize");
            listener = null;
            error = new PluginError(PluginErrorCode.Failed, "The demo format creates nothing.");
            return false;
        }
    }

    /// <summary>A path in the temp folder for the output file of one test; nothing is created there.</summary>
    private static string _TempPath() =>
        Path.Combine(Path.GetTempPath(), "ni-limit-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".limitdemo");

    /// <summary>A stack whose settings hold the stored limit, as the stack of a run holds the profile settings.</summary>
    private static Stack _Stack(SettingsManager settings)
    {
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        return builder.Build();
    }

    #endregion

    #region Source

    [Test]
    public async Task ASourceOptionStartsAtTheStoredLimitOfTheProfile()
    {
        using DemoRegistry demo = new();
        using SettingsManager profile = _Profile(stored: 256 * _MiB);
        SourceConfig config = SourceConfig.Parse("limitdemo:", demo.Registry);

        config.TryCreateSource(profile.ReadOnly, profile.GetLimit, out _, out _, static _ => { });

        await Assert.That(demo.Plugin.SeenBySource).IsEqualTo((LimitValue?)LimitValue.Of(256 * _MiB));
    }

    [Test]
    public async Task ASourceOptionStartsAtTheDefinitionDefaultWhenNothingIsStoredAndAnInlineTextOverridesAStoredLimit()
    {
        using DemoRegistry demo = new();
        using SettingsManager empty = new();
        using SettingsManager profile = _Profile(stored: 256 * _MiB);

        SourceConfig.Parse("limitdemo:", demo.Registry).TryCreateSource(empty.ReadOnly, empty.GetLimit, out _, out _, static _ => { });
        LimitValue? withoutStore = demo.Plugin.SeenBySource;
        SourceConfig.Parse("limitdemo:maxSize=64MiB", demo.Registry).TryCreateSource(profile.ReadOnly, profile.GetLimit, out _, out _, static _ => { });
        LimitValue? withInline = demo.Plugin.SeenBySource;

        await Assert.That(withoutStore).IsEqualTo((LimitValue?)_OptionDefinition.DefaultValue);
        await Assert.That(withInline).IsEqualTo((LimitValue?)LimitValue.Of(64 * _MiB));
    }

    [Test]
    public async Task AStoredLimitThatTheOptionRefusesIsReportedAndTheDefaultApplies()
    {
        using DemoRegistry demo = new();
        using SettingsManager profile = _Profile(stored: _KiB);
        List<PluginError> reported = [];

        SourceConfig.Parse("limitdemo:", demo.Registry).TryCreateSource(profile.ReadOnly, profile.GetLimit, out _, out _, reported.Add);

        await Assert.That(demo.Plugin.SeenBySource).IsEqualTo((LimitValue?)_OptionDefinition.DefaultValue);
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].Message).Contains("demo.maxSize");
        await Assert.That(reported[0].Message).Contains("allowed 1 MiB – 1 GiB, or off");
    }

    #endregion

    #region Output format

    [Test]
    public async Task AFormatOptionStartsAtTheStoredLimitOfTheStackSettingsAndAnInlineTextOverridesIt()
    {
        using DemoRegistry demo = new();
        SettingsManager profile = _Profile(stored: 256 * _MiB);
        using Stack stack = _Stack(profile);
        string path = _TempPath();

        PacketFormatConfig.Parse("limitdemo", demo.Registry).TryCreate(path, stack, stack.Settings.GetLimit, static _ => { },
            out _, out _, CancellationToken.None);
        LimitValue? stored = demo.Plugin.SeenByFormat;
        PacketFormatConfig.Parse("limitdemo:maxSize=64MiB", demo.Registry).TryCreate(path, stack, stack.Settings.GetLimit, static _ => { },
            out _, out _, CancellationToken.None);
        LimitValue? inline = demo.Plugin.SeenByFormat;

        await Assert.That(stored).IsEqualTo((LimitValue?)LimitValue.Of(256 * _MiB));
        await Assert.That(inline).IsEqualTo((LimitValue?)LimitValue.Of(64 * _MiB));
    }

    [Test]
    public async Task AStoredLimitThatTheFormatRefusesIsReportedAndTheDefaultApplies()
    {
        using DemoRegistry demo = new();
        SettingsManager profile = _Profile(stored: _KiB);
        using Stack stack = _Stack(profile);
        string path = _TempPath();
        List<PluginError> reported = [];

        PacketFormatConfig.Parse("limitdemo", demo.Registry).TryCreate(path, stack, stack.Settings.GetLimit, reported.Add,
            out _, out _, CancellationToken.None);

        await Assert.That(demo.Plugin.SeenByFormat).IsEqualTo((LimitValue?)_OptionDefinition.DefaultValue);
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].Message).Contains("allowed 1 MiB – 1 GiB, or off");
    }

    #endregion
}
