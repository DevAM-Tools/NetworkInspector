// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Argument validation of the loader: the only things <c>Load</c> throws for, next to cancellation.</summary>
internal sealed class PluginLoaderTests
{
    #region Helpers

    private static PluginLoader _Loader(LoaderHarness harness) => new(new PluginLoaderOptions(harness.CacheFile, []));

    #endregion

    #region Constructor

    [Test]
    public async Task ANullOptionsObjectIsRefused()
    {
        await Assert.That(() => new PluginLoader(null!)).Throws<ArgumentNullException>(); // deliberate null: proves the guard
    }

    [Test]
    public async Task AnEmptyCacheFileOrANullBuiltInIsRefused()
    {
        await Assert.That(() => new PluginLoader(new PluginLoaderOptions(string.Empty, []))).Throws<ArgumentException>();
        await Assert.That(() => new PluginLoader(new PluginLoaderOptions("cache.json", [null!]))).Throws<ArgumentException>(); // deliberate null entry
    }

    #endregion

    #region Load

    [Test]
    public async Task AnUndefinedTierIsRefused()
    {
        using LoaderHarness harness = new("loader-tier");
        PluginLoader loader = _Loader(harness);

        await Assert.That(() => loader.Load((PluginTier)0, [], harness.Registry, CancellationToken.None)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => loader.Load((PluginTier)4, [], harness.Registry, CancellationToken.None)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task NullAndBlankPathsAndANullFactoryAreRefused()
    {
        using LoaderHarness harness = new("loader-paths");
        PluginLoader loader = _Loader(harness);

        await Assert.That(() => loader.Load(PluginTier.Core, null!, harness.Registry, CancellationToken.None)).Throws<ArgumentNullException>();
        await Assert.That(() => loader.Load(PluginTier.Core, [null!], harness.Registry, CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(() => loader.Load(PluginTier.Core, [" "], harness.Registry, CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(() => loader.Load(PluginTier.Core, [], null!, CancellationToken.None)).Throws<ArgumentNullException>();
    }

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task TierStepsThatDoNotMatchTheApplicationTierAreRefusedBeforeAnythingLoads(DummyContracts dummy)
    {
        using LoaderHarness harness = new("loader-steps");
        List<string> calls = [];
        PluginLoader loader = new(new PluginLoaderOptions(harness.CacheFile, [new LifePlugin("A", calls)]));
        PluginTierStep host = dummy.HostStep();
        PluginTierStep app = dummy.AppStep();

        // The harness builds a factory with the given steps; the cases cover missing, extra, wrong order, and doubled steps.
        await Assert.That(() => harness.Load([], null, PluginTier.Host, [])).Throws<ArgumentException>();
        await Assert.That(() => harness.Load([], null, PluginTier.Core, [host])).Throws<ArgumentException>();
        await Assert.That(() => harness.Load([], null, PluginTier.App, [app, host])).Throws<ArgumentException>();
        await Assert.That(() => harness.Load([], null, PluginTier.App, [host, host])).Throws<ArgumentException>();
        await Assert.That(calls).IsEmpty();
        await Assert.That(loader).IsNotNull();
    }

    [Test]
    public async Task ACancelledTokenThrowsBeforeAnythingIsLoadedOrWritten()
    {
        using LoaderHarness harness = new("loader-cancel");
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);
        PluginLoader loader = _Loader(harness);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.That(() => loader.Load(PluginTier.Core, [folder], harness.Registry, cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(harness.CacheFile)).IsFalse();
    }

    #endregion
}
