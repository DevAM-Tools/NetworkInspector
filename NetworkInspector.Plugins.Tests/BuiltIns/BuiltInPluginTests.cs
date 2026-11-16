// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The four built-in plugins as the loader sees them: records, contributions, and the stack they build.</summary>
internal sealed class BuiltInPluginTests
{
    #region Helpers

    private static string[] _Names(PluginRecord record, ContributionKind kind) =>
        record.Contributions.TryGetValue(kind, out IReadOnlyList<ContributionEntry>? entries)
            ? [.. entries.Select(static entry => entry.Name)]
            : [];

    private static string _ExpectedVersion()
    {
        string informational = Expect.NotNull(typeof(NetworkInspectorBuiltIns).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()).InformationalVersion;
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? informational
            : informational[..plus];
    }

    private static string[] _ProtocolNames(Stack stack) => [.. stack.Protocols.ToArray().Select(static p => p.Name).Order(StringComparer.Ordinal)];

    #endregion

    #region Instances

    [Test]
    public async Task CreateAllReturnsFourNewPluginsInTheFixedOrder()
    {
        IReadOnlyList<IPlugin> first = NetworkInspectorBuiltIns.CreateAll();
        IReadOnlyList<IPlugin> second = NetworkInspectorBuiltIns.CreateAll();

        await Assert.That(first.Select(static plugin => plugin.Info.Name).ToArray()).IsEquivalentTo(
            ["NetworkInspector.StandardProtocols", "NetworkInspector.FileSources", "NetworkInspector.RandomSource", "NetworkInspector.Exporters"],
            CollectionOrdering.Matching);
        for (int i = 0; i < first.Count; i++)
        {
            Type type = first[i].GetType();
            await Assert.That(type.IsPublic && type.IsSealed).IsTrue();
            await Assert.That(ReferenceEquals(first[i], second[i])).IsFalse();
        }
    }

    #endregion

    #region Contributions

