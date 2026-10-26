// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Execution status of a session <see cref="Job"/>.
/// Transitions are monotonically forward (Pending → Running → terminal state).
/// <see cref="Completed"/>, <see cref="Cancelled"/> and <see cref="Failed"/> are the terminal states: once one is
/// reached the job never changes status again, waiters are released, and the job can be removed from the session.
/// </summary>
public enum JobStatus
{
    #region Values

    /// <summary>Job created but not yet started.</summary>
    Pending,

    /// <summary>Job thread is running.</summary>
    Running,

    /// <summary>Job completed successfully.</summary>
    Completed,

    /// <summary>Job was cancelled via its <see cref="CancellationToken"/>.</summary>
    Cancelled,

    /// <summary>Job terminated due to an unhandled exception.</summary>
    Failed,

    #endregion
}
