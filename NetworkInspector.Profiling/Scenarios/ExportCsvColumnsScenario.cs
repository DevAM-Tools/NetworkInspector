// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree and writes one CSV row per packet with the default columns plus three field
/// columns (<c>udp.srcport</c>, <c>udp.dstport</c>, <c>can.id</c>) — today's only way to export a few fields to CSV.
/// <para>
/// Hot path: the build-mode parse and the field-column lookup of <see cref="Exporters.Csv.CsvExporter"/> in the built tree.
/// The scenario keeps its name for the whole rework: Step 14 rewrites it to the equivalent selected-fields content (the same
/// three fields) so the final A/B run pairs it with this baseline.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal class ExportCsvColumnsScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-csv-columns";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write CSV with the default and three selected field columns.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Csv.CsvExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithDefaultColumns()
            .WithContent(CreateCsvContent(stack))
            .Build();

    /// <summary>
    /// The three fields of the baseline scenario as a selected-fields content with the first occurrence of each (the former field
    /// columns wrote the first occurrence only, so this is the like-for-like content) without text columns (one column per field, like
    /// the field columns of the previous version). Shared with the filtered variant.
    /// </summary>
    /// <param name="stack">The stack that resolves the names.</param>
    /// <returns>The content.</returns>
    protected static ExportContent CreateCsvContent(Stack stack)
    {
        FieldRequest[] requests =
        [
            new FieldRequest("udp.srcport", Occurrences.First),
            new FieldRequest("udp.dstport", Occurrences.First),
            new FieldRequest("can.id", Occurrences.First),
        ];
        if (!FieldSelection.TryCreate(stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(Invariant($"The scenario selection was refused: {error.Message}"));
        }

        return Exporters.Csv.CsvExporter.ContentTemplate with
        {
            Shape = ExportShape.SelectedFields,
            Fields = selection,
            CustomText = false,
            CustomRepresentation = false,
        };
    }

    /// <inheritdoc/>
    protected override int? GetWrittenCount(IPacketListener exporter) => ((Exporters.Csv.CsvExporter)exporter).WrittenCount;
}
