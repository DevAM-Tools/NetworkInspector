// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Roundtrip;

/// <summary>
/// Exporter → reader round trips that need no external tool: bus fields (CAN DLC 9-15, remote DLC, LIN checksum
/// model), channel routing and names, the BLF time zone, ASC timestamp precision, PCAPNG interface options, and link
/// types outside the <see cref="LinkType"/> enum.
/// </summary>
internal sealed class InProcessRoundtripTests
{
    #region Constants

    /// <summary>Whole-second anchor (April 2026) so header dates at second resolution do not shift the frames.</summary>
    private const long _EpochBaseNs = 1_777_000_000_000_000_000L;

    #endregion

    #region CAN

    [Test]
    public async Task CanDlc15SurvivesBlfAndAsc()
    {
        // SocketCAN classic with 8 payload bytes and bus DLC 15 in len8_dlc (byte 7).
        byte[] can = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(can, 0x123);
        can[4] = 8;
        can[7] = 15;
        can.AsSpan(8).Fill(0x5A);

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can", LinkType.CanSocketcan);
        Frame[] frames = [factory.Create(ifId, LinkType.CanSocketcan, _EpochBaseNs, can)];

        Frame fromBlf = _Single(_ReadBlf(_ExportBlf(frames)));
        Frame fromAsc = _Single(_ReadAsc(_ExportAsc(frames)));

        await Assert.That(fromBlf.Data.Span.SequenceEqual(can)).IsTrue();
        await Assert.That(fromAsc.Data.Span.SequenceEqual(can)).IsTrue();
    }

    [Test]
    public async Task AscRemoteFrameKeepsTheRequestedDlc()
    {
        // RTR frame requesting 5 bytes: byte 4 holds the requested count, no data follows.
        byte[] rtr = SocketCanGenerators.BuildCanClassic(0x321, [], rtr: true);
        rtr[4] = 5;

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can", LinkType.CanSocketcan);
        byte[] asc = _ExportAsc([factory.Create(ifId, LinkType.CanSocketcan, _EpochBaseNs, rtr)]);
        string text = Encoding.ASCII.GetString(asc);
        Frame back = _Single(_ReadAsc(asc));

        await Assert.That(text).Contains(" r 5");
        await Assert.That(back.Data.Span[4]).IsEqualTo((byte)5);
        await Assert.That((BinaryPrimitives.ReadUInt32BigEndian(back.Data.Span) & 0x4000_0000u) != 0).IsTrue();
    }

    #endregion

