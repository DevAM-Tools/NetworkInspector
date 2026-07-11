// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FieldInfo = System.Reflection.FieldInfo;

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// API contract tests for <see cref="Session"/> — validation, errors, and public surface gaps.
/// </summary>
internal sealed class SessionApiTests
{
    [Test]
    public async Task TryStart_NoSources_TransitionsToStopped()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool started = session.TryStart();

        await Assert.That(started).IsTrue();
        await Assert.That(session.Phase).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(session.MorePacketsExpected).IsFalse();
    }

    [Test]
    public async Task ReadPackets_AfterParse_ReturnsStoredPackets()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        Packet?[] buffer = new Packet?[frameCount];
        int read = session.ReadPackets(0, buffer);

        await Assert.That(read).IsEqualTo(frameCount);
        await Assert.That(buffer[0]).IsNotNull();
        await Assert.That(buffer[frameCount - 1]).IsNotNull();

        session.Shutdown();
    }

    [Test]
    public async Task ReadPackets_WhenQueriesDisabled_ReturnsZero()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        Packet?[] buffer = new Packet?[1];
        int read = session.ReadPackets(0, buffer);

        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task TryRemoveJob_TerminalJob_Succeeds()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        _WaitForCondition(() => sourceJob.Status is JobStatus.Completed or JobStatus.Cancelled);

        bool removed = session.TryRemoveJob(sourceJob);

        await Assert.That(removed).IsTrue();
        await Assert.That(session.GetJobs().Contains(sourceJob)).IsFalse();

        session.Shutdown();
    }

    [Test]
    public async Task TryRemoveJob_RunningJob_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        _WaitForCondition(() => sourceJob.Status == JobStatus.Running);

        try
        {
            session.TryRemoveJob(sourceJob);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.JobStillRunning);
        }
        finally
        {
            source.Release();
            session.Shutdown();
        }
    }

    [Test]
    public async Task WaitForCompletion_WithTimeout_ReturnsFalseWhenSourcesStillRunning()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(100);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();

        bool completed = session.WaitForCompletion(TimeSpan.FromMilliseconds(50));

        await Assert.That(completed).IsFalse();

        source.Release();
        session.Shutdown();
    }

    [Test]
    public async Task TryAddListener_EmptyUiName_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        EmptyNameListener listener = new();

        try
        {
            session.TryAddListener(listener, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ListenerUiNameEmpty);
        }
    }

    [Test]
    public async Task TryAddJob_EmptyUiName_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        try
        {
            session.TryAddJob("  ", "desc", _ => { }, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.JobUiNameEmpty);
        }
    }

    [Test]
    public async Task TryAddJob_DuringShutdown_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        bool added = session.TryAddJob("LateJob", "desc", _ => { }, out JobInfo? info);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task Restart_FromIdle_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        try
        {
            session.Restart(_ => TestHarness.CreateStack());
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        }
    }

    [Test]
    public async Task Restart_ConcurrentSecondCall_ThrowsInvalidOperationException()
    {
        const int frameCount = 10;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        using ManualResetEventSlim otherStarted = new(false);
        using ManualResetEventSlim releaseFactory = new(false);

        Thread thread = new(() =>
        {
            otherStarted.Set();
            session.Restart(registry =>
            {
                releaseFactory.Wait();
                return TestHarness.CreateStack(registry);
            });
        })
        {
            Name = "restart-race",
            IsBackground = true,
            CurrentCulture = CultureInfo.InvariantCulture,
        };

        thread.Start();
        otherStarted.Wait();

        try
        {
            session.Restart(registry => TestHarness.CreateStack(registry));
            throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
        }
        catch (InvalidOperationException ex)
        {
            await Assert.That(ex.Message).Contains("already in progress");
        }

        releaseFactory.Set();
        thread.Join(TimeSpan.FromSeconds(10));

        session.Shutdown();
    }

    [Test]
    public async Task Dispose_AfterSourceDisposeFailure_PopulatesShutdownErrors()
    {
        using Stack stack = TestHarness.CreateStack();
        TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        try
        {
            source.ThrowOnDispose = true;

            Session session = new(stack);
            session.TryAddFrameSource(source, out _);
            session.TryStart();
            session.WaitForCompletion();
            session.Dispose();

            await Assert.That(session.ShutdownErrors).IsNotNull();
            await Assert.That(session.ShutdownErrors!.InnerExceptions.Count).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            source.ThrowOnDispose = false;
            source.Dispose();
        }
    }

    [Test]
    public async Task GetListeners_And_GetJobs_DuringRun_AreNonEmpty()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();

        await Assert.That(session.GetListeners().Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(session.GetJobs().Count).IsGreaterThanOrEqualTo(2);

        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_AfterStoreClear_ReparsesWithIndex()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        IPacketIndexReader? indexBefore = session.PacketIndex;
        await Assert.That(indexBefore).IsNotNull();

        PacketStore store = _GetPacketStore(session);
        store.Clear();

        bool found = session.TryGetPacket(new PacketId(0), out Packet? packet);

        await Assert.That(found).IsTrue();
        await Assert.That(packet).IsNotNull();
        await Assert.That(session.PacketIndex).IsNotNull();

        session.Shutdown();
    }

    [Test]
    public async Task UseAfterDispose_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        Session session = new(stack);
        session.Dispose();

        try
        {
            session.TryStart();
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.Disposed);
        }
    }

    private static PacketStore _GetPacketStore(Session session)
    {
        FieldInfo field = typeof(Session).GetField(
            "_PacketStore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (PacketStore)field.GetValue(session)!;
    }

    private static void _WaitForCondition(Func<bool> condition, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SpinWait wait = new();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Condition was not met within {timeoutMs} ms.");
            }
            wait.SpinOnce();
        }
    }

    private sealed class EmptyNameListener : ISessionListener
    {
        public string UiName => "   ";

        public void OnNewPackets(ISessionReader session, long fromIndex, long toIndexExclusive)
        {
        }
    }

}
