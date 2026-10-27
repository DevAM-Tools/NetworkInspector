// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// What <see cref="ISession.Restarted"/> delivers: the outcome of one restart that got past the swap. A restart that was
/// refused before any change (wrong phase, running jobs under Refuse, an option out of range) raises no notification, because
/// nothing happened to the session.
///
/// <para>Thread-safety: immutable record.</para>
/// </summary>
/// <param name="Generation">
/// The generation after the restart (reads serve it when <paramref name="Succeeded"/>; the session is faulted at this
/// generation otherwise).
/// </param>
/// <param name="Succeeded"><see langword="true"/> when the new stack serves reads.</param>
/// <param name="Report">The report of the restart; <see langword="null"/> when it faulted.</param>
/// <param name="Fault">The failure that left the session <see cref="SessionPhase.Faulted"/>; <see langword="null"/> on success.</param>
public sealed record RestartNotification(int Generation, bool Succeeded, RestartReport? Report, SessionFailure? Fault);
