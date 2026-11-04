// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet as long CSV rows (all-fields shape).
/// <para>
/// <b>Why:</b> the lossless flat layout. The parse is tree-free (an observer for all fields); the cost is the number of rows,
/// one per field occurrence, which is what a user of this layout accepts.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportCsvAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-csv-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write every value field as long CSV rows (one row per field occurrence).");

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
            .WithContent(Exporters.Csv.CsvExporter.ContentTemplate)
            .Build();
}
