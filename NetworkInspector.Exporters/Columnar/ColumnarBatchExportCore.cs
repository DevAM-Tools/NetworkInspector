// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// The core of the exporters whose batch is a <see cref="ColumnarPacketBatch"/> (DuckDB, Parquet): packets enter the batch through
/// <see cref="ColumnarFeed"/>, every full batch may be handed over, and the observer of the flat shapes stages straight into the
/// open batch.
/// </summary>
/// <remarks>Thread safety: see <see cref="ColumnarExportCore{TBatch}"/>.</remarks>
/// <seealso cref="ColumnarExportCore{TBatch}"/>
/// <seealso cref="ColumnarFeed"/>
internal sealed class ColumnarBatchExportCore : ColumnarExportCore<ColumnarPacketBatch>
{
    #region Construction

    /// <summary>Creates the core of one export; no I/O happens.</summary>
    /// <param name="formatName">Format name for the error messages ("DuckDB", "Parquet").</param>
    /// <param name="content">What the export contains.</param>
    /// <param name="targetPacketCount">Stop after this many packets; 0 means unlimited.</param>
    /// <param name="pipeline">The pipeline of the export.</param>
    /// <param name="collector">The staging collector of the flat shapes, or <see langword="null"/> for the hierarchy shape.</param>
    /// <param name="raiseItemSkipped">Raises the exporter's <c>ItemSkipped</c> event; create the delegate once.</param>
    /// <param name="start">Opens the exporter's sink for the first packet; create the delegate once.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    internal ColumnarBatchExportCore(
        string formatName,
        ExportContent content,
        int targetPacketCount,
        BatchWriterPipeline<ColumnarPacketBatch> pipeline,
        ExportFieldCollector? collector,
        Action<ExportErrorEventArgs> raiseItemSkipped,
        Func<Packet, bool> start,
        CancellationToken cancellationToken)
        : base(
            formatName,
            content,
            targetPacketCount,
            pipeline,
            collector,
            raiseItemSkipped,
            start,
            checksCatalogStack: true,
            tracksFlushedBytes: true,
            cancellationToken: cancellationToken)
    {
    }

    #endregion

    #region Format hooks

    /// <inheritdoc/>
    protected override bool TryAddPacket(Packet packet, out bool shouldFlush, out ExportErrorKind kind, out string? message)
    {
        ColumnarPacketBatch batch = Pipeline.Current;
        if (!ColumnarFeed.TryAdd(batch, Collector, packet, out kind, out message))
        {
            shouldFlush = false;
            return false;
        }

        shouldFlush = batch.ShouldFlush;
        return true;
    }

    /// <inheritdoc/>
    protected override void AfterSubmit()
    {
        if (Collector is not null)
        {
            Collector.Stage = Pipeline.Current;
        }
    }

    #endregion
}
