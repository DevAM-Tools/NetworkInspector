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
/// Consumer: <c>Interlocked.Exchange(ref _Flags, 0)</c> — reads all flags and clears atomically.
/// Producers also signal <see cref="ManualResetEventSlim"/> so the consumer blocks without polling.
/// </para>
///
/// <para>
/// Thread-safety: flag field and wake event are safe for concurrent producers and one consumer.
/// </para>
/// </summary>
internal sealed class ListenerSlot : IDisposable
{
    // ── Atomic flag field ────────────────────────────────────────────────────
    // Written by producers via Interlocked.Or, read+cleared by consumer via Interlocked.Exchange.
    // Public (internal) for direct OR access from source threads without method-call overhead.
    internal int Flags;
    private readonly ISessionListener _Listener;
    private readonly ISessionReader _SessionReader;
    private readonly Job _Job;
    private readonly ManualResetEventSlim _Wake = new(initialState: false);
    // Tracks how far this listener has consumed packets from the PacketStore.
    private long _PacketCursor;
    // 0 = OnUnsubscribed not yet invoked; 1 = invoked (RunLoop finally or coordinator fallback).
    private int _OnUnsubscribedInvoked;
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
        _Job = new Job(
            jobId,
            listener.UiName,
            $"Listener: {listener.UiName}",
            RunLoop,
            onStatusChanged);
        // Create the public view once so the same reference can be registered
        // in the unified Session job list.
        Info = new JobInfo(_Job);
    }
    // ── Identity ─────────────────────────────────────────────────────────────
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
    /// Set by <see cref="Session.TryAddListener"/> after construction so that
    /// <see cref="Session.TryUnsubscribe"/> can locate the matching info without
    /// fragile name-based correlation.
    /// </summary>
    internal ListenerInfo? ListenerInfo
    {
        get; set;
    }
    /// <summary>User-visible listener name.</summary>
    internal string UiName => _Listener.UiName;
    // ── Lifecycle ────────────────────────────────────────────────────────────
    /// <summary>Starts the listener slot's background thread.</summary>
    internal void Start()
    {
        try
        {
            _Job.Start();
        }
        catch
        {
            // Thread creation failed — RunLoop never runs; coordinator must still notify.
            EnsureOnUnsubscribed();
            throw;
        }
    }
    /// <summary>
    /// Invokes <see cref="ISessionListener.OnUnsubscribed"/> at most once.
    /// Used from <see cref="RunLoop"/> and from the session coordinator when <see cref="Job.Start"/> fails.
    /// </summary>
    internal void EnsureOnUnsubscribed()
    {
        if (Interlocked.CompareExchange(ref _OnUnsubscribedInvoked, 1, 0) == 0)
        {
            _Listener.OnUnsubscribed();
        }
    }
    /// <summary>Requests cancellation of the listener slot and wakes the run loop.</summary>
    internal void Cancel()
    {
        _Job.Cancel();
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
        Interlocked.Or(ref Flags, (int)flags);
        _Wake.Set();
    }
    /// <summary>
    /// Resets the packet cursor to zero so the next <see cref="NotifyFlags.NewPackets"/>
    /// dispatch delivers all packets from the beginning. Called by
    /// <see cref="Session"/> during a stack-swap reparse before notifying listeners.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetPacketCursor()
        => Volatile.Write(ref _PacketCursor, 0);
    // ── Event-driven dispatch loop ──────────────────────────────────────────
    /// <summary>
    /// The work delegate executed by the underlying <see cref="Job"/> thread.
    /// Waits on <see cref="_Wake"/> when no flags are pending, then dispatches to
    /// <see cref="ISessionListener"/> callbacks.
    ///
    /// <para>
    /// <b>Error handling:</b>
    /// Listener callback exceptions are not caught here. They propagate to
    /// <see cref="Job.RunCore"/> which stores the exception in
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
    private void RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                // Manual-reset: clear before wait so a stale Set() cannot spin.
                _Wake.Reset();
                flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                _Wake.Wait(ct);
            }
            // Drain: process any flags set between the last Exchange and cancellation.
            int remaining = Interlocked.Exchange(ref Flags, 0);
            if (remaining != 0)
            {
                DispatchFlags((NotifyFlags)remaining);
            }
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
    /// Dispatches all set flags to the appropriate <see cref="ISessionListener"/> callbacks.
    /// Order of dispatch matches logical dependency: packets first, then sources,
    /// then jobs, then lifecycle events.
    ///
    /// <para>
    /// Exceptions are not caught — a faulty listener callback propagates through
    /// <see cref="RunLoop"/> to <see cref="Job.RunCore"/>, which stores the exception
    /// in <see cref="Job.FailureException"/> and transitions the job to Failed.
    /// </para>
    /// </summary>
    private void DispatchFlags(NotifyFlags notify)
    {
        // ── Stack swap (must precede Packets so cursor reset takes effect) ───
        if ((notify & NotifyFlags.StackChanged) != 0)
        {
            // Reset the cursor so the subsequent NewPackets branch delivers
            // all re-parsed packets from index 0.
            Volatile.Write(ref _PacketCursor, 0);
            _Listener.OnStackChanged(_SessionReader);
        }
        // ── Packets ──────────────────────────────────────────────
        if ((notify & NotifyFlags.NewPackets) != 0)
        {
            long current = _SessionReader.PacketCount;
            long cursor = Volatile.Read(ref _PacketCursor);
            if (current > cursor)
            {
                _Listener.OnNewPackets(_SessionReader, cursor, current);
                Volatile.Write(ref _PacketCursor, current);
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
}

