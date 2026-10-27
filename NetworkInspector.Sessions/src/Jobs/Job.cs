// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Represents a single unit of work in the session.
/// Each job runs on its own dedicated background thread.
///
/// <para>
/// Thread-safety: all observable state changes (status, timestamps) use
/// <see cref="Volatile"/> / <see cref="Interlocked"/> — no external locking required.
/// Terminal completion is signalled via <see cref="ManualResetEventSlim"/> so
/// <see cref="Join()"/> does not poll with timed sleeps.
/// </para>
/// </summary>
internal sealed class Job : IDisposable
{
    #region Fields

    private readonly Action<CancellationToken> _Work;
    private readonly CancellationTokenSource _Cts = new();
    // Captured once. Shutdown with a timeout may dispose this job while its thread still runs; reading
    // _Cts.Token after that throws ObjectDisposedException, so the cancel check compares against this copy.
    private readonly CancellationToken _Token;
    private readonly Action<Job, JobStatus> _OnStatusChanged;
    private readonly ManualResetEventSlim _Completed = new(initialState: false);
    // Status stored as int for Volatile/Interlocked compatibility.
    private volatile int _Status = (int)JobStatus.Pending;
    // CAS guard: 0 = not started, 1 = Start() called.
    // Prevents double-start from concurrent callers (e.g. TryAddListener
    // and Shutdown racing to Start the same ListenerSlot).
    private volatile int _StartAttempted;
    // Timestamps stored as separate value + flag to support Volatile semantics.
    // Volatile.Read/Write do not have overloads for Nullable<T> (value type).
    // The write sequence is: store value first, then Volatile.Write the flag (release).
    // The read sequence: Volatile.Read the flag (acquire), then read value.
    // This guarantees visibility through the acquire/release memory barrier pair.
    private DateTimeOffset _StartTimeValue;
    private volatile int _StartTimeSet;  // 0 = not set, 1 = set
    private DateTimeOffset _EndTimeValue;
    private volatile int _EndTimeSet;    // 0 = not set, 1 = set
    private volatile Exception? _FailureException;
    // 0 = live, 1 = Dispose ran. Read by Cancel on any thread; see Cancel.
    private volatile int _DisposedFlag;
    // Why the job ended (JobEndReason as int). Stored BEFORE the terminal status is published, with the same release rule as
    // the end time: a snapshot that sees Cancelled must already see StackReplaced or UserCancelled, otherwise an owner could
    // re-create a job that the user just cancelled. The writers are the job thread (Completed, Failed, the default for a
    // cancel without a reason) and whoever cancels (restart, unsubscribe, shutdown), which store their reason before they
    // cancel the token; the first reason wins (CompareExchange from None), except Failed, which always wins (see _RunCore).
    // Rejected: deriving the reason from the status (a Cancelled job could not tell who cancelled it) and a second lock around
    // status and reason (the status is read lock-free on every snapshot).
    private volatile int _EndReason = (int)JobEndReason.None;
    // The thread that runs the work; set by TryStart before the thread starts. Read by the restart to refuse a restart that is
    // called from a stack-bound job's own thread (under WaitThenCancel it would wait for itself, under CancelNow it would
    // cancel itself). Volatile: written by the starter, read by any thread.
    private volatile Thread? _Thread;

    #endregion

    #region Lifecycle

    /// <summary>Creates a job with the given identity, stack binding and work delegate.</summary>
    /// <param name="id">The job's identifier.</param>
    /// <param name="uiName">User-visible name.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="binding">Whether a restart ends this job; see <see cref="JobStackBinding"/>.</param>
    /// <param name="generation">The session generation the job was created in; -1 for an independent job.</param>
    /// <param name="work">The work; observes the token it receives.</param>
    /// <param name="onStatusChanged">Told after every status change; must not throw.</param>
    internal Job(
        JobId id,
        string uiName,
        string description,
        JobStackBinding binding,
        int generation,
        Action<CancellationToken> work,
        Action<Job, JobStatus> onStatusChanged)
    {
        Id = id;
        UiName = uiName;
        Description = description;
        Binding = binding;
        Generation = generation;
        _Work = work;
        _OnStatusChanged = onStatusChanged;
        _Token = _Cts.Token;
    }

