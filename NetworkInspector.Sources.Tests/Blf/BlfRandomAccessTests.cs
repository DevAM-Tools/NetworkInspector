// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Blf;
using NetworkInspector.Sources.Tests.Generators;

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for BLF random access and empty file handling.
/// Verifies frame_by_id, out-of-bounds access, and edge cases.
/// </summary>
internal sealed class BlfRandomAccessTests
{
    private static readonly byte[] BroadcastMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    private static BlfSource CreateSource(byte[] blfData) =>
        BlfSource.FromData(blfData, "test.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });

    private static void StartSource(BlfSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    // ========================================================================
    // Basic random access
    // ========================================================================

    [Test]
    public async Task RandomAccess_OutOfOrderRetrieval()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, [0xDE, 0xAD]);
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x123, [1, 2, 3, 4, 5, 6, 7, 8]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .AddCanFrame(2, can, 2_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);

        // Access frame 1 first (CAN)
        Frame? f1 = source.FrameById(new FrameId(1));
        await Assert.That(f1).IsNotNull();
        await Assert.That(f1!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(f1.Value.Id.Value).IsEqualTo(1);

        // Then access frame 0 (Ethernet)
        Frame? f0 = source.FrameById(new FrameId(0));
        await Assert.That(f0).IsNotNull();
        await Assert.That(f0!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(f0.Value.Id.Value).IsEqualTo(0);
        await Assert.That(f0.Value.Data.Span.SequenceEqual(eth)).IsTrue();
    }

    // ========================================================================
    // Out-of-bounds
    // ========================================================================

    [Test]
    public async Task RandomAccess_OutOfBoundsReturnsNull()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, [0x00]);
        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);

        // Valid
        await Assert.That(source.FrameById(new FrameId(0))).IsNotNull();

