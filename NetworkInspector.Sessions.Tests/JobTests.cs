// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Unit tests for <see cref="Job"/> and <see cref="JobInfo"/>.</summary>
internal sealed class JobTests
{
    [Test]
    public async Task Job_BeforeStart_HasNullTimestamps()
    {
        using Job job = _CreateJob(_ => { });

        await Assert.That(job.StartTime).IsNull();
        await Assert.That(job.EndTime).IsNull();
        await Assert.That(job.FailureException).IsNull();
    }

    [Test]
    public async Task Job_AfterCompletion_HasTimestamps()
    {
        using ManualResetEventSlim started = new(false);
        using Job job = _CreateJob(_ => started.Set());

        job.Start();
        started.Wait(TimeSpan.FromSeconds(5));
        job.Join();

        await Assert.That(job.StartTime).IsNotNull();
        await Assert.That(job.EndTime).IsNotNull();
        await Assert.That(job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(job.FailureException).IsNull();
    }

    [Test]
    public async Task Job_OnFailure_RecordsException()
    {
        InvalidOperationException expected = new("job failed");
        using Job job = _CreateJob(_ => throw expected);

        job.Start();
        job.Join();

        await Assert.That(job.Status).IsEqualTo(JobStatus.Failed);
        await Assert.That(job.FailureException).IsSameReferenceAs(expected);
        await Assert.That(job.EndTime).IsNotNull();
    }

    [Test]
    public async Task Job_DoubleStart_IsIgnored()
    {
        using ManualResetEventSlim gate = new(false);
        using Job job = _CreateJob(ct => gate.Wait(ct));

        job.Start();
        job.Start();

        gate.Set();
        job.Join();

        await Assert.That(job.Status).IsEqualTo(JobStatus.Completed);
    }

    [Test]
    public async Task Job_JoinWithTimeout_OnCompletedJob_ReturnsTrueImmediately()
    {
        using Job job = _CreateJob(_ => { });

        job.Start();
        job.Join();

        bool joined = job.Join(TimeSpan.FromMilliseconds(1));

        await Assert.That(joined).IsTrue();
    }

    [Test]
    public async Task Job_JoinWithTimeout_ReturnsFalseWhileRunning()
    {
        using ManualResetEventSlim gate = new(false);
        using Job job = _CreateJob(ct => gate.Wait(ct));

        job.Start();

        bool joined = job.Join(TimeSpan.FromMilliseconds(50));

        await Assert.That(joined).IsFalse();

        gate.Set();
        job.Join();
    }

    [Test]
    public async Task JobInfo_ExposesUnderlyingJobState()
    {
        InvalidOperationException expected = new("job failed");
        using Job job = _CreateJob(_ => throw expected);
        JobInfo info = new(job);

        await Assert.That(info.Id.IsValid).IsTrue();
        await Assert.That(info.UiName).IsEqualTo("TestJob");
        await Assert.That(info.Description).IsEqualTo("Test job");
        await Assert.That(info.StartTime).IsNull();

        job.Start();
        job.Join();

        await Assert.That(info.StartTime).IsNotNull();
        await Assert.That(info.EndTime).IsNotNull();
        await Assert.That(info.FailureException).IsSameReferenceAs(expected);
        await Assert.That(info.Status).IsEqualTo(JobStatus.Failed);
    }

    [Test]
    public async Task JobCancelAfterDisposeDoesNotThrow()
    {
        // The session disposes a job at shutdown while callers still hold its JobInfo; cancelling a finished job is a no-op.
        Job job = _CreateJob(static _ => { });
        job.Start();
        job.Join();
        job.Dispose();

        Exception? thrown = null;
        try
        {
            job.Cancel();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
    }

    [Test]
    public async Task JobCompletingAfterDisposeStillReachesItsTerminalStatus()
    {
        // Shutdown with a timeout disposes a job whose thread still runs. When that thread ends, publishing the terminal
        // status (which sets the completion event) must not throw on the disposed event.
        using ManualResetEventSlim gate = new(false);
        Job job = _CreateJob(ct => gate.Wait(TimeSpan.FromSeconds(30), ct));
        job.Start();
        job.Dispose();

        gate.Set();
        WaitHelper.WaitUntil(() => job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed);

        await Assert.That(job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(job.FailureException).IsNull();
    }

    [Test]
    public async Task JobTryStartStartsAPendingJobAndIgnoresARepeat()
    {
        using ManualResetEventSlim gate = new(false);
        using Job job = _CreateJob(ct => gate.Wait(TimeSpan.FromSeconds(30), ct));

        bool first = job.TryStart(out Exception? firstFailure);
        bool second = job.TryStart(out Exception? secondFailure);

        await Assert.That(first).IsTrue();
        await Assert.That(firstFailure).IsNull();
        await Assert.That(second).IsTrue();
        await Assert.That(secondFailure).IsNull();
        await Assert.That(job.Status).IsEqualTo(JobStatus.Running);
        gate.Set();
        job.Join();
    }

    private static Job _CreateJob(Action<CancellationToken> work)
    {
        return new Job(
            new JobId(1),
            "TestJob",
            "Test job",
            JobStackBinding.StackBound,
            generation: 0,
            work,
            static (_, _) => { });
    }

    #region Binding and end reason

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A finished, empty session (no sources): jobs can be added in every phase until shutdown.</summary>
    private static Session _NewSession(out Stack stack)
    {
        stack = TestHarness.CreateStack();
        Session session = new(stack);
        _ = session.TryStart(out _);
        return session;
    }

    [Test]
    public async Task TheOldOverloadAddsAStackBoundJobAndTheNewOneCanDeclareAnIndependentJob()
    {
        Session session = _NewSession(out Stack stack);
        using ManualResetEventSlim gate = new(false);
        try
        {
            _ = session.TryAddJob("bound", "old overload", ct => gate.Wait(_Timeout, ct), out JobInfo? bound, out _);
            _ = session.TryAddJob(
                "free", "independent", JobStackBinding.Independent, context => gate.Wait(_Timeout, context.CancellationToken), out JobInfo? free, out _);

            await Assert.That(bound!.Binding).IsEqualTo(JobStackBinding.StackBound);
            await Assert.That(bound.Generation).IsEqualTo(session.Generation);
            await Assert.That(free!.Binding).IsEqualTo(JobStackBinding.Independent);
            await Assert.That(free.Generation).IsEqualTo(-1);
            await Assert.That(bound.EndReason).IsEqualTo(JobEndReason.None);
        }
        finally
        {
            gate.Set();
            session.Dispose();
            stack.Dispose();
        }
    }

    [Test]
    public async Task AJobContextCarriesTheTokenTheViewItStartedInAndTheSession()
    {
        Session session = _NewSession(out Stack stack);
        using ManualResetEventSlim done = new(false);
        JobContext captured = default;
        try
        {
            _ = session.TryAddJob(
                "ctx",
                "reads its context",
                JobStackBinding.StackBound,
                context =>
                {
                    captured = context;
                    done.Set();
                },
                out _,
                out _);
            await Assert.That(done.Wait(_Timeout)).IsTrue();

            await Assert.That(ReferenceEquals(captured.Stack.Stack, stack)).IsTrue();
            await Assert.That(captured.Stack.Generation).IsEqualTo(0);
            await Assert.That(ReferenceEquals(captured.Session, session)).IsTrue();
            await Assert.That(captured.CancellationToken.CanBeCanceled).IsTrue();
        }
        finally
        {
            session.Dispose();
            stack.Dispose();
        }
    }

    [Test]
    public async Task AnUndefinedBindingIsACallerBug()
    {
        Session session = _NewSession(out Stack stack);
        try
        {
            await Assert.That(() => session.TryAddJob("x", "y", (JobStackBinding)7, static _ => { }, out _, out _))
                .Throws<ArgumentOutOfRangeException>();
        }
        finally
        {
            session.Dispose();
            stack.Dispose();
        }
    }

    [Test]
    public async Task EveryEndOfAJobCarriesItsReasonInTheSameSnapshotAsTheTerminalStatus()
    {
        Session session = _NewSession(out Stack stack);
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim shutdownJobRunning = new(false);
        try
        {
            _ = session.TryAddJob("completes", "ends by itself", static _ => { }, out JobInfo? completes, out _);
            _ = session.TryAddJob("user", "cancelled by a user", ct => gate.Wait(_Timeout, ct), out JobInfo? user, out _);
            _ = session.TryAddJob("fails", "throws", static _ => throw new InvalidOperationException("boom"), out JobInfo? fails, out _);
            _ = session.TryAddJob(
                "shutdown",
                "ended by the shutdown",
                ct =>
                {
                    shutdownJobRunning.Set();
                    _ = gate.Wait(_Timeout, ct);
                    ct.ThrowIfCancellationRequested();
                },
                out JobInfo? shutdownJob,
                out _);
            await Assert.That(shutdownJobRunning.Wait(_Timeout)).IsTrue();

            completes!.Join(); // Non-null: the add succeeded.
            fails!.Join(); // Non-null: the add succeeded.
            bool unsubscribed = session.TryUnsubscribe(user!, out _); // Non-null: the add succeeded.
            session.Shutdown(_Timeout);
            shutdownJob!.Join(); // Non-null: the add succeeded.

            await Assert.That(unsubscribed).IsTrue();
            await Assert.That((completes.Status, completes.EndReason)).IsEqualTo((JobStatus.Completed, JobEndReason.Completed));
            await Assert.That((user!.Status, user.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.UserCancelled));
            await Assert.That((fails.Status, fails.EndReason)).IsEqualTo((JobStatus.Failed, JobEndReason.Failed));
            await Assert.That((shutdownJob.Status, shutdownJob.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.SessionShutdown));
        }
        finally
        {
            gate.Set();
            session.Dispose();
            stack.Dispose();
        }
    }

    [Test]
    public async Task AListenerNeverSeesATerminalStatusWithoutItsReason()
    {
        Session session = _NewSession(out Stack stack);
        using ManualResetEventSlim gate = new(false);
        int violations = 0;
        int observedTerminal = 0;
        try
        {
            ReasonListener listener = new(
                static jobs =>
                {
                    // One status read, then the reason: a terminal status must already have its reason.
                    int bad = 0;
                    foreach (JobInfo job in jobs)
                    {
                        JobStatus status = job.Status;
                        JobEndReason reason = job.EndReason;
                        if (status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed && reason == JobEndReason.None)
                        {
                            bad++;
                        }
                    }

                    return bad;
                },
                bad => Interlocked.Add(ref violations, bad),
                () => Interlocked.Increment(ref observedTerminal));
            _ = session.TryAddListener(listener, out _, out _);

            for (int i = 0; i < 20; i++)
            {
                _ = session.TryAddJob("quick" + i.ToString(CultureInfo.InvariantCulture), "ends at once", static _ => { }, out JobInfo? job, out _);
                job!.Join(); // Non-null: the add succeeded.
            }

            WaitHelper.WaitUntil(() => Volatile.Read(ref observedTerminal) >= 1);
            await Assert.That(Volatile.Read(ref violations)).IsEqualTo(0);
        }
        finally
        {
            gate.Set();
            session.Dispose();
            stack.Dispose();
        }
    }

    /// <summary>Listener that inspects the job list on every <c>OnJobsChanged</c> with a caller-supplied check.</summary>
    private sealed class ReasonListener(Func<IReadOnlyList<JobInfo>, int> check, Action<int> report, Action sawTerminal) : ISessionListener
    {
        public string UiName => "ReasonListener";

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }

        public void OnJobsChanged(ISessionReader session)
        {
            IReadOnlyList<JobInfo> jobs = session.GetJobs();
            report(check(jobs));
            if (jobs.Any(static job => job.Status == JobStatus.Completed))
            {
                sawTerminal();
            }
        }
    }

    #endregion
}
