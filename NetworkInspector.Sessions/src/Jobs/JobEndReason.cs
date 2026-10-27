// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Why a job ended, readable together with its <see cref="JobInfo.Status"/>. The reason is stored before the terminal status
/// is published, so a snapshot that sees <see cref="JobStatus.Cancelled"/> never sees <see cref="None"/>: an owner can
/// tell "the user cancelled my export" from "a restart replaced the stack under it" and re-create the job in the second case
/// only.
///
/// <para>
/// The first reason that is recorded wins. A job that was asked to end by a restart and finishes on its own anyway therefore
/// shows <see cref="JobStatus.Completed"/> with <see cref="StackReplaced"/>: the reason says why the end was requested.
/// A job that fails is always <see cref="Failed"/>, whatever was requested before.
/// </para>
/// </summary>
public enum JobEndReason : byte
{
    #region Values

    /// <summary>The job has not ended (pending or running).</summary>
    None = 0,

    /// <summary>The job's work returned normally.</summary>
    Completed,

    /// <summary>A caller cancelled the job (<see cref="JobInfo.Cancel"/> or <see cref="ISession.TryUnsubscribe"/>).</summary>
    UserCancelled,

    /// <summary>A restart replaced the stack and ended this stack-bound job (see <see cref="JobStackBinding.StackBound"/>).</summary>
    StackReplaced,

    /// <summary>The session shut down and cancelled the job.</summary>
    SessionShutdown,

    /// <summary>The job's work threw (see <see cref="JobInfo.FailureException"/>) or its thread could not be started.</summary>
    Failed,

    #endregion
}
