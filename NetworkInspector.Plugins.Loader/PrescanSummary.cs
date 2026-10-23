// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>What the metadata pre-scan did; the numbers show what an unchanged start costs.</summary>
/// <param name="CandidateFiles">Files found below the configured paths (each file counted once).</param>
/// <param name="InspectedFiles">Candidates whose metadata was read now (cache misses).</param>
/// <param name="CachedFiles">Candidates answered from the scan cache.</param>
/// <param name="PluginAssemblies">Candidates that hold at least one plugin entry type.</param>
/// <param name="InspectionTime">Wall-clock time of the whole pre-scan.</param>
public sealed record PrescanSummary(int CandidateFiles, int InspectedFiles, int CachedFiles, int PluginAssemblies,
    TimeSpan InspectionTime);
