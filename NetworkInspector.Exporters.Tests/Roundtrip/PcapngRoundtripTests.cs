// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Pcapng;

namespace NetworkInspector.Exporters.Tests.Roundtrip;

/// <summary>
/// End-to-end PCAPNG roundtrip tests:
///   1. Generate frames in memory.
///   2. Export with <see cref="PcapngExporter"/> (nanosecond timestamps).
///   3. Validate the file with tshark 4.6.x (frame count, length, bytes, timestamps,
///      interface mapping).
///   4. Reimport with <see cref="PcapSource"/> and compare every frame against the
///      original.
/// <para>tshark is required — tests fail when it is missing.</para>
/// </summary>
internal sealed class PcapngRoundtripTests
{
    /// <summary>Reference Unix epoch base for all generated timestamps (April 2026).</summary>
    private const long EpochBaseNs = 1_777_000_000_000_000_000L;

    // ========================================================================
    // 1. Empty file (0 frames) — exporter must not crash, tshark sees 0 packets,
    //    reimport returns no frames.
    // ========================================================================
    [Test]
    public async Task Empty_File_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_empty");
        string path = dir.FilePath("empty.pcapng");

        using (PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build())
        {
            exporter.OnFinish();
        }

        // Deterministic contract: an empty export MUST still produce a valid
        // PCAPNG file with SHB and zero packets.
        await Assert.That(File.Exists(path)).IsTrue();

        int count = TsharkVerifier.GetPacketCount(path);
        await Assert.That(count).IsEqualTo(0);

        using PcapSource source = PcapSource.Open(path);
        RoundtripAssertions.StartSource(source);
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // 2. Snap length truncation keeps EPB original length and tshark frame.len.
    // ========================================================================
    [Test]
    public async Task SnapLength_Truncation_PreservesOriginalLength()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_snaplen");
        string path = dir.FilePath("snaplen.pcapng");

