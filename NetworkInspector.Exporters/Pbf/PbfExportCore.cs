// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// The core of <see cref="PbfExporter"/>: the intake and accounting of <see cref="ColumnarExportCore{TBatch}"/> over
/// <see cref="PbfBlock"/>s, plus what only PBF decides: how a packet enters the open block in each of the three feeds (columnar
/// flat shapes, standard flat shapes, field tree), and the strict nesting check of the standard layout before a block is handed
/// over.
/// </summary>
/// <remarks>Thread safety: see <see cref="ColumnarExportCore{TBatch}"/>.</remarks>
/// <seealso cref="ColumnarExportCore{TBatch}"/>
/// <seealso cref="ColumnarFeed"/>
/// <seealso cref="RowFeed"/>
internal sealed class PbfExportCore : ColumnarExportCore<PbfBlock>
{
    #region Configuration and state

    /// <summary>
    /// The standard layout's own stage for the flat shapes (reused for every packet, independent of the open block);
    /// <see langword="null"/> for the hierarchy shape and the columnar layout. Owned and disposed by the exporter.
    /// </summary>
    private readonly RowFieldStage? _RowStage;

    /// <summary>Creates the core of one PBF export; no I/O happens.</summary>
    /// <remarks>
    /// The standard layout writes every packet self-describing (names come from the packet's own field info), so it carries no
    /// catalog and needs no stack check. The estimate of the output is the block writer's committed bytes, so the core keeps no
    /// flushed-bytes figure.
    /// </remarks>
    /// <param name="format">The block layout; the columnar layout writes a catalog of the first packet's stack, the standard layout does not.</param>
    /// <param name="content">What the file contains.</param>
    /// <param name="targetPacketCount">Stop after this many packets; 0 means unlimited.</param>
    /// <param name="pipeline">The pipeline of the export.</param>
    /// <param name="collector">The staging collector of the flat shapes, or <see langword="null"/> for the hierarchy shape.</param>
    /// <param name="rowStage">The standard layout's stage for the flat shapes; <see langword="null"/> otherwise.</param>
    /// <param name="raiseItemSkipped">Raises the exporter's <c>ItemSkipped</c> event; create the delegate once.</param>
    /// <param name="start">Opens the output and writes the header; create the delegate once.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    internal PbfExportCore(
        PbfExportFormat format,
        ExportContent content,
        int targetPacketCount,
        BatchWriterPipeline<PbfBlock> pipeline,
        ExportFieldCollector? collector,
        RowFieldStage? rowStage,
        Action<ExportErrorEventArgs> raiseItemSkipped,
        Func<Packet, bool> start,
        CancellationToken cancellationToken)
        : base(
            "PBF",
            content,
            targetPacketCount,
            pipeline,
            collector,
            raiseItemSkipped,
            start,
            checksCatalogStack: format == PbfExportFormat.Columnar,
            tracksFlushedBytes: false,
            cancellationToken: cancellationToken)
    {
        _RowStage = rowStage;
    }

    #endregion

    #region Format hooks

    /// <inheritdoc/>
    protected override bool TryAddPacket(Packet packet, out bool shouldFlush, out ExportErrorKind kind, out string? message)
    {
        // Adding may fail on a malformed packet; that must degrade to a skipped packet rather than tearing down the pipeline. It
        // is all-or-nothing, so nothing of the packet is in the block when it fails. The columnar flat shapes commit what the
        // observer staged (see ColumnarFeed); the other layouts add the packet's field tree.
        if (_RowStage is not null)
        {
            // Standard layout, flat shape: the same three feeds as the columnar path, with the exporter's own stage. A tree packet
            // the observer did not see is walked through the collector first; then the closed staging of this very packet is
            // committed, and its entries are encoded as one flat record. A failing text is a skipped packet, not a throw.
            ExportFieldCollector collector = Collector!; // Invariant: the row stage and the collector are created together.
            if (!RowFeed.TryPrepare(_RowStage, collector, packet, out kind, out message))
            {
                shouldFlush = false;
                return false;
            }

            try
            {
                shouldFlush = Pipeline.Current.Standard!.AddStagedPacket(packet, _RowStage); // Invariant: a standard block has a builder.
            }
            catch (Exception ex)
            {
                shouldFlush = false;
                kind = ex is IOException
                    ? ExportErrorKind.IoError
                    : ExportErrorKind.SerializationError;
                message = ex.Message;
                return false;
            }

            return true;
        }

        if (Collector is not null)
        {
            PbfBlock open = Pipeline.Current;
            ColumnarBlockBuilder columnar = open.Columnar!; // Invariant: this branch runs for the columnar layout only.
            if (!ColumnarFeed.TryAdd(columnar.Batch, Collector, packet, out kind, out message))
            {
                shouldFlush = false;
                return false;
            }

            shouldFlush = columnar.Batch.ShouldFlush;
            return true;
        }

        try
        {
            shouldFlush = Pipeline.Current.AddPacket(packet);
        }
        catch (Exception ex)
        {
            shouldFlush = false;
            kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            message = ex.Message;
            return false;
        }

        kind = default;
        message = null;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>The strict nesting check of the standard layout; see <see cref="PassesNestingCheck"/>.</remarks>
    protected override bool BeforeSubmit(PbfBlock full) => PassesNestingCheck(full);

    /// <inheritdoc/>
    protected override void AfterSubmit()
    {
        if (Collector is not null && _RowStage is null)
        {
            Collector.Stage = Pipeline.Current.Stage!; // Invariant: a collector without a row stage belongs to the columnar layout.
        }
    }

    #endregion

    #region Nesting check

    /// <summary>
    /// Reports fields that were silently dropped by the nesting depth limit of the standard layout. In strict mode the block is
    /// dropped instead of written.
    /// </summary>
    /// <remarks>
    /// This check stays on the caller's thread because it may end the export and must raise <c>ItemSkipped</c> there;
    /// the writer thread only serializes, compresses and writes.
    /// </remarks>
    /// <param name="block">The full block about to be written.</param>
    /// <returns><see langword="true"/> when the block may be written; <see langword="false"/> when it was dropped (strict).</returns>
    internal bool PassesNestingCheck(PbfBlock block)
    {
        int truncated = block.Standard is null
            ? 0
            : block.Standard.TruncatedFieldCount;
        if (truncated == 0)
        {
            return true;
        }

        // The event names the block's first packet: the truncated subtrees belong to some packet of this block, and the block is the
        // unit that is written (tolerant) or dropped (strict).
        int firstItemIndex = PacketCount - block.PacketCount;
        if (HandleSkip(new ExportErrorEventArgs
        {
            ItemIndex = firstItemIndex,
            Kind = ExportErrorKind.MalformedData,
            Message = Invariant($"{truncated} field subtree(s) in the block starting at packet {firstItemIndex} were dropped because the")
                + Invariant($" protocol tree exceeded the maximum nesting depth ({truncated} root(s) at depth >= {StandardBlockBuilder.MaxNestingDepth}).")
                + " The block was written with incomplete field data."
        }))
        {
            return true;
        }

        // Strict: the block is not written. Its packets were counted as written when they were accepted, but none of them is in
        // the file, so the count (and with it the trailer) must not include them. Dropping the block also keeps the finish from
        // judging and skipping the same block a second time. HandleSkip counted one skipped item already; the other packets of
        // the block are added here.
        int dropped = block.PacketCount;
        block.Reset();
        PacketCount -= dropped;
        int extraSkipped = dropped - 1;
        SkippedCount = SkippedCount > int.MaxValue - extraSkipped
            ? int.MaxValue
            : SkippedCount + extraSkipped;
        return false;
    }

    #endregion
}
