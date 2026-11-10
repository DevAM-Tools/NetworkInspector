// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>
/// Session-internal bridge: holds the notification flags and pull cursor
/// for a single <see cref="ISessionListener"/>.
///
/// <para>
/// <b>Replaces:</b> <c>ListenerJob</c> (MPSC ring + signal dispatch).
/// Instead of a <see cref="ConcurrentQueue{T}"/> of heap-allocated Signal objects,
/// a single atomic <see cref="int"/> field conveys all pending notifications.
/// The listener pulls data from the shared <see cref="ISessionReader"/>.
/// </para>
///
/// <para>
/// <b>Flag protocol:</b>
/// Producers: <c>Interlocked.Or(ref slot.Flags, (int)flag)</c> — non-blocking, idempotent.
/// Consumer: <c>Interlocked.Exchange(ref Flags, 0)</c> — reads all flags and clears atomically.
/// Producers also signal <see cref="ManualResetEventSlim"/> so the consumer blocks without polling.
/// Why this shape: a notification carries no payload (the listener pulls the data from the session), so repeated
/// notifications of one kind add no information and may be merged. One word replaces a queue: no allocation per
/// event, no growth under a fast producer, and a slow listener sees one merged batch instead of a backlog.
/// </para>
///
/// <para>
/// Thread-safety: flag field and wake event are safe for concurrent producers and one consumer (the listener thread).
/// The packet cursor is written by that thread and, on a stack swap, reset by the session coordinator.
/// </para>
/// </summary>
internal sealed class ListenerSlot : IDisposable
{
    #region Fields

    // Pending notification bits (NotifyFlags). Internal so producers can Interlocked.Or it directly; all access
    // is atomic. Volatile for the plain reads, Interlocked for every read-modify-write.
    internal volatile int Flags;
    private readonly ISessionListener _Listener;
    private readonly ISessionReader _SessionReader;
    private readonly Job _Job;
    private readonly ManualResetEventSlim _Wake = new(initialState: false);
    // Tracks how far this listener has consumed announced packet ids (exclusive end of the last delivered range).
    // Written by the listener thread and reset to zero by the coordinator on a stack swap; see _DispatchFlags.
    private volatile int _PacketCursor;
    // 0 = OnUnsubscribed not yet invoked; 1 = invoked (RunLoop finally or coordinator fallback).
    private volatile int _OnUnsubscribedInvoked;
    // Pull filter for this listener. Evaluated only on the listener thread, but replaced by the
    // session coordinator thread during a stack swap, hence volatile.
    private volatile PacketFilter? _Filter;
    // Serialises matching reads and stack-swap rebind. Ingest does not take this gate.
    internal object FilterGate { get; } = new();
    // Set when a stack swap could not re-bind _Filter to the new stack. Matching reads then fail
    // with this error instead of silently degrading to "match everything".
    private volatile FilterError? _FilterFault;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates a listener slot that delivers notifications to <paramref name="listener"/>
    /// and pulls data from <paramref name="sessionReader"/>.
    /// The slot is not started until <see cref="Start"/> is called.
    /// </summary>
    internal ListenerSlot(
        JobId jobId,
        ISessionListener listener,
        ISessionReader sessionReader,
        Action<Job, JobStatus> onStatusChanged)
    {
        _Listener = listener;
        _SessionReader = sessionReader;
        // Independent, generation -1: a listener is never ended by a restart. It rebinds on StackChanged and keeps
        // delivering; waiting for it (or cancelling it) would stop the very notification that tells it about the new stack.
        _Job = new Job(
            jobId,
            listener.UiName,
            Invariant($"Listener: {listener.UiName}"),
            JobStackBinding.Independent,
            generation: -1,
            _RunLoop,
            onStatusChanged);
        // Create the public view once so the same reference can be registered
        // in the unified Session job list.
        Info = new JobInfo(_Job);
    }

    #endregion

    #region Identity

    /// <summary>The underlying job's identifier.</summary>
    internal JobId Id => _Job.Id;
    /// <summary>
    /// Public read-only view of this listener's job.
    /// Stored here so the same reference can be added to and removed from the
    /// unified <see cref="Session"/> job list.
    /// </summary>
    internal JobInfo Info
    {
        get;
    }
    /// <summary>
    /// The <see cref="ListenerInfo"/> associated with this slot.
    /// Set by <see cref="Session.TryAddListener(ISessionListener, PacketFilter?, out ListenerInfo?, out SessionFailure?)"/> after construction so that
    /// <see cref="Session.TryUnsubscribe"/> can locate the matching info without
    /// fragile name-based correlation.
    /// </summary>
    internal ListenerInfo? ListenerInfo
    {
        get; set;
    }
    /// <summary>User-visible listener name.</summary>
    internal string UiName => _Listener.UiName;

    #endregion

    #region Filter

    /// <summary>
    /// The filter this listener applies to <see cref="PacketReadMode.Matching"/> pulls.
    /// <see langword="null"/> means "no filter", which behaves exactly like
    /// <see cref="PacketFilter.AlwaysMatch"/> but skips even the always-match dispatch.
    /// </summary>
    internal PacketFilter? Filter => _Filter;

