// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The DuckDB selected-fields scenario behind a tree-free filter that keeps one packet in four
/// (<see cref="SampleFrames.ExportMixFilter"/>).
/// <para>
/// <b>Why:</b> the filter runs as an observer inside the same tree-free parse as the exporter's observer, so neither builds a
/// field tree. The reference is <c>export-duckdb-hierarchy-filter</c>, where the filter forces a tree for every frame.
/// <see cref="ExportPipelineScenarioBase.Cleanup"/> proves through the exporter's written count that exactly 25 000 packets
/// reached the database.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportDuckDbSelectedFilterScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-selected-filter";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan, keep 25 % with \"{SampleFrames.ExportMixFilter}\" and export udp.srcport and udp.dstport to DuckDB.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override string? FilterExpression => SampleFrames.ExportMixFilter;

    /// <inheritdoc/>
    protected override bool WritesFiles => true;

    /// <inheritdoc/>
    protected override int? GetWrittenCount(IPacketListener exporter) => ((Exporters.DuckDb.DuckDbExporter)exporter).WrittenCount;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.DuckDb.DuckDbExporter.CreateBuilder()
            .ToFile(Path.Combine(OutputDirectory, "out.duckdb"))
            .WithContent(CreateSelectedContent(stack))
            .Build();
}
