// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that exports pre-parsed IPv6/UDP packets in columnar PBF format
/// into a <see cref="MemoryStream"/> on every <see cref="IProfilingScenario.Run"/> call.
///
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Pbf.PbfExporter.OnPacket"/> serialisation with
/// columnar layout and LZ4 compression.
/// Frame generation and parsing happen in <see cref="IProfilingScenario.Setup"/>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportColumnarPbfScenario : ExportScenarioBase<Packet>
{
    private const int _Batch = 10_000;

    /// <inheritdoc/>
    protected override int BatchSize => _Batch;

    /// <inheritdoc/>
    protected override int InitialStreamCapacityBytes => 8 * 1024 * 1024; // 8 MiB

    /// <inheritdoc/>
    protected override Packet[] CreateItems(Stack stack, Frame[] frames)
        => FrameHelper.ParseAndMaterialize(frames, stack);

    /// <inheritdoc/>
    public override string Name => "export-columnar-pbf";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Export {_Batch:N0} parsed IPv6/UDP packets as PBF (Columnar) → MemoryStream per iteration.");

    /// <inheritdoc/>
    public override string WorkUnitName => "packets";

    /// <inheritdoc/>
    protected override void Export(MemoryStream stream, Packet[] packets)
    {
        using Exporters.Pbf.PbfExporter exporter = Exporters.Pbf.PbfExporter
            .CreateBuilder()
            .ToStream(stream)
            .WithFormat(Exporters.Pbf.PbfExportFormat.Columnar)
            .WithCompressed(true)
            .Build();

        foreach (Packet packet in packets)
        {
            if (!exporter.OnPacket(packet))
            {
                break;
            }
        }

        exporter.OnFinish();
    }
}
