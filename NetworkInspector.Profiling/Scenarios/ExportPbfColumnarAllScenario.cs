// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet to compressed PBF (Columnar) in the all-fields shape.
/// <para>
/// <b>Why:</b> the flat shape with no selection. The parse is tree-free (an observer for all fields), so it shows what the
/// topology and the node bookkeeping of the hierarchy shape cost. The reference is the hierarchy scenario of the same format.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPbfColumnarAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pbfcolumnar-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write every value field as compressed PBF (Columnar, all-fields shape).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Pbf.PbfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithFormat(Exporters.Pbf.PbfExportFormat.Columnar)
            .WithCompressed(true)
            .WithContent(new ExportContent { Shape = ExportShape.AllFields })
            .Build();
}
