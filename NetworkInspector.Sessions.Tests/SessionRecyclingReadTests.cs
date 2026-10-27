// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Covers the recycling overload of <see cref="ISessionReader.TryGetPacket(PacketId, Packet, out Packet)"/>:
/// a listener re-parses into its own packet object instead of allocating one per read.
/// </summary>
internal sealed class SessionRecyclingReadTests
{
    [Test]
    public async Task TryGetPacket_StoreOff_ReturnsTheRecycleInstance()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        // First read allocates, the following reads must land in that same object.
        session.TryGetPacket(new PacketId(0), recycle: null, out Packet? recycle, out _);
        bool found = session.TryGetPacket(new PacketId(1), recycle, out Packet? packet, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(ReferenceEquals(packet, recycle)).IsTrue();
        await Assert.That(packet!.Id).IsEqualTo(new PacketId(1));

        session.Shutdown();
    }

    /// <summary>
    /// The recycled re-parse must produce the same fields as an allocating re-parse of the same id —
    /// recycling resets the field storage rather than appending to it.
    /// </summary>
    [Test]
    public async Task TryGetPacket_StoreOff_RecycledReadMatchesAllocatingRead()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        session.TryGetPacket(new PacketId(0), recycle: null, out Packet? recycle, out _);
        session.TryGetPacket(new PacketId(3), recycle, out Packet? recycled, out _);
        session.TryGetPacket(new PacketId(3), recycle: null, out Packet? allocated, out _);

        await Assert.That(_FieldIds(recycled!)).IsEquivalentTo(_FieldIds(allocated!));

