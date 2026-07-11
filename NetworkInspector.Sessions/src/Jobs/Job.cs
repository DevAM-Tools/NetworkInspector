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
    private readonly Action<Job, JobStatus> _OnStatusChanged;
    private readonly ManualResetEventSlim _Completed = new(initialState: false);
    // Status stored as int for Volatile/Interlocked compatibility.
    private int _Status = (int)JobStatus.Pending;
    // CAS guard: 0 = not started, 1 = Start() called.
    // Prevents double-start from concurrent callers (e.g. TryAddListener
    // and Shutdown racing to Start the same ListenerSlot).
    private int _StartAttempted;
    // Timestamps stored as separate value + flag to support Volatile semantics.
    // Volatile.Read/Write do not have overloads for Nullable<T> (value type).
    // The write sequence is: store value first, then Volatile.Write the flag (release).
    // The read sequence: Volatile.Read the flag (acquire), then read value.
    // This guarantees visibility through the acquire/release memory barrier pair.
    private DateTimeOffset _StartTimeValue;
    private int _StartTimeSet;  // 0 = not set, 1 = set
    private DateTimeOffset _EndTimeValue;
    private int _EndTimeSet;    // 0 = not set, 1 = set
    private Exception? _FailureException;

    #endregion

    #region Lifecycle

    /// <summary>Creates a job with the given identity and work delegate.</summary>
    internal Job(
        JobId id,
        string uiName,
        string description,
        Action<CancellationToken> work,
        Action<Job, JobStatus> onStatusChanged)
    {
        Id = id;
        UiName = uiName;
        Description = description;
        _Work = work;
        _OnStatusChanged = onStatusChanged;
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

    #endregion

    #region Observable state

    /// <summary>Current execution status. Volatile read — always current.</summary>
    internal JobStatus Status => (JobStatus)Volatile.Read(ref _Status);
    /// <summary>When the job thread started. Null if not yet started.</summary>
    internal DateTimeOffset? StartTime
    {
        get
        {
            if (Volatile.Read(ref _StartTimeSet) != 0)
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
            if (Volatile.Read(ref _EndTimeSet) != 0)
            {
                return _EndTimeValue;
            }

            return null;
        }
    }
    /// <summary>Exception that caused job failure, if any.</summary>
    internal Exception? FailureException => Volatile.Read(ref _FailureException!);

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
        // CAS guard: only the first caller proceeds. Concurrent or repeated
        // calls return immediately without creating a second thread.
        if (Interlocked.CompareExchange(ref _StartAttempted, 1, 0) != 0)
        {
            return;
        }
        Thread thread = new(_RunCore)
        {
            Name = $"session-job-{Id.Value}-{UiName}",
            IsBackground = true,
            CurrentCulture = CultureInfo.InvariantCulture,
        };
        // Set start time before transitioning to Running so that observers
        // never see Status==Running with a null StartTime.
        _StartTimeValue = DateTimeOffset.UtcNow;
        Volatile.Write(ref _StartTimeSet, 1);
        _SetStatus(JobStatus.Running);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // thread.Start() failed — the thread never ran and never will.
            // Transition to Failed immediately so WaitForCompletion() does not block.
            Volatile.Write(ref _FailureException, ex);
            _EndTimeValue = DateTimeOffset.UtcNow;
            Volatile.Write(ref _EndTimeSet, 1);
            _SetStatus(JobStatus.Failed);
            throw;
        }
    }
    /// <summary>Requests cancellation. The job's work delegate observes this via its
    /// <see cref="CancellationToken"/>. Thread-safe.
    /// </summary>
    internal void Cancel() => _Cts.Cancel();
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
    /// <summary>Disposes wait handles and the <see cref="CancellationTokenSource"/>.</summary>
    public void Dispose()
    {
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
        try
        {
            _Work(_Cts.Token);
            _EndTimeValue = DateTimeOffset.UtcNow;
            Volatile.Write(ref _EndTimeSet, 1);
            _SetStatus(JobStatus.Completed);
        }
        catch (OperationCanceledException oce) when (oce.CancellationToken == _Cts.Token)
        {
            // Only treat as Cancelled when this job's own token was triggered.
            // An OperationCanceledException from a different token (e.g. an external
            // token inside the work delegate) is a genuine failure and falls through
            // to the Exception catch below.
            _EndTimeValue = DateTimeOffset.UtcNow;
            Volatile.Write(ref _EndTimeSet, 1);
            _SetStatus(JobStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _FailureException, ex);
            _EndTimeValue = DateTimeOffset.UtcNow;
            Volatile.Write(ref _EndTimeSet, 1);
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
            _Completed.Set();
        }
        _OnStatusChanged(this, status);
    }

    #endregion
}

