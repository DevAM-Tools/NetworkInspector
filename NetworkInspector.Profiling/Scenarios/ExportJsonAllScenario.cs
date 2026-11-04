// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet as JSON in the all-fields shape (a flat array of
/// named occurrences). The parse is tree-free (an observer for all fields). The reference is <c>export-json-hierarchy</c>.
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportJsonAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-json-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write every value field as JSON (compact style, all-fields shape).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Json.JsonExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false })
            .Build();
}
