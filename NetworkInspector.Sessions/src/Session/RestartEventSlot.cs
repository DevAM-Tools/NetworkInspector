// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// One subscription of <see cref="ISession.Restarted"/>: its handler, its own queue of <see cref="RestartNotification"/>s,
/// and its own thread (a session job), so that handlers never influence each other and never run on the restarting thread
/// or under a session lock.
///
/// <para>
/// <b>Why a slot per handler, not one dispatcher:</b> a handler that blocks (a host that waits for its UI) would delay every
/// other handler on a shared thread, and a handler that throws must fail only itself. A thread per subscription is the model
/// <see cref="ListenerSlot"/> already uses; its cost is one idle thread per subscription, visible as the job
/// <c>restart-event</c> in <c>GetJobs()</c> so a host can count it. Why a queue and not a single slot: two restarts before the
/// handler ran must both be seen (a host that compares generations needs each report). Rejected: the thread pool (a
/// long-blocking host handler would occupy pool threads, and ordering per handler would need a serial executor anyway) and a
/// bounded queue that drops (a dropped notification is a generation the host never learns about).
/// </para>
///
/// <para>
/// <b>Queue limit (Section 4.2):</b> unbounded, one entry per restart the handler has not consumed. Threat: a handler that
/// never returns accumulates its own notifications. Class TUNE, changeable only in code; posture n/a: the growth is bounded
/// by the number of restarts, which are rare and serialized (one at a time, each re-parses the whole capture), and no peer can
/// raise it faster than a restart takes. Shutdown drains the queue within its time budget and counts what was not delivered.
/// Residual risk: a handler blocked for ever holds a few hundred bytes per restart until the session ends.
/// </para>
///
/// <para>
/// <b>Thread-safety:</b> <see cref="Post"/> may be called from any thread (a lock-guarded enqueue, then a wake); the handler
/// runs on this slot's job thread only, one notification at a time, in generation order. A handler exception ends the job
/// <see cref="JobStatus.Failed"/> with the exception (the top-level boundary of a dedicated thread, the same contract as a
/// listener callback) and the slot stops receiving; other slots are untouched.
/// </para>
/// </summary>
internal sealed class RestartEventSlot : IDisposable
{
    #region Fields

    private readonly Action<RestartNotification> _Handler;

    // Guards _Pending. A short section: an enqueue, a dequeue, a count; no I/O, no callback, no await inside.
    private readonly Lock _Gate = new();
    private readonly Queue<RestartNotification> _Pending = new();
    private readonly ManualResetEventSlim _Wake = new(initialState: false);
    private readonly Job _Job;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates the slot. It is not started until <see cref="TryStart"/> is called.
    /// </summary>
    /// <param name="jobId">The job id of the slot's thread.</param>
    /// <param name="handler">The subscriber; runs on the slot's thread.</param>
    /// <param name="onStatusChanged">Told after every status change of the slot's job (the session raises <c>JobStatusChanged</c>).</param>
    internal RestartEventSlot(JobId jobId, Action<RestartNotification> handler, Action<Job, JobStatus> onStatusChanged)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(onStatusChanged);
        _Handler = handler;

