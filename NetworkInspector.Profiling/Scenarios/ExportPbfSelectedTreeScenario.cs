// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree (build mode, no plan) and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every
/// packet as compressed PBF (Standard).
/// <para>
/// <b>Why:</b> the tree-packet counterpart of <c>export-pbf-selected</c>: the selected fields are read straight from the tree
/// (see <c>export-json-selected-tree</c> for the reason). Compare with <c>export-pbf-hierarchy</c>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPbfSelectedTreeScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pbf-selected-tree";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames with a field tree and write udp.srcport and udp.dstport as compressed PBF (Standard).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Pbf.PbfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithFormat(Exporters.Pbf.PbfExportFormat.Standard)
            .WithCompressed(true)
            .WithContent(CreateSelectedContent(stack) with { FrameBytes = false })
            .Build();
}
