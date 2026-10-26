// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// What the application knows about one plugin after loading (and after starting). Immutable: a later step (the start) does
/// not change a record but replaces it by a copy with another state. <see cref="Instance"/> is the live plugin object, whose
/// own thread safety is the plugin's business.
/// </summary>
/// <param name="Name">Plugin name; for plugins that failed before their info could be read, the entry type name or the configured path.</param>
/// <param name="Version">Plugin version text; for skipped or failed entry types the assembly version from metadata.</param>
/// <param name="Tier">Tier of the entry type.</param>
/// <param name="State">Loaded, Failed, or Skipped.</param>
/// <param name="Reason">Why the plugin is Failed or Skipped; <see langword="null"/> when Loaded.</param>
/// <param name="Path">Assembly path, or the configured path for a missing path; the application assembly for built-ins.</param>
/// <param name="BuiltIn"><see langword="true"/> for plugins passed in <see cref="PluginLoaderOptions.BuiltInPlugins"/>.</param>
/// <param name="License">License text of the plugin info; empty when none was read.</param>
/// <param name="LoadTime">
/// Time the loader spent on this plugin: loading its assembly (booked on the first entry type of the assembly), creating it,
/// reading the info, configuring, running the tier steps, and committing. It does not include the start.
/// </param>
/// <param name="Dependencies">Private dependencies declared in the plugin info; empty when the info was not read.</param>
/// <param name="Contributions">
/// Named contributions by kind; empty for a plugin that failed or was skipped while loading. A plugin that fails later, in
/// its start, keeps the entries it had (the registry refuses them).
/// </param>
/// <param name="Instance">The plugin object; <see langword="null"/> unless the plugin is Loaded (a failed start clears it).</param>
public sealed record PluginRecord(string Name, string Version, PluginTier Tier, PluginLoadState State, string? Reason,
    string Path, bool BuiltIn, string License, TimeSpan LoadTime, IReadOnlyList<PluginDependency> Dependencies,
    IReadOnlyDictionary<ContributionKind, IReadOnlyList<ContributionEntry>> Contributions, IPlugin? Instance);
