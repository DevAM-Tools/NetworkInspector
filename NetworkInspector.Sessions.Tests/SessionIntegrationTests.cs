// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Integration tests for <see cref="Session"/> — full lifecycle: Start → packets → listener → shutdown.
/// </summary>
internal sealed class SessionIntegrationTests
{
    [Test]
    public async Task StartAndRun_ListenerSeesAllPackets()
    {
        const int frameCount = 100;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Give the listener slot time to process remaining flags.
        WaitHelper.WaitUntil(() => listener.TotalPacketsSeen >= frameCount);

        session.Shutdown();

        await Assert.That(session.PacketCount).IsEqualTo(frameCount);
        await Assert.That(session.FrameCount).IsEqualTo(frameCount);
        await Assert.That(listener.TotalPacketsSeen).IsEqualTo(frameCount);
    }

    [Test]
    public async Task Listener_ReceivesAllSourcesCompleted()
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

        // Wait for the AllSourcesCompleted flag to propagate.
        WaitHelper.WaitUntil(() => listener.AllSourcesCompletedCount > 0);

        session.Shutdown();

        await Assert.That(listener.AllSourcesCompletedCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Listener_ReceivesPhaseChanged()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Wait for the listener to process all flags.
        WaitHelper.WaitUntil(() => listener.AllSourcesCompletedCount > 0);

        session.Shutdown();

        // PhaseChanged should have been received at least for Running and Stopped transitions.
        await Assert.That(listener.PhaseChangedCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Shutdown_ListenerReceivesShuttingDownAndUnsubscribed()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        // Wait for listener thread to drain.
        WaitHelper.WaitUntil(() => listener.UnsubscribedCount > 0);

        await Assert.That(listener.ShuttingDownCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetPacket_ReturnsStoredPacket()
    {
        const int frameCount = 10;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Packets 0 through frameCount-1 should be in the store.
        bool foundFirst = session.TryGetPacket(new PacketId(0), out Packet? first);
        bool foundLast = session.TryGetPacket(new PacketId(frameCount - 1), out Packet? last);

        await Assert.That(foundFirst).IsTrue();
        await Assert.That(foundLast).IsTrue();
        await Assert.That(first).IsNotNull();
        await Assert.That(last).IsNotNull();
        await Assert.That(first!.Id).IsEqualTo(new PacketId(0));
        await Assert.That(last!.Id).IsEqualTo(new PacketId(frameCount - 1));

        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_InvalidId_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();

        using Session session = new(stack);
        bool found = session.TryGetPacket(PacketId.Invalid, out Packet? result);

        await Assert.That(found).IsFalse();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Restart_ClearsCountersAndReparsesFromSources()
    {
        const int frameCount = 20;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Wait for listener to catch up.
        WaitHelper.WaitUntil(() => listener.TotalPacketsSeen >= frameCount);

        await Assert.That(session.PacketCount).IsEqualTo(frameCount);

        // Restart with a new stack built via factory (same registry, can use new settings).
        // Sources are NOT stopped — frames are re-parsed from random-access storage.
        session.Restart(registry => TestHarness.CreateStack(registry));

        // After restart, all sources had already finished, so phase is Stopped
        // immediately (no source threads to re-run).
        await Assert.That(session.Phase).IsEqualTo(SessionPhase.Stopped);

        // PacketCount equals frameCount because all frames were re-parsed from
        // the random-access source via the PacketToFrameMap.
        await Assert.That(session.PacketCount).IsEqualTo(frameCount);

        // Listener receives re-parsed packets via StackChanged + NewPackets notification.
        WaitHelper.WaitUntil(() => listener.TotalPacketsSeen >= frameCount * 2);
        await Assert.That(listener.TotalPacketsSeen).IsGreaterThanOrEqualTo(frameCount * 2);

        // Listener received exactly one OnStackChanged callback.
        WaitHelper.WaitUntil(() => listener.StackChangedCount >= 1);
        await Assert.That(listener.StackChangedCount).IsEqualTo(1);

        session.Shutdown();
    }

    [Test]
    public async Task PacketIndex_IsPopulatedDuringParsing()
    {
        const int frameCount = 10;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);

        // Before start, no index should exist.
        await Assert.That(session.PacketIndex).IsNull();

        session.TryStart();
        session.WaitForCompletion();

        // After parsing, the index should be populated.
        PacketIndexReaderView? index = session.PacketIndex;
        await Assert.That(index).IsNotNull();
        await Assert.That(index!.Value.Source).IsNotNull();

        session.Shutdown();
    }

    [Test]
    public async Task MultipleListeners_AllReceivePackets()
    {
        const int frameCount = 50;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener1 = new();
        TestSessionListener listener2 = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener1, out _);
        session.TryAddListener(listener2, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Both listeners should see all packets.
        WaitHelper.WaitUntil(() =>
            listener1.TotalPacketsSeen >= frameCount &&
            listener2.TotalPacketsSeen >= frameCount);

        session.Shutdown();

        await Assert.That(listener1.TotalPacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener2.TotalPacketsSeen).IsEqualTo(frameCount);
    }

    [Test]
    public async Task Dispose_ImplicitShutdown()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        TestSessionListener listener = new();

        Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener, out _);
        session.TryStart();
        session.WaitForCompletion();

        // Dispose triggers graceful shutdown.
        session.Dispose();

        // Listener should have been notified and unsubscribed.
        WaitHelper.WaitUntil(() => listener.UnsubscribedCount > 0);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Session_IdlePhase_BeforeStart()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(session.Phase).IsEqualTo(SessionPhase.Idle);
        await Assert.That(session.PacketCount).IsEqualTo(0);
        await Assert.That(session.FrameCount).IsEqualTo(0);
        await Assert.That(session.MorePacketsExpected).IsFalse();
    }

    [Test]
    public async Task TryAddFrameSource_AfterStart_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source1 = TestFrameSource.WithUdpFrames(5);
        using TestFrameSource source2 = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source1, out _);
        session.TryStart();

