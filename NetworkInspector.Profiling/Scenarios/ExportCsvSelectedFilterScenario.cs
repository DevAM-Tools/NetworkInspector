// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The CSV selected-fields scenario behind a tree-free filter that keeps one packet in four
/// (<see cref="SampleFrames.ExportMixFilter"/>). The reference is <c>export-csv-columns-filter</c>, where the filter forces a
/// field tree for every frame. <see cref="ExportPipelineScenarioBase.Cleanup"/> proves through the exporter's written count that
/// exactly 25 000 packets were written.
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportCsvSelectedFilterScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-csv-selected-filter";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan, keep 25 % with \"{SampleFrames.ExportMixFilter}\" and write udp.srcport and udp.dstport as wide CSV rows.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override string? FilterExpression => SampleFrames.ExportMixFilter;

    /// <inheritdoc/>
    protected override int? GetWrittenCount(IPacketListener exporter) => ((Exporters.Csv.CsvExporter)exporter).WrittenCount;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Csv.CsvExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(Exporters.Csv.CsvExporter.ContentTemplate with { Shape = ExportShape.SelectedFields, Fields = CreateSelectedContent(stack).Fields })
            .Build();
}
