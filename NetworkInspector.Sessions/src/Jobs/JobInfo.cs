// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Public read-only view of a running or completed session job.
/// Thin wrapper around <see cref="Job"/> — all reads are lock-free.
/// </summary>
public sealed class JobInfo
{
    private readonly Job _Job;

    /// <summary>Creates a <see cref="JobInfo"/> that wraps the given internal job.</summary>
    internal JobInfo(Job job)
    {
        _Job = job;
    }

    /// <summary>Unique job identifier within the session.</summary>
    public JobId Id => _Job.Id;

    /// <summary>User-visible job name.</summary>
    public string UiName => _Job.UiName;

    /// <summary>Human-readable description of what the job does.</summary>
    public string Description => _Job.Description;

    /// <summary>Current execution status. Volatile read — always current.</summary>
    public JobStatus Status => _Job.Status;

    /// <summary>When the job thread started. Null if not yet started.</summary>
    public DateTimeOffset? StartTime => _Job.StartTime;

    /// <summary>When the job thread ended. Null if still running.</summary>
    public DateTimeOffset? EndTime => _Job.EndTime;

    /// <summary>Exception that caused job failure, if any.</summary>
    public Exception? FailureException => _Job.FailureException;

    /// <summary>Requests cancellation. Thread-safe.</summary>
    public void Cancel() => _Job.Cancel();

    /// <summary>
    /// Blocks the calling thread until the job reaches a terminal state
    /// (<see cref="JobStatus.Completed"/>, <see cref="JobStatus.Cancelled"/>,
    /// or <see cref="JobStatus.Failed"/>).
    /// Returns immediately if the job is already in a terminal state.
    /// </summary>
    public void Join() => _Job.Join();
}
