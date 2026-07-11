// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Stress;

/// <summary>
/// Stress exercises concurrent <see cref="IRandomAccessFrameSource.FrameById"/> access against
/// sequential baselines. Guards regressions in mmap pooling and BLF container caching.
/// </summary>
internal sealed class FrameByIdStressTests
{
    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    [Test]
    public async Task Pcap_FrameById_ConcurrentReadsMatchSequentialBaseline()
    {
        const int count = 128;
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        byte[][] payloads = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            payloads[i] = [(byte)i, (byte)(i >> 8)];
            byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, payloads[i]);
            writer.WriteFrame(0, (long)(i + 1) * 1_000_000L, eth);
        }

        byte[] pcap = writer.Build();
        using PcapSource source = PcapSource.FromData(pcap, "stress.pcapng");

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        ReadOnlyMemory<byte>[] sequential = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++)
        {
            Frame? f = source.FrameById(new FrameId(i));
            await Assert.That(f).IsNotNull();
            sequential[i] = f!.Value.Data;
        }

        ConcurrentDictionary<int, bool> outcomes = new();
        Parallel.For(0, count, i =>
        {
            Frame? f = source.FrameById(new FrameId(i));
            bool match = f.HasValue && f.Value.Data.Span.SequenceEqual(sequential[i].Span);
            outcomes[i] = match;
        });

        await Assert.That(outcomes.Count).IsEqualTo(count);
        await Assert.That(outcomes.Values.All(static v => v)).IsTrue();
    }

    [Test]
    public async Task Blf_FrameById_ConcurrentReadsMatchSequentialBaseline()
    {
        const int count = 96;
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x123, [1, 2, 3, 4, 5, 6, 7, 8]);

        BlfTestGenerator gen = new();
        for (int i = 0; i < count; i++)
        {
            _ = gen.AddCanFrame(1, can, (long)(i + 1) * 1_000_000L);
        }

        byte[] blf = gen.Build();
        using BlfSource source = BlfSource.FromData(
            blf,
            "stress.blf",
            new BlfSourceOptions { ScanMode = ScanMode.Full });

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        ReadOnlyMemory<byte>[] sequential = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++)
        {
            Frame? f = source.FrameById(new FrameId(i));
            await Assert.That(f).IsNotNull();
            sequential[i] = f!.Value.Data;
        }

        ConcurrentDictionary<int, bool> outcomes = new();
        Parallel.For(0, count, i =>
        {
            Frame? f = source.FrameById(new FrameId(i));
            bool match = f.HasValue && f.Value.Data.Span.SequenceEqual(sequential[i].Span);
            outcomes[i] = match;
        });

        await Assert.That(outcomes.Count).IsEqualTo(count);
        await Assert.That(outcomes.Values.All(static v => v)).IsTrue();
    }
}
