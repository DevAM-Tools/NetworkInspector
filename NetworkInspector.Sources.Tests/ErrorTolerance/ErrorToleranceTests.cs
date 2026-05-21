// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.ErrorTolerance;

/// <summary>
/// Tests for <see cref="ErrorToleranceMode"/> behavior across both
/// <see cref="PcapStreamSource"/> and <see cref="BlfStreamSource"/>.
/// Verifies strict/tolerant modes, FrameSkipped events, error counters,
/// and stream truncation handling (H1 / H4 / M6 audit items).
/// </summary>
internal sealed class ErrorToleranceTests
{
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    // ========================================================================
    // Helper methods
    // ========================================================================

    /// <summary>Creates a PcapStreamSource from raw bytes.</summary>
    private static PcapStreamSource CreatePcapSource(byte[] data) =>
        PcapStreamSource.FromStream(new MemoryStream(data), "truncated.pcapng");

    /// <summary>Creates a BlfStreamSource from raw bytes.</summary>
    private static BlfStreamSource CreateBlfSource(byte[] data) =>
        BlfStreamSource.FromStream(new MemoryStream(data), "truncated.blf");

    /// <summary>Starts a frame source with a fresh registry.</summary>
    private static void StartSource(IFrameSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    /// <summary>
    /// Builds a valid PCAPNG byte array containing the given number of Ethernet frames,
    /// then truncates it at the specified byte position.
    /// </summary>
    private static byte[] BuildTruncatedPcapNg(int validFrameCount, int truncateAt)
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xCA, 0xFE]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);

        // Write enough frames so the file is long enough to truncate
        for (int i = 0; i < validFrameCount + 1; i++)
        {
            writer.WriteFrame(0, (long)(i + 1) * 1_000_000, eth);
        }

        byte[] fullData = writer.Build();
        // Truncate mid-way through the last frame
        int cutPoint = Math.Min(truncateAt, fullData.Length - 1);
        return fullData[..cutPoint];
    }

    /// <summary>
    /// Builds a valid BLF byte array containing the given number of Ethernet frames,
    /// then truncates it at the specified byte position.
    /// </summary>
    private static byte[] BuildTruncatedBlf(int validFrameCount, int truncateAt)
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xBE, 0xEF]);

        BlfTestGenerator gen = new();
        for (int i = 0; i < validFrameCount + 1; i++)
        {
            gen.AddEthernetFrame(1, eth, (long)(i + 1) * 1_000_000);
        }

        byte[] fullData = gen.Build();
        int cutPoint = Math.Min(truncateAt, fullData.Length - 1);
        return fullData[..cutPoint];
    }

    /// <summary>Reads all frames from a source until exhaustion, returns count.</summary>
    private static int DrainFrames(IFrameSource source)
    {
        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }
        return count;
    }

    // ========================================================================
    // PCAPNG — Tolerant mode (default)
    // ========================================================================

    [Test]
    public async Task PcapNg_Tolerant_TruncatedStream_RaisesFrameSkipped()
    {
        // Build a file with 2 valid frames + 1 that will be truncated
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xCA, 0xFE]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteFrame(0, 1_000_000, eth);
        writer.WriteFrame(0, 2_000_000, eth);
        writer.WriteFrame(0, 3_000_000, eth);
        byte[] fullData = writer.Build();

        // Truncate partway through the third frame's block data
        // SHB = 28, IDB = 32, EPB(16-byte eth) = 32+16+padding = ~52 each
        // We want to cut mid-third EPB, so cut a few bytes before end
        byte[] truncated = fullData[..(fullData.Length - 4)];

        using PcapStreamSource source = CreatePcapSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        // Should have read the 2 valid frames
        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);

        // At least one error for the truncated block
        await Assert.That(source.SkippedFrameCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.HasErrors).IsTrue();

        // FrameSkipped event fired
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);
    }

    // ========================================================================
    // PCAPNG — Strict mode
    // ========================================================================

    [Test]
    public async Task PcapNg_Strict_TruncatedStream_StopsImmediately()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xCA, 0xFE]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteFrame(0, 1_000_000, eth);
        writer.WriteFrame(0, 2_000_000, eth);
        writer.WriteFrame(0, 3_000_000, eth);
        byte[] fullData = writer.Build();

        // Truncate mid-third frame
        byte[] truncated = fullData[..(fullData.Length - 4)];

        using PcapStreamSource source = CreatePcapSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Strict;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        // Should still read the 2 valid frames before hitting truncation
        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);

        // Error stats incremented even in strict mode
        await Assert.That(source.SkippedFrameCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);

        // FrameSkipped event is always fired so subscribers can log the first
        // offending block even when strict mode aborts the source (SOURCE_GUIDE.md §12.2).
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);

        // Subsequent calls return null (exhausted)
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // PCAPNG — Fully valid file produces no errors
    // ========================================================================

    [Test]
    public async Task PcapNg_ValidFile_NoErrors()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xAA]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteFrame(0, 1_000_000, eth);
        writer.WriteFrame(0, 2_000_000, eth);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = CreatePcapSource(pcapData);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
        await Assert.That(source.ErrorCount).IsEqualTo(0);
        await Assert.That(source.HasErrors).IsFalse();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    // ========================================================================
    // PCAPNG — Truncation at SHB boundary (no valid data at all)
    // ========================================================================

    [Test]
    public async Task PcapNg_TruncatedAtSHB_NoFrames()
    {
        // Build a valid file and truncate within the SHB itself
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xBB]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteFrame(0, 1_000_000, eth);
        byte[] fullData = writer.Build();

        // Truncate within the SHB (first 28 bytes is SHB, cut at 20)
        byte[] truncated = fullData[..20];

        using PcapStreamSource source = CreatePcapSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        StartSource(source);
        int readCount = DrainFrames(source);

        await Assert.That(readCount).IsEqualTo(0);
        await Assert.That(source.ReadFrameCount).IsEqualTo(0);
    }

    // ========================================================================
    // BLF — Tolerant mode
    // ========================================================================

    [Test]
    public async Task Blf_Tolerant_TruncatedStream_RaisesFrameSkipped()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xDE, 0xAD]);

        BlfTestGenerator gen = new();
        gen.AddEthernetFrame(1, eth, 1_000_000);
        gen.AddEthernetFrame(1, eth, 2_000_000);
        gen.AddEthernetFrame(1, eth, 3_000_000);
        byte[] fullData = gen.Build();

        // Truncate mid-third frame
        byte[] truncated = fullData[..(fullData.Length - 4)];

        using BlfStreamSource source = CreateBlfSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        // Should have read the 2 valid frames
        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);

        // At least one error for the truncated block
        await Assert.That(source.SkippedFrameCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.HasErrors).IsTrue();

        // FrameSkipped event fired with TruncatedStream kind
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);
    }

    // ========================================================================
    // BLF — Strict mode
    // ========================================================================

    [Test]
    public async Task Blf_Strict_TruncatedStream_StopsImmediately()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xDE, 0xAD]);

        BlfTestGenerator gen = new();
        gen.AddEthernetFrame(1, eth, 1_000_000);
        gen.AddEthernetFrame(1, eth, 2_000_000);
        gen.AddEthernetFrame(1, eth, 3_000_000);
        byte[] fullData = gen.Build();

        // Truncate mid-third frame
        byte[] truncated = fullData[..(fullData.Length - 4)];

        using BlfStreamSource source = CreateBlfSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Strict;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        // Should still read the 2 valid frames before truncation
        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);

        // Error stats incremented even in strict mode
        await Assert.That(source.SkippedFrameCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);

        // FrameSkipped event is fired regardless of tolerance mode (per SOURCE_GUIDE.md §12.2),
        // so subscribers can log the first offending object even when the source aborts.
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);

        // Subsequent calls return null
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // BLF — Valid file produces no errors
    // ========================================================================

    [Test]
    public async Task Blf_ValidFile_NoErrors()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xBB]);

        BlfTestGenerator gen = new();
        gen.AddEthernetFrame(1, eth, 1_000_000);
        gen.AddEthernetFrame(1, eth, 2_000_000);
        byte[] blfData = gen.Build();

        using BlfStreamSource source = CreateBlfSource(blfData);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        StartSource(source);
        int readCount = DrainFrames(source);

        await Assert.That(readCount).IsEqualTo(2);
        await Assert.That(source.ReadFrameCount).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
        await Assert.That(source.ErrorCount).IsEqualTo(0);
        await Assert.That(source.HasErrors).IsFalse();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    // ========================================================================
    // Mode switching mid-read
    // ========================================================================

    [Test]
    public async Task PcapNg_SwitchFromTolerantToStrict_MidRead()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xCC]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        // Write 3 valid frames — no truncation, so mode switch has no effect on errors
        writer.WriteFrame(0, 1_000_000, eth);
        writer.WriteFrame(0, 2_000_000, eth);
        writer.WriteFrame(0, 3_000_000, eth);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = CreatePcapSource(pcapData);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        StartSource(source);

        // Read first frame in Tolerant mode
        Frame? frame1 = source.NextFrame();
        await Assert.That(frame1).IsNotNull();

        // Switch to Strict
        source.ErrorTolerance = ErrorToleranceMode.Strict;

        // Continue reading — with valid data, mode switch shouldn't block
        Frame? frame2 = source.NextFrame();
        await Assert.That(frame2).IsNotNull();

        Frame? frame3 = source.NextFrame();
        await Assert.That(frame3).IsNotNull();

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(3);
    }

    // ========================================================================
    // Counters — ReadFrameCount, SkippedFrameCount, ErrorCount
    // ========================================================================

    [Test]
    public async Task Blf_Tolerant_CountersUpdateCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x01, 0x02]);

        BlfTestGenerator gen = new();
        // Add 5 frames, truncate so last one is incomplete
        for (int i = 0; i < 5; i++)
        {
            gen.AddEthernetFrame(1, eth, (long)(i + 1) * 1_000_000);
        }
        byte[] fullData = gen.Build();
        byte[] truncated = fullData[..(fullData.Length - 10)];

        using BlfStreamSource source = CreateBlfSource(truncated);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        StartSource(source);

        // Read all available frames
        int readCount = DrainFrames(source);

        // 4 valid frames + 1 truncated = 4 read, >=1 skipped
        await Assert.That(readCount).IsEqualTo(4);
        await Assert.That(source.ReadFrameCount).IsEqualTo(4);
        await Assert.That(source.SkippedFrameCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(source.HasErrors).IsTrue();
    }

    // ========================================================================
    // Default tolerance mode is Tolerant
    // ========================================================================

    [Test]
    public async Task PcapNg_DefaultMode_IsTolerant()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xAA]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteFrame(0, 1_000_000, eth);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = CreatePcapSource(pcapData);
        // Do NOT set ErrorTolerance — should default to Tolerant
        await Assert.That(source.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Tolerant);
    }

    [Test]
    public async Task Blf_DefaultMode_IsTolerant()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xAA]);
        byte[] blfData = new BlfTestGenerator().AddEthernetFrame(1, eth, 1_000_000).Build();

        using BlfStreamSource source = CreateBlfSource(blfData);
        await Assert.That(source.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Tolerant);
    }
}
