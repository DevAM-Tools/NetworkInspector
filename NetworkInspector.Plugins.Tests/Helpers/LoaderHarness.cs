// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// One loader run in a private folder: its own scan cache file, a new registry per load, and the folder for plugin copies.
/// Each test owns one harness, so tests share nothing. Not thread-safe; a test uses it from one thread.
/// </summary>
internal sealed class LoaderHarness : IDisposable
{
    #region Types

    // Wraps the registry and replaces its (empty) tier steps, so a test can act as a host or app application.
    private sealed class SteppedFactory(PluginRegistry registry, IReadOnlyList<PluginTierStep> steps) : IPluginBuilderFactory
    {
        public IPluginBuilder Create(PluginRecordDraft plugin, ContributionRecorder recorder) => registry.Create(plugin, recorder);

        public IReadOnlyList<PluginTierStep> TierSteps { get; } = steps;
    }

    #endregion

    #region Lifecycle

    /// <summary>Creates the harness and its folder.</summary>
    /// <param name="topic">Short topic used in the folder name.</param>
    internal LoaderHarness(string topic)
    {
        Folder = new TempFolder(topic);
        Registry = new();
    }

    /// <summary>Disposes the registry and deletes the folder.</summary>
    public void Dispose()
    {
        Registry.Dispose();
        Folder.Dispose();
    }

    #endregion

    #region Public API

    /// <summary>The private folder of this harness.</summary>
    internal TempFolder Folder { get; }

    /// <summary>The scan cache file of this harness.</summary>
    internal string CacheFile => Folder.FilePath("scan-cache.json");

    /// <summary>The registry of the last <see cref="Load"/>.</summary>
    internal PluginRegistry Registry { get; private set; }

    /// <summary>Loads with a new registry (the old one is disposed) and the same cache file.</summary>
    /// <param name="paths">Configured paths.</param>
    /// <param name="builtIns">Built-in plugins; none by default.</param>
    /// <param name="tier">Application tier.</param>
    /// <param name="steps">Tier steps for hosts and apps; none for the core tier.</param>
    /// <returns>The load report.</returns>
    internal PluginLoadReport Load(IReadOnlyList<string> paths, IReadOnlyList<IPlugin>? builtIns = null, PluginTier tier = PluginTier.Core,
        IReadOnlyList<PluginTierStep>? steps = null)
    {
        Registry.Dispose();
        Registry = new();
        IPluginBuilderFactory factory = steps is null
            ? Registry
            : new SteppedFactory(Registry, steps);
        PluginLoader loader = new(new PluginLoaderOptions(CacheFile, builtIns ?? []));
        return loader.Load(tier, paths, factory, CancellationToken.None);
    }

    #endregion
}
