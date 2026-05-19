// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Blf;
using NetworkInspector.Sources.Tests.Generators;

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for basic Ethernet frame reading from BLF sources.
/// Verifies single/multiple frames, timestamp ordering, data round-trip, and VLAN handling.
/// </summary>
internal sealed class BlfEthernetTests
{
    /// <summary>Common source MAC for tests.</summary>
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Common destination MAC (broadcast) for tests.</summary>
    private static readonly byte[] DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Creates a fully-scanned BlfSource from generated data.</summary>
    private static BlfSource CreateSource(byte[] blfData) =>
        BlfSource.FromData(blfData, "test.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });

    /// <summary>Starts the source with a fresh registry.</summary>
    private static FrameInterfaceRegistry StartSource(BlfSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return registry;
    }

    // ========================================================================
    // Single frame
    // ========================================================================

    [Test]
    public async Task SingleEthernetFrame_ParsedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xDE, 0xAD, 0xBE, 0xEF]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(1);

        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame.Value.Id.Value).IsEqualTo(0);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(eth)).IsTrue();

        // No more frames
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Multiple frames
    // ========================================================================

    [Test]
    public async Task MultipleEthernetFrames_AllParsedInOrder()
    {
        byte[] dstMac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        BlfTestGenerator gen = new();
        List<byte[]> expected = [];

        for (int i = 0; i < 10; i++)
        {
            byte[] payload = Enumerable.Range(0, 20).Select(j => (byte)((i + j) & 0xFF)).ToArray();
            byte[] eth = FrameBuilders.BuildEthernetFrame(dstMac, SrcMac, 0x0800, payload);
            expected.Add(eth);
            gen.AddEthernetFrame(1, eth, (i + 1) * 1_000_000L);
        }

        using BlfSource source = CreateSource(gen.Build());
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(10);

        StartSource(source);
        for (int i = 0; i < 10; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(i);
            await Assert.That(frame.Value.LinkType).IsEqualTo(LinkType.Ethernet);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expected[i])).IsTrue();
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Timestamps
    // ========================================================================

    [Test]
    public async Task EthernetFrames_TimestampsStrictlyIncreasing()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x00]);

        long[] offsets = [1_000_000, 5_000_000, 10_000_000, 50_000_000, 100_000_000];
        BlfTestGenerator gen = new();
        foreach (long offset in offsets)
        {
            gen.AddEthernetFrame(1, eth, offset);
        }

        using BlfSource source = CreateSource(gen.Build());
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
    // Data round-trip
    // ========================================================================

    [Test]
    public async Task EthernetFrame_DataPreservedExactly()
    {
        // Test with various payloads: empty, single byte, MTU-sized, all byte values
        byte[][] payloads =
        [
            [],                                          // Empty payload (just Ethernet header)
            [0x00],                                      // Single byte
            Enumerable.Repeat((byte)0xFF, 1500).ToArray(), // MTU-size
            Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), // All byte values
        ];

        foreach (byte[] payload in payloads)
        {
            byte[] eth = FrameBuilders.BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x0800, payload);

            byte[] blfData = new BlfTestGenerator()
                .AddEthernetFrame(1, eth, 1_000_000)
                .Build();

            using BlfSource source = CreateSource(blfData);
            StartSource(source);
            Frame? frame = source.NextFrame();

            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Data.Span.SequenceEqual(eth)).IsTrue();
        }
    }

    // ========================================================================
    // VLAN-tagged frames
    // ========================================================================

    [Test]
    public async Task EthernetVlanTagged_ParsedWithVlanHeader()
    {
        byte[] vlanFrame = FrameBuilders.BuildVlanEthernetFrame(
            DstMac, SrcMac, 100, 0x0800, [0xDE, 0xAD, 0xBE, 0xEF]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, vlanFrame, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);

        // VLAN frame should have 802.1Q header
        await Assert.That(frame.Value.Data.Length >= 18).IsTrue();
        // TPID should be 0x8100
        await Assert.That(frame.Value.Data.Span[12]).IsEqualTo((byte)0x81);
        await Assert.That(frame.Value.Data.Span[13]).IsEqualTo((byte)0x00);
    }

    // ========================================================================
    // Source metadata
    // ========================================================================

    [Test]
    public async Task BlfSource_UiNameMatchesProvided()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0x00]);
        byte[] blfData = new BlfTestGenerator().AddEthernetFrame(1, eth, 1_000_000).Build();

        using BlfSource source = BlfSource.FromData(blfData, "my_trace.blf");
        await Assert.That(source.UiName).IsEqualTo("my_trace.blf");
    }
}
