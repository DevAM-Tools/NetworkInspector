// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Hands the export-mix frames to the BLF exporter without parsing — the frame exporter whose container compression the
/// rework moves to a writer thread.
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Blf.BlfExporter"/> object encoding, container fill, and compression on the caller thread today.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportBlfFramesScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-blf-frames";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Hand {FrameCount:N0} export-mix frames to the BLF exporter without parsing them.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => false;

    /// <inheritdoc/>
    protected override IPacketListener? CreatePacketExporter(Stack stack) => null;

    /// <inheritdoc/>
    protected override IFrameListener CreateFrameExporter(Stack stack) =>
        Exporters.Blf.BlfExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .Build();
}
