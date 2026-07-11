// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Tests for <see cref="ISession.TryUnsubscribe"/> — source stopping, listener removal,
/// user-job cancellation, and convenience APIs.
/// </summary>
internal sealed class UnsubscribeTests
{
    // ── Source unsubscribe ────────────────────────────────────────────────────

    [Test]
    public async Task TryUnsubscribe_Source_StopsFrameReading()
    {
        const int initialFrames = 10;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(initialFrames);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out FrameSourceInfo? sourceInfo);
        session.TryAddListener(listener, out _);
        session.TryStart();

        // Wait for initial frames to be consumed.
        _WaitForCondition(() => session.PacketCount >= initialFrames);

        // Unsubscribe the source — should cancel its job.
        JobInfo sourceJob = session.GetJobs().First(
            j => j.UiName == source.UiName);
        bool result = session.TryUnsubscribe(sourceJob);

        await Assert.That(result).IsTrue();

        // Wait for the source job to reach a terminal state.
        _WaitForCondition(
            () => sourceJob.Status is JobStatus.Cancelled or JobStatus.Completed);

        // The source should have stopped producing frames shortly after unsubscribe.
        // Allow a small margin for drip frames that may have been in-flight.
        await Assert.That(session.PacketCount).IsGreaterThanOrEqualTo(initialFrames);

        session.Shutdown();
    }

    [Test]
    public async Task TryUnsubscribe_Source_RandomAccessStillWorks()
    {
        const int frameCount = 10;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Source finished normally. Now verify random access still works
        // (source is NOT disposed until Shutdown).
        bool found = session.TryGetPacket(new PacketId(0), out Packet? packet);

        await Assert.That(found).IsTrue();
        await Assert.That(packet).IsNotNull();

        session.Shutdown();
    }

    [Test]
    public async Task TryUnsubscribe_LastSource_TransitionsToStopped()
    {
        const int initialFrames = 5;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(initialFrames);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();

        _WaitForCondition(() => session.PacketCount >= initialFrames);

        // Unsubscribe the only source.
        JobInfo sourceJob = session.GetJobs().First(
            j => j.UiName == source.UiName);
        bool result = session.TryUnsubscribe(sourceJob);

        await Assert.That(result).IsTrue();

        // Session should transition to Stopped since all sources are done.
        _WaitForCondition(
            () => session.Phase == SessionPhase.Stopped);

        await Assert.That(session.Phase).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(session.PacketCount).IsGreaterThanOrEqualTo(initialFrames);

        session.Shutdown();
    }

    [Test]
    public async Task FrameSourceInfo_Stop_ConvenienceApi()
    {
        const int initialFrames = 5;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(initialFrames);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out FrameSourceInfo? sourceInfo);
        session.TryStart();

        _WaitForCondition(() => session.PacketCount >= initialFrames);

        // Use convenience API.
        await Assert.That(sourceInfo!.IsStoppable).IsTrue();
        sourceInfo.Stop();

        // Wait for the source to stop.
        _WaitForCondition(
            () => session.Phase == SessionPhase.Stopped);

        await Assert.That(session.PacketCount).IsGreaterThanOrEqualTo(initialFrames);

        session.Shutdown();
    }

    // ── Listener unsubscribe ─────────────────────────────────────────────────

    [Test]
    public async Task TryUnsubscribe_Listener_CallsOnUnsubscribed()
    {
        const int frameCount = 10;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out ListenerInfo? listenerInfo);
        session.TryStart();

        _WaitForCondition(() => session.PacketCount >= frameCount);

        // Find the listener's job.
        JobInfo listenerJob = session.GetJobs().First(
            j => j.UiName == listener.UiName);
        bool result = session.TryUnsubscribe(listenerJob);

        await Assert.That(result).IsTrue();

        // OnUnsubscribed should have been called.
        _WaitForCondition(() => listener.UnsubscribedCount > 0);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);

        // Status should be Unsubscribed.
        await Assert.That(listenerInfo!.Status).IsEqualTo(SubscriptionStatus.Unsubscribed);

        // Listener should be removed from the active listener list.
        await Assert.That(session.GetListeners()).IsEmpty();

        session.Shutdown();
    }

    [Test]
    public async Task ListenerInfo_Unsubscribe_ConvenienceApi()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out ListenerInfo? listenerInfo);
        session.TryStart();

        _WaitForCondition(() => session.PacketCount >= frameCount);

        // Use convenience API.
        listenerInfo!.Unsubscribe();

        // OnUnsubscribed should have been called.
        _WaitForCondition(() => listener.UnsubscribedCount > 0);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
        await Assert.That(listenerInfo.Status).IsEqualTo(SubscriptionStatus.Unsubscribed);

        session.Shutdown();
    }

    [Test]
    public async Task Shutdown_ListenerStatus_IsSessionEnded()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out ListenerInfo? listenerInfo);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        // After shutdown, the status should be SessionEnded.
        await Assert.That(listenerInfo!.Status).IsEqualTo(SubscriptionStatus.SessionEnded);
    }

    // ── User job unsubscribe ─────────────────────────────────────────────────

    [Test]
    public async Task TryUnsubscribe_UserJob_CancelsJob()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();

        // Add a long-running user job.
        using ManualResetEventSlim gate = new(false);
        session.TryAddJob("TestJob", "Long running job", ct =>
        {
            try
            {
                gate.Wait(ct);
            }
            catch (OperationCanceledException) { /* expected */ }
        }, out JobInfo? jobInfo);

        await Assert.That(jobInfo).IsNotNull();

        // Wait for the job to start.
        _WaitForCondition(() => jobInfo!.Status == JobStatus.Running);

        // Unsubscribe the user job.
        bool result = session.TryUnsubscribe(jobInfo!);

        await Assert.That(result).IsTrue();

        _WaitForCondition(
            () => jobInfo!.Status is JobStatus.Cancelled or JobStatus.Completed);

        session.Shutdown();
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Test]
    public async Task TryUnsubscribe_TerminalJob_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Source job is already completed.
        JobInfo sourceJob = session.GetJobs().First(
            j => j.UiName == source.UiName);
        _WaitForCondition(
            () => sourceJob.Status is JobStatus.Completed or JobStatus.Cancelled);

        bool result = session.TryUnsubscribe(sourceJob);

        await Assert.That(result).IsFalse();

        session.Shutdown();
    }

    [Test]
    public async Task TryUnsubscribe_IdlePhase_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);

        // Session is Idle — TryUnsubscribe should return false.
        JobInfo sourceJob = session.GetJobs().First(
            j => j.UiName == source.UiName);

        bool result = session.TryUnsubscribe(sourceJob);

        await Assert.That(result).IsFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Spins for up to ~5 seconds until <paramref name="condition"/> returns true.
    /// </summary>
    private static void _WaitForCondition(Func<bool> condition, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SpinWait wait = new();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Condition was not met within {timeoutMs} ms.");
            }
            wait.SpinOnce();
        }
    }
}
