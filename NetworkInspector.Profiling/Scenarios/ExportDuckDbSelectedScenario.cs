// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every packet to DuckDB.
/// <para>
/// <b>Why:</b> the headline of the rework. The exporter demands a tree-free parse with an observer for two fields, so no field
/// tree is built or walked. The reference is the hierarchy scenario of the same format, frozen as the baseline.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportDuckDbSelectedScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-selected";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and export udp.srcport and udp.dstport to DuckDB.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

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
