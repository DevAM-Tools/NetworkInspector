// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.ValueCaches;

/// <summary>
/// How a <see cref="ValueCacheSlot"/> obtains rows for <see cref="IValueCacheListener.OnNewRows"/>.
/// </summary>
internal enum ValueCacheFillMode
{
    /// <summary>
    /// Slot thread skip-parses each id from its frame with <c>recordOnReplay: true</c>, then notifies.
    /// Used by <c>TryAddValueCache</c>. The recycle packet is never published.
    /// </summary>
    PullFill = 0,

    /// <summary>
    /// Parse thread already recorded the ingest writer. Slot notifies without recording.
    /// Used when both <see cref="SessionOptions.ValueCache"/> and
    /// <see cref="SessionOptions.ValueCacheListener"/> are set.
    /// </summary>
    NotifyOnly = 1,
}

/// <summary>
/// Session-internal bridge: holds the notification flags and pull cursor
/// for a single <see cref="IValueCacheListener"/>.
///
/// <para>
/// Flag protocol matches <see cref="ListenerSlot"/>: producers
/// <c>Interlocked.Or</c> flags and signal <see cref="ManualResetEventSlim"/>;
/// the slot thread <c>Interlocked.Exchange</c>s flags and pulls from
/// <see cref="ISessionReader"/>.
/// </para>
///
/// <para>
/// Thread-safety: flag field and wake event are safe for concurrent producers and one consumer.
/// The writer is single-threaded on this slot (PullFill) or on the parse thread (NotifyOnly).
/// </para>
/// </summary>
internal sealed class ValueCacheSlot : IDisposable
{
    #region Fields

    // Pending notifications as NotifyFlags bits. Producers (source threads, coordinator) OR bits in; only the slot
    // thread clears them, with Exchange. Internal so the producers can pass it by ref to Interlocked.
    internal volatile int Flags;
    private IValueCacheListener _Listener { get; }
    private ISessionReader _SessionReader { get; }
    private ValueCacheRequest _Request { get; }
    private ValueCacheFillMode _FillMode { get; }

    // PullFill only: builds a fresh cache for a new stack from the stored request (see _RebindAfterStackChange).
    private Func<Stack, ValueCache> _Rebuild { get; }

    // NotifyOnly only: returns the session's current ingest cache, which Restart replaces.
    private Func<ValueCache?> _GetIngestWriter { get; }

    // PullFill only: takes a lease on the session's current stack for one skip-parse. The lease keeps a stack that a Restart
    // just replaced from being disposed while this slot thread still parses on it (see RetirableStack), and it refuses while
    // queries are disabled. Rejected: reading ISessionReader.Stack and parsing without a lease (the stack could be shut down
    // under the parse).
    private TryLeaseStack _TryLeaseStack { get; }
    private Job _Job { get; }

    // Wakes the slot thread. Manual-reset so a Set between the thread's flag check and its Wait is not lost.
    private readonly ManualResetEventSlim _Wake = new(initialState: false);

    // The cache this slot fills (PullFill) or observes (NotifyOnly). Written by the slot thread on a stack change and by
    // the coordinator in BindWriter; volatile so each side sees the other's swap.
    private volatile ValueCache _Writer;

    // First packet id this slot has not yet delivered. Advanced by the slot thread; set to 0 after a restart, by the
    // coordinator (ResetPacketCursor) and again by the slot thread when it handles StackChanged, so the next NewPackets
    // window starts from 0.
    private volatile int _PacketCursor;

    // 0 = OnUnsubscribed not called yet, 1 = called (guards the at-most-once contract).
    private volatile int _OnUnsubscribedInvoked;

    // PullFill only: the slot's private skip-parse packet, reused for every id. Touched only by the slot thread and
    // never published, so recycling it in place is safe.
    private Packet? _Recycle;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates a value-cache slot. The slot is not started until <see cref="Start"/> is called.
    /// </summary>
    internal ValueCacheSlot(
        JobId jobId,
        IValueCacheListener listener,
        ISessionReader sessionReader,
        ValueCacheRequest request,
        ValueCache writer,
        ValueCacheFillMode fillMode,
        Func<Stack, ValueCache> rebuild,
        Func<ValueCache?> getIngestWriter,
        TryLeaseStack tryLeaseStack,
        Action<Job, JobStatus> onStatusChanged)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(sessionReader);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(getIngestWriter);
        ArgumentNullException.ThrowIfNull(tryLeaseStack);

