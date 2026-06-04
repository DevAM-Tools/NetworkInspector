// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Execution status of a session <see cref="Job"/>.
/// Transitions are monotonically forward (Pending → Running → terminal state).
/// </summary>
public enum JobStatus
{
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
}
