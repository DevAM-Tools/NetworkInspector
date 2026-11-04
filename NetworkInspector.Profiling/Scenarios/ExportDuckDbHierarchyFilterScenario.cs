// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The DuckDB hierarchy scenario behind a tree filter that keeps one packet in four (<see cref="SampleFrames.ExportMixFilter"/>).
/// <para>
/// <b>Why:</b> today a filter forces a field tree for every frame, and the exporter sees only the matching 25 %. This is the
/// reference for filter plus batching export that the tree-free filter of the rework is compared with. <see cref="ExportPipelineScenarioBase.Cleanup"/>
/// proves through the exporter's written count that exactly 25 000 packets reached the database.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportDuckDbHierarchyFilterScenario : ExportDuckDbHierarchyScenario
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-hierarchy-filter";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames, keep 25 % with \"{SampleFrames.ExportMixFilter}\", export them with hierarchy to DuckDB.");

    /// <inheritdoc/>
    protected override string? FilterExpression => SampleFrames.ExportMixFilter;
}
