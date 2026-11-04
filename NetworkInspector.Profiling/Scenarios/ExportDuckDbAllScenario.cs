// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet to DuckDB in the all-fields shape.
/// <para>
/// <b>Why:</b> the flat shape with no selection. The parse is tree-free (an observer for all fields), so it shows what the
/// topology and the node bookkeeping of the hierarchy shape cost. The reference is the hierarchy scenario of the same format.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportDuckDbAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and export every value field to DuckDB (all-fields shape).");

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
            .WithContent(new ExportContent { Shape = ExportShape.AllFields })
            .Build();
}