        // Adding a source after start should return false.
        bool added = session.TryAddFrameSource(source2, out FrameSourceInfo? info);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();

        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task GetFrameSources_ReturnsRegisteredSources()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);

        IReadOnlyList<FrameSourceInfo> sources = session.GetFrameSources();
        await Assert.That(sources.Count).IsEqualTo(1);
        await Assert.That(sources[0].UiName).IsEqualTo("TestSource");
    }

    [Test]
    public async Task RedissectOnly_TwoListeners_EachRedissectsEveryPacket()
    {
        const int frameCount = 64;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        RedissectListener listener1 = new("L1");
        RedissectListener listener2 = new("L2");

        using Session session = new(stack, SessionOptions.RedissectOnly);
        await Assert.That(session.StoreParsedPackets).IsFalse();
        await Assert.That(session.IndexPackets).IsFalse();

        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener1, out _);
        session.TryAddListener(listener2, out _);
        session.TryStart();
        await Assert.That(session.PacketIndex).IsNull();
        session.WaitForCompletion();
        session.Shutdown();

        await Assert.That(listener1.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener2.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener1.Misses).IsEqualTo(0);
        await Assert.That(listener2.Misses).IsEqualTo(0);

        PacketStore store = _GetPacketStore(session);
        await Assert.That(store.Get(new PacketId(0))).IsNull();
    }

    private static PacketStore _GetPacketStore(Session session)
    {
        System.Reflection.FieldInfo field = typeof(Session).GetField(
            "_PacketStore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (PacketStore)field.GetValue(session)!;
    }

    private sealed class RedissectListener : ISessionListener
    {
        private int _PacketsSeen;
        private int _Misses;

        internal RedissectListener(string name) => UiName = name;

        internal int PacketsSeen => Volatile.Read(ref _PacketsSeen);

        internal int Misses => Volatile.Read(ref _Misses);

        public string UiName { get; }

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                if (!session.TryGetPacket(new PacketId(i), out Packet? packet) || packet is null)
                {
                    Interlocked.Increment(ref _Misses);
                    continue;
                }

                Interlocked.Increment(ref _PacketsSeen);
            }
        }
    }
}
