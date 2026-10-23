// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// One tier method of the plugin contracts. The loader runs it for every plugin whose tier is at least <see cref="Tier"/>,
/// after every plugin's <c>Configure</c>.
/// </summary>
/// <param name="Tier">The first tier that has this method (Host for <c>ConfigureHost</c>, App for <c>ConfigureApp</c>).</param>
/// <param name="MethodName">Name of the method; it appears in failure reasons such as <c>ConfigureHost failed: …</c>.</param>
/// <param name="Configure">
/// Casts plugin and builder to the tier's interfaces and calls the method. The contract assembly of that tier owns the step,
/// so the loader needs no reference to the higher contracts.
/// </param>
public sealed record PluginTierStep(PluginTier Tier, string MethodName, Action<IPlugin, IPluginBuilder> Configure);
