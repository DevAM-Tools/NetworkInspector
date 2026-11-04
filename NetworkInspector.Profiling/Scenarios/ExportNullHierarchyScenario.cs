// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree and hands each packet to an exporter that does nothing — the parse-cost reference.
/// <para>
/// <b>Why it exists:</b> every other scenario of this family is parse plus export. Subtracting this scenario from them shows
/// what the exporter itself costs, and it is the number the tree-free parse of the exporter rework has to beat. Hot path:
/// <see cref="Packet.TryParse(Packet, PacketId, Stack, Frame, in ParseOptions, out ParseFailure)"/> in build mode.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportNullHierarchyScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-null-hierarchy";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames with a field tree and discard them (parse cost reference).");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) => new NullListener();

    /// <summary>A packet listener that accepts every packet and writes nothing.</summary>
    private sealed class NullListener : IPacketListener
    {
        /// <inheritdoc/>
        public string UiName => "null";

        /// <inheritdoc/>
        public string? Description => null;

        /// <inheritdoc/>
        public bool OnPacket(Packet packet) => true;

        /// <inheritdoc/>
        public void OnFinish()
        {
        }
    }
}
