// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The BLF frames scenario in <see cref="ExportWriteMode.Synchronous"/>: the caller's thread compresses and writes each full
/// container itself.
/// <para>
/// <b>Why:</b> control for the background default (<c>export-blf-frames</c>): the same workload, one open container, no overlap.
/// The writer thread must never make an export slower than this.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportBlfFramesSyncScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-blf-frames-sync";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Hand {FrameCount:N0} export-mix frames to the BLF exporter without parsing them, written synchronously.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => false;

    /// <inheritdoc/>
    protected override IPacketListener? CreatePacketExporter(Stack stack) => null;

    /// <inheritdoc/>
    protected override IFrameListener CreateFrameExporter(Stack stack) =>
        Exporters.Blf.BlfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
}
