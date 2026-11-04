// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree (build mode, no plan) and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every
/// packet as JSON objects.
/// <para>
/// <b>Why:</b> a packet that carries a field tree reaches the exporter with the selection still in force: the collector reads the
/// selected fields straight from the tree instead of walking all of it. The first final measurement of the rework showed that this
/// path can cost five times more than intended when it walks the tree (the CSV columns), and only the CSV had a scenario for it.
/// Compare with <c>export-json-selected</c> (no tree) and <c>export-json-hierarchy</c> (the whole tree is written).
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportJsonSelectedTreeScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-json-selected-tree";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames with a field tree and write udp.srcport and udp.dstport as JSON (compact style).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Json.JsonExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(CreateSelectedContent(stack) with { FrameBytes = false })
            .Build();
}
