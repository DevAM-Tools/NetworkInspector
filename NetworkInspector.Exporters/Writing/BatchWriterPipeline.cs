// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>
/// Writes full batches of an exporter either on a dedicated writer thread (<see cref="ExportWriteMode.Background"/>) while the
/// caller fills the next batch, or inline on the caller's thread (<see cref="ExportWriteMode.Synchronous"/>).
/// <para>
/// <b>Why:</b> encoding, compression and I/O of a batch (a DuckDB transaction, a Parquet row group, an LZ4 block, a zlib
/// container) take as long as parsing thousands of packets. Writing on a second thread lets both run at the same time; the
/// hand-off costs at most two thread wake-ups per batch, so the per-packet path stays free of synchronization.
/// </para>
/// <para>
/// <b>Memory:</b> two batches in background mode (one filling, one writing), one in synchronous mode. The caller waits when it is
/// a whole batch ahead of the writer; that wait is the back pressure that keeps memory bounded.
/// </para>
/// <para>
/// <b>Errors:</b> a failed write never ends on the writer thread's stack: it becomes a <see cref="LostBatch"/> that the exporter
/// takes with <see cref="TryTakeLost"/> on its own thread, where counters and <c>ItemSkipped</c> live. In background mode a
/// failure is therefore seen one batch later (at the latest in <see cref="Complete"/>); in synchronous mode right after
/// <see cref="Submit"/>. Either way the batch is emptied and reused, so a failing output cannot grow memory. A writer thread that
/// cannot start is an exception of <see cref="Submit"/>; the pipeline stays consistent and the next call tries again.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Current"/>, <see cref="Submit"/>, <see cref="HasLost"/>, <see cref="TryTakeLost"/>,
/// <see cref="Complete"/> and <see cref="Dispose"/> are called from one caller thread. The writer thread only touches the batch
/// it was handed, the <see cref="IBatchWriter{TBatch}"/>, and the lost-batch queue (a <see cref="ConcurrentQueue{T}"/>, the
/// cheapest correct structure for a rare multi-item hand-back; the flag in front of it is volatile so the per-packet check is
/// one read).
/// </para>
/// </summary>
/// <typeparam name="TBatch">The batch type the exporter fills and the writer writes.</typeparam>
/// <seealso cref="BatchHandOff{TBatch}"/>
/// <seealso cref="IBatchWriter{TBatch}"/>
internal sealed class BatchWriterPipeline<TBatch> : IDisposable
    where TBatch : class, IExportBatch
{
    #region Configuration and state

    /// <summary>Format name for the writer thread name and the lost-batch message.</summary>
    private readonly string _FormatName;

    /// <summary>Whether full batches are written on the writer thread or inline.</summary>
    private readonly ExportWriteMode _Mode;

    /// <summary>Creates one empty batch; called once (synchronous) or twice (background).</summary>
    private readonly Func<TBatch> _CreateBatch;

    /// <summary>Writes a full batch; called on the writer thread (background) or the caller's thread (synchronous).</summary>
    private readonly IBatchWriter<TBatch> _Writer;

    /// <summary>Every created batch, for <see cref="Dispose"/> (one or two).</summary>
    private readonly List<TBatch> _AllBatches = new(2);

    /// <summary>Creates a pipeline without allocating a batch or starting a thread.</summary>
    /// <param name="formatName">Format name for the thread name ("{format} export writer") and the lost-batch message.</param>
    /// <param name="mode">Background or synchronous writing.</param>
    /// <param name="createBatch">Creates one empty batch; called once (synchronous) or twice (background).</param>
    /// <param name="writer">Writes a full batch.</param>
    /// <exception cref="ArgumentException"><paramref name="formatName"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="createBatch"/> or <paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    internal BatchWriterPipeline(string formatName, ExportWriteMode mode, Func<TBatch> createBatch, IBatchWriter<TBatch> writer)
    {
        ArgumentException.ThrowIfNullOrEmpty(formatName);
        ArgumentNullException.ThrowIfNull(createBatch);
        ArgumentNullException.ThrowIfNull(writer);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown export write mode.");
        }

        // Nothing is allocated and no thread starts here: exporters create the pipeline in their constructor (a tree-free
        // observer stages the first packet into Current before the first OnPacket), while an export that never receives a
        // packet must leave no thread and no file behind. Batch memory is created on the first Current access, the second
        // batch and the thread on the first background Submit.
        _FormatName = formatName;
        _Mode = mode;
        _CreateBatch = createBatch;
        _Writer = writer;
    }

    /// <summary>Remembers a created batch for <see cref="Dispose"/> (at most two exist).</summary>
    /// <param name="batch">The batch that was just created.</param>
    /// <returns><paramref name="batch"/>, so a creation can be tracked in one expression.</returns>
    private TBatch _Track(TBatch batch)
    {
        _AllBatches.Add(batch);
        return batch;
    }

    #endregion

    #region Filling and hand-off

    /// <summary>The batch the caller is filling; null before the first access and while the writer owns it.</summary>
    private TBatch? _Current;

    /// <summary>
    /// Hand-off to the writer thread. Created on the first background submit; null in synchronous mode, for an export shorter than
    /// one batch, and after a writer thread failed to start.
    /// </summary>
    private BatchHandOff<TBatch>? _HandOff;

    /// <summary>The writer thread; null until it runs and after <see cref="_StopThread"/>.</summary>
    private Thread? _Thread;

    /// <summary>
    /// The second batch of the hand-off. Created on the first background submit and kept, so a retry after a failed thread start
    /// does not create a third batch.
    /// </summary>
    private TBatch? _Spare;

    /// <summary>The batch the caller fills; created on first access.</summary>
    /// <exception cref="InvalidOperationException">The pipeline is completed.</exception>
    internal TBatch Current
    {
        get
        {
            if (_Completed)
            {
                throw new InvalidOperationException("The batch writer pipeline is completed.");
            }

            // Lazy: the first access (collector construction or the first commit) creates the first batch.
            _Current ??= _Track(_CreateBatch());
            return _Current;
        }
    }

    /// <summary>
    /// Items in the batch the caller is filling, without creating it: 0 before the first <see cref="Current"/> access and after
    /// <see cref="Complete"/>. Lets an exporter read its progress (<c>EstimatedOutputBytes</c>) and finish without allocating a
    /// batch it never used. Caller's thread only.
    /// </summary>
    internal int CurrentPacketCount => _Current is null
        ? 0
        : _Current.PacketCount;

    /// <summary>In-memory size estimate of the batch the caller is filling, without creating it (see <see cref="CurrentPacketCount"/>).</summary>
    internal long CurrentEstimatedSizeBytes => _Current is null
        ? 0
        : _Current.EstimatedSizeBytes;

    /// <summary>
    /// Hands the full <see cref="Current"/> to the writer and makes the other batch current (background), or writes it inline and
    /// resets it (synchronous).
    /// </summary>
    /// <param name="firstItemIndex">Index of the batch's first item, for the error event of a failed write.</param>
    /// <exception cref="InvalidOperationException">The pipeline is completed.</exception>
    /// <exception cref="OutOfMemoryException">Background mode: the writer thread could not start; the batch is still current.</exception>
    internal void Submit(int firstItemIndex)
    {
        TBatch batch = Current;
        WriteJob<TBatch> job = new(batch, batch.PacketCount, firstItemIndex, batch.EstimatedSizeBytes);
        if (_Mode == ExportWriteMode.Synchronous)
        {
            _WriteAndReset(in job);
            return;
        }

        // From here the writer owns the batch; the caller does not touch it until it comes back empty. A failed thread start
        // throws before _Current is cleared, so the full batch stays with the caller.
        BatchHandOff<TBatch> handOff = _EnsureBackground();
        _Current = null;
        _Current = handOff.Exchange(in job);
    }

    /// <summary>
    /// Creates the second batch, the hand-off, and the writer thread on the first background submit. Deferred to here so an
    /// export with fewer packets than one batch never starts a thread (the last batch is written inline by Complete).
    /// </summary>
    /// <returns>The hand-off served by the running writer thread.</returns>
    /// <exception cref="OutOfMemoryException">The writer thread could not start; the pipeline is unchanged and a retry starts again.</exception>
    private BatchHandOff<TBatch> _EnsureBackground()
    {
        if (_HandOff is not null)
        {
            return _HandOff;
        }

        // The spare batch is created once; a retry after a failed start reuses it instead of creating a third batch.
        _Spare ??= _Track(_CreateBatch());
        BatchHandOff<TBatch> handOff = new(_Spare);

        // Background thread so an abandoned exporter cannot keep the process alive; invariant culture so nothing written on
        // this thread depends on the host's locale.
        Thread thread = new(_WriterLoop)
        {
            Name = Invariant($"{_FormatName} export writer"),
            IsBackground = true,
            CurrentCulture = CultureInfo.InvariantCulture,
            CurrentUICulture = CultureInfo.InvariantCulture,
        };

        // Invariant: _HandOff and _Thread are set together and only once the thread runs. The writer loop receives its hand-off
        // as the start argument, so nothing is published before the start succeeded; a failed start leaves both null, the
        // exception propagates from Submit, and the next Submit (or Complete, which then writes inline) starts from a
        // consistent state. A thread that failed to start must not be joined, so it is never stored.
        try
        {
            thread.Start(handOff);
        }
        catch
        {
            handOff.Dispose();
            throw;
        }

        _HandOff = handOff;
        _Thread = thread;
        return handOff;
    }

    #endregion

    #region Writer thread

    /// <summary>
    /// Writer thread body. Every batch is returned, also after a failure, so the caller never waits for ever; failures of
    /// <see cref="IBatchWriter{TBatch}.Write"/> are caught in <see cref="_WriteAndReset"/> (the top of a worker thread must not let
    /// an exception escape, because that would end the process).
    /// </summary>
    /// <param name="state">The <see cref="BatchHandOff{TBatch}"/> this thread serves; passed by <see cref="Thread.Start(object?)"/>.</param>
    private void _WriterLoop(object? state)
    {
        // Invariant: Start passes the hand-off this thread serves.
        BatchHandOff<TBatch> handOff = (BatchHandOff<TBatch>)state!;
        while (handOff.TryTakeFull(out WriteJob<TBatch> job))
        {
            _WriteAndReset(in job);
            handOff.ReturnEmpty(job.Batch);
        }
    }

    /// <summary>
    /// Writes one batch and empties it whether the write succeeded or not. A failure becomes a <see cref="LostBatch"/>; nothing
    /// escapes.
    /// </summary>
    /// <param name="job">The full batch with its accounting numbers.</param>
    private void _WriteAndReset(in WriteJob<TBatch> job)
    {
        Exception? failure = null;
        try
        {
            _Writer.Write(job.Batch);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Emptied whether the write succeeded or not: keeping the rows of a failed batch would rewrite partial output on the
        // next attempt (duplicates) and let memory grow while the output keeps failing.
        try
        {
            job.Batch.Reset();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        if (failure is not null)
        {
            ExportErrorKind kind = failure is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            _Lost.Enqueue(new LostBatch(
                job.PacketCount,
                job.FirstItemIndex,
                job.EstimatedBytes,
                kind,
                Invariant($"{_FormatName} batch write failed, {job.PacketCount} packets lost: {failure.Message}")));

            // After the enqueue: a reader that sees true finds the item.
            _HasLost = true;
        }
    }

    #endregion

    #region Lost batches

    /// <summary>Batches whose write failed, in the order they failed; filled by the writer, drained by the caller.</summary>
    private readonly ConcurrentQueue<LostBatch> _Lost = new();

    /// <summary>Set by the writer after the enqueue; read per packet by the caller.</summary>
    private volatile bool _HasLost;

    /// <summary>Whether a lost batch waits for <see cref="TryTakeLost"/>; one volatile read, cheap enough per packet.</summary>
    internal bool HasLost => _HasLost;

    /// <summary>Takes the oldest lost batch, if any. Caller's thread only.</summary>
    /// <param name="lost">The lost batch on success.</param>
    /// <returns><see langword="true"/> when <paramref name="lost"/> holds a batch; <see langword="false"/> when none is waiting.</returns>
    internal bool TryTakeLost(out LostBatch lost)
    {
        // Cleared before the read: a batch the writer queues meanwhile sets the flag again, so the next check sees it. A flag
        // that is set without an item (the writer enqueued, the caller took the item, the writer then set the flag) only costs
        // one empty dequeue.
        _HasLost = false;
        return _Lost.TryDequeue(out lost);
    }

    #endregion

    #region Completion and release

    /// <summary><see langword="true"/> once <see cref="Complete"/> or <see cref="Dispose"/> ran; <see cref="Current"/> then refuses.</summary>
    private bool _Completed;

    /// <summary><see langword="true"/> once <see cref="Dispose"/> ran.</summary>
    private bool _Disposed;

    /// <summary>Whether <see cref="Complete"/> ran.</summary>
    internal bool IsCompleted => _Completed;

    /// <summary>
    /// Writes the last batch (when it holds items), waits until every batch is written, and ends the writer thread. Idempotent.
    /// Afterwards the exporter takes the lost batches and completes its output (catalog, trailer).
    /// </summary>
    /// <param name="firstItemIndex">Index of the last batch's first item, for the error event of a failed write.</param>
    internal void Complete(int firstItemIndex)
    {
        if (_Completed)
        {
            return;
        }

        _Completed = true;
        TBatch? last = _Current;
        _Current = null;
        if (last is not null && last.PacketCount > 0)
        {
            WriteJob<TBatch> job = new(last, last.PacketCount, firstItemIndex, last.EstimatedSizeBytes);

            // An export shorter than one batch never started a thread (and a thread that failed to start left no hand-off): the
            // last batch is written inline.
            if (_HandOff is null)
            {
                _WriteAndReset(in job);
            }
            else
            {
                _HandOff.Publish(in job);
            }
        }

        _StopThread();
    }

    /// <summary>
    /// Stops the writer thread without writing the current batch (an aborted export) and releases both batches. A batch that was
    /// already handed to the writer is still written, because the thread ends only after its queue is empty.
    /// </summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        _Completed = true;
        _Current = null;
        _StopThread();
        foreach (TBatch batch in _AllBatches)
        {
            batch.Dispose();
        }

        _HandOff?.Dispose();
    }

    /// <summary>Tells the writer thread that no more batches follow and waits until it ended; does nothing without a thread.</summary>
    private void _StopThread()
    {
        Thread? thread = _Thread;
        _Thread = null;
        if (thread is null)
        {
            return;
        }

        // Invariant: _HandOff exists whenever _Thread does (both are set together once the thread runs).
        _HandOff!.Close();
        thread.Join();
    }

    #endregion
}
