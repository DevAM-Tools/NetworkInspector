// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that exports pre-parsed IPv6/UDP packets as JSON (Compact)
/// into a <see cref="MemoryStream"/> on every <see cref="IProfilingScenario.Run"/> call.
///
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Json.JsonExporter.OnPacket"/> serialisation,
/// including field enumeration and SIMD escape scanning.
/// Frame generation and parsing happen in <see cref="IProfilingScenario.Setup"/>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportJsonScenario : ExportScenarioBase<Packet>
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
    public override string Name => "export-json";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Export {_Batch:N0} parsed IPv6/UDP packets as JSON (Compact) → MemoryStream per iteration.");

    /// <inheritdoc/>
    public override string WorkUnitName => "packets";

    /// <inheritdoc/>
    protected override void Export(MemoryStream stream, Packet[] packets)
    {
        using Exporters.Json.JsonExporter exporter = Exporters.Json.JsonExporter
            .CreateBuilder()
            .ToStream(stream)
            .WithFormat(Exporters.Json.JsonExportFormat.Compact)
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