        // Independent, generation -1: a restart never ends the subscription whose job it reports to.
        _Job = new Job(
            jobId,
            "restart-event",
            "Restart event handler",
            JobStackBinding.Independent,
            generation: -1,
            _RunLoop,
            onStatusChanged);
        Info = new JobInfo(_Job);
    }

    #endregion

    #region Identity

    /// <summary>Public read-only view of the slot's job; the same reference is registered in the session's job list.</summary>
    internal JobInfo Info { get; }

    /// <summary>Whether this slot was created for <paramref name="handler"/> (delegate equality, as <c>-=</c> uses).</summary>
    internal bool Handles(Action<RestartNotification> handler) => _Handler.Equals(handler);

    /// <summary>Whether <paramref name="job"/> is this slot's job.</summary>
    internal bool Owns(JobInfo job) => ReferenceEquals(Info, job);

    /// <summary>Whether the calling thread is the thread of the handler (it must not join it).</summary>
    internal bool IsHandlerThread => _Job.IsCurrentThread;

    /// <summary>The slot's job status.</summary>
    internal JobStatus Status => _Job.Status;

    /// <summary>Notifications posted and not yet delivered. A test seam and the shutdown report.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_Gate)
            {
                return _Pending.Count;
            }
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Queues <paramref name="notification"/> and wakes the handler's thread. Never blocks on the handler; ignored once the
    /// handler's job has ended (a failed or cancelled subscription receives nothing more).
    /// </summary>
    internal void Post(RestartNotification notification)
    {
        // A slot whose job already ended (the handler threw, or it was cancelled) would only grow a queue nobody drains.
        if (_Job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)
        {
            return;
        }

        lock (_Gate)
        {
            _Pending.Enqueue(notification);
        }

        // Set after the enqueue, outside the lock: the loop re-checks the queue after it resets the event, so a wake that
        // races with the loop's last check is not lost (see _RunLoop). Safe on a disposed slot: Set on a disposed
        // ManualResetEventSlim does not throw, and nobody waits on it any more.
        _Wake.Set();
    }

    /// <summary>Starts the slot's thread. A failed start is recorded on the job (<see cref="JobStatus.Failed"/>) and returned.</summary>
    internal bool TryStart([NotNullWhen(false)] out Exception? failure) => _Job.TryStart(out failure);

    /// <summary>
    /// Requests the end of the subscription and wakes the loop. The loop delivers what is queued, then exits.
    /// </summary>
    /// <param name="reason">Why the slot is ended; recorded before the token fires.</param>
    internal void Cancel(JobEndReason reason)
    {
        _Job.Cancel(reason);
        _Wake.Set();
    }

    /// <summary>Waits until the slot's job is terminal or <paramref name="timeout"/> elapsed.</summary>
    /// <returns><see langword="true"/> when the job ended.</returns>
    internal bool Join(TimeSpan timeout) => _Job.Join(timeout);

    /// <summary>Waits without limit until the slot's job is terminal.</summary>
    internal void Join() => _Job.Join();

    /// <inheritdoc/>
    /// <remarks>The session disposes a slot only after its thread reached a terminal state, or after the shutdown budget ran out.</remarks>
    public void Dispose()
    {
        _Wake.Dispose();
        _Job.Dispose();
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// The work of the slot's job: deliver queued notifications one by one, then wait for the next one.
    /// </summary>
    /// <remarks>
    /// Lost-wakeup safe, like <c>ListenerSlot</c>: the event is reset BEFORE the final queue check, so a producer that enqueued
    /// after the drain but set the event before the reset is seen by the check, and one that acts after the check sets the
    /// event again and the wait returns at once. Cancellation is observed between notifications: a drain in progress
    /// completes (shutdown delivers what is queued within its budget); a handler that never returns keeps the job Running,
    /// like a listener that ignores cancellation. The loop never touches the event after it saw the cancellation, because the
    /// session may dispose the slot while a stuck handler is still running (shutdown with a spent budget).
    /// </remarks>
    private void _RunLoop(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_MayDeliver(cancellationToken) && _TryDequeue(out RestartNotification? notification))
            {
                // A handler exception is not caught here: it ends this job as Failed (Job._RunCore is the top-level
                // boundary of the thread) and leaves every other slot and the session untouched.
                _Handler(notification);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _Wake.Reset();
            if (PendingCount > 0)
            {
                continue;
            }

            try
            {
                _Wake.Wait(cancellationToken);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                // Expected: this slot's own cancellation. The loop head drains what was queued before the cancellation.
            }
        }

        // The job maps this exception to the Cancelled status (a normal return would end it Completed).
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Whether the loop may hand another queued notification to the handler. Always before the cancellation; after it only
    /// for a session shutdown, which promises to deliver what is queued within its budget. A subscriber that was removed
    /// (<see cref="JobEndReason.UserCancelled"/>) asked for no more calls, so the rest of its queue is dropped.
    /// </summary>
    private bool _MayDeliver(CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested || _Job.EndReason == JobEndReason.SessionShutdown;

    /// <summary>Dequeues the next notification under the gate.</summary>
    private bool _TryDequeue([NotNullWhen(true)] out RestartNotification? notification)
    {
        lock (_Gate)
        {
            return _Pending.TryDequeue(out notification);
        }
    }

    #endregion
}
