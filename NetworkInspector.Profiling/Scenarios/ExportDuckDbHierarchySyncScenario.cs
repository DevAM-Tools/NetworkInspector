// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The DuckDB hierarchy scenario in <see cref="ExportWriteMode.Synchronous"/>: the caller's thread writes each full batch itself.
/// <para>
/// <b>Why:</b> the writer thread must never make an export slower. This scenario is the control for the background default
/// (<c>export-duckdb-hierarchy</c>): the same workload, one batch in memory, no overlap. A background result below this one
/// would mean that the second thread costs more than it overlaps.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportDuckDbHierarchySyncScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-hierarchy-sync";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and export them with hierarchy to DuckDB, written synchronously.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool WritesFiles => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.DuckDb.DuckDbExporter.CreateBuilder()
            .ToFile(Path.Combine(OutputDirectory, "out.duckdb"))
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
}
