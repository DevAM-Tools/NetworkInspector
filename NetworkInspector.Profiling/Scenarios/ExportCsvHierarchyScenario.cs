// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree and writes every node as a CSV row with its parent and path (hierarchy shape).
/// <para>
/// <b>Why:</b> the tree layout needs the tree, so it demands one; this is the reference for what a tree costs in CSV.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportCsvHierarchyScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-csv-hierarchy";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write the field tree as CSV rows (one row per node, with parent and path).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override int? GetWrittenCount(IPacketListener exporter) => ((Exporters.Csv.CsvExporter)exporter).WrittenCount;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Csv.CsvExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(Exporters.Csv.CsvExporter.ContentTemplate with { Shape = ExportShape.Hierarchy })
            .Build();
}
