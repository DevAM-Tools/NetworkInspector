// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// The <see cref="ISession.Restarted"/> event: one slot (queue, thread, independent job) per handler, delivered off the
/// restarting thread, in generation order, never blocking the restart and never letting one handler influence another. Every
/// interleaving is forced with gates; there are no sleeps. The only waits are bounded by <see cref="_Timeout"/>.
/// </summary>
internal sealed class SessionRestartEventTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The generations two consecutive restarts deliver.</summary>
    private static readonly int[] _ExpectedOneTwo = [1, 2];

    /// <summary>The jobs of the session that belong to restart-event subscriptions.</summary>
    private static JobInfo[] _EventJobs(Session session) => [.. session.GetJobs().Where(static job => job.UiName == "restart-event")];

    #endregion

    #region Delivery

    [Test]
    public async Task AHandlerRunsOnItsOwnJobThreadAndNeverBlocksTheRestart()
    {
        using RestartRig rig = new(frameCount: 6);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int handlerThreadId = -1;
        string? handlerThreadName = null;
        Action<RestartNotification> handler = notification =>
        {
            handlerThreadId = Environment.CurrentManagedThreadId;
            handlerThreadName = Thread.CurrentThread.Name;
            entered.Set();
            _ = release.Wait(_Timeout);
        };
        rig.Session.Restarted += handler;
        JobInfo[] whileSubscribed = _EventJobs(rig.Session);

        int restartingThreadId = -1;
        bool restarted = await RestartRig.OnDedicatedThread(() =>
        {
            restartingThreadId = Environment.CurrentManagedThreadId;
            return rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);
        }).WaitAsync(_Timeout);
        await Assert.That(entered.Wait(_Timeout)).IsTrue();

        // The restart returned while the handler is still parked inside its callback.
        release.Set();
        rig.Session.Restarted -= handler;

        await Assert.That(restarted).IsTrue();
        await Assert.That(handlerThreadId).IsNotEqualTo(restartingThreadId);
        await Assert.That(handlerThreadName).IsNotNull();
        await Assert.That(handlerThreadName!).EndsWith("-restart-event");
        await Assert.That(handlerThreadName).StartsWith("session-job-");
        await Assert.That(whileSubscribed.Length).IsEqualTo(1);
        await Assert.That(whileSubscribed[0].Binding).IsEqualTo(JobStackBinding.Independent);
        await Assert.That(_EventJobs(rig.Session).Length).IsEqualTo(0);
    }

    [Test]
    public async Task AParkedHandlerDoesNotDelayAnotherHandlerAndEachGetsEveryNotificationInOrder()
    {
        using RestartRig rig = new(frameCount: 6);
        using ManualResetEventSlim parkedEntered = new(false);
        using ManualResetEventSlim parkedRelease = new(false);
        using CountdownEvent secondSeen = new(2);
        List<int> parkedGenerations = [];
        List<int> freeGenerations = [];
        Action<RestartNotification> parked = notification =>
        {
            lock (parkedGenerations)
            {
                parkedGenerations.Add(notification.Generation);
            }

            parkedEntered.Set();
            _ = parkedRelease.Wait(_Timeout);
        };
        Action<RestartNotification> free = notification =>
        {
            lock (freeGenerations)
            {
                freeGenerations.Add(notification.Generation);
            }

            _ = secondSeen.Signal();
        };
        rig.Session.Restarted += parked;
        rig.Session.Restarted += free;

        _ = rig.RestartOntoStandard();
        await Assert.That(parkedEntered.Wait(_Timeout)).IsTrue();
        _ = rig.RestartOntoStandard();

        // The second handler received both notifications while the first is still parked inside its first callback.
        bool freeGotBoth = secondSeen.Wait(_Timeout);
        int parkedSeenWhileParked;
        lock (parkedGenerations)
        {
            parkedSeenWhileParked = parkedGenerations.Count;
        }

        parkedRelease.Set();
        WaitHelper.WaitUntil(() =>
        {
            lock (parkedGenerations)
            {
                return parkedGenerations.Count == 2;
            }
        });
        rig.Session.Restarted -= parked;
        int jobsAfterRemovingParked = _EventJobs(rig.Session).Length;
        rig.Session.Restarted -= free;

        await Assert.That(freeGotBoth).IsTrue();
        await Assert.That(parkedSeenWhileParked).IsEqualTo(1);
        await Assert.That(freeGenerations).IsEquivalentTo(_ExpectedOneTwo);
        await Assert.That(parkedGenerations).IsEquivalentTo(_ExpectedOneTwo);
        await Assert.That(freeGenerations[0]).IsLessThan(freeGenerations[1]);
        await Assert.That(jobsAfterRemovingParked).IsEqualTo(1);
    }

    [Test]
    public async Task AThrowingHandlerFailsOnlyItsOwnSlotAndTheOthersAndTheSessionGoOn()
    {
        using RestartRig rig = new(frameCount: 6);
        TestSessionListener listener = new();
        _ = rig.Session.TryAddListener(listener, out _, out _);
        using CountdownEvent secondSeen = new(2);
        int throwingCalls = 0;
        Action<RestartNotification> throwing = notification =>
        {
            _ = Interlocked.Increment(ref throwingCalls);
            throw new InvalidOperationException("boom");
        };
        Action<RestartNotification> healthy = notification => _ = secondSeen.Signal();
        rig.Session.Restarted += throwing;
        rig.Session.Restarted += healthy;
        JobInfo throwingJob = _EventJobs(rig.Session)[0];
        int jobsChangedBefore = listener.JobsChangedCount;

        _ = rig.RestartOntoStandard();
        throwingJob.Join();
        _ = rig.RestartOntoStandard();
        bool healthyGotBoth = secondSeen.Wait(_Timeout);
        bool reads = rig.Session.TryGetPacket(new PacketId(0), out _, out _);

        Exception? shutdownFailure = null;
        try
        {
            rig.Session.Shutdown(_Timeout);
        }
        catch (Exception ex)
        {
            shutdownFailure = ex;
        }

        await Assert.That(healthyGotBoth).IsTrue();
        await Assert.That((throwingJob.Status, throwingJob.EndReason)).IsEqualTo((JobStatus.Failed, JobEndReason.Failed));
        await Assert.That(throwingJob.FailureException?.Message).IsEqualTo("boom");
        await Assert.That(Volatile.Read(ref throwingCalls)).IsEqualTo(1);
        await Assert.That(reads).IsTrue();
        WaitHelper.WaitUntil(() => listener.JobsChangedCount > jobsChangedBefore);
        await Assert.That(shutdownFailure).IsNull();
    }

    [Test]
    public async Task TheNotificationCarriesTheGenerationAndReadsAreEnabledWhenTheHandlerRuns()
    {
        using RestartRig rig = new(frameCount: 6);
        List<(int Notified, int Seen, bool Enabled, bool Succeeded, bool HasReport)> seen = [];
        using CountdownEvent both = new(2);
        Session session = rig.Session;
        Action<RestartNotification> handler = notification =>
        {
            lock (seen)
            {
                seen.Add((notification.Generation, session.Generation, session.QueriesEnabled, notification.Succeeded, notification.Report is not null));
            }

            _ = both.Signal();
        };
        rig.Session.Restarted += handler;

        // The handler runs asynchronously, so the first notification is awaited before the second restart: only then "the
        // session already reads the generation of the notification" can be asserted for it (a later restart would move it).
        _ = rig.RestartOntoStandard();
        WaitHelper.WaitUntil(() => both.CurrentCount == 1);
        _ = rig.RestartOntoStandard();
        bool delivered = both.Wait(_Timeout);

        await Assert.That(delivered).IsTrue();
        (int Notified, int Seen, bool Enabled, bool Succeeded, bool HasReport)[] copy;
        lock (seen)
        {
            copy = [.. seen];
        }

        await Assert.That(copy[0]).IsEqualTo((1, 1, true, true, true));
        await Assert.That(copy[1]).IsEqualTo((2, 2, true, true, true));
    }

    [Test]
    public async Task AFaultedRestartDeliversANotificationThatSaysSo()
    {
        using RestartRig rig = new(frameCount: 6, wrapSource: static inner => new HoleRandomAccessSource(inner, holeFrameIndex: 2));
        using ManualResetEventSlim delivered = new(false);
        RestartNotification? received = null;
        rig.Session.Restarted += notification =>
        {
            received = notification;
            delivered.Set();
        };

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);

        await Assert.That(restarted).IsFalse();
        await Assert.That(delivered.Wait(_Timeout)).IsTrue();
        await Assert.That(received!.Succeeded).IsFalse();
        await Assert.That(received.Report).IsNull();
        await Assert.That(received.Fault!.Value.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(received.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task ARefusedRestartRaisesNoNotification()
    {
        using RestartRig rig = new(frameCount: 4);
        int calls = 0;
        Action<RestartNotification> handler = notification => _ = Interlocked.Increment(ref calls);
        rig.Session.Restarted += handler;

        bool restarted = rig.Session.TryRestart(
            registry => TestHarness.CreateStack(registry),
            new RestartOptions { JobWaitTimeout = TimeSpan.Zero },
            out _,
            out _);
        rig.Session.Restarted -= handler;

        await Assert.That(restarted).IsFalse();
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
    }

    #endregion

    #region Subscription

    [Test]
    public async Task ARemovedHandlerReceivesNothingMoreAndRemovingAnUnknownHandlerIsANoOp()
    {
        using RestartRig rig = new(frameCount: 4);
        int calls = 0;
        Action<RestartNotification> handler = notification => _ = Interlocked.Increment(ref calls);
        rig.Session.Restarted += handler;
        rig.Session.Restarted -= handler;
        rig.Session.Restarted -= handler;

        _ = rig.RestartOntoStandard();

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        await Assert.That(_EventJobs(rig.Session).Length).IsEqualTo(0);
    }

    [Test]
    public async Task AHandlerCannotRemoveItselfFromInsideItsOwnCallback()
    {
        using RestartRig rig = new(frameCount: 4);
        using ManualResetEventSlim done = new(false);
        Exception? caught = null;
        Action<RestartNotification>? handler = null;
        handler = notification =>
        {
            try
            {
                rig.Session.Restarted -= handler;
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }

            done.Set();
        };
        rig.Session.Restarted += handler;

        _ = rig.RestartOntoStandard();
        await Assert.That(done.Wait(_Timeout)).IsTrue();
        rig.Session.Restarted -= handler;

        await Assert.That(caught).IsNotNull();
    }

    [Test]
    public async Task ASubscriptionIsAJobThatTryUnsubscribeEnds()
    {
        using RestartRig rig = new(frameCount: 4);
        int calls = 0;
        Action<RestartNotification> handler = notification => _ = Interlocked.Increment(ref calls);
        rig.Session.Restarted += handler;
        JobInfo job = _EventJobs(rig.Session)[0];

        bool unsubscribed = rig.Session.TryUnsubscribe(job, out _);
        _ = rig.RestartOntoStandard();

        await Assert.That(unsubscribed).IsTrue();
        await Assert.That(_EventJobs(rig.Session).Length).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribingAfterShutdownStartedIsRefusedWithInvalidPhase()
    {
        using RestartRig rig = new(frameCount: 4);
        rig.Session.Shutdown(_Timeout);

        SessionException? thrown = null;
        try
        {
            rig.Session.Restarted += static _ => { };
        }
        catch (SessionException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown?.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
    }

    #endregion

    #region Shutdown

    [Test]
    public async Task ShutdownDeliversWhatIsQueuedBeforeItReturnsAndEndsTheSlotWithSessionShutdown()
    {
        using RestartRig rig = new(frameCount: 4);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int delivered = 0;
        Action<RestartNotification> handler = notification =>
        {
            entered.Set();
            _ = release.Wait(_Timeout);
            _ = Interlocked.Increment(ref delivered);
        };
        rig.Session.Restarted += handler;
        JobInfo job = _EventJobs(rig.Session)[0];

        // One notification is being delivered (parked), one is queued behind it.
        _ = rig.RestartOntoStandard();
        await Assert.That(entered.Wait(_Timeout)).IsTrue();
        _ = rig.RestartOntoStandard();
        Task shutdown = RestartRig.OnDedicatedThread(() =>
        {
            rig.Session.Shutdown(_Timeout);
            return 0;
        });
        release.Set();
        await shutdown.WaitAsync(_Timeout);

        await Assert.That(Volatile.Read(ref delivered)).IsEqualTo(2);
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.SessionShutdown));
    }

    [Test]
    public async Task ShutdownWithoutBudgetReportsTheUndeliveredNotificationsAndTheStuckHandlerKeepsItsJobRunning()
    {
        using RestartRig rig = new(frameCount: 4);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        Action<RestartNotification> handler = notification =>
        {
            entered.Set();
            _ = release.Wait(_Timeout);
        };
        rig.Session.Restarted += handler;
        JobInfo job = _EventJobs(rig.Session)[0];
        _ = rig.RestartOntoStandard();
        await Assert.That(entered.Wait(_Timeout)).IsTrue();
        _ = rig.RestartOntoStandard();

        AggregateException? thrown = null;
        try
        {
            rig.Session.Shutdown(TimeSpan.Zero);
        }
        catch (AggregateException ex)
        {
            thrown = ex;
        }

        JobStatus statusWhileStuck = job.Status;
        release.Set();
        // The session disposed the job at shutdown (the budget was zero), so Join is not allowed any more: poll the status.
        WaitHelper.WaitUntil(() => job.Status == JobStatus.Cancelled);

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("1 restart notifications were not delivered");
        await Assert.That(statusWhileStuck).IsEqualTo(JobStatus.Running);
    }

    #endregion
}
