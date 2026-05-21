// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Roundtrip;

/// <summary>
/// End-to-end BLF roundtrip tests:
///   1. Generate frames in memory.
///   2. Export with <see cref="BlfExporter"/>.
///   3. Validate the file with tshark 4.6.x (frame count, length, bytes, timestamps,
///      interface mapping). tshark 4.6 has built-in BLF support.
///   4. Reimport with <see cref="BlfSource"/> and compare every frame against the
///      original (data, timestamp, link type, interface mapping).
/// <para>
/// BLF stores timestamps in 10 µs ticks natively, so all generated test timestamps are
/// chosen as exact 10 µs multiples. Comparisons are then made with <see cref="RoundtripAssertions.ExactNs"/>
/// tolerance — no rounding losses sneak in. Originals can still be expressed in
/// nanoseconds; only the stored value is constrained.
/// </para>
/// <para>tshark is required — tests fail when it is missing.</para>
/// </summary>
internal sealed class BlfRoundtripTests
{
    /// <summary>Reference Unix epoch base in nanoseconds (April 2026, 10 µs aligned).</summary>
    private const long EpochBaseNs = 1_777_000_000_000_000_000L;

    /// <summary>10 µs tick in nanoseconds — the BLF native timestamp resolution.</summary>
    private const long TickNs = 10_000L;

    // ========================================================================
    // 1. Empty file (0 frames).
    // ========================================================================
    [Test]
    public async Task Empty_File_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_empty");
        string path = dir.FilePath("empty.blf");

        using (BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build())
        {
            exporter.OnFinish();
        }

        // Deterministic contract: a successfully-built exporter MUST produce a
        // parseable BLF file even when no frames were written. The file therefore
        // has to exist, contain a valid LOGG header (so tshark and BlfSource can
        // both open it), and surface zero packets.
        await Assert.That(File.Exists(path)).IsTrue();

        int count = TsharkVerifier.GetPacketCount(path);
        await Assert.That(count).IsEqualTo(0);

