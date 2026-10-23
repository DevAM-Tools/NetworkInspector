// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>Result of one pre-scan: the summary, the plugin assemblies (ordered by path), and configured paths that do not exist.</summary>
/// <param name="Summary">Counters and time.</param>
/// <param name="PluginAssemblies">Candidates with at least one entry type, ordered by path (ordinal).</param>
/// <param name="MissingPaths">Full paths of configured entries that are neither a folder nor a file.</param>
internal sealed record PrescanOutcome(PrescanSummary Summary, IReadOnlyList<PrescanResult> PluginAssemblies,
    IReadOnlyList<string> MissingPaths);
