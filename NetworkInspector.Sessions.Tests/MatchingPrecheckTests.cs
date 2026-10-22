// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Covers the adaptive switch of the matching-read pre-check.</summary>
internal sealed class MatchingPrecheckTests
{
    #region Helpers

    private static MatchingPrecheck _Create(Stack stack)
    {
        PacketFilter filter = SessionFixture.CompileOrThrow(stack, "udp.dstport == 7");
        FilterObserverOptions options = FilterObserverOptions.Default;
        FilterObserver observer = PacketFilter.Compile("udp.dstport == 7", stack, in options).Value;
        return new MatchingPrecheck(filter, observer);
    }

    /// <summary>Feeds one full window; <paramref name="maybeMatches"/> of its results could match.</summary>
    private static void _Window(MatchingPrecheck precheck, int maybeMatches)
    {
        for (int i = 0; i < MatchingPrecheck.Window; i++)
        {
            precheck.Record(maybeMatch: i < maybeMatches);
        }
    }

    /// <summary>Counts the ids a backoff skips until the pre-check is active again.</summary>
    private static int _BackoffLength(MatchingPrecheck precheck)
    {
        int skipped = 0;
        while (!precheck.IsActive)
        {
            precheck.OnSkippedId();
            skipped++;
        }

        return skipped;
    }

    #endregion

    #region Tests

    [Test]
    public async Task MostlyMatchingWindowTurnsThePrecheckOffForTheInitialBackoff()
    {
        using Stack stack = TestHarness.CreateStack();
        MatchingPrecheck precheck = _Create(stack);

        _Window(precheck, (MatchingPrecheck.Window / 2) + 1);
        bool offAfterWindow = !precheck.IsActive;
        for (int i = 0; i < MatchingPrecheck.InitialBackoff - 1; i++)
        {
            precheck.OnSkippedId();
        }

        bool stillOffOneBeforeEnd = !precheck.IsActive;
        precheck.OnSkippedId();

        await Assert.That(offAfterWindow).IsTrue();
        await Assert.That(stillOffOneBeforeEnd).IsTrue();
        await Assert.That(precheck.IsActive).IsTrue();
    }

    [Test]
    public async Task HalfMatchingWindowKeepsThePrecheckOn()
    {
        using Stack stack = TestHarness.CreateStack();
        MatchingPrecheck precheck = _Create(stack);

        // Exactly half could match: the window does not trip the switch.
        _Window(precheck, MatchingPrecheck.Window / 2);
        bool activeAfterHalf = precheck.IsActive;

        // A partial window never decides.
        for (int i = 0; i < MatchingPrecheck.Window - 1; i++)
        {
            precheck.Record(maybeMatch: true);
        }

        bool activeBeforeWindowEnd = precheck.IsActive;
        precheck.Record(maybeMatch: true);

        await Assert.That(activeAfterHalf).IsTrue();
        await Assert.That(activeBeforeWindowEnd).IsTrue();
        await Assert.That(precheck.IsActive).IsFalse();
    }

    [Test]
    public async Task RepeatedDenseWindowsDoubleTheBackoffUpToTheCap()
    {
        using Stack stack = TestHarness.CreateStack();
        MatchingPrecheck precheck = _Create(stack);
        List<int> backoffs = [];

        for (int round = 0; round < 10; round++)
        {
            _Window(precheck, MatchingPrecheck.Window);
            backoffs.Add(_BackoffLength(precheck));
        }

        List<int> expected = [];
        for (int round = 0, backoff = MatchingPrecheck.InitialBackoff; round < 10; round++)
        {
            expected.Add(backoff);
            backoff = Math.Min(backoff * 2, MatchingPrecheck.MaxBackoff);
        }

        await Assert.That(backoffs).IsEquivalentTo(expected);
        await Assert.That(backoffs[^1]).IsEqualTo(MatchingPrecheck.MaxBackoff);
    }

    [Test]
    public async Task SparseWindowResetsTheBackoff()
    {
        using Stack stack = TestHarness.CreateStack();
        MatchingPrecheck precheck = _Create(stack);

        _Window(precheck, MatchingPrecheck.Window);
        _ = _BackoffLength(precheck);
        _Window(precheck, MatchingPrecheck.Window);
        int doubled = _BackoffLength(precheck);
        _Window(precheck, 0);
        bool activeAfterSparse = precheck.IsActive;
        _Window(precheck, MatchingPrecheck.Window);
        int afterReset = _BackoffLength(precheck);

        await Assert.That(doubled).IsEqualTo(2 * MatchingPrecheck.InitialBackoff);
        await Assert.That(activeAfterSparse).IsTrue();
        await Assert.That(afterReset).IsEqualTo(MatchingPrecheck.InitialBackoff);
    }

    [Test]
    public async Task OnSkippedIdWhileActiveChangesNothing()
    {
        using Stack stack = TestHarness.CreateStack();
        MatchingPrecheck precheck = _Create(stack);

        precheck.OnSkippedId();

        await Assert.That(precheck.IsActive).IsTrue();
        await Assert.That(precheck.Options.FieldTree).IsEqualTo(FieldTreeMode.Skip);
        await Assert.That(precheck.Options.Observers.Length).IsEqualTo(1);

        // The skip parse uses the owner-expanding watch, not the observer's own, so lazy containers open as for the
        // field-tree filter (the watch itself is tested in FilterLazyMaterializeTests).
        await Assert.That(precheck.Options.Observers.Span[0].Watch == precheck.Observer.Watch).IsFalse();
    }

    #endregion
}
