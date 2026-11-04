// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// A restart that fails after the swap leaves the session <see cref="SessionPhase.Faulted"/>: the fault is kept, reads and new
/// work are refused with it, ingest stays parked, and another restart or the shutdown are the ways out. Every interleaving is
/// forced with gates; there are no sleeps. The only waits are bounded by <see cref="_Timeout"/>.
/// </summary>
internal sealed class SessionFaultTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    private const int _Frames = 10;

    /// <summary>
    /// A running session over a gated live source: <see cref="_Frames"/> frames are ingested, the source then waits for a
    /// release, so the ingest thread is alive and can be resumed at an exact point.
    /// </summary>
    private sealed class LiveRig : IDisposable
    {
        private readonly Stack _InitialStack;

        internal LiveRig()
        {
            _InitialStack = TestHarness.CreateStack();
            Source = new GatedRandomAccessSource(freeFrames: _Frames, totalFrames: _Frames + 2);
            Session = new Session(_InitialStack);
            _ = Session.TryAddFrameSource(Source, out _, out _);
            SourceJob = Session.GetJobs().First(job => job.UiName == "GatedSource");
            _ = Session.TryStart(out _);
            WaitHelper.WaitUntil(() => Session.PacketCount == _Frames && Source.Waiting.IsSet);
        }

        internal Session Session { get; }

        internal GatedRandomAccessSource Source { get; }

        internal JobInfo SourceJob { get; }

        public void Dispose()
        {
            Session.Dispose();
            _InitialStack.Dispose();
            Source.Dispose();
        }
    }

    /// <summary>Faults the session of <paramref name="rig"/>: frame 5 can no longer be re-read when the restart re-parses.</summary>
    private static (bool Restarted, SessionFailure? Failure) _Fault(LiveRig rig)
    {
        rig.Source.FailFrameId = 5;
        bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out SessionFailure? failure);
        return (restarted, failure);
    }

    #endregion

    #region State

    [Test]
    public async Task AFrameThatCannotBeReReadLeavesTheSessionFaultedAndRefusesEverythingWithTheFault()
    {
        using LiveRig rig = new();
        NullValueCacheListener cacheListener = new();
        int packetsBefore = rig.Session.PacketCount;

        (bool restarted, SessionFailure? failure) = _Fault(rig);

        bool packet = rig.Session.TryGetPacket(0, out _, out SessionFailure? packetFailure);
        bool listener = rig.Session.TryAddListener(new TestSessionListener(), out _, out SessionFailure? listenerFailure);
        bool cache = rig.Session.TryAddValueCache(
            cacheListener, new ValueCacheRequest { FieldNames = ["udp.dstport"] }, out _, out SessionFailure? cacheFailure);
        bool boundJob = rig.Session.TryAddJob("a", "bound", static _ => { }, out _, out SessionFailure? boundFailure);
        bool freeJob = rig.Session.TryAddJob("b", "free", JobStackBinding.Independent, static _ => { }, out _, out SessionFailure? freeFailure);
        bool started = rig.Session.TryStart(out SessionFailure? startFailure);
        bool unsubscribed = rig.Session.TryUnsubscribe(rig.SourceJob, out SessionFailure? unsubscribeFailure);
        rig.Source.ReleaseNextFrame();
        WaitHelper.WaitUntil(() => rig.Source.HandedOut == _Frames + 1);

        await Assert.That(restarted).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Faulted);
        await Assert.That(rig.Session.Fault!.Value.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(rig.Session.Fault!.Value.Message).IsEqualTo(failure.Value.Message);
        await Assert.That(rig.Session.QueriesEnabled).IsFalse();
        await Assert.That(rig.Session.Generation).IsEqualTo(1);
        await Assert.That(packet).IsFalse();
        await Assert.That(packetFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(packetFailure.Value.Message).Contains(failure.Value.Message);
        await Assert.That(listener || cache || boundJob || freeJob || started || unsubscribed).IsFalse();
        await Assert.That(listenerFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(cacheFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(boundFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(freeFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(startFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(unsubscribeFailure!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);

        // Ingest stays parked: the gate is closed, the frame the source handed out was not parsed.
        await Assert.That(rig.Session.ParseGateIsOpen).IsFalse();
        await Assert.That(rig.Session.PacketCount).IsLessThanOrEqualTo(packetsBefore);
        await Assert.That(rig.SourceJob.Status).IsEqualTo(JobStatus.Running);
    }

    [Test]
    public async Task ASecondRestartRecoversWithDenseIdsAndResumedIngest()
    {
        using LiveRig rig = new();
        TestSessionListener listener = new();
        _ = rig.Session.TryAddListener(listener, out _, out _);
        WaitHelper.WaitUntil(() => rig.Session.Phase == SessionPhase.Running);
        _ = _Fault(rig);
        rig.Source.ReleaseNextFrame();
        WaitHelper.WaitUntil(() => rig.Source.HandedOut == _Frames + 1);
        rig.Source.FailFrameId = -1;
        int stackChangedBefore = listener.StackChangedCount;

        bool recovered = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out RestartReport? report, out _);

        // The parked source now ingests the frame it already handed out: the next id is exactly the old count.
        WaitHelper.WaitUntil(() => rig.Session.PacketCount == _Frames + 1);
        bool read = rig.Session.TryGetPacket(_Frames, out Packet? last, out _);

        await Assert.That(recovered).IsTrue();
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Running);
        await Assert.That(rig.Session.Fault).IsNull();
        await Assert.That(rig.Session.Generation).IsEqualTo(2);
        await Assert.That(report!.ReparsedPackets).IsEqualTo(_Frames);
        await Assert.That(read).IsTrue();
        await Assert.That(last!.Id.Value).IsEqualTo(_Frames);
        await Assert.That(rig.SourceJob.Status).IsEqualTo(JobStatus.Running);
        WaitHelper.WaitUntil(() => listener.StackChangedCount == stackChangedBefore + 1);
        await Assert.That(rig.Session.ParseGateIsOpen).IsTrue();
    }

    [Test]
    public async Task ShutdownFromTheFaultedStateCompletesAndCancelsTheParkedSourceWithSessionShutdown()
    {
        using LiveRig rig = new();
        _ = _Fault(rig);
        rig.Source.ReleaseNextFrame();
        WaitHelper.WaitUntil(() => rig.Source.HandedOut == _Frames + 1);

        Exception? thrown = null;
        try
        {
            rig.Session.Shutdown(_Timeout);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        rig.SourceJob.Join();
        bool read = rig.Session.TryGetPacket(0, out _, out SessionFailure? readFailure);

        await Assert.That(thrown).IsNull();
        await Assert.That((rig.SourceJob.Status, rig.SourceJob.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.SessionShutdown));
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(rig.Session.Fault).IsNull();
        await Assert.That(read).IsFalse();
        await Assert.That(readFailure!.Value.Code).IsEqualTo(SessionErrorCode.QueriesDisabled);
    }

    [Test]
    public async Task ABugInTheReparseFaultsTheSessionAndStillPropagates()
    {
        // A source that throws something other than a SessionException is a bug, not an expected failure: it propagates, but
        // the session is faulted all the same instead of being left half-swapped.
        HookRandomAccessSource? hooked = null;
        using RestartRig rig = new(frameCount: 4, wrapSource: inner => hooked = new HookRandomAccessSource(inner));
        hooked!.OnFrameById = static _ => throw new InvalidOperationException("source bug"); // Non-null: the rig wrapped the source.

        InvalidOperationException? thrown = null;
        try
        {
            _ = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(rig.Session.Phase).IsEqualTo(SessionPhase.Faulted);
        await Assert.That(rig.Session.Fault!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
        await Assert.That(rig.Session.Fault!.Value.Message).Contains("source bug");
    }

    #endregion
}
