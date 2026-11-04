// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet as compressed PBF (Standard) in the all-fields shape.
/// <para>
/// <b>Why:</b> the flat shape with no selection: a flat list of field records per packet instead of nested records. The parse is
/// tree-free (an observer for all fields). The reference is <c>export-pbf-hierarchy</c>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPbfAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pbf-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write every value field as compressed PBF (Standard, all-fields shape).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Pbf.PbfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithFormat(Exporters.Pbf.PbfExportFormat.Standard)
            .WithCompressed(true)
            .WithContent(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false })
            .Build();
}
