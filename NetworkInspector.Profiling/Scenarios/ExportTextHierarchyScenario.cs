// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree and writes every packet as text (default detail level) — today's only text shape.
/// <para>
/// <b>Hot path:</b> the build-mode parse and the tree walk of <see cref="Exporters.Text.TextExporter"/>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportTextHierarchyScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-text-hierarchy";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write them as text with the full field tree.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Text.TextExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .Build();
}
