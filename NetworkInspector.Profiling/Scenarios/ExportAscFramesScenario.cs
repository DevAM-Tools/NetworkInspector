// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Hands the export-mix frames to the ASC exporter without parsing — the frame-exporter reference (no parse work at all).
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Asc.AscExporter"/> line formatting into the reused stream.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportAscFramesScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-asc-frames";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Hand {FrameCount:N0} export-mix frames to the ASC exporter without parsing them.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => false;

    /// <inheritdoc/>
    protected override IPacketListener? CreatePacketExporter(Stack stack) => null;

    /// <inheritdoc/>
    protected override IFrameListener CreateFrameExporter(Stack stack) =>
        Exporters.Asc.AscExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .Build();
}