        session.Shutdown();
    }

    /// <summary>
    /// Reading the same ids repeatedly through one recycle object must stay stable: the second pass
    /// has to reproduce what the first pass produced, not accumulate or lose fields.
    /// </summary>
    [Test]
    public async Task TryGetPacket_StoreOff_RepeatedRecycledReads_StayStable()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        Packet? recycle = null;
        List<int> firstPass = _ReadFieldCounts(session, frameCount, ref recycle);
        List<int> secondPass = _ReadFieldCounts(session, frameCount, ref recycle);

        await Assert.That(secondPass).IsEquivalentTo(firstPass);

        session.Shutdown();
    }

    /// <summary>
    /// Default session reparses into the caller's recycle packet; the session does not keep a stored instance.
    /// </summary>
    [Test]
    public async Task TryGetPacket_DefaultSession_ReusesCallerRecycle()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        session.TryGetPacket(new PacketId(0), recycle: null, out Packet? recycle, out _);
        bool found = session.TryGetPacket(new PacketId(2), recycle, out Packet? packet, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(ReferenceEquals(packet, recycle)).IsTrue();
        await Assert.That(packet!.Id).IsEqualTo(new PacketId(2));
        await Assert.That(packet.HasFieldTree).IsTrue();

        session.Shutdown();
    }

    [Test]
    public async Task ReadPackets_PreallocatedRecycles_ReusesInstances()
    {
        const int frameCount = 4;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        Packet?[] buffer = new Packet?[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            session.TryGetPacket(new PacketId(i), out buffer[i], out _);
        }

        Packet?[] originals = new Packet?[frameCount];
        buffer.CopyTo(originals, 0);

        int read = session.ReadPackets(0, buffer);
        await Assert.That(read).IsEqualTo(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            await Assert.That(ReferenceEquals(buffer[i], originals[i])).IsTrue();
            await Assert.That(buffer[i]!.Id).IsEqualTo(new PacketId(i));
            await Assert.That(buffer[i]!.HasFieldTree).IsTrue();
        }

        session.Shutdown();
    }

    /// <summary>
    /// A recycle candidate that the packet layer refuses (here: a packet built on a different stack)
    /// must not surface as a failure — the read falls back to allocating a fresh packet.
    /// </summary>
    [Test]
    public async Task TryGetPacket_ForeignRecycle_FallsBackToFreshPacket()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using Stack foreignStack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using TestFrameSource foreignSource = TestFrameSource.WithUdpFrames(frameCount);

        using Session foreignSession = new(foreignStack, SessionOptions.RedissectOnly);
        foreignSession.TryAddFrameSource(foreignSource, out _, out _);
        foreignSession.TryStart(out _);
        foreignSession.WaitForCompletion();
        foreignSession.TryGetPacket(new PacketId(0), recycle: null, out Packet? foreignPacket, out _);

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        bool found = session.TryGetPacket(new PacketId(1), foreignPacket, out Packet? packet, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(ReferenceEquals(packet, foreignPacket)).IsFalse();
        await Assert.That(packet!.Id).IsEqualTo(new PacketId(1));

        session.Shutdown();
        foreignSession.Shutdown();
    }

    /// <summary>
    /// Two listeners reading in parallel, each with its own recycle packet: the per-thread ownership
    /// rule is what keeps this safe, and neither listener may lose a packet.
    /// </summary>
    [Test]
    public async Task TwoRecyclingListeners_EachSeesEveryPacket()
    {
        const int frameCount = 64;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        RecyclingListener listener1 = new("R1");
        RecyclingListener listener2 = new("R2");

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(listener1, out _, out _);
        session.TryAddListener(listener2, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        session.Shutdown();

        await Assert.That(listener1.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener2.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener1.Misses).IsEqualTo(0);
        await Assert.That(listener2.Misses).IsEqualTo(0);
    }

    /// <summary>
    /// A restart replaces the stack but keeps the frame registry, so a buffer of recycled packets of the old stack is rebound
    /// to the new one: the same instances come back, parsed on the new stack. The steady-state read after the restart costs
    /// what it cost before it (what is left is the parse itself); a fresh packet per slot would add the packet and its field
    /// storage to every slot.
    /// </summary>
    [Test]
    public async Task ABufferReadBeforeARestartComesBackAsTheSameInstancesOnTheNewStackAtTheSameAllocationCost()
    {
        const int slots = 64;
        using RestartRig rig = new(frameCount: slots);
        Packet?[] buffer = new Packet?[slots];
        int firstRead = rig.Session.ReadPackets(0, buffer);
        Packet?[] originals = new Packet?[slots];
        buffer.CopyTo(originals, 0);

        // Steady state before the restart: the second recycled read.
        _ = rig.Session.ReadPackets(0, buffer);
        long start = GC.GetAllocatedBytesForCurrentThread();
        _ = rig.Session.ReadPackets(0, buffer);
        long steadyBefore = GC.GetAllocatedBytesForCurrentThread() - start;

        Stack newStack = rig.RestartOntoStandard();
        _ = rig.Session.ReadPackets(0, buffer); // The rebind read: warms up the new stack's parse paths.
        start = GC.GetAllocatedBytesForCurrentThread();
        int secondRead = rig.Session.ReadPackets(0, buffer);
        long steadyAfter = GC.GetAllocatedBytesForCurrentThread() - start;

        await Assert.That(firstRead).IsEqualTo(slots);
        await Assert.That(secondRead).IsEqualTo(slots);
        for (int i = 0; i < slots; i++)
        {
            await Assert.That(ReferenceEquals(buffer[i], originals[i])).IsTrue();
            await Assert.That(ReferenceEquals(buffer[i]!.Stack, newStack)).IsTrue();
        }

        // 16 B per slot absorb tiering and cache effects; a fresh packet per slot is at least 400 B.
        await Assert.That(steadyAfter).IsLessThanOrEqualTo(steadyBefore + (slots * 16L));
    }

    /// <summary>
    /// The source thread keeps one recycle packet across frames. A restart replaces the stack under it; the next frame it
    /// ingests must be parsed into that same instance, rebound to the new stack.
    /// </summary>
    [Test]
    public async Task TheIngestRecyclePacketOfASourceThreadSurvivesARestart()
    {
        using Stack initial = TestHarness.CreateStack();
        using GatedRandomAccessSource source = new(freeFrames: 4, totalFrames: 6);
        using Session session = new(initial);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        WaitHelper.WaitUntil(() => session.PacketCount == 4 && source.Waiting.IsSet);

        // First restart onto a probe; the released frame is the first ingest after it and records the recycle instance.
        ProbeProtocol? first = null;
        _ = session.RestartOrThrow(registry => ProbeStack.Create(registry, out first));
        source.ReleaseNextFrame();
        WaitHelper.WaitUntil(() => session.PacketCount == 5);
        Packet? beforeSecondRestart = first!.LastParsedPacket; // Non-null: the factory ran.

        // Second restart onto another probe: the same instance must arrive there, now bound to the second stack.
        ProbeProtocol? second = null;
        Stack? secondStack = null;
        _ = session.RestartOrThrow(registry => secondStack = ProbeStack.Create(registry, out second));
        source.ReleaseNextFrame();
        WaitHelper.WaitUntil(() => session.PacketCount == 6);
        Packet? afterSecondRestart = second!.LastParsedPacket; // Non-null: the factory ran.
        session.Shutdown(TimeSpan.FromSeconds(30));

        await Assert.That(beforeSecondRestart).IsNotNull();
        await Assert.That(ReferenceEquals(beforeSecondRestart, afterSecondRestart)).IsTrue();
        await Assert.That(ReferenceEquals(afterSecondRestart!.Stack, secondStack)).IsTrue();
    }

    private static List<int> _ReadFieldCounts(Session session, int frameCount, ref Packet? recycle)
    {
        List<int> counts = [];
        for (int i = 0; i < frameCount; i++)
        {
            if (!session.TryGetPacket(new PacketId(i), recycle, out Packet? packet, out _))
            {
                counts.Add(-1);
                continue;
            }

            recycle = packet;
            counts.Add(packet.FieldCount(materialize: true));
        }

        return counts;
    }

    private static List<int> _FieldIds(Packet packet)
    {
        List<int> ids = [];
        foreach (Core.Fields.Field field in packet.IterFieldsFlat(materialize: true))
        {
            ids.Add(field.FieldId.Value);
        }

        return ids;
    }

    /// <summary>
    /// Reads every announced packet into one long-lived packet object. Its callback runs on the
    /// listener's own slot thread, so that object is never touched by another thread.
    /// </summary>
    private sealed class RecyclingListener : ISessionListener
    {
        private int _PacketsSeen;
        private int _Misses;
        private Packet? _Recycle;

        internal RecyclingListener(string name) => UiName = name;

        internal int PacketsSeen => Volatile.Read(ref _PacketsSeen);

        internal int Misses => Volatile.Read(ref _Misses);

        public string UiName { get; }

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                if (!session.TryGetPacket(new PacketId(i), _Recycle, out Packet? packet, out _))
                {
                    Interlocked.Increment(ref _Misses);
                    continue;
                }

                _Recycle = packet;
                Interlocked.Increment(ref _PacketsSeen);
            }
        }
    }
}
