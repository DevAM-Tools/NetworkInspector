// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Allocation behaviour of the restart re-parse: it must recycle one packet instead of allocating one per frame.</summary>
[Category("Slow")]
internal sealed class SessionRestartAllocationTests
{
    /// <summary>
    /// Bytes the calling thread allocates while <see cref="Session.Restart"/> re-parses <paramref name="frameCount"/> frames.
    /// The re-parse runs on the calling thread, so the per-thread counter sees all of it. The packet index is off: it grows
    /// with every packet by design and would hide the packet allocation this test is about.
    /// </summary>
    private static long _MeasureRestartAllocation(int frameCount)
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { IndexPackets = false });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => session.PacketCount == frameCount && session.Phase == SessionPhase.Stopped);

        long before = GC.GetAllocatedBytesForCurrentThread();
        session.RestartOrThrow(registry => TestHarness.CreateStack(registry));
        long after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    /// <summary>
    /// The stack lease on the read path costs two interlocked operations and no allocation: a recycling
    /// <c>TryGetPacket</c> allocates what the bare frame read plus the bare recycling parse allocate. The tolerance of
    /// 8 B per call absorbs the allocations of the JIT and the tiering on the measuring thread; a lease that allocated
    /// (a boxed or captured lease is at least 24 B) would exceed it on every call.
    /// </summary>
    [Test]
    public async Task StackLeaseOnTheReadPathAllocatesNothing()
    {
        const int frameCount = 64;
        const int calls = 4000;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { IndexPackets = false });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => session.PacketCount == frameCount && session.Phase == SessionPhase.Stopped);
        ParseOptions options = new(FieldTreeMode.Build);

        // Warm up both loops (JIT, first-use tables), then measure one after the other on this thread.
        Packet? throughSession = null;
        Packet? bare = null;
        _ = _ReadThroughSession(session, ref throughSession, frameCount);
        _ = _ReadBare(source, stack, in options, ref bare, frameCount);

        long sessionBytes = 0;
        long bareBytes = 0;
        for (int i = 0; i < calls; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = session.TryGetPacket(i % frameCount, throughSession, out throughSession, out _);
            sessionBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            _ = _ReadBareOne(source, stack, in options, ref bare, i % frameCount);
            bareBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        await Assert.That(Math.Abs(sessionBytes - bareBytes)).IsLessThan(calls * 8L);
    }

    private static bool _ReadThroughSession(Session session, ref Packet? recycle, int frameCount)
    {
        bool all = true;
        for (int i = 0; i < frameCount; i++)
        {
            all &= session.TryGetPacket(i, recycle, out recycle, out _);
        }

        return all;
    }

    private static bool _ReadBare(TestFrameSource source, Stack stack, in ParseOptions options, ref Packet? recycle, int frameCount)
    {
        bool all = true;
        for (int i = 0; i < frameCount; i++)
        {
            all &= _ReadBareOne(source, stack, in options, ref recycle, i);
        }

        return all;
    }

    private static bool _ReadBareOne(TestFrameSource source, Stack stack, in ParseOptions options, ref Packet? recycle, int index)
    {
        Frame? frame = source.FrameById(index);
        if (frame is null)
        {
            return false;
        }

        if (recycle is not null && Packet.TryParse(recycle, index, stack, frame.Value, in options, out _))
        {
            return true;
        }

        bool parsed = Packet.TryParse(index, stack, frame.Value, in options, out Packet? created, out _);
        recycle = created;
        return parsed;
    }

    [Test]
    public async Task RestartReparseAllocatesNoPacketPerFrame()
    {
        // The stack the factory builds is a fixed cost; only the re-parse scales with the frame count. Comparing two sizes
        // cancels the fixed part. Without recycling, every frame costs a new Packet (about 400 bytes); with it, what is left
        // is the skip parse itself (about 110 bytes per frame in the parser), well below that.
        const int small = 200;
        const int large = 2200;
        _ = _MeasureRestartAllocation(small);

        long smallBytes = _MeasureRestartAllocation(small);
        long largeBytes = _MeasureRestartAllocation(large);

        long perExtraFrame = (largeBytes - smallBytes) / (large - small);
        await Assert.That(perExtraFrame).IsLessThan(200);
    }
}