    #region LIN

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)2)]
    public async Task LinChecksumModelSurvivesBlfAndAsc(byte checksumTypeBits)
    {
        byte[] lin = LinGenerators.BuildLinFrame(0x10, [1, 2, 3], checksum: 0x5C, checksumTypeBits: checksumTypeBits);

        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("lin", LinkType.Lin);
        Frame[] frames = [factory.Create(ifId, LinkType.Lin, _EpochBaseNs, lin)];

        Frame fromBlf = _Single(_ReadBlf(_ExportBlf(frames)));
        Frame fromAsc = _Single(_ReadAsc(_ExportAsc(frames)));

        await Assert.That(fromBlf.Data.Span[4] & 0x03).IsEqualTo((int)checksumTypeBits);
        await Assert.That(fromAsc.Data.Span[4] & 0x03).IsEqualTo((int)checksumTypeBits);
        await Assert.That(fromBlf.Data.Span.SequenceEqual(lin)).IsTrue();
        await Assert.That(fromAsc.Data.Span.SequenceEqual(lin)).IsTrue();
    }

    #endregion

    #region Channels and channel names

    [Test]
    public async Task AscSourcedChannelIsKeptInBlf()
    {
        // An interface read from ASC carries asc.channel; the BLF exporter must route it to that channel, not to 1.
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface(
            "CAN 3", LinkType.CanSocketcan, new Dictionary<string, object> { [AscInterfacePropertyKeys.Channel] = 3 });
        Frame[] frames = [factory.Create(ifId, LinkType.CanSocketcan, _EpochBaseNs, SocketCanGenerators.BuildCanClassic(0x10, [1]))];

        Frame back = _Single(_ReadBlf(_ExportBlf(frames)));

        await Assert.That(_Interface(back).Properties[BlfInterfacePropertyKeys.Channel]).IsEqualTo((object)3L);
    }

    [Test]
    public async Task BlfChannelNameSurvivesTheRoundTrip()
    {
        RoundtripFrameFactory factory = new();
        FrameInterfaceId named = factory.AddInterface(
            "Powertrain", LinkType.CanSocketcan, new Dictionary<string, object> { [BlfInterfacePropertyKeys.Channel] = 2L });
        FrameInterfaceId plain = factory.AddInterface(
            "CAN 4", LinkType.CanSocketcan, new Dictionary<string, object> { [BlfInterfacePropertyKeys.Channel] = 4L });
        Frame[] frames =
        [
            factory.Create(named, LinkType.CanSocketcan, _EpochBaseNs, SocketCanGenerators.BuildCanClassic(0x10, [1])),
            factory.Create(plain, LinkType.CanSocketcan, _EpochBaseNs + 1_000, SocketCanGenerators.BuildCanClassic(0x11, [2])),
            factory.Create(named, LinkType.CanSocketcan, _EpochBaseNs + 2_000, SocketCanGenerators.BuildCanClassic(0x12, [3])),
        ];

        List<Frame> back = _ReadBlf(_ExportBlf(frames));

        await Assert.That(back.Count).IsEqualTo(3);
        await Assert.That(_Interface(back[0]).UiName).IsEqualTo("Powertrain");
        await Assert.That(_Interface(back[1]).UiName).IsEqualTo("CAN 4");
        await Assert.That(back[2].InterfaceId).IsEqualTo(back[0].InterfaceId);
    }

    #endregion

    #region BLF time zone

    [Test]
    public async Task BlfDefaultsToUtcOnBothSides()
    {
        Frame[] frames = _CanFrames(_EpochBaseNs + 123_450_000);

        Frame back = _Single(_ReadBlf(_ExportBlf(frames)));

        // Default writer and default reader agree on any machine time zone (1 ms date resolution, anchor whole second).
        await Assert.That(back.Timestamp.AsNanos).IsEqualTo(frames[0].Timestamp.AsNanos);
    }

    [Test]
    public async Task BlfExplicitTimeZoneIsAppliedByWriterAndReader()
    {
        TimeZoneInfo plusFive = TimeZoneInfo.CreateCustomTimeZone("ni-test+5", TimeSpan.FromHours(5), "UTC+5", "UTC+5");
        Frame[] frames = _CanFrames(_EpochBaseNs);
        byte[] blf = _ExportBlf(frames, plusFive);

        Frame sameZone = _Single(_ReadBlf(blf, plusFive));
        Frame asUtc = _Single(_ReadBlf(blf, TimeZoneInfo.Utc));

        // The SYSTEMTIME holds civil time in the writer's zone: read as UTC it is five hours late.
        await Assert.That(sameZone.Timestamp.AsNanos).IsEqualTo(frames[0].Timestamp.AsNanos);
        await Assert.That(asUtc.Timestamp.AsNanos - frames[0].Timestamp.AsNanos).IsEqualTo(5L * 3_600_000_000_000L);
    }

    #endregion

    #region ASC timestamp precision

    [Test]
    public async Task AscNineDecimalsKeepNanoseconds()
    {
        Frame[] frames = _CanFrames(_EpochBaseNs, _EpochBaseNs + 1, _EpochBaseNs + 123_456_789);

        byte[] asc = _ExportAsc(frames, timestampDecimals: AscExporter.MaxTimestampDecimals);
        List<Frame> back = _ReadAsc(asc);

        await Assert.That(back.Count).IsEqualTo(3);
        await Assert.That(back[1].Timestamp.AsNanos - back[0].Timestamp.AsNanos).IsEqualTo(1L);
        await Assert.That(back[2].Timestamp.AsNanos - back[0].Timestamp.AsNanos).IsEqualTo(123_456_789L);
        await Assert.That(Encoding.ASCII.GetString(asc)).Contains("0.123456789 ");
    }

    [Test]
    public async Task AscDefaultWritesMicrosecondsRoundedToNearest()
    {
        Frame[] frames = _CanFrames(_EpochBaseNs, _EpochBaseNs + 123_456_789, _EpochBaseNs + 1_999_999_500);

        string text = Encoding.ASCII.GetString(_ExportAsc(frames));

        // 123 456 789 ns rounds up to 0.123457; 1.9999995 s carries into the seconds.
        await Assert.That(text).Contains("0.123457 ");
        await Assert.That(text).Contains("2.000000 ");
        await Assert.That(text).DoesNotContain("0.123456789");
    }

    [Test]
    [Arguments(AscExporter.MinTimestampDecimals - 1)]
    [Arguments(AscExporter.MaxTimestampDecimals + 1)]
    public async Task AscTimestampDecimalsOutsideTheRangeAreRejected(int decimals)
    {
        await Assert.That(() => AscExporter.CreateBuilder().WithTimestampDecimals(decimals))
            .Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region PCAPNG interface options and link types

    [Test]
    public async Task PcapngInterfaceOptionsSurviveTheRoundTrip()
    {
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.Registry.Register(
            _FactorySourceId(factory),
            "eth0",
            "uplink port",
            LinkType.Ethernet,
            new Dictionary<string, object>
            {
                [PcapInterfacePropertyKeys.Hardware] = "NIC model 7",
                [PcapInterfacePropertyKeys.Os] = "test os",
                [PcapInterfacePropertyKeys.Speed] = 1_000_000_000UL,
                [PcapInterfacePropertyKeys.TxSpeed] = 100_000_000UL,
                [PcapInterfacePropertyKeys.RxSpeed] = 200_000_000UL,
            });
        Frame[] frames = [factory.Create(ifId, LinkType.Ethernet, _EpochBaseNs, FrameGenerators.BuildEthernetIpv4UdpFrame(64))];

        FrameInterfaceInfo back = _Interface(_Single(_ReadPcapng(_ExportPcapng(frames))));

        await Assert.That(back.UiName).IsEqualTo("eth0");
        await Assert.That(back.Description).IsEqualTo("uplink port");
        await Assert.That(back.Properties[PcapInterfacePropertyKeys.Hardware]).IsEqualTo((object)"NIC model 7");
        await Assert.That(back.Properties[PcapInterfacePropertyKeys.Os]).IsEqualTo((object)"test os");
        await Assert.That(back.Properties[PcapInterfacePropertyKeys.Speed]).IsEqualTo((object)1_000_000_000UL);
        await Assert.That(back.Properties[PcapInterfacePropertyKeys.TxSpeed]).IsEqualTo((object)100_000_000UL);
        await Assert.That(back.Properties[PcapInterfacePropertyKeys.RxSpeed]).IsEqualTo((object)200_000_000UL);
    }

    [Test]
    public async Task PcapngUnknownLinkTypePassesThrough()
    {
        LinkType unknown = (LinkType)300;
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01];
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("raw", unknown);

        Frame back = _Single(_ReadPcapng(_ExportPcapng([factory.Create(ifId, unknown, _EpochBaseNs, payload)])));

        await Assert.That((int)back.LinkType).IsEqualTo(300);
        await Assert.That(back.Data.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task PcapngKeepsEveryLinkType()
    {
        // One interface and one frame per enum value; the reader must report each link type unchanged.
        RoundtripFrameFactory factory = new();
        List<Frame> frames = [];
        long ts = _EpochBaseNs;
        foreach (LinkType linkType in Enum.GetValues<LinkType>())
        {
            FrameInterfaceId ifId = factory.AddInterface($"if {(int)linkType}", linkType);
            frames.Add(factory.Create(ifId, linkType, ts, FrameGenerators.BuildEthernetIpv4UdpFrame(64)));
            ts += 1_000;
        }

        List<Frame> back = _ReadPcapng(_ExportPcapng(frames));

        await Assert.That(back.Count).IsEqualTo(frames.Count);
        List<LinkType> mismatches = [];
        for (int i = 0; i < frames.Count; i++)
        {
            if (back[i].LinkType != frames[i].LinkType || !back[i].Data.Span.SequenceEqual(frames[i].Data.Span))
            {
                mismatches.Add(frames[i].LinkType);
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    #endregion

    #region Helpers

    private static Frame[] _CanFrames(params long[] timestampsNs)
    {
        RoundtripFrameFactory factory = new();
        FrameInterfaceId ifId = factory.AddInterface("can", LinkType.CanSocketcan);
        Frame[] frames = new Frame[timestampsNs.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = factory.Create(ifId, LinkType.CanSocketcan, timestampsNs[i], SocketCanGenerators.BuildCanClassic(0x100u + (uint)i, [(byte)i]));
        }

        return frames;
    }

    private static FrameSourceId _FactorySourceId(RoundtripFrameFactory factory) => factory.Registry.AllSources[0].Id;

    private static FrameInterfaceInfo _Interface(Frame frame) => frame.Registry.Get(frame.InterfaceId)!;

    private static Frame _Single(List<Frame> frames) => frames.Count == 1
        ? frames[0]
        : throw new InvalidOperationException($"Expected one frame, got {frames.Count}.");

    private static byte[] _ExportBlf(IReadOnlyList<Frame> frames, TimeZoneInfo? zone = null)
    {
        using MemoryStream ms = new();
        BlfExporter.Builder builder = BlfExporter.CreateBuilder().ToStream(ms);
        if (zone is not null)
        {
            _ = builder.WithTimestampTimeZone(zone);
        }

        using (BlfExporter exporter = builder.Build())
        {
            _ExportAll(exporter, frames);
        }

        return ms.ToArray();
    }

    private static byte[] _ExportAsc(IReadOnlyList<Frame> frames, int timestampDecimals = AscExporter.MinTimestampDecimals)
    {
        using MemoryStream ms = new();
        using (AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).WithTimestampDecimals(timestampDecimals).Build())
        {
            _ExportAll(exporter, frames);
        }

        return ms.ToArray();
    }

    private static byte[] _ExportPcapng(IReadOnlyList<Frame> frames)
    {
        using MemoryStream ms = new();
        using (PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build())
        {
            _ExportAll(exporter, frames);
        }

        return ms.ToArray();
    }

    private static void _ExportAll(IFrameListener exporter, IReadOnlyList<Frame> frames)
    {
        foreach (Frame frame in frames)
        {
            if (!exporter.OnFrame(frame))
            {
                throw new InvalidOperationException($"Exporter rejected frame {frame.Id.Value}.");
            }
        }

        exporter.OnFinish();
        if (exporter is IErrorTolerantExporter { HasErrors: true })
        {
            throw new InvalidOperationException("Exporter reported errors.");
        }
    }

    private static List<Frame> _ReadBlf(byte[] data, TimeZoneInfo? zone = null)
    {
        BlfSourceOptions options = new() { ScanMode = ScanMode.Full, TimestampTimeZone = zone ?? TimeZoneInfo.Utc };
        return _Drain(BlfSource.FromData(data, "roundtrip.blf", options));
    }

    private static List<Frame> _ReadAsc(byte[] data) => _Drain(AscSource.FromData(data, "roundtrip.asc"));

    private static List<Frame> _ReadPcapng(byte[] data) => _Drain(PcapngSource.FromData(data, "roundtrip.pcapng"));

    /// <summary>Reads every frame, then disposes the source. The frames own their bytes (in-memory sources) and keep
    /// the registry alive themselves.</summary>
    private static List<Frame> _Drain(IFrameSource source)
    {
        using IFrameSource owned = source;

        // A round trip must read back cleanly: any skip event fails the test with the reader's own message.
        if (source is IErrorTolerantFrameSource tolerant)
        {
            tolerant.FrameSkipped += static (_, e) => throw new InvalidOperationException($"Reader skipped a frame: {e.Kind}: {e.Message}");
        }

        FrameInterfaceRegistry registry = new();
        source.Start(registry.RegisterSource(source), registry);
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    #endregion
}
