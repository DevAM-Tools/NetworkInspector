// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Intake and accounting of the exporters that collect packets in batches and write them column by column (DuckDB, Parquet, and
/// both PBF layouts): the stop checks, the one-stack rule, the lazy start, the tree-less hierarchy check, the packet and skip
/// counters, the hand-off of a full batch to the <see cref="BatchWriterPipeline{TBatch}"/>, the lost-batch accounting, and the
/// error channel (<c>ItemSkipped</c>). One copy means one rule: a change to how these exporters count, skip, or stop is made here
/// and applies to all of them.
/// <para>
/// <b>What stays in the exporter:</b> its builder, the opening of its sink (<c>start</c> delegate), the
/// <see cref="IBatchWriter{TBatch}"/> that writes a batch, the finish sequence, and the public statistics members, which forward to
/// this type. <b>What a format decides</b> is exactly what differs between the formats and is therefore a hook: how a packet enters
/// the open batch (<see cref="TryAddPacket"/>), a check before a full batch leaves (<see cref="BeforeSubmit"/>), and where the
/// observer stages the next packet after the swap (<see cref="AfterSubmit"/>).
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. Every member runs on the exporter's caller thread; the writer thread of the pipeline
/// never touches this type (it reports a failed batch through the pipeline's lost-batch queue, which <see cref="DrainLostBatches"/>
/// takes on the caller's thread).
/// </para>
/// <para>
/// <b>Cost:</b> one virtual call per packet (<see cref="TryAddPacket"/>) and one per full batch (<see cref="BeforeSubmit"/>,
/// <see cref="AfterSubmit"/>); the call is small against the work of adding a packet to a batch. The per-packet path allocates
/// nothing: the delegates are created once by the exporter, and an error event is allocated only for a skipped packet.
/// </para>
/// </summary>
/// <typeparam name="TBatch">The batch type the exporter fills and its writer writes.</typeparam>
/// <seealso cref="ColumnarBatchExportCore"/>
/// <seealso cref="ColumnarFeed"/>
/// <seealso cref="BatchWriterPipeline{TBatch}"/>
internal abstract class ColumnarExportCore<TBatch>
    where TBatch : class, IExportBatch
{
    #region Configuration and state

    /// <summary>Format name for the error messages ("{format} packet write failed: …").</summary>
    private readonly string _FormatName;

    /// <summary>What the export contains; its field selection (if any) pins the stack of the export.</summary>
    private readonly ExportContent _Content;

    /// <summary>Stop after this many packets; 0 means unlimited.</summary>
    private readonly int _TargetPacketCount;

    /// <summary>Cooperative cancellation; checked on every <see cref="OnPacket"/> call.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>Raises the exporter's <c>ItemSkipped</c> event with the exporter as sender; created once by the exporter.</summary>
    private readonly Action<ExportErrorEventArgs> _RaiseItemSkipped;

    /// <summary>
    /// Opens the exporter's sink for the first packet and returns whether it is open; on failure the exporter has called
    /// <see cref="ReportStartFailure"/> already. Created once by the exporter.
    /// </summary>
    private readonly Func<Packet, bool> _Start;

    /// <summary>
    /// Whether every packet must come from the stack of the first one (the format writes a catalog of that stack). A format whose
    /// records are self-describing writes any stack and needs no check.
    /// </summary>
    private readonly bool _ChecksCatalogStack;

    /// <summary>
    /// Whether <see cref="FlushedOutputBytes"/> is kept. The formats that estimate their output from a figure of their own
    /// (PBF: the committed bytes of the block writer) leave it off.
    /// </summary>
    private readonly bool _TracksFlushedBytes;

    /// <summary>
    /// The stack of the first packet: the catalog of the output (protocol table, field ids, presence bitmap) is built from it, and
    /// every field id in the output is an id of that stack. Threat: a packet of another stack (a session restart between two
    /// <see cref="OnPacket"/> calls) carries field ids that name other fields in this catalog, so writing it would corrupt the
    /// output silently. Mitigation: the packet is refused as a fatal, reported error
    /// (<see cref="ExportErrorKind.StackMismatch"/>), the rows of the earlier packets are still finished by the exporter's finish
    /// sequence, and the owner of the export (whose job a restart ended anyway) starts a new export on the new stack. Rejected:
    /// skipping only that packet in Tolerant mode (every later packet of the new stack would be skipped one by one, each reported,
    /// for no output anybody wants) and a second catalog (the formats have one catalog per output). Residual risk: none for
    /// catalog exporters; a stack that is not the first packet's but identical in content is refused too. <see langword="null"/>
    /// before the first packet and after the finish (a finished export accepts no packet, so the stack is not kept alive).
    /// </summary>
    private Stack? _CatalogStack;

    /// <summary>The batches, the writer thread, and the lost-batch queue of the export; owned by the exporter.</summary>
    protected BatchWriterPipeline<TBatch> Pipeline { get; }

    /// <summary>
    /// The staging collector of the flat shapes (selected fields, all fields); <see langword="null"/> for the hierarchy shape,
    /// which is written from the field tree.
    /// </summary>
    protected ExportFieldCollector? Collector { get; }

    /// <summary>Creates the core of one export; no I/O happens and no batch is created.</summary>
    /// <param name="formatName">Format name for the error messages; not empty.</param>
    /// <param name="content">What the export contains.</param>
    /// <param name="targetPacketCount">Stop after this many packets; 0 means unlimited.</param>
    /// <param name="pipeline">The pipeline of the export; its batches are filled and handed over through this core.</param>
    /// <param name="collector">The staging collector of the flat shapes, or <see langword="null"/> for the hierarchy shape.</param>
    /// <param name="raiseItemSkipped">Raises the exporter's <c>ItemSkipped</c> event; create the delegate once.</param>
    /// <param name="start">Opens the exporter's sink for the first packet; see <c>_Start</c>. Create the delegate once.</param>
    /// <param name="checksCatalogStack">Whether packets of another stack than the first one's are refused.</param>
    /// <param name="tracksFlushedBytes">Whether <see cref="FlushedOutputBytes"/> is kept.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    protected ColumnarExportCore(
        string formatName,
        ExportContent content,
        int targetPacketCount,
        BatchWriterPipeline<TBatch> pipeline,
        ExportFieldCollector? collector,
        Action<ExportErrorEventArgs> raiseItemSkipped,
        Func<Packet, bool> start,
        bool checksCatalogStack,
        bool tracksFlushedBytes,
        CancellationToken cancellationToken)
    {
        _FormatName = formatName;
        _Content = content;
        _TargetPacketCount = targetPacketCount;
        _CancellationToken = cancellationToken;
        Pipeline = pipeline;
        Collector = collector;
        _RaiseItemSkipped = raiseItemSkipped;
        _Start = start;
        _ChecksCatalogStack = checksCatalogStack;
        _TracksFlushedBytes = tracksFlushedBytes;
    }

    #endregion

    #region Statistics and state

    /// <summary>
    /// Packets accepted so far; a packet counts as soon as it is part of the open batch, so the figure may include packets that
    /// are still buffered. A lost or dropped batch takes its packets back.
    /// </summary>
    internal int PacketCount { get; private protected set; }

    /// <summary>Packets skipped so far (refused, lost with a failed batch, or dropped with a batch); saturates at <see cref="int.MaxValue"/>.</summary>
    internal int SkippedCount { get; private protected set; }

    /// <summary>Errors reported so far; saturates at <see cref="int.MaxValue"/>.</summary>
    internal int ErrorCount { get; private set; }

    /// <summary>
    /// Estimated bytes of the batches handed to the writer so far (a batch that was reported lost is taken out again); saturates at
    /// <see cref="long.MaxValue"/>. Always 0 when the core does not track it. Read by the exporter's <c>EstimatedOutputBytes</c>,
    /// which never touches the file system.
    /// </summary>
    internal long FlushedOutputBytes { get; private set; }

    /// <summary>
    /// Latched by a fatal error (start failure, stack mismatch, strict-mode failure, finish failure); the export accepts no more
    /// packets.
    /// </summary>
    internal bool HasError { get; private set; }

    /// <summary>Set when the lazy start was attempted; a failed start is not retried.</summary>
    internal bool Started { get; private set; }

    /// <summary>Set by the first <see cref="TryBeginFinish"/>; a finished export accepts no packet.</summary>
    internal bool Finished { get; private set; }

    /// <summary>How an expected failure of one packet is handled: skipped and reported (tolerant) or ending the export (strict).</summary>
    internal ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <summary>Whether the export has stopped: finished, failed, cancelled, or the target count is reached.</summary>
    internal bool IsFinished => Finished || HasError
        || _CancellationToken.IsCancellationRequested
        || (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount);

    /// <summary>
    /// Records that the lazy start was attempted. The exporter calls it first in its start, before any work, so a failed start is
    /// not retried with the next packet (the error flag then stops the export).
    /// </summary>
    internal void MarkStarted() => Started = true;

    /// <summary>
    /// Latches the fatal state without counting or reporting an error: for a start that cannot even begin (the output was released
    /// already), where a late call on a disposed exporter must not start an error loop.
    /// </summary>
    internal void LatchError() => HasError = true;

    #endregion

    #region Packet intake

    /// <summary>
    /// Accepts one packet: the stop checks, the stack rules, the lazy start, and the add and hand-off of
    /// <see cref="_Handle"/>.
    /// </summary>
    /// <param name="packet">The packet the exporter was asked to write.</param>
    /// <returns>
    /// <see langword="false"/> when the export must stop (cancelled, finished, a fatal error, strict mode after a skipped packet,
    /// or the target count); otherwise <see langword="true"/>, also when the packet itself was skipped in tolerant mode.
    /// </returns>
    internal bool OnPacket(Packet packet)
    {
        // Stop signals first: cancellation, an earlier fatal error, or a finished exporter all end the export. After the finish
        // the sink and the batches are released, so a late packet must never reach them.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (HasError || Finished)
        {
            return false;
        }

        // A field selection was resolved against one stack: its field ids name other fields in any other stack, so a packet of
        // another stack is refused before anything is created (the same fatal StackMismatch path as the catalog check).
        if (_Content.Fields is not null && !ReferenceEquals(packet.Stack, _Content.Fields.Stack))
        {
            _ReportStackMismatch();
            return false;
        }

        // Lazy start: nothing is created (file, directory, stream) until the first packet arrives, so an export without packets
        // leaves nothing behind. The start reports its own failure and returns false.
        if (!Started && !_Start(packet))
        {
            return false;
        }

        // One stack per export: see _CatalogStack. A format with self-describing records writes every stack.
        if (_ChecksCatalogStack)
        {
            _CatalogStack ??= packet.Stack;
            if (!ReferenceEquals(packet.Stack, _CatalogStack))
            {
                _ReportStackMismatch();
                return false;
            }
        }

        return _Handle(packet);
    }

    /// <summary>
    /// A packet of another stack than the catalog's arrived: latches the fatal state, counts it, and raises
    /// <c>ItemSkipped</c> with <see cref="ExportErrorKind.StackMismatch"/>. The packet is not written; the rows of the earlier
    /// packets are still finished by the exporter's finish sequence.
    /// </summary>
    private void _ReportStackMismatch()
    {
        HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        _RaiseItemSkipped(new ExportErrorEventArgs
        {
            ItemIndex = PacketCount,
            Kind = ExportErrorKind.StackMismatch,
            Message = "The packet was parsed on another protocol stack than this export's catalog; start a new export after a session restart.",
        });
    }

    #endregion

    #region Adding a packet and the batch hand-off

    /// <summary>
    /// How a packet enters the open batch (the one place the formats differ on the per-packet path). All-or-nothing: when it
    /// fails, the batch is exactly as it was, so only this packet is skipped.
    /// </summary>
    /// <param name="packet">The packet; its stack is the catalog's stack, and a hierarchy packet carries a field tree.</param>
    /// <param name="shouldFlush">When the method returns <see langword="true"/>: whether the batch reached a limit and must be handed over.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>; the core prefixes the format name.</param>
    /// <returns><see langword="true"/> when the packet is part of the open batch.</returns>
    protected abstract bool TryAddPacket(Packet packet, out bool shouldFlush, out ExportErrorKind kind, out string? message);

    /// <summary>
    /// A check on the caller's thread before the full batch is handed to the writer; a format that must judge the batch here (it
    /// may end the export and must raise <c>ItemSkipped</c> on this thread) overrides it.
    /// </summary>
    /// <param name="full">The full batch about to be handed over.</param>
    /// <returns><see langword="false"/> when the batch was dropped and the export stopped; the default lets every batch pass.</returns>
    protected virtual bool BeforeSubmit(TBatch full) => true;

    /// <summary>
    /// Runs after the hand-off, when <see cref="Pipeline"/> offers the next batch: points the observer of the flat shapes at it.
    /// </summary>
    protected abstract void AfterSubmit();

    /// <summary>Adds one packet to the open batch, handing the batch to the writer if it is full.</summary>
    /// <param name="packet">The packet; its stack is the catalog's stack.</param>
    /// <returns>
    /// <see langword="false"/> when the export must stop (a fatal error, strict mode after a skipped packet, or the target count);
    /// otherwise <see langword="true"/>, also when the packet itself was skipped in tolerant mode.
    /// </returns>
    private bool _Handle(Packet packet)
    {
        // A batch the writer failed to write since the last packet is accounted first, so the counters and the target check
        // below see what is really in the output. In strict mode that failure ends the export here.
        if (Pipeline.HasLost)
        {
            DrainLostBatches(reportAlways: false);
            if (HasError)
            {
                return false;
            }
        }

        // Target reached: refuse further packets so the caller stops feeding the exporter.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // Hierarchy packets without a field tree carry nothing to put into the output; counted as a skipped item. The flat
        // shapes accept a tree-free packet: its fields were staged while it was parsed (a packet that was not observed and has
        // no tree is refused by the commit with a message that names the cause). An expected input error is refused here
        // without an exception; every format reports the same kind and message.
        if (Collector is null && !packet.HasFieldTree)
        {
            return HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ExportErrorKind.SerializationError,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
        }

        // The packet is added first and the (possibly large) hand-off happens only when a limit is reached. Adding is
        // all-or-nothing: when it fails, the batch is exactly as it was, so only this packet is skipped.
        if (!TryAddPacket(packet, out bool shouldFlush, out ExportErrorKind kind, out string? message))
        {
            return HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = kind,
                Message = Invariant($"{_FormatName} packet write failed: {message}"),
            });
        }

        // The packet is counted before the hand-off: the batch that leaves includes it, and a batch that is dropped or lost
        // takes its packets back out of the count together with the others (see DrainLostBatches).
        PacketCount++;
        if (!shouldFlush)
        {
            return true;
        }

        return _SubmitFullBatch();
    }

    /// <summary>
    /// Hands the full open batch to the writer, switches the observer to the next batch, and reports a write that failed
    /// synchronously.
    /// </summary>
    /// <returns><see langword="false"/> when the export has stopped (a dropped batch, or a lost batch in strict mode).</returns>
    private bool _SubmitFullBatch()
    {
        TBatch full = Pipeline.Current;
        if (!BeforeSubmit(full))
        {
            return false;
        }

        if (_TracksFlushedBytes)
        {
            // Saturating add: the sum is only a progress estimate; wrapping to a negative value would be worse than pinning it at
            // the maximum.
            long estimated = full.EstimatedSizeBytes;
            FlushedOutputBytes = FlushedOutputBytes > long.MaxValue - estimated
                ? long.MaxValue
                : FlushedOutputBytes + estimated;
        }

        // Waiting is the design here: in background mode Submit waits while the writer is still busy with the previous batch
        // and returns the emptied spare (back pressure bounds the memory to two batches); in synchronous mode it writes the batch
        // right here.
        Pipeline.Submit(firstItemIndex: PacketCount - full.PacketCount);

        // The next packet stages into the batch that is being filled now (the one Submit returned, not the one it handed over).
        AfterSubmit();

        // Synchronous mode wrote the batch inside Submit, so a failed write is already queued: report it in this very call
        // (uniform timing). In background mode the flag is rarely set yet; the per-packet check reports it later.
        if (Pipeline.HasLost)
        {
            DrainLostBatches(reportAlways: false);
            return !HasError;
        }

        return true;
    }

    #endregion

    #region Error accounting

    /// <summary>
    /// Handles a skipped packet: increments the counters, raises <c>ItemSkipped</c> in tolerant mode, and latches the error in
    /// strict mode.
    /// </summary>
    /// <param name="error">The error to report; its <see cref="ExportErrorEventArgs.ItemIndex"/> names the skipped item.</param>
    /// <returns><see langword="false"/> in strict mode (the export ends); <see langword="true"/> in tolerant mode.</returns>
    protected bool HandleSkip(ExportErrorEventArgs error)
    {
        if (SkippedCount < int.MaxValue)
        {
            SkippedCount++;
        }

        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            HasError = true;
            return false;
        }

        _RaiseItemSkipped(error);
        return true;
    }

    /// <summary>
    /// Accounts the batches the writer reported as lost: their packets leave <see cref="PacketCount"/>, count as skipped, and each
    /// batch raises one error. Runs on the caller's thread only.
    /// </summary>
    /// <param name="reportAlways">
    /// <see langword="true"/> at the end of the export: the error is raised and latched whatever the tolerance mode, like every
    /// finish-time error. <see langword="false"/> during the export: strict mode latches the error without an event, tolerant
    /// mode raises <c>ItemSkipped</c> and goes on.
    /// </param>
    internal void DrainLostBatches(bool reportAlways)
    {
        // TryTakeLost clears the pipeline's flag before it reads the queue: a batch the writer queues meanwhile sets it again, so it
        // is seen by the next call at the latest.
        while (Pipeline.TryTakeLost(out LostBatch lost))
        {
            // The packets were counted as written when they were accepted; take them back so the counters describe what is in the
            // output.
            PacketCount = Math.Max(0, PacketCount - lost.PacketCount);
            SkippedCount = SkippedCount > int.MaxValue - lost.PacketCount
                ? int.MaxValue
                : SkippedCount + lost.PacketCount;
            FlushedOutputBytes = Math.Max(0, FlushedOutputBytes - lost.EstimatedBytes);
            if (ErrorCount < int.MaxValue)
            {
                ErrorCount++;
            }

            if (!reportAlways && ErrorTolerance == ErrorToleranceMode.Strict)
            {
                HasError = true;
                continue;
            }

            if (reportAlways)
            {
                HasError = true;
            }

            _RaiseItemSkipped(new ExportErrorEventArgs
            {
                ItemIndex = lost.FirstItemIndex,
                Kind = lost.Kind,
                Message = lost.Message,
            });
        }
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <c>ItemSkipped</c> as item 0.</summary>
    /// <param name="kind">The kind of the failure.</param>
    /// <param name="message">The reason, for the event.</param>
    internal void ReportStartFailure(ExportErrorKind kind, string message)
    {
        HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        _RaiseItemSkipped(new ExportErrorEventArgs
        {
            ItemIndex = 0,
            Kind = kind,
            Message = message,
        });
    }

    /// <summary>
    /// Error channel of the finish sequence (<see cref="ExporterErrorReport"/>): marks the error, counts it, and raises
    /// <c>ItemSkipped</c>.
    /// </summary>
    /// <param name="kind">The kind of the failed finish step.</param>
    /// <param name="message">The reason of the failure.</param>
    internal void ReportFinishError(ExportErrorKind kind, string message)
    {
        // A failure while finalizing the output is fatal for it (the footer, catalog, or trailer may be missing), so it always sets
        // the error flag regardless of ErrorTolerance. Counters saturate instead of wrapping.
        HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        _RaiseItemSkipped(new ExportErrorEventArgs
        {
            ItemIndex = PacketCount,
            Kind = kind,
            Message = message,
        });
    }

    #endregion

    #region Finish

    /// <summary>
    /// Latches the finished state, once. The exporter calls it first in <c>OnFinish</c> and returns when it says the finish already
    /// ran (idempotent finish).
    /// </summary>
    /// <returns><see langword="true"/> for the first call, which must run the finish sequence; <see langword="false"/> afterwards.</returns>
    internal bool TryBeginFinish()
    {
        if (Finished)
        {
            return false;
        }

        // A finished exporter accepts no packet (OnPacket checks Finished first); do not keep the stack alive.
        Finished = true;
        _CatalogStack = null;
        return true;
    }

    #endregion
}