    /// <summary>
    /// The error from the last failed re-bind of <see cref="Filter"/> to a new stack, or
    /// <see langword="null"/> when the filter is usable.
    /// </summary>
    internal FilterError? FilterFault => _FilterFault;

    /// <summary>
    /// The filter instance <see cref="Precheck"/> was built for. Read and written only under <see cref="FilterGate"/>;
    /// a mismatch with <see cref="Filter"/> means the pre-check must be rebuilt.
    /// </summary>
    internal PacketFilter? PrecheckOwner { get; set; }

    /// <summary>
    /// The skip-parse pre-check of matching reads for <see cref="PrecheckOwner"/>, or <see langword="null"/> when that
    /// filter is not eligible. Read and written only under <see cref="FilterGate"/>.
    /// </summary>
    internal MatchingPrecheck? Precheck { get; set; }

    /// <summary>Installs the filter for this listener and clears any previous re-bind failure.</summary>
    /// <param name="filter">The filter, already bound to the session stack; <see langword="null"/> means no filter.</param>
    internal void SetFilter(PacketFilter? filter)
    {
        // The four fields describe one consistent filter state (the filter, its fault and the cached pre-check built
        // for it), so they change together under the gate that matching reads also hold. The section is a few
        // assignments: no I/O, no callbacks.
        lock (FilterGate)
        {
            _Filter = filter;
            _FilterFault = null;
            PrecheckOwner = null;
            Precheck = null;
        }
    }

    /// <summary>
    /// Drops the filter and records why it could not be re-bound. Dropping the instance is
    /// deliberate: its field ids belong to the retired stack, so evaluating it would compare
    /// against the wrong fields.
    /// </summary>
    internal void SetFilterFault(FilterError failure)
    {
        // Same consistency reasoning as SetFilter. With the filter null and the fault set, matching reads fail with
        // the fault instead of treating the missing filter as "no filter" (which would let every packet through).
        lock (FilterGate)
        {
            _Filter = null;
            _FilterFault = failure;
            PrecheckOwner = null;
            Precheck = null;
        }
    }

    #endregion

    #region Public API

    /// <summary>Starts the listener slot's background thread.</summary>
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

