// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Created by the application for its tier. <see cref="Create"/> returns the most derived builder of the application
/// (<c>IPluginBuilder</c>, <c>IHostPluginBuilder</c>, <c>IAppPluginBuilder</c>); <see cref="TierSteps"/> lists the tier methods
/// above <see cref="IPlugin.Configure"/> in ascending tier order, which the loader calls itself: none for the CLI,
/// <c>ConfigureHost</c> for the host, <c>ConfigureHost</c> and <c>ConfigureApp</c> for the app. The loader cannot reference
/// the higher contracts, so the application hands their methods over as data.
/// <para>Thread safety: called on the loading thread only.</para>
/// </summary>
public interface IPluginBuilderFactory
{
    /// <summary>
    /// Creates the staging builder of one plugin. Contributions must not become visible to readers before the recorder's
    /// commit actions run: register the commit with <see cref="ContributionRecorder.OnCommit"/>.
    /// </summary>
    /// <param name="plugin">Identity of the plugin that is configured.</param>
    /// <param name="recorder">Collects the named contributions of the plugin and its commit actions.</param>
    /// <returns>The builder the loader hands to <see cref="IPlugin.Configure"/> and to the tier steps.</returns>
    IPluginBuilder Create(PluginRecordDraft plugin, ContributionRecorder recorder);

    /// <summary>
    /// The tier methods above <c>Configure</c>, in ascending tier order, one per tier up to the application tier. The loader
    /// checks the list on every <see cref="PluginLoader.Load"/> (count and tiers) and rejects a mismatch with an
    /// <see cref="ArgumentException"/>, because a missing step would silently skip a whole tier of every plugin.
    /// </summary>
    IReadOnlyList<PluginTierStep> TierSteps { get; }
}
