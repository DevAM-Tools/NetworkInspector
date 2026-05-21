// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Tests for PcapNG source with multiple interfaces.
/// Verifies that frames captured on different interfaces are correctly distinguished.
/// </summary>
internal sealed class PcapSourceMultiInterfaceTests
{
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private static PcapSource CreateSource(byte[] pcapData) =>
        PcapSource.FromData(pcapData, "test.pcapng");

    private static FrameInterfaceRegistry StartSource(PcapSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return registry;
    }

    // ========================================================================
    // Two Ethernet interfaces
    // ========================================================================

    [Test]
    public async Task TwoEthernetInterfaces_FramesAssignedCorrectly()
    {
        using PcapNgTestWriter writer = new();
        uint iface0 = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        uint iface1 = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        byte[] eth0 = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xAA]);
        byte[] eth1 = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xBB]);

        writer.WriteFrame(iface0, 1_000_000_000, eth0);
        writer.WriteFrame(iface1, 2_000_000_000, eth1);

        using PcapSource source = CreateSource(writer.Build());
        FrameInterfaceRegistry registry = StartSource(source);

        Frame? f0 = source.NextFrame();
        Frame? f1 = source.NextFrame();

        await Assert.That(f0).IsNotNull();
        await Assert.That(f1).IsNotNull();

        // Both frames should have valid interface IDs
        await Assert.That(f0!.Value.HasInterface).IsTrue();
        await Assert.That(f1!.Value.HasInterface).IsTrue();

        // The interface IDs should be different
        await Assert.That(f0.Value.InterfaceId).IsNotEqualTo(f1.Value.InterfaceId);

        // Data should match
        await Assert.That(f0.Value.Data.Span.SequenceEqual(eth0)).IsTrue();
        await Assert.That(f1.Value.Data.Span.SequenceEqual(eth1)).IsTrue();
    }

    // ========================================================================
    // Interleaved frames across interfaces
    // ========================================================================

    [Test]
    public async Task InterleavedFrames_AllReadCorrectly()
    {
        using PcapNgTestWriter writer = new();
        uint iface0 = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        uint iface1 = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        // Alternate frames between two interfaces
        for (int i = 0; i < 10; i++)
        {
            uint iface = (i % 2 == 0) ? iface0 : iface1;
            byte[] payload = [(byte)i];
            byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, payload);
            writer.WriteFrame(iface, (i + 1) * 1_000_000_000L, eth);
        }

        using PcapSource source = CreateSource(writer.Build());
        StartSource(source);

        int count = 0;
        while (source.NextFrame() is { } frame)
        {
            await Assert.That(frame.HasInterface).IsTrue();
            await Assert.That(frame.LinkType).IsEqualTo(LinkType.Ethernet);
            count++;
        }

        await Assert.That(count).IsEqualTo(10);
    }

    // ========================================================================
    // Many frames random access with multiple interfaces
    // ========================================================================

    [Test]
    public async Task MultiInterface_RandomAccess_CorrectData()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        List<byte[]> expected = [];
        for (int i = 0; i < 20; i++)
        {
            uint iface = (uint)(i % 2);
            byte[] payload = Enumerable.Repeat((byte)i, 10).ToArray();
            byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, payload);
            expected.Add(eth);
            writer.WriteFrame(iface, (i + 1) * 1_000_000_000L, eth);
        }

        using PcapSource source = CreateSource(writer.Build());
        StartSource(source);

        // Access frames from both interfaces out of order
        int[] accessOrder = [15, 3, 19, 0, 7, 11];
        foreach (int id in accessOrder)
        {
            Frame? frame = source.FrameById(new FrameId(id));
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Data.Span.SequenceEqual(expected[id])).IsTrue();
        }
    }
}
