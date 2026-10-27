// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// Test-side shortcut for the many tests whose subject is not the restart result: it calls
/// <see cref="ISession.TryRestart(Func{FrameInterfaceRegistry, Stack}, RestartOptions, out RestartReport?, out SessionFailure?)"/>
/// and turns a refusal into a <see cref="SessionException"/>, so a test that only needs "the session was restarted" stays one
/// line and a refusal still fails it loudly (never silently, Section 4.1). Tests that are about the refusal or the report call
/// <c>TryRestart</c> themselves.
/// </summary>
internal static class SessionRestartExtensions
{
    /// <summary>
    /// Restarts <paramref name="session"/> onto the stack <paramref name="stackFactory"/> builds.
    /// </summary>
    /// <param name="session">The session to restart.</param>
    /// <param name="stackFactory">Builds the new stack from the session's registry.</param>
    /// <param name="options">The restart options; <see langword="null"/> uses <see cref="RestartOptions.Default"/>.</param>
    /// <returns>The report of the restart.</returns>
    /// <exception cref="SessionException">The restart was refused; carries the failure's code and message.</exception>
    internal static RestartReport RestartOrThrow(
        this ISession session,
        Func<FrameInterfaceRegistry, Stack> stackFactory,
        RestartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryRestart(stackFactory, options ?? RestartOptions.Default, out RestartReport? report, out SessionFailure? failure))
        {
            throw new SessionException(failure.Value.Code, failure.Value.Message);
        }

        return report;
    }
}