    #endregion

    #region Identity

    /// <summary>Unique job identifier within the session.</summary>
    internal JobId Id
    {
        get;
    }
    /// <summary>User-visible job name.</summary>
    internal string UiName
    {
        get;
    }
    /// <summary>Human-readable description of what the job does.</summary>
    internal string Description
    {
        get;
    }

    /// <summary>Whether a restart ends this job.</summary>
    internal JobStackBinding Binding { get; }

    /// <summary>The session generation the job was created in; -1 for an <see cref="JobStackBinding.Independent"/> job.</summary>
    internal int Generation { get; }

    #endregion

    #region Observable state

    /// <summary>Current execution status. Volatile read — always current.</summary>
    internal JobStatus Status => (JobStatus)_Status;

    /// <summary>Why the job ended; <see cref="JobEndReason.None"/> until it did. Stored before the terminal status.</summary>
    internal JobEndReason EndReason => (JobEndReason)_EndReason;

    /// <summary>Whether the calling thread is the thread that runs this job's work.</summary>
    internal bool IsCurrentThread => ReferenceEquals(_Thread, Thread.CurrentThread);
    /// <summary>When the job thread started. Null if not yet started.</summary>
    internal DateTimeOffset? StartTime
    {
        get
        {
            if (_StartTimeSet != 0)
            {
                return _StartTimeValue;
            }

            return null;
        }
    }
    /// <summary>When the job thread ended. Null if still running.</summary>
    internal DateTimeOffset? EndTime
    {
        get
        {
            if (_EndTimeSet != 0)
            {
                return _EndTimeValue;
            }

            return null;
        }
    }
    /// <summary>Exception that caused job failure, if any.</summary>
    internal Exception? FailureException => _FailureException;

    #endregion

    #region Public API