        // Out of bounds
        await Assert.That(source.FrameById(new FrameId(1))).IsNull();
        await Assert.That(source.FrameById(new FrameId(100))).IsNull();
    }

    // ========================================================================
    // Many frames
    // ========================================================================

    [Test]
    public async Task RandomAccess_ManyFrames_RandomOrder()
    {
        int frameCount = 50;
        BlfTestGenerator gen = new();
        List<byte[]> expectedFrames = [];

        for (int i = 0; i < frameCount; i++)
        {
            byte[] payload = Enumerable.Repeat((byte)i, 10).ToArray();
            byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, payload);
            expectedFrames.Add(eth);
            gen.AddEthernetFrame(1, eth, (i + 1) * 1_000_000L);
        }

        using BlfSource source = CreateSource(gen.Build());
        StartSource(source);

        // Access in pseudo-random order
        int[] accessOrder = [25, 0, 49, 10, 30, 5, 48, 1, 40, 15, 35, 20, 45, 2, 44, 12];
        foreach (int idx in accessOrder)
        {
            Frame? frame = source.FrameById(new FrameId(idx));
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(idx);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expectedFrames[idx])).IsTrue();
        }
    }

    [Test]
    public async Task RandomAccess_SameFrameTwice_ReturnsIdenticalData()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, [0xAA, 0xBB]);
        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);

        Frame? f1 = source.FrameById(new FrameId(0));
        Frame? f2 = source.FrameById(new FrameId(0));

        await Assert.That(f1).IsNotNull();
        await Assert.That(f2).IsNotNull();
        await Assert.That(f1!.Value.Data.Span.SequenceEqual(eth)).IsTrue();
        await Assert.That(f2!.Value.Data.Span.SequenceEqual(eth)).IsTrue();
    }

    // ========================================================================
    // Empty BLF
    // ========================================================================

    [Test]
    public async Task EmptyBlfFile_ReturnsZeroFrames()
    {
        byte[] blfData = new BlfTestGenerator().Build();

        using BlfSource source = CreateSource(blfData);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(0);

        StartSource(source);
        await Assert.That(source.NextFrame()).IsNull();
    }

    [Test]
    public async Task EmptyBlfFile_RandomAccessReturnsNull()
    {
        byte[] blfData = new BlfTestGenerator().Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);

        await Assert.That(source.FrameById(new FrameId(0))).IsNull();
    }

    // ========================================================================
    // Lifecycle guards — pre-Start contract
    // ========================================================================

    [Test]
    public async Task FrameById_BeforeStart_ThrowsInvalidOperationException()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, [0xAA]);
        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        // FrameById() before Start() must throw, consistent with AscSource and PcapSource.
        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NextFrame_BeforeStart_ThrowsInvalidOperationException()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        using BlfSource source = CreateSource(blfData);
        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }

    // ========================================================================
    // Concurrency — thread-safety of FrameById
    // ========================================================================

    /// <summary>
    /// Stress-tests concurrent <see cref="BlfSource.FrameById"/> calls from multiple
    /// threads to verify that the container cache lock, double-checked decompression,
    /// and interface registration lock are all correct under real race conditions.
    ///
    /// Design: 50 frames across 4 distinct containers (so cache misses and hits both
    /// occur during the parallel phase) accessed by 16 tasks performing 500 total
    /// lookups in a round-robin pattern.  Each returned frame is validated for ID
    /// and payload correctness, catching any data-race corruption.
    /// </summary>
    [Test]
    public async Task FrameById_ConcurrentAccess_NoRaceConditions()
    {
        const int FrameCount = 50;
        const int Parallelism = 16;
        const int TotalAccesses = 500;

        BlfTestGenerator gen = new();
        List<byte[]> expectedFrames = [];

        for (int i = 0; i < FrameCount; i++)
        {
            // Each frame has a unique last byte so data corruption is detectable.
            byte[] payload = new byte[12];
            payload[^1] = (byte)(i & 0xFF);
            byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, payload);
            expectedFrames.Add(eth);
            gen.AddEthernetFrame(1, eth, (i + 1) * 1_000_000L);
        }

        using BlfSource source = CreateSource(gen.Build());
        StartSource(source);

        List<Exception> exceptions = [];
        List<string> failures = [];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, TotalAccesses),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            async (i, _) =>
            {
                int frameIndex = i % FrameCount;
                Frame? frame = source.FrameById(new FrameId(frameIndex));

                if (frame is null)
                {
                    lock (failures)
                    {
                        failures.Add($"FrameById({frameIndex}) returned null");
                    }
                    return;
                }

                if (frame.Value.Id.Value != frameIndex)
                {
                    lock (failures)
                    {
                        failures.Add(
                            $"FrameById({frameIndex}): expected Id={frameIndex}, got Id={frame.Value.Id.Value}");
                    }
                    return;
                }

                if (!frame.Value.Data.Span.SequenceEqual(expectedFrames[frameIndex]))
                {
                    lock (failures)
                    {
                        failures.Add($"FrameById({frameIndex}): data mismatch (possible race corruption)");
                    }
                }

                await Task.CompletedTask.ConfigureAwait(false); // yield scheduling point
            }).ConfigureAwait(false);

        await Assert.That(failures).IsEmpty();
        await Assert.That(exceptions).IsEmpty();
    }

    /// <summary>
    /// Verifies that <see cref="BlfSource.Dispose"/> racing concurrently with multiple
    /// <see cref="BlfSource.FrameById"/> callers does not corrupt state, deadlock, or
    /// throw unhandled exceptions.  Callers may receive null or
    /// <see cref="ObjectDisposedException"/>; both outcomes are acceptable.
    /// </summary>
    [Test]
    public async Task FrameById_ConcurrentWithDispose_NoDeadlockOrCorruption()
    {
        const int FrameCount = 20;
        const int Parallelism = 8;
        const int TotalAccesses = 200;

        BlfTestGenerator gen = new();
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] eth = FrameBuilders.BuildEthernetFrame(BroadcastMac, SrcMac, 0x0800, [(byte)i]);
            gen.AddEthernetFrame(1, eth, (i + 1) * 1_000_000L);
        }

        BlfSource source = CreateSource(gen.Build());
        StartSource(source);

        // Dispose races against the parallel readers after half the accesses.
        int accessCount = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, TotalAccesses),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            async (i, _) =>
            {
                // Dispose after roughly half the tasks have been scheduled.
                if (Interlocked.Increment(ref accessCount) == TotalAccesses / 2)
                {
                    source.Dispose();
                }

                try
                {
                    // Acceptable outcomes: valid Frame, null (disposed / not found),
                    // or ObjectDisposedException.  Anything else is a bug.
                    source.FrameById(new FrameId(i % FrameCount));
                }
                catch (ObjectDisposedException) { /* expected after Dispose */ }

                await Task.CompletedTask.ConfigureAwait(false); // yield scheduling point
            }).ConfigureAwait(false);

        // Ensure Dispose was actually called (idempotent second call must not throw).
        source.Dispose();

        // If we reach here without deadlock or unhandled exception the test passes.
    }

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        using BlfSource source = CreateSource(blfData);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
