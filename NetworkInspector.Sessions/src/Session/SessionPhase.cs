// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Lifecycle phase of a <see cref="Session"/>.
/// <para>
/// Transitions: <see cref="Idle"/> to <see cref="Running"/> on start (or straight to <see cref="Stopped"/> without any
/// source); <see cref="Running"/> to <see cref="Stopped"/> when the last source job ends; <see cref="Running"/> or
/// <see cref="Stopped"/> to <see cref="Restarting"/> and back to <see cref="Running"/> (a source is still active) or
/// <see cref="Stopped"/> (none is); <see cref="Restarting"/> to <see cref="Faulted"/> when the re-parse fails after the swap,
/// and <see cref="Faulted"/> to <see cref="Restarting"/> again by another restart; <see cref="Idle"/>,
/// <see cref="Running"/> or <see cref="Faulted"/> to <see cref="ShuttingDown"/> and then <see cref="Stopped"/> (a session that
/// is already <see cref="Stopped"/> stays so during shutdown, and shutdown waits for a running restart first). The numeric
/// order of the values is therefore not a transition order: a restart goes back from <see cref="Restarting"/> to
/// <see cref="Running"/>, and <see cref="Stopped"/> is left again by a restart.
/// </para>
/// </summary>
public enum SessionPhase
{
    #region Values

    /// <summary>Session created, sources and listeners may be added. Not yet started.</summary>
    Idle,

    /// <summary>Session started, source jobs are running.</summary>
    Running,

    /// <summary>
    /// Session is restarting: protocol stack swapped, frames being re-parsed. Source threads wait at the parse gate
    /// and packet queries are disabled until the re-parse ends.
    /// </summary>
    Restarting,

    /// <summary>Session is shutting down gracefully or forcefully.</summary>
    ShuttingDown,

    /// <summary>
    /// Ingest has ended: all source jobs finished (or were cancelled), or shutdown completed. After a normal source
    /// end the session can still be restarted and its listener threads keep running until <c>Shutdown</c>; after
    /// shutdown, queries are disabled and the session is final.
    /// </summary>
    Stopped,

    /// <summary>
    /// A restart failed after the stack was swapped (a frame could not be re-read): counters, packet map and index are a
    /// partial rewrite, so reads and new work are refused with <see cref="SessionErrorCode.SessionFaulted"/> and ingest stays
    /// parked at the parse gate (a resumed source would allocate ids the new stack's watermark has not reached).
    /// <see cref="ISessionReader.Fault"/> keeps the failure. Another restart (it re-parses from id 0) and shutdown are the
    /// only ways out. Appended after the earlier values so their numbers stay stable.
    /// </summary>
    Faulted,

    #endregion
}