    /// <summary>
    /// Starts the job on a new background thread.
    /// Transitions status from <see cref="JobStatus.Pending"/> to <see cref="JobStatus.Running"/>.
    /// If <see cref="Thread.Start()"/> throws (e.g. <see cref="OutOfMemoryException"/>),
    /// the job is immediately transitioned to <see cref="JobStatus.Failed"/> so that
    /// callers of <see cref="Join()"/> never block indefinitely.
    /// The exception is re-thrown so the session coordinator can propagate the failure.
    ///
    /// <para>
    /// <b>Double-start guard:</b>
    /// Uses a CAS on <see cref="_StartAttempted"/> so that concurrent Start() calls
    /// (e.g. TryAddListener and Shutdown racing on the same ListenerSlot) are safe.
    /// The second caller returns immediately; the first caller's thread wins.
    /// </para>
    /// </summary>
    internal void Start()
    {
        if (!TryStart(out Exception? failure))
        {
            // Rethrow the recorded start failure with its original stack, as the previous in-place `throw;` did.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    /// Starts the job like <see cref="Start"/>, but reports a thread that could not be started as a return value instead of
    /// an exception. The failure is already recorded on the job (<see cref="FailureException"/>, status
    /// <see cref="JobStatus.Failed"/>, status callback raised), so a caller that has nothing more to do with it can continue
    /// with its other jobs without a catch block.
    /// </summary>
    /// <param name="failure">The exception that stopped the thread from starting; <see langword="null"/> on success.</param>
    /// <returns>
    /// <see langword="true"/> when the job was started by this call or had been started before; <see langword="false"/>
    /// when this call tried to start the thread and failed.
    /// </returns>
    internal bool TryStart([NotNullWhen(false)] out Exception? failure)
    {
        // CAS guard: only the first caller proceeds. Concurrent or repeated
        // calls return immediately without creating a second thread.
        if (Interlocked.CompareExchange(ref _StartAttempted, 1, 0) != 0)
        {
            failure = null;
            return true;
        }
        Thread thread = new(_RunCore)
        {
            Name = $"session-job-{Id.Value}-{UiName}",
            IsBackground = true,
            CurrentCulture = CultureInfo.InvariantCulture,
        };
        // Published before Start(): a restart that runs on this very thread must be recognized from the first instruction
        // of the work (see IsCurrentThread).
        _Thread = thread;
        // Set start time before transitioning to Running so that observers
        // never see Status==Running with a null StartTime.
        // Running is also published before the thread starts: a very short job could otherwise reach a terminal
        // status first, and the late Running write would then overwrite it (and Join would wait forever).
        _StartTimeValue = DateTimeOffset.UtcNow;
        _StartTimeSet = 1;
        _SetStatus(JobStatus.Running);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // thread.Start() failed — the thread never ran and never will. Any exception is possible here (resource
            // exhaustion above all), and none may leave the job in Running. Transition to Failed immediately so
            // WaitForCompletion() does not block; the exception is recorded and handed to the caller, not swallowed.
            _FailureException = ex;
            _ = Interlocked.Exchange(ref _EndReason, (int)JobEndReason.Failed);
            _EndTimeValue = DateTimeOffset.UtcNow;
            _EndTimeSet = 1;
            _SetStatus(JobStatus.Failed);
            failure = ex;
            return false;
        }

        failure = null;
        return true;
    }
    /// <summary>Requests cancellation. The job's work delegate observes this via its
    /// <see cref="CancellationToken"/>. Thread-safe. After <see cref="Dispose"/> the call does nothing: the session
    /// disposes a job at shutdown while callers still hold its <see cref="JobInfo"/>, and a concurrent unsubscribe may
    /// cancel a job that shutdown just disposed; neither may fail on the disposed token source.
    /// </summary>
    internal void Cancel()
    {
        // A cancel without a reason (a caller that holds the Job directly) records no reason here; the job thread falls back
        // to UserCancelled when it observes the token (see _RunCore).
        // A disposed job is over (Dispose runs after Join, or after shutdown already cancelled it), so there is nothing
        // left to cancel and no caller has to be told.
        if (_DisposedFlag != 0)
        {
            return;
        }

        try
        {
            _Cts.Cancel();
        }
        catch (ObjectDisposedException) when (_DisposedFlag != 0)
        {
            // Dispose ran between the check above and the cancel. Same outcome as the early return; any other
            // ObjectDisposedException (the filter is false) is a bug and propagates.
            return;
        }
    }
    /// <summary>
    /// Cancels the job and records why. The reason is stored first, then the token fires, so whoever observes the cancelled
    /// job (its thread, a snapshot of the job list) already sees the reason. The first reason wins: a restart that cancels a
    /// job before the shutdown does leaves <see cref="JobEndReason.StackReplaced"/> in place.
    /// </summary>
    /// <param name="reason">Why the job is ended; must not be <see cref="JobEndReason.None"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is <see cref="JobEndReason.None"/>: a cancel always has a cause.</exception>
    internal void Cancel(JobEndReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reason, JobEndReason.None);
        SetEndReason(reason);
        Cancel();
    }

    /// <summary>
    /// Records why the job is being ended; the first reason wins. Thread-safe; safe on a disposed job (a plain field).
    /// </summary>
    internal void SetEndReason(JobEndReason reason)
        => _ = Interlocked.CompareExchange(ref _EndReason, (int)reason, (int)JobEndReason.None);

