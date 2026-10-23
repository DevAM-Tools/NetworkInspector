// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The application tier decides which plugins load. Stand-ins for the host and app contracts carry the real assembly and type
/// names, so the pre-scan recognizes their tiers; the loader runs the application's tier steps itself, after every
/// <c>Configure</c>.
/// </summary>
internal sealed class PluginTierTests
{
    #region Constants

    private static readonly string[] _HostStepLog = ["Fake.Host.ConfigureHost"];

    // Plugins load in path order (Fake.App.dll before Fake.Host.dll); all host steps run before the app step.
    private static readonly string[] _AppStepLog = ["Fake.App.ConfigureHost", "Fake.Host.ConfigureHost", "Fake.App.ConfigureApp"];

    private static readonly string[] _StepCalls =
    [
        "A.Configure", "B.Configure", "C.Configure", "D.Configure", "A.ConfigureHost", "B.ConfigureHost", "D.ConfigureHost",
    ];

    #endregion

    #region Helpers

    // A folder with the sample and both fake plugins (and no contract assemblies).
    private static string _Prepare(LoaderHarness harness, DummyContracts dummy)
    {
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);
        TestPluginFiles.CopyInto(dummy.FakeHostPath, folder);
        TestPluginFiles.CopyInto(dummy.FakeAppPath, folder);
        return folder;
    }

    private static string[] _LoadedFrom(string folder) =>
    [
        .. AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.IsDynamic ? string.Empty : assembly.Location)
            .Where(location => location.StartsWith(folder, StringComparison.OrdinalIgnoreCase)),
    ];

    #endregion

    #region Skipped and accepted

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task HostAndAppPluginsAreSkippedUnderTheCoreTierWithoutLoadingTheirAssemblies(DummyContracts dummy)
    {
        using LoaderHarness harness = new("tier-skip");
        string folder = _Prepare(harness, dummy);

        PluginLoadReport report = harness.Load([folder]);

        PluginRecord host = report.Plugins.Single(static record => record.Tier == PluginTier.Host);
        PluginRecord app = report.Plugins.Single(static record => record.Tier == PluginTier.App);
        await Assert.That(host.State).IsEqualTo(PluginLoadState.Skipped);
        await Assert.That(host.Reason).IsEqualTo("needs the host");
        await Assert.That(app.State).IsEqualTo(PluginLoadState.Skipped);
        await Assert.That(app.Reason).IsEqualTo("needs the app");
        await Assert.That(host.Name).IsEqualTo("FakeHostPlugin");
        await Assert.That(host.Version).IsEqualTo("0.0.0.0");
        await Assert.That(report.Plugins.Single(static record => record.Name == "Sample.Protocol").State).IsEqualTo(PluginLoadState.Loaded);

        // The skipped assemblies were never loaded: only the sample came from the folder.
        await Assert.That(_LoadedFrom(folder).Length).IsEqualTo(1);
    }

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task UnderTheHostTierOnlyTheAppPluginIsSkipped(DummyContracts dummy)
    {
        dummy.LoadIntoDefault();
        using LoaderHarness harness = new("tier-host");
        string folder = _Prepare(harness, dummy);
        List<string> steps = [];

        PluginLoadReport report = harness.Load([folder], tier: PluginTier.Host, steps: [dummy.HostStep(steps)]);

        await Assert.That(report.Plugins.Single(static record => record.Tier == PluginTier.Host).State).IsEqualTo(PluginLoadState.Loaded);
        PluginRecord app = report.Plugins.Single(static record => record.Tier == PluginTier.App);
        await Assert.That(app.State).IsEqualTo(PluginLoadState.Skipped);
        await Assert.That(app.Reason).IsEqualTo("needs the app");
        await Assert.That(steps).IsEquivalentTo(_HostStepLog, CollectionOrdering.Matching);
    }

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task UnderTheAppTierAllThreeLoadAndTheStepsRunForTheirTiersAndAbove(DummyContracts dummy)
    {
        dummy.LoadIntoDefault();
        using LoaderHarness harness = new("tier-app");
        string folder = _Prepare(harness, dummy);
        List<string> steps = [];

        PluginLoadReport report = harness.Load([folder], tier: PluginTier.App, steps: [dummy.HostStep(steps), dummy.AppStep(steps)]);

        await Assert.That(report.Plugins.Count).IsEqualTo(3);
        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Loaded)).IsTrue();
        await Assert.That(steps).IsEquivalentTo(_AppStepLog, CollectionOrdering.Matching);

        // One type identity: the contract the fake implements is the default-context one.
        Type hostInterface = Expect.NotNull(dummy.HostContract.GetType("NetworkInspector.Host.IHostPlugin"));
        Type fake = Expect.NotNull(report.Plugins.Single(static record => record.Name == "Fake.Host").Instance).GetType();
        await Assert.That(fake.GetInterfaces()).Contains(hostInterface);
    }

    #endregion

    #region Tier steps

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task EveryConfigureRunsBeforeTheFirstTierStepAndACoreOnlyPluginGetsNoStep(DummyContracts dummy)
    {
        using LoaderHarness harness = new("tier-steps");
        List<string> calls = [];
        IReadOnlyList<IPlugin> plugins = dummy.CreateLoggingPlugins(calls);

        PluginLoadReport report = harness.Load([], plugins, PluginTier.Host, [dummy.HostStep()]);

        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Loaded)).IsTrue();
        await Assert.That(calls).IsEquivalentTo(_StepCalls, CollectionOrdering.Matching);
    }

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task AThrowingTierStepFailsOnlyItsPluginAndPublishesNothingOfIt(DummyContracts dummy)
    {
        using LoaderHarness harness = new("tier-throw");
        List<string> calls = [];
        IReadOnlyList<IPlugin> plugins = dummy.CreateLoggingPlugins(calls, bThrows: true);

        PluginLoadReport report = harness.Load([], plugins, PluginTier.Host, [dummy.HostStep()]);

        PluginRecord b = report.Plugins.Single(static record => record.Name == "B");
        await Assert.That(b.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(b.Reason).IsEqualTo("ConfigureHost failed: InvalidOperationException: host boom");
        await Assert.That(harness.Registry.TryGetSource("SourceB", out _, out _)).IsFalse();
        await Assert.That(harness.Registry.TryGetSource("SourceA", out _, out _)).IsTrue();
        await Assert.That(report.Plugins.Single(static record => record.Name == "A").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins.Single(static record => record.Name == "D").State).IsEqualTo(PluginLoadState.Loaded);
    }

    #endregion
}
