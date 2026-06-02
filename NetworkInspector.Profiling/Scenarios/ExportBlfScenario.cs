// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that exports pre-generated IPv6/UDP frames as BLF
/// into a <see cref="MemoryStream"/> on every <see cref="IProfilingScenario.Run"/> call.
///
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Blf.BlfExporter.OnFrame"/> serialisation and compression.
/// Frame generation happens in <see cref="IProfilingScenario.Setup"/>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportBlfScenario : ExportScenarioBase<Frame>
{
    private const int Batch = 10_000;

    /// <inheritdoc/>
    protected override int BatchSize => Batch;

    /// <inheritdoc/>
    protected override int InitialStreamCapacityBytes => 4 * 1024 * 1024; // 4 MiB

    /// <inheritdoc/>
    protected override Frame[] CreateItems(Stack stack, Frame[] frames) => frames;

    /// <inheritdoc/>
    public override string Name => "export-blf";

    /// <inheritdoc/>
    public override string Description =>
        $"Export {Batch:N0} IPv6/UDP frames as BLF → MemoryStream per iteration.";

    /// <inheritdoc/>
    public override string WorkUnitName => "frames";

    /// <inheritdoc/>
    protected override void Export(MemoryStream stream, Frame[] frames)
    {
        using Exporters.Blf.BlfExporter exporter = Exporters.Blf.BlfExporter
            .CreateBuilder()
            .ToStream(stream)
            .Build();

        foreach (Frame frame in frames)
        {
            if (!exporter.OnFrame(frame))
            {
                break;
            }
        }

        exporter.OnFinish();
    }
}
