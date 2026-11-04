// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Hands the export-mix frames to the PCAPNG exporter without parsing — the frame-exporter reference (no parse work at all).
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Pcapng.PcapngExporter"/> block writing into the reused stream.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportPcapngFramesScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-pcapng-frames";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Hand {FrameCount:N0} export-mix frames to the PCAPNG exporter without parsing them.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => false;

    /// <inheritdoc/>
    protected override IPacketListener? CreatePacketExporter(Stack stack) => null;

    /// <inheritdoc/>
    protected override IFrameListener CreateFrameExporter(Stack stack) =>
        Exporters.Pcapng.PcapngExporter.CreateBuilder()
            .ToStream(Stream!) // Invariant: stream formats create the stream in Setup.
            .Build();
}
