// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Thrown when a session operation fails due to an invalid state or configuration.
/// </summary>
public sealed class SessionException(SessionErrorCode code, string message)
    : Exception(message)
{
    /// <summary>Identifies the root cause of the failure.</summary>
    public SessionErrorCode Code { get; } = code;
}
