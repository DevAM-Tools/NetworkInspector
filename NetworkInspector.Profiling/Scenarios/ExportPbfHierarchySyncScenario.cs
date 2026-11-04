// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The compressed PBF (Standard) hierarchy scenario in <see cref="ExportWriteMode.Synchronous"/>: the caller's thread serializes,
/// compresses and writes each full block itself.
/// <para>
/// <b>Why:</b> control for the background default (<c>export-pbf-hierarchy</c>): the same workload, one open block, no
/// overlap. The writer thread must never make an export slower than this.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPbfHierarchySyncScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pbf-hierarchy-sync";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write them as compressed PBF (Standard) with the full field tree, written synchronously.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Pbf.PbfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithFormat(Exporters.Pbf.PbfExportFormat.Standard)
            .WithCompressed(true)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
}
