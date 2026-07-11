// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for <see cref="BlfSource"/> in <see cref="ScanMode.Lazy"/> mode.
/// Verifies incremental scanning, on-demand frame availability, and random
/// access after sequential reading.
/// </summary>
internal sealed class BlfLazyScanTests
{
    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Creates a BlfSource in Lazy scan mode from in-memory data.</summary>
    private static BlfSource _CreateLazySource(byte[] data) =>
        BlfSource.FromData(data, "lazy-test.blf", new BlfSourceOptions { ScanMode = ScanMode.Lazy });

    /// <summary>Creates a BlfSource in Full scan mode from in-memory data.</summary>
    private static BlfSource _CreateFullSource(byte[] data) =>
        BlfSource.FromData(data, "full-test.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });



    // ========================================================================
    // EstimatedFrameCount is null initially in lazy mode
    // ========================================================================

    [Test]
    public async Task LazyMode_EstimatedFrameCount_NullBeforeFullConsumption()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xAA]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .AddEthernetFrame(1, eth, 2_000_000)
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        // Before Start(), EstimatedFrameCount should be null since indexing isn't complete
        await Assert.That(source.EstimatedFrameCount).IsNull();
    }

    // ========================================================================
    // Full mode: EstimatedFrameCount available immediately
    // ========================================================================

    [Test]
    public async Task FullMode_EstimatedFrameCount_AvailableImmediately()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xAA]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .AddEthernetFrame(1, eth, 2_000_000)
            .AddEthernetFrame(1, eth, 3_000_000)
            .Build();

        using BlfSource source = _CreateFullSource(blfData);
        // After full scan, frame count should be known
        await Assert.That(source.EstimatedFrameCount).IsNotNull();
        await Assert.That(source.EstimatedFrameCount!.Value).IsEqualTo(3);
    }

    // ========================================================================
    // Lazy mode reads all frames sequentially
    // ========================================================================

    [Test]
    public async Task LazyMode_NextFrame_ReadsAllFrames()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xBB]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .AddEthernetFrame(1, eth, 2_000_000)
            .AddEthernetFrame(1, eth, 3_000_000)
            .AddEthernetFrame(1, eth, 4_000_000)
            .AddEthernetFrame(1, eth, 5_000_000)
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(5);
        await Assert.That(source.ReadFrameCount).IsEqualTo(5);
    }

    // ========================================================================
    // Full vs Lazy produce same results
    // ========================================================================

    [Test]
    public async Task LazyAndFull_ProduceSameFrameCount()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xCC]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .AddEthernetFrame(1, eth, 2_000_000)
            .AddEthernetFrame(1, eth, 3_000_000)
            .Build();

        // Read all frames in lazy mode
        using BlfSource lazySource = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(lazySource);
        int lazyCount = 0;
        while (lazySource.NextFrame() is not null)
        {
            lazyCount++;
        }

        // Read all frames in full mode
        using BlfSource fullSource = _CreateFullSource(blfData);
        SourceTestFixture.InitializeAndStartSource(fullSource);
        int fullCount = 0;
        while (fullSource.NextFrame() is not null)
        {
            fullCount++;
        }

        await Assert.That(lazyCount).IsEqualTo(fullCount);
        await Assert.That(lazyCount).IsEqualTo(3);
    }

    // ========================================================================
    // Random access after lazy sequential reading
    // ========================================================================

    [Test]
    public async Task LazyMode_FrameById_WorksAfterSequentialRead()
    {
        byte[] eth1 = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x01]);
        byte[] eth2 = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x02]);
        byte[] eth3 = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x03]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth1, 1_000_000)
            .AddEthernetFrame(1, eth2, 2_000_000)
            .AddEthernetFrame(1, eth3, 3_000_000)
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        // Read all sequentially to populate the index
        while (source.NextFrame() is not null)
        {
        }

        // Now random access should work
        Frame? f0 = source.FrameById(new FrameId(0));
        Frame? f1 = source.FrameById(new FrameId(1));
        Frame? f2 = source.FrameById(new FrameId(2));

        await Assert.That(f0).IsNotNull();
        await Assert.That(f1).IsNotNull();
        await Assert.That(f2).IsNotNull();

        // Payload bytes should match what we put in
        await Assert.That(f0!.Value.Data.Span[^1]).IsEqualTo((byte)0x01);
        await Assert.That(f1!.Value.Data.Span[^1]).IsEqualTo((byte)0x02);
        await Assert.That(f2!.Value.Data.Span[^1]).IsEqualTo((byte)0x03);
    }

    // ========================================================================
    // FrameById for not-yet-scanned frame returns null in lazy mode
    // ========================================================================

    [Test]
    public async Task LazyMode_FrameById_OutOfRange_ReturnsNull()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xDD]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        // Without reading, random access to an out-of-range ID should be null
        Frame? invalid = source.FrameById(new FrameId(999));
        await Assert.That(invalid).IsNull();
    }

    // ========================================================================
    // Lazy mode with no frames
    // ========================================================================

    [Test]
    public async Task LazyMode_EmptyFile_NoFrames()
    {
        // Build BLF with just a file header, no object data
        byte[] blfData = new BlfTestGenerator().Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(0);
    }

    // ========================================================================
    // Default ScanMode is Lazy
    // ========================================================================

    [Test]
    public async Task DefaultOptions_ScanMode_IsLazy()
    {
        BlfSourceOptions options = new();
        await Assert.That(options.ScanMode).IsEqualTo(ScanMode.Lazy);
    }
}
