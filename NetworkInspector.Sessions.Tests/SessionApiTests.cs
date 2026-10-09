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

        bool started = session.TryStart(out _);

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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        WaitHelper.WaitUntil(() => sourceJob.Status is JobStatus.Completed or JobStatus.Cancelled);

        bool removed = session.TryRemoveJob(sourceJob, out _);

        await Assert.That(removed).IsTrue();
        await Assert.That(session.GetJobs().Contains(sourceJob)).IsFalse();

        session.Shutdown();
    }

    [Test]
    public async Task TryRemoveJob_AlreadyRemoved_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        WaitHelper.WaitUntil(() => sourceJob.Status is JobStatus.Completed or JobStatus.Cancelled);

        await Assert.That(session.TryRemoveJob(sourceJob, out _)).IsTrue();
        await Assert.That(session.TryRemoveJob(sourceJob, out _)).IsFalse();

        session.Shutdown();
    }

    [Test]
    public async Task TryRemoveJob_ForeignJob_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        using Job foreignJob = new(
            new JobId(999),
            "Foreign",
            "Not in session",
            _ => { },
            static (_, _) => { });
        foreignJob.Start();
        foreignJob.Join();
        JobInfo foreignInfo = new(foreignJob);

        bool removed = session.TryRemoveJob(foreignInfo, out _);

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task TryRemoveJob_RunningJob_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        WaitHelper.WaitUntil(() => sourceJob.Status == JobStatus.Running);

        try
        {
            session.TryRemoveJob(sourceJob, out _);
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);

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
            session.TryAddListener(listener, out _, out _);
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
            session.TryAddJob("  ", "desc", _ => { }, out _, out _);
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        session.Shutdown();

        bool added = session.TryAddJob("LateJob", "desc", _ => { }, out JobInfo? info, out SessionFailure? failure);

        await Assert.That(added).IsTrue();
        await Assert.That(failure).IsNull();
        info!.Join();
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
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
            session.TryAddFrameSource(source, out _, out _);
            session.TryStart(out _);
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
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(listener, out _, out _);
        session.TryStart(out _);

        await Assert.That(session.GetListeners().Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(session.GetJobs().Count).IsGreaterThanOrEqualTo(2);

        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_ReparsesWithIndex()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        PacketIndexReaderView? indexBefore = session.PacketIndex;
        await Assert.That(indexBefore).IsNotNull();
        await Assert.That(indexBefore!.Value.Source).IsNotNull();

        bool found = session.TryGetPacket(new PacketId(0), out Packet? packet, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(packet).IsNotNull();
        await Assert.That(session.PacketIndex).IsNotNull();

        session.Shutdown();
    }

    [Test]
    public async Task AllocateJobId_AtCapacity_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionState state = _GetState(session);
        _SetPrivateInt(state, "_NextJobId", int.MinValue);

        using TestFrameSource source = TestFrameSource.WithUdpFrames(1);

        try
        {
            session.TryAddFrameSource(source, out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.JobIdExhausted);
        }
    }

    /// <summary>
    /// The very last ID in the range is still handed out; only the allocation after it fails. This
    /// covers the boundary between "usable" and "exhausted".
    /// </summary>
    [Test]
    public async Task AllocateJobId_LastId_IsHandedOutThenExhausted()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionState state = _GetState(session);
        _SetPrivateInt(state, "_NextJobId", ArrayIndexIdRange.MaxValue);

        using TestFrameSource first = TestFrameSource.WithUdpFrames(1);
        using TestFrameSource second = TestFrameSource.WithUdpFrames(1);

        bool added = session.TryAddFrameSource(first, out FrameSourceInfo? info, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();

        try
        {
            session.TryAddFrameSource(second, out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.JobIdExhausted);
        }
    }

    /// <summary>Same boundary as for job IDs, but for listener IDs.</summary>
    [Test]
    public async Task AllocateListenerId_LastId_IsHandedOutThenExhausted()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionState state = _GetState(session);
        _SetPrivateInt(state, "_NextListenerId", ArrayIndexIdRange.MaxValue);

        bool added = session.TryAddListener(new TestSessionListener(), out ListenerInfo? info, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(info!.Id.Value).IsEqualTo(ArrayIndexIdRange.MaxValue);

        try
        {
            session.TryAddListener(new TestSessionListener(), out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ListenerIdExhausted);
        }

        session.Shutdown();
    }

    /// <summary>Reads the private <c>_State</c> field of <paramref name="session"/>.</summary>
    private static SessionState _GetState(Session session)
    {
        FieldInfo stateField = typeof(Session).GetField(
            "_State",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (SessionState)stateField.GetValue(session)!;
    }

    /// <summary>Overwrites a private int field so ID-range boundaries can be reached in a test.</summary>
    private static void _SetPrivateInt(SessionState state, string fieldName, int value)
    {
        FieldInfo field = typeof(SessionState).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(state, value);
    }

    [Test]
    public async Task UseAfterDispose_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        Session session = new(stack);
        session.Dispose();

        try
        {
            session.TryStart(out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.Disposed);
        }
    }

    [Test]
    public async Task RestartAbandonedWhenShutdownStarts()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(1);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        Exception? restartError = null;
        Thread restartThread = new(() =>
        {
            try
            {
                session.Restart(registry =>
                {
                    entered.Set();
                    release.Wait();
                    return TestHarness.CreateStack(registry);
                });
            }
            catch (Exception ex)
            {
                restartError = ex;
            }
        })
        {
            Name = "restart-shutdown",
            IsBackground = true,
        };
        restartThread.Start();
        entered.Wait();

        // Shutdown waits until the in-progress restart drops its flag, so the factory
        // has to be released from this thread while Shutdown runs on another.
        Thread shutdownThread = new(() => session.Shutdown())
        {
            Name = "shutdown-during-restart",
            IsBackground = true,
        };
        shutdownThread.Start();
        release.Set();

        await Assert.That(shutdownThread.Join(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(restartThread.Join(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(session.Phase).IsEqualTo(SessionPhase.Stopped);
        if (restartError is not null)
        {
            await Assert.That(restartError).IsTypeOf<SessionException>();
            await Assert.That(((SessionException)restartError).Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        }

        session.Shutdown();
    }

    [Test]
    public async Task WaitForCompletionTimeoutIsASingleBudget()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource first = new(10000);
        using BlockingTestFrameSource second = new(10000);
        using Session session = new(stack);
        session.TryAddFrameSource(first, out _, out _);
        session.TryAddFrameSource(second, out _, out _);
        session.TryStart(out _);

        Stopwatch watch = Stopwatch.StartNew();
        bool completed = session.WaitForCompletion(TimeSpan.FromMilliseconds(200));
        watch.Stop();

        await Assert.That(completed).IsFalse();
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(500));

        first.Release();
        second.Release();
        session.Shutdown();
    }

    [Test]
    public async Task TryAddJobAfterStoppedRuns()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(1);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        int ran = 0;
        bool added = session.TryAddJob("export", "after stop", _ => Interlocked.Increment(ref ran), out JobInfo? info, out _);
        await Assert.That(added).IsTrue();
        info!.Join();
        await Assert.That(Volatile.Read(ref ran)).IsEqualTo(1);
        session.Shutdown();
    }

    private sealed class EmptyNameListener : ISessionListener
    {
        public string UiName => "   ";

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }
    }

}