        _Listener = listener;
        _SessionReader = sessionReader;
        _Request = request;
        _Writer = writer;
        _FillMode = fillMode;
        _Rebuild = rebuild;
        _GetIngestWriter = getIngestWriter;
        _TryLeaseStack = tryLeaseStack;
        // Independent, generation -1: a value-cache slot rebuilds its cache from the stored request on StackChanged; a restart
        // never ends it (same reasoning as for the listener slot).
        _Job = new Job(
            jobId,
            listener.UiName,
            fillMode == ValueCacheFillMode.NotifyOnly
                ? $"ValueCache ingest: {listener.UiName}"
                : $"ValueCache: {listener.UiName}",
            JobStackBinding.Independent,
            generation: -1,
            _RunLoop,
            onStatusChanged);
        Info = new JobInfo(_Job);
    }

    #endregion

    #region Identity

    /// <summary>The underlying job's identifier.</summary>
    internal JobId Id => _Job.Id;

    /// <summary>Public job view registered in the unified session job list.</summary>
    internal JobInfo Info
    {
        get;
    }

    /// <summary>
    /// Public subscription view. Set by the session after construction so
    /// <see cref="Session.TryUnsubscribe"/> can locate the matching info.
    /// </summary>
    internal ValueCacheInfo? ValueCacheInfo
    {
        get; set;
    }

    /// <summary>Fill strategy for <see cref="NotifyFlags.NewPackets"/>.</summary>
    internal ValueCacheFillMode FillMode => _FillMode;

    /// <summary>Current writer. Session may bind a new ingest writer on Restart.</summary>
    internal ValueCache Writer => _Writer;

    /// <summary>Name-based request used to rebuild the writer after a stack swap.</summary>
    internal ValueCacheRequest Request => _Request;

    #endregion

    #region Public API

    /// <summary>
    /// Starts the slot's background thread. A second call is a no-op (the job guards against a double start). When the
    /// thread cannot be started the job is already Failed; the listener is then told it is unsubscribed here, because
    /// the run loop that would do it never runs, and the failure is rethrown to the caller.
    /// </summary>
    internal void Start()
    {
        if (!TryStart(out Exception? failure))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// Starts the slot's thread like <see cref="Start"/>, but returns <see langword="false"/> instead of throwing when the
    /// thread cannot be started. The job is then already Failed with <paramref name="failure"/> recorded
    /// (<see cref="JobInfo.FailureException"/>), and the listener has been told it is unsubscribed.
    /// </summary>
    /// <param name="failure">The exception that stopped the thread from starting; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the slot is running or was started before.</returns>
    internal bool TryStart([NotNullWhen(false)] out Exception? failure)
    {
        if (_Job.TryStart(out failure))
        {
            return true;
        }

        EnsureOnUnsubscribed();
        return false;
    }

    /// <summary>
    /// Invokes <see cref="IValueCacheListener.OnUnsubscribed"/> at most once.
    /// Used from <see cref="_RunLoop"/> and from the session coordinator when <see cref="Job.Start"/> fails.
    /// </summary>
    internal void EnsureOnUnsubscribed()
    {
        if (Interlocked.CompareExchange(ref _OnUnsubscribedInvoked, 1, 0) == 0)
        {
            _Listener.OnUnsubscribed();
        }
    }

    /// <summary>
    /// Requests cancellation of the slot and wakes the run loop. Safe on a disposed slot (shutdown may cancel a slot from its
    /// snapshot that a concurrent unsubscribe has already disposed).
    /// </summary>
    /// <param name="reason">Why the slot is ended; recorded before the token fires (see <see cref="Job.Cancel(JobEndReason)"/>).</param>
    internal void Cancel(JobEndReason reason)
    {
        _Job.Cancel(reason);
        _Wake.Set();
    }

    /// <summary>Current job status.</summary>
    internal JobStatus Status => _Job.Status;

    /// <summary>Blocks until the job reaches a terminal state.</summary>
    internal void Join() => _Job.Join();

    /// <inheritdoc/>
    /// <remarks>The session disposes a slot only after its thread reached a terminal state (Join).</remarks>
    public void Dispose()
    {
        _Wake.Dispose();
        _Job.Dispose();
    }

    /// <summary>
    /// Sets the given flag(s) on this slot and wakes the listener thread. Non-blocking, thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Notify(NotifyFlags flags)
    {
        // Safe on a disposed slot: a source thread holding a snapshot from before the unsubscribe may notify a slot the
        // session already disposed. Set() on a disposed ManualResetEventSlim does not throw (only Wait and Reset check
        // disposal), nobody can wait on it any more (the slot is disposed after its thread was joined), and no lock is
        // needed per frame; a test pins this.
        // Called once per ingested frame per slot, so it is one atomic OR plus, rarely, a wake. The wake is signalled
        // only when none of the requested bits was pending: if one already was, the call that set it has signalled
        // (or is about to) and the slot thread has not drained it yet, so repeating the Set would only add cost. This
        // is the coalescing of wakeups: many frames, one wake.
        int prior = Interlocked.Or(ref Flags, (int)flags);
        if ((prior & (int)flags) == 0)
        {
            _Wake.Set();
        }
    }

    /// <summary>
    /// Resets the packet cursor to zero so the next <see cref="NotifyFlags.NewPackets"/>
    /// dispatch delivers all packets from the beginning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetPacketCursor()
        => _PacketCursor = 0;

    /// <summary>
    /// Replaces the writer this NotifyOnly slot aliases (new ingest cache after Restart).
    /// PullFill slots ignore this; they rebuild from <see cref="_Request"/> on StackChanged.
    /// </summary>
    internal void BindWriter(ValueCache writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _Writer = writer;
        ValueCacheInfo?.SetWriter(writer);
    }

    /// <summary>Marks the current writer abandoned. Session Restart calls this for PullFill writers.</summary>
    internal void AbandonWriter() => _Writer.Abandon();

    #endregion

    #region Private helpers

    /// <summary>
    /// Wait-loop matching <see cref="ListenerSlot"/>: drain flags, wait on <see cref="_Wake"/>,
    /// dispatch. Callback exceptions propagate to <see cref="Job"/> and fail only this slot.
    /// </summary>
    private void _RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain everything pending in one atomic step; flags set meanwhile start the next round.
                int flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                // Nothing pending: clear the manual-reset event, then look once more before waiting. A producer that
                // sets a bit and then the event between the first Exchange and this Reset would otherwise have its
                // wake erased by the Reset and the slot would sleep with work pending (lost wakeup).
                _Wake.Reset();
                flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                // The cancellation interrupts the wait; it leaves the loop through the drain below instead of an exception,
                // because an exception would skip the drain and drop what a producer raised just before the cancel.
                if (!_WaitForWork(ct))
                {
                    break;
                }
            }

            // Drain once, whichever way the loop was left (loop condition while dispatching, or interrupted wait): deliver
            // what was set before the cancellation took effect, then leave through the finally block that tells the
            // listener it is unsubscribed. One pass keeps the exit bounded.
            int remaining = Interlocked.Exchange(ref Flags, 0);
            if (remaining != 0)
            {
                _DispatchFlags((NotifyFlags)remaining);
            }

            // Every cancelled slot ends Cancelled (the job maps this exception); a normal return would end it Completed.
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            EnsureOnUnsubscribed();
        }
    }

    /// <summary>
    /// Blocks on the wake event until a producer signals or the job is cancelled.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the event was signalled; <see langword="false"/> when the cancellation ended the wait.
    /// </returns>
    private bool _WaitForWork(CancellationToken ct)
    {
        try
        {
            _Wake.Wait(ct);
            return true;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
        {
            // Expected: this slot's own cancellation. Reported as a return value so the caller can still drain the flags
            // that were raised before it. A cancellation from any other token is not ours and propagates.
            return false;
        }
    }

    /// <summary>
    /// Dispatches flags in the same order as <see cref="ListenerSlot"/>:
    /// StackChanged, packets, sources, jobs, lifecycle.
    /// </summary>
    private void _DispatchFlags(NotifyFlags notify)
    {
        // StackChanged first: the writer must be rebound and the cursor reset before the NewPackets branch below
        // refills or reports rows, so the whole re-parsed range is delivered from id 0 into the new cache.
        if ((notify & NotifyFlags.StackChanged) != 0)
        {
            _RebindAfterStackChange();
            _PacketCursor = 0;
            _Listener.OnStackChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.NewPackets) != 0)
        {
            int current = _SessionReader.PacketCount;
            int cursor = _PacketCursor;
            if (current > cursor)
            {
                // PullFill may stop early when a restart or shutdown disables queries. The cursor then
                // stays at the first id that was not filled; Restart resets it and refills from 0.
                // NotifyOnly: the parse thread already recorded every announced id, so the whole window is ready.
                int filledTo = current;
                if (_FillMode == ValueCacheFillMode.PullFill)
                {
                    filledTo = _FillWindow(cursor, current);
                }

                // Report only the ids that are actually in the cache, and advance the cursor only after the callback
                // returned.
                if (filledTo > cursor)
                {
                    _Listener.OnNewRows(_SessionReader, _Writer.AsReadOnlyView(), cursor, filledTo);
                    _PacketCursor = filledTo;
                }
            }
        }

        if ((notify & (NotifyFlags.SourceAdded | NotifyFlags.SourceCompleted)) != 0)
        {
            _Listener.OnSourcesChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.AllSourcesCompleted) != 0)
        {
            _Listener.OnAllSourcesCompleted(_SessionReader);
        }

        if ((notify & (NotifyFlags.JobAdded | NotifyFlags.JobStatusChanged | NotifyFlags.JobRemoved)) != 0)
        {
            _Listener.OnJobsChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.PhaseChanged) != 0)
        {
            _Listener.OnPhaseChanged(_SessionReader.Phase);
        }

        if ((notify & NotifyFlags.ShuttingDown) != 0)
        {
            _Listener.OnShuttingDown();
        }
    }

    /// <summary>
    /// PullFill: abandon the evicted writer and construct a new cache from the stored request.
    /// NotifyOnly: alias the session's new ingest writer (already created under the parse lock).
    /// </summary>
    private void _RebindAfterStackChange()
    {
        if (_FillMode == ValueCacheFillMode.NotifyOnly)
        {
            ValueCache? ingest = _GetIngestWriter();
            if (ingest is not null)
            {
                _Writer = ingest;
                ValueCacheInfo?.SetWriter(ingest);
            }

            return;
        }

        _Writer.Abandon();
        _Writer = _Rebuild(_SessionReader.Stack);
        ValueCacheInfo?.SetWriter(_Writer);
    }

    /// <summary>
    /// Skip-parses packets <paramref name="fromId"/> .. <paramref name="toIdExclusive"/>-1 from the
    /// captured frame via <see cref="ISessionReader.TryGetFrame"/>. Replay ids still record because
    /// <c>recordOnReplay</c> is true. The slot-private recycle packet is never published.
    /// </summary>
    /// <returns>
    /// The first id that was <b>not</b> filled: <paramref name="toIdExclusive"/> on success, or the id at
    /// which the session refused the read because queries are disabled (restart or shutdown in progress),
    /// at which the writer was abandoned, or at which the parser found the writer to belong to another stack.
    /// The caller keeps its cursor there.
    /// </returns>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.FrameUnavailable"/> when an announced frame cannot be re-read; that
    /// fails the slot, as documented on <see cref="IValueCacheListener.OnNewRows"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The parser rejected an announced frame for a reason other than a stale writer; that fails the slot as well.
    /// </exception>
    private int _FillWindow(int fromId, int toIdExclusive)
    {
        // One id at a time, in ascending order: the writer records rows in packet order, so ids must not be skipped
        // or reordered. The frame is read straight from its source; no packet is built just to be thrown away.
        for (int id = fromId; id < toIdExclusive; id++)
        {
            if (!_SessionReader.TryGetFrame(id, out Frame frame, out SessionFailure? failure))
            {
                // A paused session is not a missing frame: Restart resets the cursor and refills,
                // Shutdown cancels the slot. Only a genuine miss fails the slot.
                if (failure?.Code == SessionErrorCode.QueriesDisabled)
                {
                    return id;
                }

                throw new SessionException(
                    SessionErrorCode.FrameUnavailable,
                    FormattableString.Invariant(
                        $"ValueCache PullFill cannot load frame for PacketId {id.ToString(CultureInfo.InvariantCulture)}."));
            }

            // A Restart abandons this writer from another thread; stop before parsing into it and report how far we got.
            if (_Writer.IsAbandoned)
            {
                return id;
            }

            // Skip-parse with this slot's writer. recordOnReplay is required: ingest already first-parsed every announced
            // id (without this slot's cache), so the parser sees a replay, which would not write a cache otherwise. Skip
            // mode because only the cache rows are needed, not a field tree.
            PacketId packetId = id;

            // The lease keeps the stack from being disposed under this parse when a Restart replaces it meanwhile, and it is
            // refused while queries are disabled: the same outcome as a refused TryGetFrame (the cursor stays, Restart refills).
            if (!_TryLeaseStack(out StackLease lease))
            {
                return id;
            }

            using (lease)
            {
                try
                {
                    if (!_TryParseInto(lease.Stack, packetId, frame, out ParseFailure parseFailure))
                    {
                        // The writer belongs to a stack that was swapped out (a Restart that has not rebound this slot yet).
                        // Nothing is wrong with the frame: stop at this id and keep the cursor here, exactly like an
                        // abandoned writer. The StackChanged flag that follows the swap rebuilds the cache and refills from 0.
                        if (parseFailure is ParseFailure.CacheStackMismatch or ParseFailure.StackMismatch)
                        {
                            return id;
                        }

                        // Any other rejection means an announced, already first-parsed frame cannot be parsed again: a
                        // broken invariant, not something to skip. Returning the end of the window here would report ids as
                        // delivered that were never recorded, so the slot fails instead (documented for an unreadable frame).
                        throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"ValueCache PullFill cannot parse PacketId {id.ToString(CultureInfo.InvariantCulture)}: {parseFailure}."));
                    }
                }
                catch (InvalidOperationException) when (_Writer.IsAbandoned)
                {
                    // The writer was abandoned between the check above and the record (ValueCache throws "evicted").
                    return id;
                }
            }
        }

        return toIdExclusive;
    }

    /// <summary>
    /// Skip-parses one frame into this slot's writer on <paramref name="stack"/>, recycling the slot's private packet when the
    /// parser accepts it. The caller holds a lease on <paramref name="stack"/>.
    ///
    /// <para>
    /// The recycle packet may still belong to the stack before a restart: the parser rebinds it to <paramref name="stack"/>
    /// (same frame registry), so the slot keeps its one packet across a restart. The slot still stops on
    /// <see cref="ParseFailure.CacheStackMismatch"/> (its writer is of the old stack until the slot rebuilds it on
    /// <see cref="NotifyFlags.StackChanged"/>), because rebinding the packet does not make a writer of another stack valid.
    /// </para>
    /// </summary>
    /// <returns><see langword="false"/> with the parser's reason in <paramref name="failure"/> when the parse was rejected.</returns>
    private bool _TryParseInto(Stack stack, PacketId packetId, Frame frame, out ParseFailure failure)
    {
        ParseOptions options = new(FieldTreeMode.Skip, _Writer, recordOnReplay: true);
        if (_Recycle is not null && Packet.TryParse(_Recycle, packetId, stack, frame, in options, out _))
        {
            failure = ParseFailure.None;
            return true;
        }

        if (!Packet.TryParse(packetId, stack, frame, in options, out Packet? parsed, out failure))
        {
            return false;
        }

        _Recycle = parsed;
        return true;
    }

    #endregion
}
