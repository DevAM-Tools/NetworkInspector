// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <see cref="Session.Shutdown"/> must cancel, join and dispose every job it owns, including user jobs from
/// <c>TryAddJob</c>, within the one timeout budget, and a <see cref="JobInfo"/> must stay usable after the job was disposed.
/// </summary>
internal sealed class SessionShutdownCompletenessTests
{
    [Test]
    public async Task ShutdownCancelsAndJoinsUserJob()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        int cleanedUp = 0;
        bool added = session.TryAddJob(
            "UserJob",
            "waits for cancellation, then cleans up",
            ct =>
            {
                _ = ct.WaitHandle.WaitOne();
                Volatile.Write(ref cleanedUp, 1);
                ct.ThrowIfCancellationRequested();
            },
            out JobInfo? job,
            out _);

        session.Shutdown();

        // Joined: the cleanup after the cancellation has run before Shutdown returned.
        await Assert.That(added).IsTrue();
        await Assert.That(Volatile.Read(ref cleanedUp)).IsEqualTo(1);
        await Assert.That(job!.Status).IsEqualTo(JobStatus.Cancelled);
    }

    [Test]
    public async Task ShutdownWithZeroTimeoutDoesNotWaitForUserJobThatIgnoresCancellation()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using ManualResetEventSlim release = new(false);
        bool added = session.TryAddJob(
            "StubbornJob",
            "ignores its token",
            _ => release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None),
            out JobInfo? job,
            out _);

        session.Shutdown(TimeSpan.Zero);

        // The budget is spent (zero): shutdown returns although the job runs on. When the job ends later it finishes
        // normally even though the session already disposed it.
        await Assert.That(added).IsTrue();
        await Assert.That(job!.Status).IsEqualTo(JobStatus.Running);
        release.Set();
        WaitHelper.WaitUntil(() => job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed);
        await Assert.That(job.Status).IsEqualTo(JobStatus.Completed);
    }

    [Test]
    public async Task UserJobAddedAfterShutdownIsRefused()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.Shutdown();

        bool added = session.TryAddJob("Late", "late", static _ => { }, out JobInfo? job, out SessionFailure? failure);

        await Assert.That(added).IsFalse();
        await Assert.That(job).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
    }

    [Test]
    public async Task CancelOnSourceJobAfterShutdownDoesNotThrow()
    {
        // A JobInfo outlives the session's disposal of its job (callers keep it from GetJobs). Cancelling a job that is
        // already over is a no-op, not an ObjectDisposedException from the disposed token source.
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(2);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        JobInfo sourceJob = session.GetJobs().First(job => job.UiName == source.UiName);
        session.Shutdown();

        Exception? thrown = null;
        try
        {
            sourceJob.Cancel();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
    }

    [Test]
    public async Task CancelOnUserJobAfterShutdownDoesNotThrow()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddJob("UserJob", "quick", static _ => { }, out JobInfo? job, out _);
        session.Shutdown();

        Exception? thrown = null;
        try
        {
            job!.Cancel();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
    }
}
