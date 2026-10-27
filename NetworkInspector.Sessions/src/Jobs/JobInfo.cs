// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Public read-only view of a running or completed session job.
/// Thin wrapper around <see cref="Job"/> — all reads are lock-free.
/// <para>
/// Why a wrapper: <see cref="Job"/> is internal and also exposes lifecycle members (<c>Start</c>, <c>Dispose</c>) that
/// only the session may call. This view hands out just the observable state plus cancel and join, so a caller cannot
/// start or dispose a job it does not own.
/// </para>
/// <para>
/// Thread safety: safe to use from any thread; every member delegates to the job's volatile / interlocked state.
/// </para>
/// </summary>
public sealed class JobInfo
{
    #region Fields

    private readonly Job _Job;

    #endregion

    /// <summary>Creates a <see cref="JobInfo"/> that wraps the given internal job.</summary>
    /// <param name="job">The job to expose. Not validated: only the session creates instances.</param>
    internal JobInfo(Job job)
    {
        _Job = job;
    }

    #region Properties

    /// <summary>The wrapped job, for the session that owns it (start, cancel, dispose). Not part of the public view.</summary>
    internal Job Job => _Job;

    /// <summary>Unique job identifier within the session.</summary>
    public JobId Id => _Job.Id;

    /// <summary>User-visible job name.</summary>
    public string UiName => _Job.UiName;

    /// <summary>Human-readable description of what the job does.</summary>
    public string Description => _Job.Description;

    /// <summary>Current execution status. Volatile read — always current.</summary>
    public JobStatus Status => _Job.Status;

    /// <summary>
    /// Whether a restart ends this job. <see cref="JobStackBinding.StackBound"/> for every job added without a binding and
    /// for none of the session's own slots; <see cref="JobStackBinding.Independent"/> for sources, listeners, value caches
    /// and restart-event subscriptions.
    /// </summary>
    public JobStackBinding Binding => _Job.Binding;

    /// <summary>
    /// The session generation the job was created in (see <see cref="ISessionReader.Generation"/>); -1 for an
    /// <see cref="JobStackBinding.Independent"/> job, which belongs to no generation.
    /// </summary>
    public int Generation => _Job.Generation;

    /// <summary>
    /// Why the job ended; <see cref="JobEndReason.None"/> while it is pending or running. Stored before the terminal
    /// <see cref="Status"/> is published, so a snapshot that reads the status first and the reason second never sees a
    /// terminal status with <see cref="JobEndReason.None"/>.
    /// </summary>
    public JobEndReason EndReason => _Job.EndReason;

    /// <summary>When the job thread started. Null if not yet started.</summary>
    public DateTimeOffset? StartTime => _Job.StartTime;

    /// <summary>When the job thread ended. Null if still running.</summary>
    public DateTimeOffset? EndTime => _Job.EndTime;

    /// <summary>
    /// Exception that caused job failure, if any. It is the original exception object and may carry internal
    /// details; do not forward it to untrusted parties.
    /// </summary>
    public Exception? FailureException => _Job.FailureException;

    #endregion

    /// <summary>
    /// Requests cancellation of the job's work delegate. Thread-safe. The request is cooperative: the job reaches
    /// <see cref="JobStatus.Cancelled"/> only when its work observes the token; use <see cref="Join"/> to wait for it.
    /// The reason is recorded as <see cref="JobEndReason.UserCancelled"/> (a caller of this view is a user of the job); a
    /// restart or the shutdown records its own reason before it cancels, and the first reason wins.
    /// </summary>
    public void Cancel() => _Job.Cancel(JobEndReason.UserCancelled);

    /// <summary>
    /// Blocks the calling thread until the job reaches a terminal state
    /// (<see cref="JobStatus.Completed"/>, <see cref="JobStatus.Cancelled"/>,
    /// or <see cref="JobStatus.Failed"/>).
    /// Returns immediately if the job is already in a terminal state.
    /// </summary>
    public void Join() => _Job.Join();
}
