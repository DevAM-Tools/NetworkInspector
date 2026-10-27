// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// What a completed restart did, so a host can tell the user without reconstructing it from job lists and listener states.
///
/// <para>Thread-safety: immutable record; the lists are never modified after the report was built.</para>
/// </summary>
/// <param name="Generation">The generation that serves reads now (see <see cref="ISessionReader.Generation"/>).</param>
/// <param name="ReparsedPackets">The number of packets re-parsed on the new stack; equals <see cref="ISessionReader.PacketCount"/> at that moment.</param>
/// <param name="JobsWaitedFor">The stack-bound jobs the restart waited for under <see cref="RestartJobPolicy.WaitThenCancel"/> that ended in time.</param>
/// <param name="JobsCancelled">
/// The stack-bound jobs the restart cancelled (<see cref="Jobs.JobEndReason.StackReplaced"/>): all of them under
/// <see cref="RestartJobPolicy.CancelNow"/>, the ones that outlasted the bound or the wait token under the wait policy.
/// </param>
/// <param name="FiltersNotRebound">
/// The listeners whose filter names a field the new stack does not define: their matching reads fail until the listener is
/// replaced, their unfiltered reads work.
/// </param>
/// <param name="WaitDuration">How long the job phase took (zero when there was nothing to wait for).</param>
/// <param name="ReparseDuration">How long the re-parse of all packets took.</param>
public sealed record RestartReport(
    int Generation,
    int ReparsedPackets,
    IReadOnlyList<JobId> JobsWaitedFor,
    IReadOnlyList<JobId> JobsCancelled,
    IReadOnlyList<ListenerId> FiltersNotRebound,
    TimeSpan WaitDuration,
    TimeSpan ReparseDuration);
