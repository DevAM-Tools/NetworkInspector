// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// What a restart does with the running <see cref="Jobs.JobStackBinding.StackBound"/> jobs. The caller (in a host: the end
/// user, through a dialog) decides; each value names what the caller gives up.
///
/// <para>
/// <b>Why the caller decides:</b> a job that exports rows or fills a chart holds field ids of the old stack. Ending it is
/// the only way to keep the restart correct, but whether its unfinished work matters is the user's call, not the library's.
/// </para>
/// </summary>
public enum RestartJobPolicy : byte
{
    #region Values

    /// <summary>
    /// Cancel every stack-bound job at once (after the new stack was built), with
    /// <see cref="Jobs.JobEndReason.StackReplaced"/>. You lose their unfinished work; the restart does not wait. The default,
    /// because it is the only policy that cannot block the caller.
    /// </summary>
    CancelNow = 0,

    /// <summary>
    /// Wait up to <see cref="RestartOptions.JobWaitTimeout"/> for the stack-bound jobs to end on their own, then cancel the rest.
    /// Jobs that end in time keep their result. You lose time: the call blocks (a worker thread, not a UI thread), and the
    /// wait can be cut short through <see cref="RestartOptions.WaitCancellation"/>, which continues as
    /// <see cref="CancelNow"/>.
    /// </summary>
    WaitThenCancel = 1,

    /// <summary>
    /// Refuse the restart while a stack-bound job is pending or running (<see cref="SessionErrorCode.JobsRunning"/>, the
    /// message names them) and change nothing. You lose the restart until the jobs end or are cancelled by their owner.
    /// </summary>
    Refuse = 2,

    #endregion
}