    /// <summary>
    /// Blocks the calling thread until the job reaches a terminal state
    /// (<see cref="JobStatus.Completed"/>, <see cref="JobStatus.Cancelled"/>,
    /// or <see cref="JobStatus.Failed"/>).
    /// Returns immediately if the job is already in a terminal state.
    /// </summary>
    internal void Join()
    {
        if (_IsTerminal(Status))
        {
            return;
        }
        _Completed.Wait();
    }
    /// <summary>
    /// Blocks until the job reaches a terminal state or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns><see langword="true"/> when the job finished before the timeout.</returns>
    internal bool Join(TimeSpan timeout)
    {
        if (_IsTerminal(Status))
        {
            return true;
        }
        return _Completed.Wait(timeout);
    }
    /// <summary>
    /// Disposes wait handles and the <see cref="CancellationTokenSource"/>. The session may dispose a job while its
    /// thread is still running (shutdown with a timeout), which is why the thread uses a token captured at
    /// construction and why <see cref="Join()"/> must not be used after disposal.
    /// </summary>
    public void Dispose()
    {
        // The flag is raised first, so a Cancel that races with this call takes the early return or the filtered catch.
        // The exchange also makes a repeated Dispose a no-op.
        if (Interlocked.Exchange(ref _DisposedFlag, 1) != 0)
        {
            return;
        }

        _Completed.Dispose();
        _Cts.Dispose();
    }

    #endregion

    #region Private helpers

    private static bool _IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed;

    /// <summary>
    /// Entry point for the job thread. Executes the work delegate and updates
    /// status regardless of outcome.
    /// </summary>
    private void _RunCore()
    {
        // Every branch stores end time (and the failure exception) BEFORE publishing the terminal status, because
        // _SetStatus releases waiters; a thread woken by Join must see them already set.
        // The end reason is stored with the end time, before the status, for the same reason: _SetStatus releases waiters.
        try
        {
            _Work(_Token);
            SetEndReason(JobEndReason.Completed);
            _EndTimeValue = DateTimeOffset.UtcNow;
            _EndTimeSet = 1;
            _SetStatus(JobStatus.Completed);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == _Token)
        {
            // Only treat as Cancelled when this job's own token was triggered.
            // An OperationCanceledException from a different token (e.g. an external
            // token inside the work delegate) is a genuine failure and falls through
            // to the Exception catch below.
            // A cancel that carried no reason (a caller that holds the JobInfo, which cancels without one) is a user cancel.
            SetEndReason(JobEndReason.UserCancelled);
            _EndTimeValue = DateTimeOffset.UtcNow;
            _EndTimeSet = 1;
            _SetStatus(JobStatus.Cancelled);
        }
        catch (Exception ex)
        {
            // Top-level boundary of a dedicated job thread: an unhandled exception here would take the process down,
            // so it is captured and exposed as the Failed status plus FailureException instead of being rethrown.
            // Failed overwrites an earlier request reason: a job that a restart asked to end and that then threw (for example
            // on a stack that was disposed under it) failed, and its owner must see that, not "stack replaced".
            _FailureException = ex;
            _ = Interlocked.Exchange(ref _EndReason, (int)JobEndReason.Failed);
            _EndTimeValue = DateTimeOffset.UtcNow;
            _EndTimeSet = 1;
            _SetStatus(JobStatus.Failed);
        }
    }
    /// <summary>
    /// Atomically updates the status and notifies the session coordinator.
    /// The callback is <see cref="Session._OnJobStatusChanged"/> which only performs
    /// <c>Interlocked.Or</c> on listener slots — it cannot throw.
    /// </summary>
    private void _SetStatus(JobStatus status)
    {
        Interlocked.Exchange(ref _Status, (int)status);
        if (_IsTerminal(status))
        {
            // The session may have disposed this job already (shutdown with a timeout while the thread still runs). Setting
            // a disposed ManualResetEventSlim does not throw (it only needs the event's state word; nobody can be waiting
            // on a disposed event), so the late terminal status is published without a guard. A test pins this.
            _Completed.Set();
        }
        _OnStatusChanged(this, status);
    }

    #endregion
}

