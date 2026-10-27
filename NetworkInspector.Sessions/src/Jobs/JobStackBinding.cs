// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// Whether a user job depends on the session's protocol stack, and therefore on the generation it was started in. A restart
/// (<see cref="ISession.TryRestart(Func{FrameInterfaceRegistry, Stack}, out RestartReport?, out SessionFailure?)"/>) ends,
/// waits for, or refuses on <see cref="StackBound"/> jobs according to its <see cref="RestartOptions"/>; it never touches an
/// <see cref="Independent"/> job.
///
/// <para>
/// <b>Why the default is <see cref="StackBound"/> (secure by default):</b> field ids, filters, value caches and exporter
/// catalogs are only valid for the stack that produced them. A job that does not say what it reads would otherwise keep
/// running on ids that name other fields after a restart and produce wrong results silently. <see cref="Independent"/> is the
/// opt-in for work that reads no session data (a timer, a file copy, a restart-event handler).
/// </para>
/// </summary>
public enum JobStackBinding : byte
{
    #region Values

    /// <summary>
    /// The job reads session data or holds stack-bound state (field ids, a filter, a cache). A restart cancels it with
    /// <see cref="JobEndReason.StackReplaced"/>, waits for it, or is refused, as the caller chose. This is the binding of
    /// every job added without one.
    /// </summary>
    StackBound = 0,

    /// <summary>The job does not depend on the stack. A restart leaves it running.</summary>
    Independent = 1,

    #endregion
}
