// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix and writes every packet with its full field tree into a DuckDB file — today's only DuckDB shape.
/// <para>
/// <b>Hot path:</b> <c>Packet.TryParse</c> (build mode) and <c>ColumnarPacketBatch.AddPacket</c> on the caller thread; the
/// exporter's writer thread appends the finished batches meanwhile. File I/O is part of the measurement.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal class ExportDuckDbHierarchyScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-duckdb-hierarchy";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and export them with hierarchy to DuckDB.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool WritesFiles => true;

    /// <inheritdoc/>
    protected override int? GetWrittenCount(IPacketListener exporter) => ((Exporters.DuckDb.DuckDbExporter)exporter).WrittenCount;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.DuckDb.DuckDbExporter.CreateBuilder()
            .ToFile(Path.Combine(OutputDirectory, "out.duckdb"))
            .Build();
}
