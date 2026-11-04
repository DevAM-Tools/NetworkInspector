// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes only <c>udp.srcport</c> and <c>udp.dstport</c> of every packet as JSON objects.
/// <para>
/// <b>Why:</b> the exporter demands a tree-free parse with an observer for two fields, so no field tree is built or walked. The
/// reference is <c>export-json-hierarchy</c>, frozen as the baseline.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportJsonSelectedScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-json-selected";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write udp.srcport and udp.dstport as JSON (compact style).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Json.JsonExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(CreateSelectedContent(stack) with { FrameBytes = false })
            .Build();
}