        // Thread creation failed — RunLoop never runs; coordinator must still notify.
        EnsureOnUnsubscribed();
        return false;
    }
    /// <summary>
    /// Invokes <see cref="ISessionListener.OnUnsubscribed"/> at most once.
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
    /// Requests cancellation of the listener slot and wakes the run loop. Safe on a disposed slot (shutdown may cancel a slot
    /// from its snapshot that a concurrent unsubscribe has already disposed).
    /// </summary>
    /// <param name="reason">Why the slot is ended; recorded before the token fires (see <see cref="Job.Cancel(JobEndReason)"/>).</param>
    internal void Cancel(JobEndReason reason)
    {
        _Job.Cancel(reason);
        _Wake.Set();
    }
    /// <summary>Current job status.</summary>
    internal JobStatus Status => _Job.Status;
    /// <summary>Blocks until the listener job reaches a terminal state.</summary>
    internal void Join() => _Job.Join();
    /// <inheritdoc/>
    public void Dispose()
    {
        _Wake.Dispose();
        _Job.Dispose();
    }
    // ── Notify helper (sets flag on this slot) ──────────────────────────────
    /// <summary>
    /// Sets the given flag(s) on this slot and wakes the listener thread. Non-blocking, thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Notify(NotifyFlags flags)
    {
        // Safe on a disposed slot: a source thread that took its slot snapshot before TryUnsubscribe removed the slot may
        // still notify it after the session disposed it. The flag word is plain memory, and Set() on a disposed
        // ManualResetEventSlim does not throw (it is only Wait and Reset that check disposal); a test pins this. No lock
        // is taken per frame to prevent the late call, and nobody can be waiting on the event any more (the slot is
        // disposed after its thread was joined).
        // Set the wake event only when none of these bits was already pending. A pending bit means the listener has
        // not consumed the flags yet (it clears them atomically before dispatching, and re-reads them after resetting
        // the event, see _RunLoop), so it will pick up this call's bits without another wakeup. Skipping the redundant
        // Set avoids taking the event's internal lock once per packet from the ingest thread.
        int prior = Interlocked.Or(ref Flags, (int)flags);
        if ((prior & (int)flags) == 0)
        {
            _Wake.Set();
        }
    }
    /// <summary>
    /// Resets the packet cursor to zero so the next <see cref="NotifyFlags.NewPackets"/>
    /// dispatch delivers all packets from the beginning. Called by
    /// <see cref="Session"/> during a stack-swap reparse before notifying listeners.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetPacketCursor()
        => _PacketCursor = 0;

    #endregion

    #region Private helpers

    /// <summary>
    /// The work delegate executed by the underlying <see cref="Job"/> thread.
    /// Waits on <see cref="_Wake"/> when no flags are pending, then dispatches to
    /// <see cref="ISessionListener"/> callbacks.
    ///
    /// <para>
    /// <b>Error handling:</b>
    /// Listener callback exceptions are not caught here. They propagate to
    /// <see cref="Job._RunCore"/> which stores the exception in
    /// <see cref="Job.FailureException"/> and transitions the job to
    /// <see cref="JobStatus.Failed"/>. A faulty listener auto-disconnects;
    /// other listeners and the session continue unaffected.
    /// </para>
    ///
    /// <para>
    /// <b>OnUnsubscribed:</b>
    /// Called in a <c>finally</c> block so the listener always receives
    /// its cleanup callback, even when a previous callback threw.
    /// </para>
    /// </summary>
    private void _RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Take every pending flag at once (merging repeated notifications) and clear them atomically, so a
                // producer that sets a flag during dispatch is seen on the next iteration instead of being lost.
                int flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                // Manual-reset: clear before wait so a stale Set() cannot spin. Lost-wakeup protection: a producer
                // that raised a flag after the Exchange above but set the event before this Reset would have its
                // wakeup erased, so the flags are read once more after the Reset. A producer that acts after the
                // second Exchange sets the event again, and the Wait below returns at once.
                _Wake.Reset();
                flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                // Cancellation also interrupts this wait, so the loop needs no timeout and the idle listener costs no CPU.
                // The interruption leaves the loop through the drain below instead of an exception: an exception would skip
                // the drain, and a flag raised just before the cancel (the last NewPackets window, ShuttingDown) would
                // be lost for a listener that was idle at that moment.
                if (!_WaitForWork(ct))
                {
                    break;
                }
            }

            // Drain once: whatever was raised before the cancellation took effect is delivered, whichever way the loop was
            // left (loop condition or interrupted wait). One pass bounds the exit; flags raised from here on belong to a
            // session that is ending and are not promised.
            int remaining = Interlocked.Exchange(ref Flags, 0);
            if (remaining != 0)
            {
                _DispatchFlags((NotifyFlags)remaining);
            }

            // The job maps this exception to the Cancelled status. Returning normally would end the job as Completed,
            // and only for the exit paths that happened to skip the exception; every cancelled slot ends Cancelled.
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // Best-effort: always attempt to notify the listener of unsubscription,
            // even if a previous callback threw. If OnUnsubscribed itself throws,
            // that exception propagates to RunCore (replacing any pending exception).
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
    /// Dispatches all set flags to the appropriate <see cref="ISessionListener"/> callbacks.
    /// Order of dispatch matches logical dependency: packets first, then sources,
    /// then jobs, then lifecycle events.
    ///
    /// <para>
    /// Exceptions are not caught — a faulty listener callback propagates through
    /// <see cref="_RunLoop"/> to <see cref="Job._RunCore"/>, which stores the exception
    /// in <see cref="Job.FailureException"/> and transitions the job to Failed.
    /// </para>
    /// </summary>
    private void _DispatchFlags(NotifyFlags notify)
    {
        // ── Stack swap (must precede Packets so cursor reset takes effect) ───
        if ((notify & NotifyFlags.StackChanged) != 0)
        {
            // Reset the cursor so the subsequent NewPackets branch delivers
            // all re-parsed packets from index 0. The coordinator resets it too (ResetPacketCursor), but an
            // in-flight NewPackets dispatch can store its end index after that reset; repeating the reset here, on
            // the thread that owns the cursor and before the packets branch below, makes the outcome independent
            // of that race.
            _PacketCursor = 0;
            _Listener.OnStackChanged(_SessionReader);
        }
        // ── Packets ──────────────────────────────────────────────
        if ((notify & NotifyFlags.NewPackets) != 0)
        {
            // Pull model: the flag only says "something arrived"; the range is derived from the session's current
            // count, so packets announced after the flag was raised are delivered in the same batch.
            int current = _SessionReader.PacketCount;
            int cursor = _PacketCursor;
            if (current > cursor)
            {
                _Listener.OnNewPackets(_SessionReader, cursor, current);
                _PacketCursor = current;
            }
        }
        // ── Sources ──────────────────────────────────────────────
        if ((notify & (NotifyFlags.SourceAdded | NotifyFlags.SourceCompleted)) != 0)
        {
            _Listener.OnSourcesChanged(_SessionReader);
        }
        if ((notify & NotifyFlags.AllSourcesCompleted) != 0)
        {
            _Listener.OnAllSourcesCompleted(_SessionReader);
        }
        // ── Jobs ─────────────────────────────────────────────────
        if ((notify & (NotifyFlags.JobAdded | NotifyFlags.JobStatusChanged | NotifyFlags.JobRemoved)) != 0)
        {
            _Listener.OnJobsChanged(_SessionReader);
        }
        // ── Session lifecycle ────────────────────────────────────
        if ((notify & NotifyFlags.PhaseChanged) != 0)
        {
            _Listener.OnPhaseChanged(_SessionReader.Phase);
        }
        if ((notify & NotifyFlags.ShuttingDown) != 0)
        {
            _Listener.OnShuttingDown();
        }
    }

    #endregion
}

