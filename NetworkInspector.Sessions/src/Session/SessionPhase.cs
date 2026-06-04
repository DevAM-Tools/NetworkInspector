// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Lifecycle phase of a <see cref="Session"/>.
/// Transitions are monotonically increasing except for <see cref="Running"/> after a restart.
/// </summary>
public enum SessionPhase
{
    /// <summary>Session created, sources and listeners may be added. Not yet started.</summary>
    Idle,

    /// <summary>Session started, source jobs are running.</summary>
    Running,

    /// <summary>Session is restarting: protocol stack swapped, frames being re-parsed.</summary>
    Restarting,

    /// <summary>Session is shutting down gracefully or forcefully.</summary>
    ShuttingDown,

    /// <summary>Session has fully stopped. All jobs completed or cancelled.</summary>
    Stopped,
}
