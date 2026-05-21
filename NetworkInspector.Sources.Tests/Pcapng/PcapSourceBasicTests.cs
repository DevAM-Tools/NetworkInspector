// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Tests for PcapNG source reading.
/// Verifies single/multiple frame reading, timestamp handling, metadata, and edge cases.
/// </summary>
internal sealed class PcapSourceBasicTests
{
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private static PcapSource CreateSource(byte[] pcapData) =>
        PcapSource.FromData(pcapData, "test.pcapng");

    private static void StartSource(PcapSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    // ========================================================================
    // Single frame
    // ========================================================================

    [Test]
    public async Task SingleFrame_ParsedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xDE, 0xAD]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, eth);
        byte[] pcapData = writer.Build();

        using PcapSource source = CreateSource(pcapData);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(1);

        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame.Value.Id.Value).IsEqualTo(0);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(eth)).IsTrue();

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Multiple frames
    // ========================================================================

    [Test]
    public async Task MultipleFrames_AllReadInOrder()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        List<byte[]> expected = [];
        for (int i = 0; i < 5; i++)
        {
            byte[] payload = [(byte)i, (byte)(i + 1), (byte)(i + 2)];
            byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, payload);
            expected.Add(eth);
            writer.WriteFrame(0, (i + 1) * 1_000_000_000L, eth);
        }

        using PcapSource source = CreateSource(writer.Build());
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(5);

        StartSource(source);
        for (int i = 0; i < 5; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(i);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expected[i])).IsTrue();
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Timestamps
    // ========================================================================

    [Test]
    public async Task Timestamps_StrictlyIncreasing()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x00]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        long[] timestamps = [1_000_000_000, 2_000_000_000, 3_000_000_000, 4_000_000_000, 5_000_000_000];
        foreach (long ts in timestamps)
        {
            writer.WriteFrame(0, ts, eth);
        }

        using PcapSource source = CreateSource(writer.Build());
        StartSource(source);

        long prevTs = long.MinValue;
        for (int i = 0; i < 5; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            long ts = frame!.Value.Timestamp.AsNanos;
            await Assert.That(ts > prevTs)
                .IsTrue()
                .Because($"Timestamps must increase: prev={prevTs}, curr={ts}");
            prevTs = ts;
        }
    }

    // ========================================================================
    // Microsecond resolution (default)
    // ========================================================================

    [Test]
    public async Task MicrosecondResolution_TimestampConvertedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x00]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false);
        // 1.5 seconds = 1_500_000_000 ns = 1_500_000 µs
        writer.WriteFrame(0, 1_500_000_000, eth);

        using PcapSource source = CreateSource(writer.Build());
        StartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();

        // With µs resolution, timestamp should be truncated to µs precision
        long ts = frame!.Value.Timestamp.AsNanos;
        // The stored value should be divisible by 1000 (µs → ns conversion)
        await Assert.That(ts % 1000).IsEqualTo(0L);
    }

    // ========================================================================
    // Random access
    // ========================================================================

    [Test]
    public async Task RandomAccess_RetrieveFrameById()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        List<byte[]> expected = [];
        for (int i = 0; i < 10; i++)
        {
            byte[] payload = Enumerable.Repeat((byte)i, 20).ToArray();
            byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, payload);
            expected.Add(eth);
            writer.WriteFrame(0, (i + 1) * 1_000_000_000L, eth);
        }

        using PcapSource source = CreateSource(writer.Build());
        StartSource(source);

        // Access frame 5 directly
        Frame? f5 = source.FrameById(new FrameId(5));
        await Assert.That(f5).IsNotNull();
        await Assert.That(f5!.Value.Id.Value).IsEqualTo(5);
        await Assert.That(f5.Value.Data.Span.SequenceEqual(expected[5])).IsTrue();

        // Access frame 0
        Frame? f0 = source.FrameById(new FrameId(0));
        await Assert.That(f0).IsNotNull();
        await Assert.That(f0!.Value.Data.Span.SequenceEqual(expected[0])).IsTrue();
    }

    // ========================================================================
    // Metadata
    // ========================================================================

    [Test]
    public async Task UiName_MatchesProvided()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x00]);
        writer.WriteFrame(0, 1_000_000_000, eth);

        using PcapSource source = PcapSource.FromData(writer.Build(), "my_capture.pcapng");
        await Assert.That(source.UiName).IsEqualTo("my_capture.pcapng");
    }

    // ========================================================================
    // Empty file
    // ========================================================================

    [Test]
    public async Task EmptyPcapNg_NoFrames()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        // No frames written

        using PcapSource source = CreateSource(writer.Build());
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(0);

        StartSource(source);
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        using PcapNgTestWriter writer = new();
        byte[] pcapData = writer.Build();
        using PcapSource source = CreateSource(pcapData);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
