// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Thrown when a session operation fails due to an invalid state or configuration: caller bugs (disposed session,
/// removing a running job), exhausted id ranges, and a restart or value-cache fill that cannot re-read a frame
/// (these fail closed instead of skipping data). Expected, locally handled failures are not exceptions; they are
/// returned as <see cref="SessionFailure"/> from the <c>Try*</c> members. Immutable; thread safety is not applicable.
/// Messages must not contain secrets or captured payload data.
/// </summary>
/// <param name="code">The machine-readable cause; callers should branch on it, not on the message text.</param>
/// <param name="message">Human-readable description. Not validated.</param>
public sealed class SessionException(SessionErrorCode code, string message)
    : Exception(message)
{
    #region Fields

    /// <summary>Identifies the root cause of the failure.</summary>
    public SessionErrorCode Code { get; } = code;

    #endregion
}
