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
            work,
            static (_, _) => { });
    }
}