        const uint snapLength = 96;

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-snap", LinkType.Ethernet);
        byte[] originalData = FrameGenerators.BuildEthernetIpv4UdpFrame(512);
        Frame original = factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 42L, originalData);

        using (PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToFile(path)
            .WithSnapLength(snapLength)
            .Build())
        {
            exporter.OnFrame(original);
            exporter.OnFinish();
        }

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);

        PcapngVerifier.EpbInfo epb = verifier.Frames[0];
        await Assert.That(epb.CapturedLength).IsEqualTo(snapLength);
        await Assert.That(epb.OriginalLength).IsEqualTo((uint)originalData.Length);

        List<TsharkRecord> records = TsharkVerifier.GetPacketRecords(path);
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].FrameLen).IsEqualTo(originalData.Length);

        using PcapSource source = PcapSource.Open(path);
        RoundtripAssertions.StartSource(source);
        Frame? reimported = source.NextFrame();
        await Assert.That(reimported).IsNotNull();
        await Assert.That(reimported!.Value.Data.Length).IsEqualTo((int)snapLength);
    }

    // ========================================================================
    // 3. Single Ethernet/IPv4/UDP frame — canonical smoke test.
    // ========================================================================
    [Test]
    public async Task SingleEthernetIpv4Udp_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_single");
        string path = dir.FilePath("single.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-test", LinkType.Ethernet);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 123_456_789L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(64)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 3. Bulk 10 000 Ethernet frames — exercises buffer reuse & lazy scan path.
    // ========================================================================
    [Test]
    public async Task EthernetBulk_10kFrames_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_bulk");
        string path = dir.FilePath("bulk.pcapng");

        const int count = 10_000;
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-bulk", LinkType.Ethernet);

        Frame[] originals = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            // Sub-microsecond timestamp deltas to exercise nanosecond resolution.
            long ts = EpochBaseNs + (i * 1_234L);
            byte[] data = FrameGenerators.BuildEthernetIpv4UdpFrame(32 + (i % 16));
            originals[i] = factory.Create(ifId, LinkType.Ethernet, ts, data);
        }

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 4. FlexRay roundtrip.
    // ========================================================================
    [Test]
    public async Task FlexRay_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_fr");
        string path = dir.FilePath("flexray.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("fr-test", LinkType.Flexray);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Flexray, EpochBaseNs + 1L,
                FlexRayGenerators.BuildFlexRayFrame(0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true)),
            factory.Create(ifId, LinkType.Flexray, EpochBaseNs + 2L,
                FlexRayGenerators.BuildFlexRayFrame(1, 20, 4, 0x1234, [0x01, 0x02, 0x03, 0x04, 0x05])),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 5. LIN roundtrip.
    // ========================================================================
    [Test]
    public async Task Lin_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_lin");
        string path = dir.FilePath("lin.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("lin-test", LinkType.Lin);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Lin, EpochBaseNs + 100L,
                LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42)),
            factory.Create(ifId, LinkType.Lin, EpochBaseNs + 200L,
                LinGenerators.BuildLinFrame(0x07, [0xAA, 0xBB, 0xCC, 0xDD], checksum: 0x77)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 6. CAN classic + CAN FD via SocketCAN encapsulation.
    // ========================================================================
    [Test]
    public async Task CanSocketCan_ClassicAndFd_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_can");
        string path = dir.FilePath("can.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can-test", LinkType.CanSocketcan);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.CanSocketcan, EpochBaseNs,
                SocketCanGenerators.BuildCanClassic(0x123, [1, 2, 3, 4])),
            factory.Create(ifId, LinkType.CanSocketcan, EpochBaseNs + 10L,
                SocketCanGenerators.BuildCanClassic(0x1ABCDEF0, [0xAA, 0xBB], extended: true)),
            factory.Create(ifId, LinkType.CanSocketcan, EpochBaseNs + 20L,
                SocketCanGenerators.BuildCanFd(0x456, new byte[16], brs: true)),
            factory.Create(ifId, LinkType.CanSocketcan, EpochBaseNs + 30L,
                SocketCanGenerators.BuildCanFd(0x789, new byte[64], extended: true, brs: true)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 7. Mixed link types in one capture — produces multiple IDBs.
    // ========================================================================
    [Test]
    public async Task MixedLinkTypes_MultipleInterfaces_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_mixed");
        string path = dir.FilePath("mixed.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ethId = factory.AddInterface("eth0", LinkType.Ethernet);
        FrameInterfaceId frId = factory.AddInterface("fr0", LinkType.Flexray);
        FrameInterfaceId linId = factory.AddInterface("lin0", LinkType.Lin);
        FrameInterfaceId canId = factory.AddInterface("can0", LinkType.CanSocketcan);

        Frame[] originals =
        [
            factory.Create(ethId, LinkType.Ethernet, EpochBaseNs + 10L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(32)),
            factory.Create(frId, LinkType.Flexray, EpochBaseNs + 20L,
                FlexRayGenerators.BuildFlexRayFrame(0, 5, 1, 0xCAFE, [0xDE, 0xAD])),
            factory.Create(linId, LinkType.Lin, EpochBaseNs + 30L,
                LinGenerators.BuildLinFrame(0x11, [0x55, 0x66])),
            factory.Create(canId, LinkType.CanSocketcan, EpochBaseNs + 40L,
                SocketCanGenerators.BuildCanClassic(0x321, [9, 8, 7])),
            // Second Ethernet frame — must still map to the same tshark interface.
            factory.Create(ethId, LinkType.Ethernet, EpochBaseNs + 50L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(48)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);

        // tshark must report at least 4 distinct interfaces (one per link type).
        List<string> ifNames = TsharkVerifier.GetInterfaceNames(path);
        await Assert.That(ifNames.Count).IsGreaterThanOrEqualTo(4);

        ReimportAndAssert(path, originals);
    }

    // ========================================================================
    // 8. Min (0-byte UDP payload) and jumbo (9000-byte) frames.
    // ========================================================================
    [Test]
    public async Task EmptyPayloadAndJumbo_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_minmax");
        string path = dir.FilePath("minmax.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-minmax", LinkType.Ethernet);

        byte[] minimal = FrameGenerators.BuildEthernetIpv4UdpFrame(0);
        byte[] jumbo = FrameGenerators.BuildEthernetIpv4UdpFrame(9000);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs, minimal),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 1L, jumbo),
        ];

        // Snap length must accommodate the jumbo frame (default is 65535 → fine).
        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 9. Identical and non-monotonic timestamps. Capture formats must accept them
    //    and the reimport must surface them unchanged.
    // ========================================================================
    [Test]
    public async Task IdenticalAndNonMonotonicTimestamps_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_ts");
        string path = dir.FilePath("ts.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-ts", LinkType.Ethernet);
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 1_000_000_000L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(8)),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 1_000_000_000L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(9)), // identical timestamp
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + 500_000_000L,
                FrameGenerators.BuildEthernetIpv4UdpFrame(10)), // earlier than previous
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 10. Stream output (MemoryStream) roundtrip.
    // ========================================================================
    [Test]
    public async Task StreamOutput_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_stream");
        string path = dir.FilePath("stream.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-stream", LinkType.Ethernet);
        Frame[] originals = new Frame[4];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + (i * 1_000L),
                FrameGenerators.BuildEthernetIpv4UdpFrame(16 + i));
        }

        // Export to MemoryStream first, then materialize to a file so tshark can read it.
        using (MemoryStream ms = new())
        {
            using (PcapngExporter exporter = PcapngExporter.CreateBuilder()
                .ToStream(ms).Build())
            {
                foreach (Frame f in originals)
                {
                    exporter.OnFrame(f);
                }
                exporter.OnFinish();
            }

            await File.WriteAllBytesAsync(path, ms.ToArray()).ConfigureAwait(false);
        }

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);

        // Reimport via in-memory FromData (the Stream-equivalent code path on the source side).
        using PcapSource source = PcapSource.FromData(await File.ReadAllBytesAsync(path).ConfigureAwait(false), "stream.pcapng");
        FrameInterfaceRegistry registry = RoundtripAssertions.StartSource(source);
        RoundtripAssertions.AssertReimportMatchesOriginals(source, registry, originals, RoundtripAssertions.ExactNs);

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 11. Reimport in Full vs Lazy scan mode — both must yield identical frames.
    // ========================================================================
    [Test]
    public async Task Reimport_FullScan_VsLazyScan()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_scan");
        string path = dir.FilePath("scan.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-scan", LinkType.Ethernet);
        Frame[] originals = new Frame[50];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + (i * 1_000_000L),
                FrameGenerators.BuildEthernetIpv4UdpFrame(64));
        }

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);

        // Pass 1: lazy scan
        using (PcapSource lazy = PcapSource.Open(path, new PcapSourceOptions { ScanMode = ScanMode.Lazy }))
        {
            FrameInterfaceRegistry reg = RoundtripAssertions.StartSource(lazy);
            RoundtripAssertions.AssertReimportMatchesOriginals(lazy, reg, originals, RoundtripAssertions.ExactNs);
        }

        // Pass 2: full scan
        using (PcapSource full = PcapSource.Open(path, new PcapSourceOptions { ScanMode = ScanMode.Full }))
        {
            FrameInterfaceRegistry reg = RoundtripAssertions.StartSource(full);
            RoundtripAssertions.AssertReimportMatchesOriginals(full, reg, originals, RoundtripAssertions.ExactNs);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 12. Random access via FrameById matches sequential NextFrame for every frame.
    // ========================================================================
    [Test]
    public async Task Reimport_RandomAccess_FrameById()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_pcapng_rand");
        string path = dir.FilePath("rand.pcapng");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-rand", LinkType.Ethernet);
        const int count = 32;
        Frame[] originals = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + (i * 1_000L),
                FrameGenerators.BuildEthernetIpv4UdpFrame(24 + i));
        }

        ExportAndClose(path, originals);

        using PcapSource source = PcapSource.Open(path, new PcapSourceOptions { ScanMode = ScanMode.Full });
        RoundtripAssertions.StartSource(source);

        // Visit in reverse plus interleaved — every random access must return a frame
        // whose data matches the corresponding original.
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = (i % 2 == 0) ? (count - 1 - (i / 2)) : (i / 2);
        }

        foreach (int idx in order)
        {
            Frame? got = source.FrameById(new FrameId(idx));
            await Assert.That(got).IsNotNull();
            await Assert.That(got!.Value.Data.Span.SequenceEqual(originals[idx].Data.Span)).IsTrue();
            await Assert.That(got.Value.Timestamp.AsNanos).IsEqualTo(originals[idx].Timestamp.AsNanos);
            await Assert.That(got.Value.LinkType).IsEqualTo(originals[idx].LinkType);
        }

        // Out of bounds returns null.
        await Assert.That(source.FrameById(new FrameId(count))).IsNull();
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>Writes <paramref name="frames"/> to <paramref name="path"/> with the default
    /// nanosecond-resolution PCAPNG exporter, flushes and disposes.</summary>
    private static void ExportAndClose(string path, IReadOnlyList<Frame> frames)
    {
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToFile(path)
            .Build();

        foreach (Frame f in frames)
        {
            exporter.OnFrame(f);
        }

        exporter.OnFinish();
    }

    /// <summary>Opens a <see cref="PcapSource"/>, drains it sequentially and asserts
    /// every frame matches the originals byte-exact and with nanosecond timestamps.</summary>
    private static void ReimportAndAssert(string path, IReadOnlyList<Frame> originals)
    {
        using PcapSource source = PcapSource.Open(path);
        FrameInterfaceRegistry registry = RoundtripAssertions.StartSource(source);
        RoundtripAssertions.AssertReimportMatchesOriginals(source, registry, originals, RoundtripAssertions.ExactNs);
    }
}
