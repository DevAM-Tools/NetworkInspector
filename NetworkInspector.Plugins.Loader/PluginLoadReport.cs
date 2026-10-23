// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>Result of one load: the pre-scan summary and one record per plugin, in load order.</summary>
/// <param name="Prescan">Counters and time of the pre-scan.</param>
/// <param name="Plugins">Built-in plugins first, then configured paths that were not found, then plugin assemblies in path order.</param>
public sealed record PluginLoadReport(PrescanSummary Prescan, IReadOnlyList<PluginRecord> Plugins);
