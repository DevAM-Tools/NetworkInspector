// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <c>TryRestart</c> with options: the job policies, the wait bound, the pending-restart refusal, the self-restart rule, the
/// report, and the expected refusals as results. Every interleaving is forced with gates; there are no sleeps except the one
/// documented smallest wait (<see cref="WaitBoundCancelsAJobThatIgnoresItsTokenWhenTheBoundElapses"/>). The only waits are
/// bounded by <see cref="_Timeout"/> so a broken implementation fails instead of hanging.
/// </summary>
internal sealed class SessionRestartPolicyTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds a stack-bound job that parks until its token fires or <paramref name="gate"/> opens.</summary>
    private static JobInfo _AddParkedBoundJob(Session session, string name, ManualResetEventSlim gate, ManualResetEventSlim? running = null)
    {
        bool added = session.TryAddJob(
            name,
            "parks until cancelled",
            token =>
            {
                running?.Set();
                _ = gate.Wait(_Timeout, token);
            },
            out JobInfo? info,
            out _);
        if (!added)
        {
            throw new InvalidOperationException("The job was not added.");
        }

        return info!; // Non-null: the add succeeded.
    }

    #endregion

    #region Policies

    [Test]
    public async Task CancelNowEndsBoundJobsAfterTheFactoryAndLeavesIndependentJobsRunning()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim boundRunning = new(false);
        int sequence = 0;
        int factorySequence = 0;
        int jobSequence = 0;
        _ = rig.Session.TryAddJob(
            "bound",
            "stack-bound",
            token =>
            {
                boundRunning.Set();
                try
                {
                    _ = gate.Wait(_Timeout, token);
                }
                catch (OperationCanceledException)
                {
                    jobSequence = Interlocked.Increment(ref sequence);
                    throw;
                }
            },
            out JobInfo? bound,
            out _);
        _ = rig.Session.TryAddJob(
            "free", "independent", JobStackBinding.Independent, context => gate.Wait(_Timeout, context.CancellationToken), out JobInfo? free, out _);
        await Assert.That(boundRunning.Wait(_Timeout)).IsTrue();

        bool restarted = rig.Session.TryRestart(
            registry =>
            {
                Stack created = TestHarness.CreateStack(registry);
                factorySequence = Interlocked.Increment(ref sequence);
                return created;
            },
            RestartOptions.Default,
            out RestartReport? report,
            out _);
        bound!.Join(); // Non-null: the add succeeded.

        await Assert.That(restarted).IsTrue();
        await Assert.That(factorySequence).IsLessThan(jobSequence);
        await Assert.That((bound.Status, bound.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.StackReplaced));
        await Assert.That(free!.Status).IsEqualTo(JobStatus.Running);
        await Assert.That(report!.JobsCancelled.Contains(bound.Id)).IsTrue();
        await Assert.That(report.JobsWaitedFor.Count).IsEqualTo(0);
        gate.Set();
    }

    [Test]
    public async Task AFactoryThatThrowsCostsNoJobAndItsExceptionPropagatesUnchanged()
    {
        using RestartRig rig = new(frameCount: 4);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        JobInfo job = _AddParkedBoundJob(rig.Session, "bound", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        InvalidOperationException expected = new("factory failed");

        InvalidOperationException? thrown = null;
        try
        {
            _ = rig.Session.TryRestart(_ => throw expected, out _, out _);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        bool boundAddedAfter = rig.Session.TryAddJob("again", "after the failed restart", static _ => { }, out _, out SessionFailure? afterFailure);

        await Assert.That(thrown).IsSameReferenceAs(expected);
        await Assert.That(job.Status).IsEqualTo(JobStatus.Running);
        await Assert.That(job.EndReason).IsEqualTo(JobEndReason.None);
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
        await Assert.That(boundAddedAfter).IsTrue();
        await Assert.That(afterFailure).IsNull();
        gate.Set();
    }

    [Test]
    public async Task WaitThenCancelKeepsAJobThatEndsInTime()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        using ManualResetEventSlim factoryReturned = new(false);
        JobInfo job = _AddParkedBoundJob(rig.Session, "bound", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        RestartOptions options = new() { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromSeconds(30) };

        RestartReport? report = null;
        Task<bool> restart = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                Stack created = TestHarness.CreateStack(registry);
                factoryReturned.Set();
                return created;
            },
            options,
            out report,
            out _));
        await Assert.That(factoryReturned.Wait(_Timeout)).IsTrue();
        JobStatus statusWhileWaiting = job.Status;

        // The gate is the clock: the job ends only now, so the restart can only have waited for it.
        gate.Set();
        bool restarted = await restart.WaitAsync(_Timeout);

        await Assert.That(statusWhileWaiting).IsEqualTo(JobStatus.Running);
        await Assert.That(restarted).IsTrue();
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Completed, JobEndReason.Completed));
        await Assert.That(report!.JobsWaitedFor.Contains(job.Id)).IsTrue();
        await Assert.That(report.JobsCancelled.Count).IsEqualTo(0);
        await Assert.That(report.WaitDuration).IsGreaterThan(TimeSpan.Zero);
    }

    [Test]
    [Category("Slow")]
    public async Task WaitBoundCancelsAJobThatIgnoresItsTokenWhenTheBoundElapses()
    {
        // The one slow case of the suite: the smallest allowed bound (1 s) is a real wait, there is no clock to inject.
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        CancellationToken seen = default;
        _ = rig.Session.TryAddJob(
            "stubborn",
            "ignores its token",
            token =>
            {
                seen = token;
                running.Set();
                _ = gate.Wait(_Timeout, CancellationToken.None); // Deliberately ignores the token.
                token.ThrowIfCancellationRequested();
            },
            out JobInfo? job,
            out _);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        RestartOptions options = new() { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromSeconds(1) };

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), options, out RestartReport? report, out _);
        JobStatus statusAtReturn = job!.Status; // Non-null: the add succeeded.
        bool tokenFired = seen.IsCancellationRequested;
        gate.Set();
        job.Join();

        await Assert.That(restarted).IsTrue();
        await Assert.That(statusAtReturn).IsEqualTo(JobStatus.Running);
        await Assert.That(tokenFired).IsTrue();
        await Assert.That(report!.JobsCancelled.Contains(job.Id)).IsTrue();
        await Assert.That(report.WaitDuration).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.StackReplaced));
    }

    [Test]
    [Category("Slow")]
    public async Task CancellingTheWaitContinuesTheRestartAsCancelNow()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        using ManualResetEventSlim factoryReturned = new(false);
        using CancellationTokenSource waitCancellation = new();
        JobInfo job = _AddParkedBoundJob(rig.Session, "bound", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        RestartOptions options = new()
        {
            JobPolicy = RestartJobPolicy.WaitThenCancel,
            JobWaitTimeout = TimeSpan.FromMinutes(10),
            WaitCancellation = waitCancellation.Token,
        };

        RestartReport? report = null;
        Task<bool> restart = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                Stack created = TestHarness.CreateStack(registry);
                factoryReturned.Set();
                return created;
            },
            options,
            out report,
            out _));
        await Assert.That(factoryReturned.Wait(_Timeout)).IsTrue();
        await waitCancellation.CancelAsync();
        bool restarted = await restart.WaitAsync(_Timeout);
        job.Join();

        await Assert.That(restarted).IsTrue();
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.StackReplaced));
        await Assert.That(report!.JobsCancelled.Contains(job.Id)).IsTrue();
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task RefuseNamesTheRunningJobsAndChangesNothing()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        JobInfo job = _AddParkedBoundJob(rig.Session, "export csv", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        bool factoryRan = false;
        RestartOptions refuse = new() { JobPolicy = RestartJobPolicy.Refuse };

        bool restarted = rig.Session.TryRestart(
            registry =>
            {
                factoryRan = true;
                return TestHarness.CreateStack(registry);
            },
            refuse,
            out RestartReport? report,
            out SessionFailure? failure);
        bool boundAddedAfter = rig.Session.TryAddJob("later", "after the refusal", static _ => { }, out _, out _);

        await Assert.That(restarted).IsFalse();
        await Assert.That(report).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.JobsRunning);
        await Assert.That(failure.Value.Message).Contains("export csv");
        await Assert.That(factoryRan).IsFalse();
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
        await Assert.That(rig.Session.QueriesEnabled).IsTrue();
        await Assert.That(job.Status).IsEqualTo(JobStatus.Running);
        await Assert.That(boundAddedAfter).IsTrue();
        gate.Set();
    }

    [Test]
    public async Task RefuseSucceedsWhenOnlyIndependentJobsRun()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        _ = rig.Session.TryAddJob(
            "free", "independent", JobStackBinding.Independent, context => gate.Wait(_Timeout, context.CancellationToken), out JobInfo? free, out _);

        bool restarted = rig.Session.TryRestart(
            registry => TestHarness.CreateStack(registry),
            new RestartOptions { JobPolicy = RestartJobPolicy.Refuse },
            out RestartReport? report,
            out _);

        await Assert.That(restarted).IsTrue();
        await Assert.That(report!.JobsCancelled.Count).IsEqualTo(0);
        await Assert.That(free!.Status).IsEqualTo(JobStatus.Running);
        gate.Set();
    }

    #endregion

    #region Option range

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task AWaitBoundBelowOneSecondIsRefusedWithTheRange(int milliseconds)
    {
        using RestartRig rig = new(frameCount: 4);
        RestartOptions options = new() { JobWaitTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), options, out _, out SessionFailure? failure);

        await Assert.That(restarted).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.RestartOptionOutOfRange);
        await Assert.That(failure.Value.Message).Contains("JobWaitTimeout");
        await Assert.That(failure.Value.Message).Contains("1 s .. 1 h");
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Stopped);
    }

    [Test]
    public async Task AWaitBoundAboveOneHourAndAnInfiniteWaitAreRefused()
    {
        using RestartRig rig = new(frameCount: 4);
        RestartOptions aboveOneHour = new() { JobWaitTimeout = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1) };
        RestartOptions infinite = new() { JobWaitTimeout = Timeout.InfiniteTimeSpan };

        bool first = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), aboveOneHour, out _, out SessionFailure? firstFailure);
        bool second = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), infinite, out _, out SessionFailure? secondFailure);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
        await Assert.That(firstFailure!.Value.Code).IsEqualTo(SessionErrorCode.RestartOptionOutOfRange);
        await Assert.That(secondFailure!.Value.Code).IsEqualTo(SessionErrorCode.RestartOptionOutOfRange);
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
    }

    [Test]
    [Arguments(1_000)]
    [Arguments(3_600_000)]
    public async Task TheBoundsOfTheWaitRangeAreAccepted(int milliseconds)
    {
        using RestartRig rig = new(frameCount: 4);
        RestartOptions options = new() { JobWaitTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), options, out _, out _);

        await Assert.That(restarted).IsTrue();
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task TheDefaultWaitBoundIsThirtySecondsAndTheDefaultPolicyIsCancelNow()
    {
        await Assert.That(RestartOptions.Default.JobWaitTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(RestartOptions.Default.JobPolicy).IsEqualTo(RestartJobPolicy.CancelNow);
        await Assert.That(RestartOptions.Default.WaitCancellation.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task AnUndefinedPolicyIsRefusedLikeAnOutOfRangeOption()
    {
        using RestartRig rig = new(frameCount: 4);
        RestartOptions options = new() { JobPolicy = (RestartJobPolicy)9 };

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), options, out _, out SessionFailure? failure);

        await Assert.That(restarted).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.RestartOptionOutOfRange);
    }

    #endregion

    #region Pending refusal

    [Test]
    public async Task StackBoundJobsAreRefusedWhileARestartIsPendingAndAcceptedAgainAfterIt()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        using ManualResetEventSlim factoryEntered = new(false);
        using ManualResetEventSlim factoryRelease = new(false);
        JobInfo parked = _AddParkedBoundJob(rig.Session, "parked", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        RestartOptions options = new() { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromSeconds(30) };

        Task<bool> restart = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                factoryEntered.Set();
                _ = factoryRelease.Wait(_Timeout);
                return TestHarness.CreateStack(registry);
            },
            options,
            out _,
            out _));
        await Assert.That(factoryEntered.Wait(_Timeout)).IsTrue();

        // While the factory is parked (the flag is raised at the job check, before the factory).
        bool boundDuringFactory = rig.Session.TryAddJob("late", "bound", static _ => { }, out _, out SessionFailure? duringFactory);
        bool independentDuringFactory = rig.Session.TryAddJob(
            "free", "independent", JobStackBinding.Independent, static _ => { }, out JobInfo? independent, out _);

        // While the restart waits for the parked job.
        factoryRelease.Set();
        bool boundDuringWait = rig.Session.TryAddJob("late2", "bound", static _ => { }, out _, out SessionFailure? duringWait);
        gate.Set();
        bool restarted = await restart.WaitAsync(_Timeout);
        bool boundAfter = rig.Session.TryAddJob("after", "bound", static _ => { }, out JobInfo? afterJob, out _);

        await Assert.That(boundDuringFactory).IsFalse();
        await Assert.That(duringFactory!.Value.Code).IsEqualTo(SessionErrorCode.RestartPending);
        await Assert.That(independentDuringFactory).IsTrue();
        await Assert.That(independent!.Binding).IsEqualTo(JobStackBinding.Independent);
        await Assert.That(boundDuringWait).IsFalse();
        await Assert.That(duringWait!.Value.Code).IsEqualTo(SessionErrorCode.RestartPending);
        await Assert.That(restarted).IsTrue();
        await Assert.That(parked.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(boundAfter).IsTrue();
        await Assert.That(afterJob!.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task APendingRestartThatFailsClearsTheRefusalOnEveryPath()
    {
        using RestartRig rig = new(frameCount: 4);

        // Path 1: the factory throws.
        try
        {
            _ = rig.Session.TryRestart(_ => throw new InvalidOperationException("boom"), out _, out _);
        }
        catch (InvalidOperationException)
        {
            // Expected: the factory's own exception propagates.
        }

        bool afterThrow = rig.Session.TryAddJob("a", "after the throw", static _ => { }, out _, out _);

        // Path 2: the ingest value-cache request cannot be satisfied (refused after the factory ran).
        using RestartRig withCache = new(
            frameCount: 4,
            new SessionOptions { ValueCache = new ValueCacheRequest { FieldNames = ["udp.dstport"] } });
        bool refused = withCache.Session.TryRestart(
            registry => ProbeStack.Create(registry, out _),
            out _,
            out SessionFailure? refusal);
        bool afterRefusal = withCache.Session.TryAddJob("b", "after the refusal", static _ => { }, out _, out _);

        await Assert.That(afterThrow).IsTrue();
        await Assert.That(refused).IsFalse();
        await Assert.That(refusal!.Value.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        await Assert.That(afterRefusal).IsTrue();
    }

    [Test]
    public async Task ARestartThatFaultedRefusesNewJobsWithTheFaultNotWithRestartPending()
    {
        using RestartRig rig = new(frameCount: 4, wrapSource: static inner => new HoleRandomAccessSource(inner, holeFrameIndex: 2));

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out SessionFailure? fault);
        bool added = rig.Session.TryAddJob("x", "after the fault", static _ => { }, out _, out SessionFailure? refusal);

        await Assert.That(restarted).IsFalse();
        await Assert.That(fault!.Value.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(added).IsFalse();
        await Assert.That(refusal!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
    }

    #endregion

    #region Self restart

    [Test]
    public async Task ARestartFromAStackBoundJobIsRefusedAndAnIndependentJobMayRestart()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim boundDone = new(false);
        using ManualResetEventSlim freeDone = new(false);
        bool boundRestarted = true;
        SessionFailure? boundFailure = null;
        bool freeRestarted = false;
        _ = rig.Session.TryAddJob(
            "bound",
            "calls TryRestart",
            token =>
            {
                boundRestarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out boundFailure);
                boundDone.Set();
            },
            out _,
            out _);
        await Assert.That(boundDone.Wait(_Timeout)).IsTrue();
        int generationAfterBound = rig.Session.Generation;

        _ = rig.Session.TryAddJob(
            "free",
            "independent job that restarts",
            JobStackBinding.Independent,
            context =>
            {
                freeRestarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);
                freeDone.Set();
                _ = gate.Wait(_Timeout, context.CancellationToken);
            },
            out JobInfo? free,
            out _);
        await Assert.That(freeDone.Wait(_Timeout)).IsTrue();

        await Assert.That(boundRestarted).IsFalse();
        await Assert.That(boundFailure!.Value.Code).IsEqualTo(SessionErrorCode.RestartFromJob);
        await Assert.That(generationAfterBound).IsEqualTo(0);
        await Assert.That(freeRestarted).IsTrue();
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
        await Assert.That(free!.Status).IsEqualTo(JobStatus.Running);
        gate.Set();
    }

    #endregion

    #region Shutdown during the wait

    [Test]
    [Category("Slow")]
    public async Task ShutdownDuringTheWaitAbandonsTheRestartAndEndsTheJobWithSessionShutdown()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        using ManualResetEventSlim factoryReturned = new(false);
        JobInfo job = _AddParkedBoundJob(rig.Session, "bound", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        RestartOptions options = new() { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromMinutes(10) };
        ProbeProtocol? probe = null;

        SessionFailure? failure = null;
        Task<bool> restart = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                Stack created = ProbeStack.Create(registry, out probe);
                factoryReturned.Set();
                return created;
            },
            options,
            out _,
            out failure));
        await Assert.That(factoryReturned.Wait(_Timeout)).IsTrue();

        // Shutdown waits for the restart (today's _RestartIdle), the wait notices the flag, and the restart is abandoned.
        Task shutdown = RestartRig.OnDedicatedThread(() =>
        {
            rig.Session.Shutdown(_Timeout);
            return 0;
        });
        bool restarted = await restart.WaitAsync(_Timeout);
        await shutdown.WaitAsync(_Timeout);
        job.Join();

        await Assert.That(restarted).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        await Assert.That(failure.Value.Message).Contains("shutdown");
        await Assert.That(probe!.ShutdownCount).IsEqualTo(1);
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.SessionShutdown));
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
    }

    #endregion

    #region Report

    [Test]
    public async Task TheReportListsTheCancelledJobAndTheListenerWhoseFilterCouldNotBeRebound()
    {
        using RestartRig rig = new(frameCount: 12);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        JobInfo job = _AddParkedBoundJob(rig.Session, "bound", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();
        PacketFilter filter = SessionFixture.CompileOrThrow(rig.InitialStack, "udp.dstport == 53");
        _ = rig.Session.TryAddListener(new TestSessionListener(), filter, out ListenerInfo? listener, out _);

        // The probe stack defines no udp fields, so the listener's filter cannot be re-bound.
        bool restarted = rig.Session.TryRestart(registry => ProbeStack.Create(registry, out _), out RestartReport? report, out _);
        job.Join();

        await Assert.That(restarted).IsTrue();
        await Assert.That(report!.Generation).IsEqualTo(1);
        await Assert.That(report.ReparsedPackets).IsEqualTo(rig.Session.PacketCount);
        await Assert.That(report.JobsCancelled.Contains(job.Id)).IsTrue();
        await Assert.That(report.FiltersNotRebound.Contains(listener!.Id)).IsTrue();
        await Assert.That(report.ReparseDuration).IsGreaterThan(TimeSpan.Zero);
        gate.Set();
    }

    [Test]
    public async Task AFilterThatCannotBeReboundFailsMatchingReadsButNotUnfilteredOnesAndTheListenerStillGetsStackChanged()
    {
        using RestartRig rig = new(frameCount: 12);
        TestSessionListener listener = new();
        PacketFilter filter = SessionFixture.CompileOrThrow(rig.InitialStack, "udp.dstport == 53");
        _ = rig.Session.TryAddListener(listener, filter, out ListenerInfo? info, out _);

        bool restarted = rig.Session.TryRestart(registry => ProbeStack.Create(registry, out _), out RestartReport? report, out _);
        WaitHelper.WaitUntil(() => listener.StackChangedCount == 1);
        PacketRef[] destination = new PacketRef[12];
        bool matching = rig.Session.TryReadPackets(
            info!.Id, 0, destination, PacketReadMode.Matching, out _, out _, out FilterError? filterFailure, out _);
        bool all = rig.Session.TryReadPackets(info.Id, 0, destination, PacketReadMode.All, out int allCount, out _, out _, out _);

        await Assert.That(restarted).IsTrue();
        await Assert.That(report!.FiltersNotRebound.Contains(info.Id)).IsTrue();
        await Assert.That(matching).IsFalse();
        await Assert.That(filterFailure).IsNotNull();
        await Assert.That(all).IsTrue();
        await Assert.That(allCount).IsEqualTo(12);
        await Assert.That(listener.StackChangedCount).IsEqualTo(1);
    }

    [Test]
    public async Task TheOverloadWithoutOptionsReturnsTheSameShapeOfReport()
    {
        using RestartRig rig = new(frameCount: 6);

        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out RestartReport? report, out SessionFailure? failure);

        await Assert.That(restarted).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(report!.Generation).IsEqualTo(1);
        await Assert.That(report.ReparsedPackets).IsEqualTo(6);
        await Assert.That(report.JobsCancelled.Count).IsEqualTo(0);
        await Assert.That(report.JobsWaitedFor.Count).IsEqualTo(0);
        await Assert.That(report.FiltersNotRebound.Count).IsEqualTo(0);
    }

    #endregion

    #region Expected refusals and caller bugs

    [Test]
    public async Task ARestartBeforeStartIsRefusedWithInvalidPhaseNotThrown()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool restarted = session.TryRestart(registry => TestHarness.CreateStack(registry), out RestartReport? report, out SessionFailure? failure);

        await Assert.That(restarted).IsFalse();
        await Assert.That(report).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
    }

    [Test]
    public async Task ASecondRestartWhileOneRunsIsRefusedWithRestartInProgress()
    {
        using RestartRig rig = new(frameCount: 4);
        using ManualResetEventSlim factoryEntered = new(false);
        using ManualResetEventSlim factoryRelease = new(false);
        Task<bool> first = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                factoryEntered.Set();
                _ = factoryRelease.Wait(_Timeout);
                return TestHarness.CreateStack(registry);
            },
            out _,
            out _));
        await Assert.That(factoryEntered.Wait(_Timeout)).IsTrue();

        bool second = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out SessionFailure? failure);
        factoryRelease.Set();
        bool firstRestarted = await first.WaitAsync(_Timeout);

        await Assert.That(second).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.RestartInProgress);
        await Assert.That(firstRestarted).IsTrue();
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task AnIngestValueCacheRequestTheNewStackCannotSatisfyRefusesTheRestartAndChangesNothing()
    {
        using RestartRig rig = new(
            frameCount: 4,
            new SessionOptions { ValueCache = new ValueCacheRequest { FieldNames = ["udp.dstport"] } });
        ProbeProtocol? probe = null;

        bool restarted = rig.Session.TryRestart(
            registry => ProbeStack.Create(registry, out probe),
            out RestartReport? report,
            out SessionFailure? failure);

        await Assert.That(restarted).IsFalse();
        await Assert.That(report).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
        await Assert.That(rig.Session.QueriesEnabled).IsTrue();
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(probe!.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task ARestartRefusedByTheIngestRequestCostsNoRunningJobUnderEveryPolicy()
    {
        // The refusal depends only on the new stack, so it is decided before the job phase: a running export must survive a
        // restart that cannot happen (a regression would cancel it with StackReplaced and then refuse).
        using RestartRig rig = new(
            frameCount: 4,
            new SessionOptions { ValueCache = new ValueCacheRequest { FieldNames = ["udp.dstport"] } });
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim running = new(false);
        JobInfo bound = _AddParkedBoundJob(rig.Session, "export", gate, running);
        await Assert.That(running.Wait(_Timeout)).IsTrue();

        SessionFailure?[] refusals = new SessionFailure?[2];
        _ = rig.Session.TryRestart(
            registry => ProbeStack.Create(registry, out _),
            new RestartOptions { JobPolicy = RestartJobPolicy.CancelNow },
            out _,
            out refusals[0]);
        _ = rig.Session.TryRestart(
            registry => ProbeStack.Create(registry, out _),
            new RestartOptions { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromSeconds(1) },
            out _,
            out refusals[1]);

        await Assert.That(refusals[0]!.Value.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        await Assert.That(refusals[1]!.Value.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        await Assert.That(bound.Status).IsEqualTo(JobStatus.Running);
        await Assert.That(bound.EndReason).IsEqualTo(JobEndReason.None);
        gate.Set();
    }

    [Test]
    public async Task CallerBugsStillThrow()
    {
        using RestartRig rig = new(frameCount: 4);

        await Assert.That(() => rig.Session.TryRestart(null!, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), null!, out _, out _))
            .Throws<ArgumentNullException>();
        await Assert.That(() => rig.Session.TryRestart(_ => null!, out _, out _)).Throws<InvalidOperationException>();
        await Assert.That(() => rig.Session.TryRestart(_ => TestHarness.CreateStack(), out _, out _)).Throws<ArgumentException>();
        await Assert.That(rig.Session.Generation).IsEqualTo(0);
    }

    [Test]
    public async Task ARestartOnADisposedSessionThrowsDisposed()
    {
        using Stack stack = TestHarness.CreateStack();
        Session session = new(stack);
        session.Dispose();

        SessionException? thrown = null;
        try
        {
            _ = session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);
        }
        catch (SessionException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown?.Code).IsEqualTo(SessionErrorCode.Disposed);
    }

    #endregion
}