    [Test]
    public async Task TheLoaderRecordsFourLoadedBuiltInsWithTheirContributions()
    {
        using LoaderHarness harness = new("builtins-records");

        PluginLoadReport report = harness.Load([], NetworkInspectorBuiltIns.CreateAll());

        await Assert.That(report.Plugins.Count).IsEqualTo(4);
        foreach (PluginRecord record in report.Plugins)
        {
            await Assert.That(Invariant($"{record.Name}:{record.State}:{record.BuiltIn}:{record.Tier}:{record.License}:{record.Version}"))
                .IsEqualTo(Invariant($"{record.Name}:Loaded:True:Core:MIT:{_ExpectedVersion()}"));
            await Assert.That(record.Dependencies).IsEmpty();
        }

        PluginRecord protocols = report.Plugins.Single(static r => r.Name == "NetworkInspector.StandardProtocols");
        string[] protocolNames = _Names(protocols, ContributionKind.Protocol);
        await Assert.That(protocolNames).Contains("eth");
        await Assert.That(protocolNames).Contains("ip");
        await Assert.That(protocolNames).Contains("udp");
        await Assert.That(protocolNames).Contains("tcp");
        await Assert.That(protocolNames).Contains("dns");
        await Assert.That(protocolNames).Contains("http");
        await Assert.That(_Names(protocols, ContributionKind.Heuristic).Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(_Names(protocols, ContributionKind.Reassembly)).Contains("dns");
        await Assert.That(_Names(protocols, ContributionKind.Reassembly)).Contains("http");
        await Assert.That(_Names(protocols, ContributionKind.Setting)).Contains("tcp.relative_sequence_numbers");
        await Assert.That(_Names(protocols, ContributionKind.Setting)).Contains("signal_message.config_file");

        // The file sources, the random source, and the exporters each register exactly their own kinds.
        await Assert.That(_Names(report.Plugins.Single(static r => r.Name == "NetworkInspector.FileSources"), ContributionKind.Source))
            .IsEquivalentTo(["BLF", "PCAPNG", "PCAP", "ASC"], CollectionOrdering.Matching);
        await Assert.That(_Names(report.Plugins.Single(static r => r.Name == "NetworkInspector.RandomSource"), ContributionKind.Source))
            .IsEquivalentTo(["Random"], CollectionOrdering.Matching);
        await Assert.That(_Names(report.Plugins.Single(static r => r.Name == "NetworkInspector.Exporters"), ContributionKind.Exporter))
            .IsEquivalentTo(["BLF", "PCAPNG", "ASC", "CSV", "JSON", "Text", "PBF", "Parquet", "DuckDB"], CollectionOrdering.Matching);
        await Assert.That(harness.Registry.Sources.Count).IsEqualTo(5);
        await Assert.That(harness.Registry.FrameExporters.Count).IsEqualTo(3);
        await Assert.That(harness.Registry.PacketExporters.Count).IsEqualTo(6);
    }

    [Test]
    [Category("Slow")]
    public async Task ACopyOfTheAssemblyInAPluginFolderIsADuplicateNotASecondLoad()
    {
        using LoaderHarness harness = new("builtins-duplicate");
        string pluginFolder = Path.Combine(harness.Folder.Path, "plugins");
        TestPluginFiles.CopyInto(typeof(NetworkInspectorBuiltIns).Assembly.Location, pluginFolder);

        PluginLoadReport report = harness.Load([pluginFolder], NetworkInspectorBuiltIns.CreateAll());

        // The built-ins come first; the copies found in the folder lose against them by name.
        await Assert.That(report.Plugins.Count(static r => r.State == PluginLoadState.Loaded && r.BuiltIn)).IsEqualTo(4);
        await Assert.That(report.Plugins.Where(static r => !r.BuiltIn).All(static r => r.State != PluginLoadState.Loaded)).IsTrue();
        await Assert.That(harness.Registry.Sources.Count).IsEqualTo(5);
    }

    #endregion

    #region Stack

    [Test]
    public async Task TheBuiltInStackEqualsTheStackOfTheDirectRegistration()
    {
        using LoaderHarness harness = new("builtins-stack");
        harness.Load([], NetworkInspectorBuiltIns.CreateAll());
        using SettingsManager viaPluginSettings = new();
        using SettingsManager directSettings = new();
        StackBuilder viaPlugins = new(viaPluginSettings, new FrameInterfaceRegistry());
        StackBuilder direct = new(directSettings, new FrameInterfaceRegistry());

        IReadOnlyList<PluginContributionFailure> failures = harness.Registry.ApplyStackContributions(viaPlugins);
        direct.RegisterStandardProtocols();
        Stack pluginStack = viaPlugins.Build();
        Stack directStack = direct.Build();

        await Assert.That(failures).IsEmpty();
        await Assert.That(_ProtocolNames(pluginStack)).IsEquivalentTo(_ProtocolNames(directStack), CollectionOrdering.Matching);
        await Assert.That(pluginStack.Fields.ToArray().Select(static f => f.Name).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(directStack.Fields.ToArray().Select(static f => f.Name).Order(StringComparer.Ordinal).ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(pluginStack.ProtocolTableInfos.ToArray().Select(static t => t.Name).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(directStack.ProtocolTableInfos.ToArray().Select(static t => t.Name).Order(StringComparer.Ordinal).ToArray(),
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConfigurationWarningsAreLoggedOnceTheProtocolsPluginIsStarted()
    {
        using LoaderHarness harness = new("builtins-warning");
        using LifecycleContexts contexts = new();
        PluginLoadReport report = harness.Load([], NetworkInspectorBuiltIns.CreateAll());
        PluginRecord record = report.Plugins.Single(static r => r.Name == "NetworkInspector.StandardProtocols");
        IPlugin plugin = Expect.NotNull(record.Instance);
        string missing = Path.Combine(harness.Folder.Path, "missing-signals.json");

        // The probe build at load time ran without a log: nothing was logged, and nothing is replayed at the start.
        await plugin.StartAsync(contexts.For(record), CancellationToken.None);
        await Assert.That(contexts.LogOf(record.Name)).IsEmpty();

        // A stack with a clean profile has nothing to report; a profile that points to a missing file makes the next stack
        // report it. Every stack gets a settings manager of its own, as every session does.
        using SettingsManager clean = new();
        harness.Registry.ApplyStackContributions(new StackBuilder(clean, new()));
        await Assert.That(contexts.LogOf(record.Name)).IsEmpty();
        using SettingsManager broken = new();
        broken.PreloadValue("signal_message.config_file", SettingValue.String(missing));
        harness.Registry.ApplyStackContributions(new StackBuilder(broken, new()));

        IReadOnlyList<LogEntry> entries = contexts.LogOf(record.Name);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Level).IsEqualTo("Warning");
        await Assert.That(entries[0].Text).Contains("signal_message.config_file");

        // After the stop a late stack build writes nothing into the log.
        await plugin.StopAsync(CancellationToken.None);
        using SettingsManager late = new();
        late.PreloadValue("signal_message.config_file", SettingValue.String(missing));
        harness.Registry.ApplyStackContributions(new StackBuilder(late, new()));
        await Assert.That(contexts.LogOf(record.Name).Count).IsEqualTo(1);
    }

    #endregion
}
