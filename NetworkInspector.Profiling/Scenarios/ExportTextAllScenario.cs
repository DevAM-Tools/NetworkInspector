// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix by plan and writes every value field of every packet as text lines in the all-fields shape. The parse
/// is tree-free (an observer for all fields). The reference is <c>export-text-hierarchy</c>.
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportTextAllScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-text-all";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames by plan and write every value field as text lines (all-fields shape).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool UsePlan => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Text.TextExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithContent(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false })
            .Build();
}
