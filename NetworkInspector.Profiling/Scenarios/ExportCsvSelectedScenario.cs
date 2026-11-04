// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every packet as wide CSV rows.
/// <para>
/// <b>Why:</b> the CSV headline of the rework. The exporter demands a tree-free parse with an observer for two fields, so no
/// field tree is built or walked. The reference is <c>export-csv-columns</c>, which reads its fields from a built tree.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportCsvSelectedScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-csv-selected";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write udp.srcport and udp.dstport as wide CSV rows.");

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
            .WithContent(Exporters.Csv.CsvExporter.ContentTemplate with { Shape = ExportShape.SelectedFields, Fields = CreateSelectedContent(stack).Fields })
            .Build();
}
