// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every packet as compressed PBF (Standard).
/// <para>
/// <b>Why:</b> the row-oriented counterpart of the selected-fields scenarios. The exporter demands a tree-free parse with an
/// observer for two fields, so no field tree is built or walked. The reference is <c>export-pbf-hierarchy</c>, frozen as the baseline.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPbfSelectedScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pbf-selected";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write udp.srcport and udp.dstport as compressed PBF (Standard).");

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
            .WithContent(CreateSelectedContent(stack) with { FrameBytes = false })
            .Build();
}