        using BlfSource source = BlfSource.Open(path);
        RoundtripAssertions.StartSource(source);
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // 2. Single Ethernet frame.
    // ========================================================================
    [Test]
    public async Task SingleEthernet_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_eth");
        string path = dir.FilePath("eth.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-test", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (123L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(64)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 3. CAN classic — standard 11-bit and extended 29-bit IDs, varied DLCs.
    // ========================================================================
    [Test]
    public async Task CanClassic_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_can");
        string path = dir.FilePath("can.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can0", LinkType.CanSocketcan,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        List<Frame> originals = new();
        // DLC 0..8 plus standard + extended IDs
        for (int dlc = 0; dlc <= 8; dlc++)
        {
            byte[] data = new byte[dlc];
            for (int i = 0; i < dlc; i++)
            {
                data[i] = (byte)(0x10 + i);
            }
            originals.Add(factory.Create(ifId, LinkType.CanSocketcan,
                EpochBaseNs + (((long)dlc + 1) * TickNs),
                SocketCanGenerators.BuildCanClassic((uint)(0x100 + dlc), data)));
        }
        // Extended IDs (29-bit)
        originals.Add(factory.Create(ifId, LinkType.CanSocketcan,
            EpochBaseNs + (50L * TickNs),
            SocketCanGenerators.BuildCanClassic(0x1ABCDEF0, [0xDE, 0xAD, 0xBE, 0xEF], extended: true)));

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Classic CAN ID in 11‑bit numeric range but with SocketCAN EFF set must round-trip BLF flag 0x04.
    /// </summary>
    [Test]
    public async Task CanClassic_ExtendedLowIdPreserved_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_can_eff_low");
        string path = dir.FilePath("can_eff_low.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can0", LinkType.CanSocketcan,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        Frame[] originals =
        [
            factory.Create(ifId, LinkType.CanSocketcan, EpochBaseNs + TickNs,
                SocketCanGenerators.BuildCanClassic(0x123, [0xAA, 0xBB], extended: true)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 4. CAN FD — BRS/EDL flags, DLC up to 64.
    // ========================================================================
    [Test]
    public async Task CanFd_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_canfd");
        string path = dir.FilePath("canfd.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("canfd0", LinkType.CanSocketcan,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)2 });

        // CAN FD valid DLCs map to 0,1,...,8,12,16,20,24,32,48,64 — exercise the boundaries.
        int[] dlcs = [0, 1, 8, 12, 16, 20, 24, 32, 48, 64];
        Frame[] originals = new Frame[dlcs.Length * 2];
        int idx = 0;
        for (int i = 0; i < dlcs.Length; i++)
        {
            byte[] payload = new byte[dlcs[i]];
            for (int j = 0; j < payload.Length; j++)
            {
                payload[j] = (byte)((i * 13) + j);
            }
            // Without BRS
            originals[idx++] = factory.Create(ifId, LinkType.CanSocketcan,
                EpochBaseNs + (((long)idx + 1) * TickNs),
                SocketCanGenerators.BuildCanFd((uint)(0x200 + i), payload));
            // With BRS + extended id
            originals[idx++] = factory.Create(ifId, LinkType.CanSocketcan,
                EpochBaseNs + (((long)idx + 1) * TickNs),
                SocketCanGenerators.BuildCanFd((uint)(0x10000000 + i), payload, extended: true, brs: true));
        }

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 5. LIN.
    // ========================================================================
    [Test]
    public async Task Lin_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_lin");
        string path = dir.FilePath("lin.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("lin0", LinkType.Lin,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Lin, EpochBaseNs + TickNs,
                LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42)),
            factory.Create(ifId, LinkType.Lin, EpochBaseNs + (2L * TickNs),
                LinGenerators.BuildLinFrame(0x07, [0xAA, 0xBB, 0xCC, 0xDD], checksum: 0x77)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 6. FlexRay.
    // ========================================================================
    [Test]
    public async Task FlexRay_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_fr");
        string path = dir.FilePath("fr.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("fr0", LinkType.Flexray,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Flexray, EpochBaseNs + TickNs,
                FlexRayGenerators.BuildFlexRayFrame(0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true)),
            factory.Create(ifId, LinkType.Flexray, EpochBaseNs + (2L * TickNs),
                FlexRayGenerators.BuildFlexRayFrame(0, 20, 4, 0x1234, [0x01, 0x02, 0x03, 0x04, 0x05])),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 7. Mixed bus types with multiple channels.
    // ========================================================================
    [Test]
    public async Task Mixed_AllBusTypes_MultipleChannels_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_mixed");
        string path = dir.FilePath("mixed.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId eth = factory.AddInterface("eth-1", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        FrameInterfaceId can1 = factory.AddInterface("can-1", LinkType.CanSocketcan,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        FrameInterfaceId can2 = factory.AddInterface("can-2", LinkType.CanSocketcan,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)2 });
        FrameInterfaceId lin = factory.AddInterface("lin-1", LinkType.Lin,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        FrameInterfaceId fr = factory.AddInterface("fr-1", LinkType.Flexray,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        Frame[] originals =
        [
            factory.Create(eth,  LinkType.Ethernet,    EpochBaseNs + (1L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(32)),
            factory.Create(can1, LinkType.CanSocketcan, EpochBaseNs + (2L * TickNs),
                SocketCanGenerators.BuildCanClassic(0x111, [1, 2, 3])),
            factory.Create(can2, LinkType.CanSocketcan, EpochBaseNs + (3L * TickNs),
                SocketCanGenerators.BuildCanFd(0x222, new byte[16], brs: true)),
            factory.Create(lin,  LinkType.Lin,         EpochBaseNs + (4L * TickNs),
                LinGenerators.BuildLinFrame(0x09, [0x55, 0x66])),
            factory.Create(fr,   LinkType.Flexray,     EpochBaseNs + (5L * TickNs),
                FlexRayGenerators.BuildFlexRayFrame(0, 5, 1, 0xCAFE, [0xDE, 0xAD])),
            // Second Ethernet on the same channel — must still map consistently.
            factory.Create(eth,  LinkType.Ethernet,    EpochBaseNs + (6L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(48)),
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 8. Bulk 10 000 frames.
    // ========================================================================
    [Test]
    public async Task EthernetBulk_10kFrames_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_bulk");
        string path = dir.FilePath("bulk.blf");

        const int count = 10_000;
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-bulk", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        Frame[] originals = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            long ts = EpochBaseNs + ((long)(i + 1) * TickNs);
            byte[] data = FrameGenerators.BuildEthernetIpv4UdpFrame(32 + (i % 16));
            originals[i] = factory.Create(ifId, LinkType.Ethernet, ts, data);
        }

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 9. Compression matrix — every level produces a tshark-readable file with
    //    identical reimport semantics.
    // ========================================================================
    [Test]
    [Arguments(BlfCompressionLevel.None)]
    [Arguments(BlfCompressionLevel.Fast)]
    [Arguments(BlfCompressionLevel.Default)]
    [Arguments(BlfCompressionLevel.Best)]
    public async Task Compression_Levels_Roundtrip(BlfCompressionLevel level)
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new($"rt_blf_cmp_{level}");
        string path = dir.FilePath("cmp.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-cmp", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals = new Frame[256];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + ((long)(i + 1) * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(64 + (i % 32)));
        }

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToFile(path)
            .WithCompressionLevel(level)
            .Build())
        {
            foreach (Frame f in originals)
            {
                exporter.OnFrame(f);
            }
            exporter.OnFinish();
        }

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 10a. Non-monotonic timestamps — clamping path.
    //      When a frame arrives with an absolute timestamp *earlier* than the
    //      file's start anchor (= the first frame's timestamp), BlfWriter cannot
    //      represent a negative relative tick, so it clamps the offset to zero.
    //      The output must be a valid BLF file that tshark accepts, and the
    //      reimport must surface the clamped (= start-anchor) timestamp for the
    //      out-of-order frame.
    // ========================================================================
    [Test]
    public async Task NonMonotonicTimestamp_ClampsToStartAnchor_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_nonmono");
        string path = dir.FilePath("nonmono.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-ts", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        byte[] data0 = FrameGenerators.BuildEthernetIpv4UdpFrame(8);
        byte[] data1 = FrameGenerators.BuildEthernetIpv4UdpFrame(9);
        byte[] data2 = FrameGenerators.BuildEthernetIpv4UdpFrame(10);

        // Frame 2 has a timestamp earlier than frame 0 (the start anchor).
        // The BLF writer clamps its relative tick to 0, so it appears in the
        // output with the same absolute time as frame 0.
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (500L  * TickNs), data0),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (1_000L * TickNs), data1),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (250L  * TickNs), data2), // non-monotonic
        ];

        ExportAndClose(path, originals);

        // Build the expected representation after clamping: frame 2 appears with
        // the start-anchor timestamp (= frame 0's timestamp) in the output file.
        long startAnchorNs = EpochBaseNs + (500L * TickNs);
        Frame[] expectedAfterClamping =
        [
            originals[0],
            originals[1],
            factory.Create(ifId, LinkType.Ethernet, startAnchorNs, data2),
        ];

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, expectedAfterClamping, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, expectedAfterClamping);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 10b. Identical (duplicate) timestamps — must round-trip exactly.
    // ========================================================================
    [Test]
    public async Task IdenticalTimestamps_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_ts");
        string path = dir.FilePath("ts.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-ts", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });

        // Two frames share the same timestamp; both must round-trip intact.
        Frame[] originals =
        [
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (500L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(8)),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (1_000L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(9)),
            factory.Create(ifId, LinkType.Ethernet, EpochBaseNs + (1_000L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(10)), // duplicate of frame 1
        ];

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);
        ReimportAndAssert(path, originals);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 11. Stream output (MemoryStream) roundtrip.
    // ========================================================================
    [Test]
    public async Task StreamOutput_Roundtrip()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_stream");
        string path = dir.FilePath("stream.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-stream", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals = new Frame[8];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + ((long)(i + 1) * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(16 + i));
        }

        using (MemoryStream ms = new())
        {
            using (BlfExporter exporter = BlfExporter.CreateBuilder()
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

        using BlfSource source = BlfSource.FromData(await File.ReadAllBytesAsync(path).ConfigureAwait(false), "stream.blf");
        FrameInterfaceRegistry registry = RoundtripAssertions.StartSource(source);
        RoundtripAssertions.AssertReimportMatchesOriginals(source, registry, originals, RoundtripAssertions.ExactNs);

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 12. Reimport in Full vs Lazy scan mode — both must yield identical frames.
    // ========================================================================
    [Test]
    public async Task Reimport_FullScan_VsLazyScan()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_scan");
        string path = dir.FilePath("scan.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-scan", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        Frame[] originals = new Frame[50];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + ((long)(i + 1) * 100L * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(64));
        }

        ExportAndClose(path, originals);

        RoundtripAssertions.AssertTsharkMatchesOriginals(path, originals, RoundtripAssertions.ExactNs);

        // Pass 1: lazy scan
        using (BlfSource lazy = BlfSource.Open(path, new BlfSourceOptions { ScanMode = ScanMode.Lazy }))
        {
            FrameInterfaceRegistry reg = RoundtripAssertions.StartSource(lazy);
            RoundtripAssertions.AssertReimportMatchesOriginals(lazy, reg, originals, RoundtripAssertions.ExactNs);
        }

        // Pass 2: full scan
        using (BlfSource full = BlfSource.Open(path, new BlfSourceOptions { ScanMode = ScanMode.Full }))
        {
            FrameInterfaceRegistry reg = RoundtripAssertions.StartSource(full);
            RoundtripAssertions.AssertReimportMatchesOriginals(full, reg, originals, RoundtripAssertions.ExactNs);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ========================================================================
    // 13. Random access via FrameById.
    // ========================================================================
    [Test]
    public async Task Reimport_RandomAccess_FrameById()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("rt_blf_rand");
        string path = dir.FilePath("rand.blf");

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("eth-rand", LinkType.Ethernet,
            new Dictionary<string, object> { [FrameInterfacePropertyKeys.BlfChannel] = (ushort)1 });
        const int count = 32;
        Frame[] originals = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            originals[i] = factory.Create(ifId, LinkType.Ethernet,
                EpochBaseNs + ((long)(i + 1) * TickNs),
                FrameGenerators.BuildEthernetIpv4UdpFrame(24 + i));
        }

        ExportAndClose(path, originals);

        // Random access requires the index to be populated → use Full scan.
        using BlfSource source = BlfSource.Open(path, new BlfSourceOptions { ScanMode = ScanMode.Full });
        RoundtripAssertions.StartSource(source);

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

        await Assert.That(source.FrameById(new FrameId(count))).IsNull();
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static void ExportAndClose(string path, IReadOnlyList<Frame> frames)
    {
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToFile(path)
            .Build();

        foreach (Frame f in frames)
        {
            exporter.OnFrame(f);
        }

        exporter.OnFinish();
    }

    private static void ReimportAndAssert(string path, IReadOnlyList<Frame> originals)
    {
        using BlfSource source = BlfSource.Open(path);
        FrameInterfaceRegistry registry = RoundtripAssertions.StartSource(source);
        RoundtripAssertions.AssertReimportMatchesOriginals(source, registry, originals, RoundtripAssertions.ExactNs);
    }
}
