// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The registry: lookups and their refusal texts, name rules, settings per scope, concurrent reads, and sealed builders.</summary>
internal sealed class PluginRegistryTests
{
    #region Constants

    private static readonly string[] _SourceKinds = ["Alpha", "beta"];

    #endregion

    #region Lookup

    [Test]
    public async Task AnEmptyRegistryListsNothingAndNamesNoKnownKinds()
    {
        using PluginRegistry registry = new();

        await Assert.That(registry.Sources).IsEmpty();
        await Assert.That(registry.FrameExporters).IsEmpty();
        await Assert.That(registry.PacketExporters).IsEmpty();
        await Assert.That(registry.TryGetSource("x", out _, out string? problem)).IsFalse();
        await Assert.That(problem).IsEqualTo("Unknown source kind 'x'. Known kinds: none.");
    }

    [Test]
    public async Task LookupsAreCaseInsensitiveAndKeepRegistrationOrder()
    {
        using LoaderHarness harness = new("registry-lookup");
        harness.Load([], [
            new ScriptedPlugin("P1", static b =>
            {
                b.AddSource(ScriptedPlugin.Source("Alpha"));
                b.AddFrameExporter(ScriptedPlugin.FrameExporter("FRM"));
            }),
            new ScriptedPlugin("P2", static b =>
            {
                b.AddSource(ScriptedPlugin.Source("beta"));
                b.AddPacketExporter(ScriptedPlugin.PacketExporter("PKT"));
            }),
        ]);
        PluginRegistry registry = harness.Registry;

        await Assert.That(registry.Sources.Select(static source => source.Kind).ToArray()).IsEquivalentTo(_SourceKinds, CollectionOrdering.Matching);
        await Assert.That(registry.TryGetSource("ALPHA", out SourceRegistration? alpha, out _)).IsTrue();
        await Assert.That(Expect.NotNull(alpha).Kind).IsEqualTo("Alpha");
        await Assert.That(registry.TryGetPacketExporter("FRM", out _, out string? writesFrames)).IsFalse();
        await Assert.That(writesFrames).IsEqualTo("Format 'FRM' writes frames.");
        await Assert.That(registry.TryGetFrameExporter("PKT", out _, out string? writesPackets)).IsFalse();
        await Assert.That(writesPackets).IsEqualTo("Format 'PKT' writes packets.");
        await Assert.That(registry.TryGetFrameExporter("nope", out _, out string? unknown)).IsFalse();
        await Assert.That(unknown).IsEqualTo("Unknown format 'nope'. Known formats: FRM, PKT.");
        await Assert.That(registry.SourceOwner("ALPHA")).IsEqualTo("P1");
        await Assert.That(registry.FormatOwner("pkt")).IsEqualTo("P2");
        await Assert.That(registry.SourceOwner("unknown")).IsNull();
    }

