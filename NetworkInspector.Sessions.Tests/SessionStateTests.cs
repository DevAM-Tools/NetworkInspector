// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Unit tests for the phase transitions of <see cref="SessionState"/>.</summary>
internal sealed class SessionStateTests
{
    [Test]
    public async Task TryTransitionChangesThePhaseWhenItMatchesTheExpectedOne()
    {
        SessionState state = new();
        state.SetPhase(SessionPhase.Running);

        bool changed = state.TryTransition(SessionPhase.Running, SessionPhase.Stopped);

        await Assert.That(changed).IsTrue();
        await Assert.That(state.Phase).IsEqualTo(SessionPhase.Stopped);
    }

    [Test]
    public async Task TryTransitionLeavesAConcurrentlyChangedPhaseAlone()
    {
        // The last source thread reads Running, then Shutdown publishes ShuttingDown before the source thread stores
        // Stopped. The conditional transition must not overwrite the newer phase.
        SessionState state = new();
        state.SetPhase(SessionPhase.ShuttingDown);

        bool changed = state.TryTransition(SessionPhase.Running, SessionPhase.Stopped);

        await Assert.That(changed).IsFalse();
        await Assert.That(state.Phase).IsEqualTo(SessionPhase.ShuttingDown);
    }

    [Test]
    public async Task TryTransitionFromIdleOnlyOneCallerWins()
    {
        SessionState state = new();
        int winners = 0;

        ConcurrencyHelper.RunTogether(8, _ =>
        {
            if (state.TryTransition(SessionPhase.Idle, SessionPhase.Running))
            {
                _ = Interlocked.Increment(ref winners);
            }
        });

        await Assert.That(winners).IsEqualTo(1);
        await Assert.That(state.Phase).IsEqualTo(SessionPhase.Running);
    }
}
