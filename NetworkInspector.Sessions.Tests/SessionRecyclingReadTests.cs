// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Covers the recycling overload of <see cref="ISessionReader.TryGetPacket(PacketId, Packet, out Packet)"/>:
/// a listener that reads with the packet store off should be able to re-parse into its own packet
/// object instead of allocating one per read.
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        // First read allocates, the following reads must land in that same object.
        session.TryGetPacket(new PacketId(0), recycle: null, out Packet? recycle);
        bool found = session.TryGetPacket(new PacketId(1), recycle, out Packet? packet);

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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        session.TryGetPacket(new PacketId(0), recycle: null, out Packet? recycle);
        session.TryGetPacket(new PacketId(3), recycle, out Packet? recycled);
        session.TryGetPacket(new PacketId(3), recycle: null, out Packet? allocated);

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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        Packet? recycle = null;
        List<int> firstPass = _ReadFieldCounts(session, frameCount, ref recycle);
        List<int> secondPass = _ReadFieldCounts(session, frameCount, ref recycle);

        await Assert.That(secondPass).IsEquivalentTo(firstPass);

        session.Shutdown();
    }

    /// <summary>
    /// With the store on, a hit must hand out the stored instance. The caller's recycle packet stays
    /// untouched, which is what lets a caller pass one unconditionally.
    /// </summary>
    [Test]
    public async Task TryGetPacket_StoreOn_ReturnsStoredInstanceAndLeavesRecycleAlone()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        // A packet the caller owns: re-parsed from the source, never handed to the store.
        Frame frame = source.FrameById(new FrameId(0))!.Value;
        Packet owned = Packet.ParseFrame(new PacketId(0), stack, frame);
        int ownedFieldCount = owned.FieldCount(materialize: true);

        bool found = session.TryGetPacket(new PacketId(2), owned, out Packet? packet);

        await Assert.That(found).IsTrue();
        await Assert.That(ReferenceEquals(packet, owned)).IsFalse();
        await Assert.That(owned.Id).IsEqualTo(new PacketId(0));
        await Assert.That(owned.FieldCount(materialize: false)).IsEqualTo(ownedFieldCount);

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
        foreignSession.TryAddFrameSource(foreignSource, out _);
        foreignSession.TryStart();
        foreignSession.WaitForCompletion();
        foreignSession.TryGetPacket(new PacketId(0), recycle: null, out Packet? foreignPacket);

        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool found = session.TryGetPacket(new PacketId(1), foreignPacket, out Packet? packet);

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
        session.TryAddFrameSource(source, out _);
        session.TryAddListener(listener1, out _);
        session.TryAddListener(listener2, out _);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        await Assert.That(listener1.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener2.PacketsSeen).IsEqualTo(frameCount);
        await Assert.That(listener1.Misses).IsEqualTo(0);
        await Assert.That(listener2.Misses).IsEqualTo(0);
    }

    private static List<int> _ReadFieldCounts(Session session, int frameCount, ref Packet? recycle)
    {
        List<int> counts = [];
        for (int i = 0; i < frameCount; i++)
        {
            if (!session.TryGetPacket(new PacketId(i), recycle, out Packet? packet))
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
                if (!session.TryGetPacket(new PacketId(i), _Recycle, out Packet? packet))
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
