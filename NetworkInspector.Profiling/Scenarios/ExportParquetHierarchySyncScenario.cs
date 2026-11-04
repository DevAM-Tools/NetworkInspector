// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The Parquet hierarchy scenario in <see cref="ExportWriteMode.Synchronous"/>: the caller's thread encodes and writes each row
/// group itself.
/// <para>
/// <b>Why:</b> control for the background default (<c>export-parquet-hierarchy</c>): the same workload, one batch in memory, no
/// overlap. The writer thread must never make an export slower than this.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportParquetHierarchySyncScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-parquet-hierarchy-sync";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write them with the full field tree as a Parquet dataset, written synchronously.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool WritesFiles => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Parquet.ParquetExporter.CreateBuilder()
            .ToDirectory(OutputDirectory)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
}
