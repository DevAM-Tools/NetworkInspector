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
        using PcapngSource source = PcapngSource.FromData(pcap, "stress.pcapng");

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

    [Test]
    public async Task PcapngLazyScanServesIndexedFramesWhileScanning()
    {
        const int count = 256;
        using PcapNgTestWriter writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        byte[][] expected = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            expected[i] = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, (byte)(i >> 8)]);
            writer.WriteFrame(0, (long)(i + 1) * 1_000L, expected[i]);
        }

        using TempCaptureFile file = new(writer.Build(), ".pcapng");
        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { ScanMode = ScanMode.Lazy });

        await _AssertLazyScanFrameById(source, expected);
    }

    [Test]
    public async Task BlfLazyScanServesIndexedFramesWhileScanning()
    {
        const int count = 256;
        BlfTestGenerator gen = new();
        byte[][] expected = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            // Eight data bytes: the builder pads to 16 bytes, which is also the captured length the reader returns for DLC 8.
            expected[i] = FrameBuilders.BuildSocketCanClassic(0x100u + (uint)(i & 0xFF), [(byte)i, (byte)(i >> 8), 0, 0, 0, 0, 0, 0]);
            _ = gen.AddCanFrame(1, expected[i], (long)(i + 1) * 1_000L);
        }

        using BlfSource source = BlfSource.FromData(gen.Build(), "lazy.blf", new BlfSourceOptions { ScanMode = ScanMode.Lazy });

        await _AssertLazyScanFrameById(source, expected);
    }

    [Test]
    public async Task AscFrameByIdConcurrentReadsMatchSequentialBaseline()
    {
        const int count = 128;
        StringBuilder text = new("base hex timestamps absolute\nBegin Triggerblock\n");
        for (int i = 0; i < count; i++)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"{(i + 1) / 1000.0:F6} 1 {0x100 + i:X} Rx d 2 {i & 0xFF:X2} {i >> 8:X2}\n");
        }

        _ = text.Append("End TriggerBlock\n");
        using AscSource source = AscSource.FromText(text.ToString(), "stress.asc");
        SourceTestFixture.InitializeAndStartSource(source);

        List<byte[]> sequential = [];
        while (source.NextFrame() is { } frame)
        {
            sequential.Add(frame.Data.ToArray());
        }

        ConcurrentDictionary<int, bool> outcomes = new();
        Parallel.For(0, count * 4, n =>
        {
            int i = n % count;
            Frame? f = source.FrameById(new FrameId(i));
            outcomes[n] = f.HasValue && f.Value.Data.Span.SequenceEqual(sequential[i]);
        });

        await Assert.That(sequential.Count).IsEqualTo(count);
        await Assert.That(outcomes.Values.All(static v => v)).IsTrue();
    }

    /// <summary>
    /// Shared body of the lazy-scan variants: before the scan reaches a frame, <c>FrameById</c> returns null for it;
    /// once indexed it returns the frame, also while another thread keeps scanning.
    /// </summary>
    private static async Task _AssertLazyScanFrameById(IRandomAccessFrameSource source, byte[][] expected)
    {
        SourceTestFixture.InitializeAndStartSource(source);

        // Read a prefix: those ids are indexed, the tail is not yet.
        const int prefix = 10;
        for (int i = 0; i < prefix; i++)
        {
            _ = source.NextFrame();
        }

        for (int i = 0; i < prefix; i++)
        {
            Frame? f = source.FrameById(new FrameId(i));
            await Assert.That(f).IsNotNull();
            await Assert.That(f!.Value.Data.Span.SequenceEqual(expected[i])).IsTrue();
        }

        await Assert.That(source.FrameById(new FrameId(expected.Length - 1))).IsNull();

        // Scan the rest on one thread while readers hit every id: each result is null (not indexed yet) or correct.
        int wrong = 0;
        Task scan = Task.Run(() =>
        {
            while (source.NextFrame() is not null)
            {
            }
        });
        Parallel.For(0, expected.Length * 4, n =>
        {
            int i = n % expected.Length;
            Frame? f = source.FrameById(new FrameId(i));
            if (f.HasValue && !f.Value.Data.Span.SequenceEqual(expected[i]))
            {
                _ = Interlocked.Increment(ref wrong);
            }
        });
        await scan;

        // After the scan every id resolves.
        int missing = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Frame? f = source.FrameById(new FrameId(i));
            if (!f.HasValue || !f.Value.Data.Span.SequenceEqual(expected[i]))
            {
                missing++;
            }
        }

        await Assert.That(wrong).IsEqualTo(0);
        await Assert.That(missing).IsEqualTo(0);
    }
}
