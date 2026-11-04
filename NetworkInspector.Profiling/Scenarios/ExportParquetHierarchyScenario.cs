// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parses the export mix with a field tree and writes a Parquet dataset (one directory) with the full hierarchy — today's only Parquet shape.
/// <para>
/// <b>Hot path:</b> the build-mode parse, <c>ColumnarPacketBatch.AddPacket</c>, and the row-group writes of the Parquet sink on
/// the caller thread today. File I/O is part of the measurement.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportParquetHierarchyScenario : ExportPipelineScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "export-parquet-hierarchy";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames and write them with the full field tree as a Parquet dataset.");

    /// <inheritdoc/>
    protected override bool ParsesFrames => true;

    /// <inheritdoc/>
    protected override bool WritesFiles => true;

    /// <inheritdoc/>
    protected override IPacketListener CreatePacketExporter(Stack stack) =>
        Exporters.Parquet.ParquetExporter.CreateBuilder()
            .ToDirectory(OutputDirectory)
            .Build();
}