    [Test]
    public async Task ADuplicateKindOrFormatFailsTheLaterPlugin()
    {
        using LoaderHarness harness = new("registry-duplicates");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b =>
            {
                b.AddSource(ScriptedPlugin.Source("Alpha"));
                b.AddFrameExporter(ScriptedPlugin.FrameExporter("frm"));
            }),
            new ScriptedPlugin("P2", static b => b.AddSource(ScriptedPlugin.Source("alpha"))),
            new ScriptedPlugin("P3", static b => b.AddPacketExporter(ScriptedPlugin.PacketExporter("FRM"))),
        ]);

        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins[1].Reason)).Contains("Source kind 'alpha' is already registered by plugin 'P1'.");
        await Assert.That(report.Plugins[2].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins[2].Reason)).Contains("Format 'FRM' is already registered by plugin 'P1'.");
    }

    [Test]
    [Arguments("")]
    [Arguments("a b")]
    [Arguments("1x")]
    public async Task InvalidKindNamesFailThePlugin(string kind)
    {
        using LoaderHarness harness = new("registry-names");

        PluginLoadReport report = harness.Load([], [new ScriptedPlugin("P1", b => b.AddSource(ScriptedPlugin.Source(kind)))]);

        await Assert.That(report.Plugins.Single().State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins.Single().Reason)).StartsWith("Configure failed: ArgumentException:");
    }

    [Test]
    public async Task AKindOf256CharactersIsAcceptedAnd257IsNot()
    {
        using LoaderHarness harness = new("registry-name-length");
        string accepted = "k" + new string('x', 255);

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", b => b.AddSource(ScriptedPlugin.Source(accepted))),
            new ScriptedPlugin("P2", b => b.AddSource(ScriptedPlugin.Source(accepted + "x"))),
        ]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Failed);
    }

    [Test]
    public async Task BadRegistrationDetailsFailThePlugin()
    {
        using LoaderHarness harness = new("registry-details");
        SourceRegistration noDot = ScriptedPlugin.Source("K1") with { FileExtensions = ["blf"] };
        SourceRegistration nullCreate = ScriptedPlugin.Source("K2") with { Create = null! }; // deliberate null: the builder must refuse it
        SourceRegistration nullName = ScriptedPlugin.Source("K3") with { UiName = null! };
        ExporterRegistration<IPacketListener> noExtDot = ScriptedPlugin.PacketExporter("F1") with { FileExtension = "txt" };

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", b => b.AddSource(noDot)),
            new ScriptedPlugin("P2", b => b.AddSource(nullCreate)),
            new ScriptedPlugin("P3", b => b.AddSource(nullName)),
            new ScriptedPlugin("P4", b => b.AddPacketExporter(noExtDot)),
        ]);

        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Failed)).IsTrue();
    }

    [Test]
    public async Task OptionActionsThatRegisterBadOrDuplicateNamesFailAtCommit()
    {
        using LoaderHarness harness = new("registry-options");
        SourceRegistration badName = ScriptedPlugin.Source("K1") with
        {
            RegisterOptions = static options => options.RegisterBoolSetting("bad name", "Bad", string.Empty, true),
        };
        SourceRegistration twice = ScriptedPlugin.Source("K2") with
        {
            RegisterOptions = static options =>
            {
                options.RegisterBoolSetting("flag", "Flag", string.Empty, true);
                options.RegisterBoolSetting("flag", "Flag", string.Empty, true);
            },
        };

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", b => b.AddSource(badName)),
            new ScriptedPlugin("P2", b => b.AddSource(twice)),
        ]);

        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Failed)).IsTrue();
        await Assert.That(Expect.NotNull(report.Plugins[0].Reason)).StartsWith("Registration failed:");
    }

    #endregion

    #region Disable

    [Test]
    public async Task DisablingAPluginRefusesItsContributionsButKeepsItsOwnerKnown()
    {
        using LoaderHarness harness = new("registry-disable");
        harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSource(ScriptedPlugin.Source("Alpha"))),
            new ScriptedPlugin("P2", static b => b.AddSource(ScriptedPlugin.Source("Beta"))),
        ]);
        PluginRegistry registry = harness.Registry;

        registry.Disable("P1", "plugin P1 failed to start");
        registry.Disable("P1", "a second reason changes nothing");

        await Assert.That(registry.IsDisabled("P1")).IsTrue();
        await Assert.That(registry.Sources.Select(static source => source.Kind).ToArray()).IsEquivalentTo(["Beta"]);
        await Assert.That(registry.SourceOwner("alpha")).IsEqualTo("P1");
        await Assert.That(registry.TryGetSource("alpha", out _, out string? problem)).IsFalse();
        await Assert.That(problem).IsEqualTo("Source kind 'alpha' is not available: plugin P1 failed to start.");
    }

    [Test]
    public async Task ApplyStackContributionsSkipsDisabledPluginsAndCallsAfterEachForEnabledOnes()
    {
        using LoaderHarness harness = new("registry-stack");
        harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddStackContribution(static stack => stack.RegisterProtocol(new MarkerProtocol("p1")))),
            new ScriptedPlugin("P2", static b => b.AddStackContribution(static stack => stack.RegisterProtocol(new MarkerProtocol("p2")))),
        ]);
        harness.Registry.Disable("P1", "plugin P1 failed to start");
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        List<string> after = [];

        IReadOnlyList<PluginContributionFailure> failures = harness.Registry.ApplyStackContributions(builder, after.Add);

        await Assert.That(failures).IsEmpty();
        await Assert.That(after).IsEquivalentTo(["P2"]);
        await Assert.That(builder.GetProtocolId("p1")).IsNull();
        await Assert.That(builder.GetProtocolId("p2")).IsNotNull();
    }

    #endregion

    #region Settings

    [Test]
    public async Task SettingsAreAppliedPerScopeAndAlsoForDisabledPlugins()
    {
        using LoaderHarness harness = new("registry-settings");
        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("Test.Healthy", static b =>
            {
                b.AddSettings(SettingScope.Application, static r => r.RegisterI64Setting("test.healthy.level", "Level", "test.healthy", 1));
                b.AddSettings(SettingScope.Profile, static r => r.RegisterI64Setting("test.healthy.profile", "Level", "test.healthy", 2));
            }),
        ]);
        harness.Registry.Disable("Test.Healthy", "plugin Test.Healthy failed to start");
        using SettingsManager application = new();
        using SettingsManager profile = new();

        IReadOnlyList<PluginContributionFailure> appFailures = harness.Registry.ApplySettings(SettingScope.Application, application);
        IReadOnlyList<PluginContributionFailure> profileFailures = harness.Registry.ApplySettings(SettingScope.Profile, profile);

        await Assert.That(appFailures).IsEmpty();
        await Assert.That(profileFailures).IsEmpty();
        await Assert.That(application.GetSetting("test.healthy.level")).IsNotNull();
        await Assert.That(application.GetSetting("test.healthy.profile")).IsNull();
        await Assert.That(profile.GetSetting("test.healthy.profile")).IsNotNull();
        await Assert.That(Expect.NotNull(report.Plugins.Single().Contributions[ContributionKind.Setting]).Count).IsEqualTo(2);
    }

    [Test]
    public async Task APluginCannotRegisterASettingThatTheLoaderOwns()
    {
        using LoaderHarness harness = new("registry-loader-settings");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSettings(SettingScope.Application, static r => r.RegisterStringArraySetting("plugins.paths", "Paths", "plugins", []))),
        ]);

        await Assert.That(report.Plugins.Single().State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins.Single().Reason)).StartsWith("Registration failed: DuplicateNameSettingsException:");
    }

    [Test]
    public async Task SessionScopeIsRefusedAtConfigurationAndAtApply()
    {
        using LoaderHarness harness = new("registry-session-scope");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSettings(SettingScope.Session, static r => r.RegisterBoolSetting("x.y", "Y", "x", true))),
        ]);

        await Assert.That(report.Plugins.Single().State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins.Single().Reason)).StartsWith("Configure failed: ArgumentOutOfRangeException:");
        using SettingsManager manager = new();
        await Assert.That(() => harness.Registry.ApplySettings(SettingScope.Session, manager)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TheSameSettingNameInTwoPluginsFailsTheSecondAtCommit()
    {
        using LoaderHarness harness = new("registry-duplicate-setting");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSettings(SettingScope.Application, static r => r.RegisterBoolSetting("shared.flag", "Flag", "shared", true))),
            new ScriptedPlugin("P2", static b => b.AddSettings(SettingScope.Application, static r => r.RegisterBoolSetting("shared.flag", "Flag", "shared", false))),
        ]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins[1].Reason)).StartsWith("Registration failed: DuplicateNameSettingsException:");
    }

    [Test]
    public async Task AFailedCommitLeavesNoSettingNameBehindForALaterPlugin()
    {
        using LoaderHarness harness = new("registry-failed-commit");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSettings(SettingScope.Application, static r =>
            {
                r.RegisterBoolSetting("probe.flag", "Flag", "probe", true);
                throw new InvalidOperationException("boom after the first registration");
            })),
            new ScriptedPlugin("P2", static b => b.AddSettings(SettingScope.Application, static r =>
                r.RegisterBoolSetting("probe.flag", "Flag", "probe", false))),
        ]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AFailedCommitLeavesNoProtocolBehindForALaterPlugin()
    {
        using LoaderHarness harness = new("registry-failed-protocol");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddStackContribution(static stack =>
            {
                stack.RegisterProtocol(new MarkerProtocol("shared"));
                throw new InvalidOperationException("boom after the protocol");
            })),
            new ScriptedPlugin("P2", static b => b.AddStackContribution(static stack => stack.RegisterProtocol(new MarkerProtocol("shared")))),
        ]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task APluginThatFailsAfterAHealthyOneStillConflictsWithThatHealthyOne()
    {
        using LoaderHarness harness = new("registry-failed-after-healthy");

        PluginLoadReport report = harness.Load([], [
            new ScriptedPlugin("P1", static b => b.AddSettings(SettingScope.Application, static r => r.RegisterBoolSetting("shared.flag", "Flag", "shared", true))),
            new ScriptedPlugin("P2", static b => b.AddSettings(SettingScope.Application, static r =>
            {
                r.RegisterBoolSetting("other.flag", "Flag", "other", true);
                throw new InvalidOperationException("boom");
            })),
            new ScriptedPlugin("P3", static b => b.AddSettings(SettingScope.Application, static r => r.RegisterBoolSetting("shared.flag", "Flag", "shared", false))),
        ]);

        // The rebuilt probes still know the healthy plugin's name, so the duplicate is found, and they forgot the failed one's.
        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(report.Plugins[2].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(report.Plugins[2].Reason)).StartsWith("Registration failed: DuplicateNameSettingsException:");
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ReadersSeeConsistentSnapshotsWhilePluginsAreDisabled()
    {
        using LoaderHarness harness = new("registry-concurrent");
        List<IPlugin> plugins = [];
        for (int index = 0; index < 20; index++)
        {
            int number = index;
            plugins.Add(new ScriptedPlugin("P" + number.ToString(CultureInfo.InvariantCulture), b =>
            {
                b.AddSource(ScriptedPlugin.Source("S" + number.ToString(CultureInfo.InvariantCulture)));
                b.AddPacketExporter(ScriptedPlugin.PacketExporter("E" + number.ToString(CultureInfo.InvariantCulture)));
                b.AddStackContribution(stack => stack.RegisterProtocol(new MarkerProtocol("p" + number.ToString(CultureInfo.InvariantCulture))));
            }));
        }

        harness.Load([], plugins);
        PluginRegistry registry = harness.Registry;
        using Barrier start = new(5);

        Task<string?>[] readers = new Task<string?>[4];
        for (int reader = 0; reader < readers.Length; reader++)
        {
            readers[reader] = Task.Factory.StartNew<string?>(() =>
            {
                start.SignalAndWait();
                int lastCount = int.MaxValue;
                for (int loop = 0; loop < 2000; loop++)
                {
                    // Disabling is monotonic and removes source and exporter of a plugin in one step.
                    int count = registry.Sources.Count;
                    if (count > lastCount || count < 10 || count > 20)
                    {
                        return "source count not monotonic or out of range: " + count.ToString(CultureInfo.InvariantCulture);
                    }

                    lastCount = count;
                    bool sourceAvailable = registry.TryGetSource("S3", out _, out _);
                    bool exporterAvailable = registry.TryGetPacketExporter("E3", out _, out _);
                    if (!sourceAvailable && exporterAvailable)
                    {
                        return "source gone while its exporter is still available";
                    }

                    using SettingsManager settings = new();
                    StackBuilder builder = new(settings, new FrameInterfaceRegistry());
                    if (registry.ApplyStackContributions(builder).Count != 0)
                    {
                        return "a stack contribution failed";
                    }
                }

                return null;
            }, TaskCreationOptions.LongRunning);
        }

        start.SignalAndWait();
        for (int index = 0; index < 10; index++)
        {
            registry.Disable("P" + index.ToString(CultureInfo.InvariantCulture), "plugin failed to start");
        }

        string?[] results = await Task.WhenAll(readers);

        await Assert.That(results.All(static result => result is null)).IsTrue();
        await Assert.That(registry.Sources.Count).IsEqualTo(10);
    }

    #endregion

    #region Sealed builders

    [Test]
    public async Task AKeptBuilderRefusesUseAfterTheCommit()
    {
        using LoaderHarness harness = new("registry-sealed");
        IPluginBuilder? kept = null;
        ScriptedPlugin plugin = new("Keeper", builder => kept = builder);

        harness.Load([], [plugin]);

        IPluginBuilder keptBuilder = Expect.NotNull(kept);
        await Assert.That(() => keptBuilder.AddSource(ScriptedPlugin.Source("Late"))).Throws<InvalidOperationException>()
            .WithMessage("The plugin builder can only be used while the plugin is configured.");
    }

    [Test]
    public async Task TheRecorderRefusesCommitActionsAfterTheCommitButStillRecords()
    {
        ContributionRecorder recorder = new("P1");
        recorder.OnCommit(static () => { });
        recorder.Commit();

        await Assert.That(() => recorder.OnCommit(static () => { })).Throws<InvalidOperationException>();
        recorder.Record(ContributionKind.Protocol, "late", "late callback");
        await Assert.That(recorder.Snapshot()[ContributionKind.Protocol].Single().Name).IsEqualTo("late");
        await Assert.That(() => recorder.Commit()).Throws<InvalidOperationException>();
    }

    #endregion

    #region Test doubles

    // A protocol that parses nothing; it exists so that stack contributions have something to register.
    private sealed class MarkerProtocol(string name) : IProtocol
    {
        public string Name { get; } = name;

        public string UiName => Name;

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    #endregion
}
