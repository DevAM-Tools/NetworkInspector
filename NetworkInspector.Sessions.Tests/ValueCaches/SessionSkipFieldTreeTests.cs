// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Ingest skip-parses; listeners still get Build packets; PullFill skip-records from frames.
/// </summary>
internal sealed class SessionSkipFieldTreeTests
{
    #region Helpers

    private static ValueCacheRequest _UdpPortRequest() =>
        new()
        {
            FieldNames = ["udp.srcport"],
        };

    private static ValueCacheRequest _AllFieldsRequest() =>
        new()
        {
            RecordAllFields = true,
        };

    private static Packet _ParseSkip(Stack stack, int packetId = 0)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(0),
            TestHarness.GenerateUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(packetId), stack, frame, FieldTreeMode.Skip);
    }

    private sealed class RecordingValueCacheListener(string uiName = "skip-vc") : IValueCacheListener
    {
        private int _RowsSeen;
        private int _SeriesCount;

        public string UiName { get; } = uiName;

        internal int RowsSeen => Volatile.Read(ref _RowsSeen);

        internal int SeriesCount => Volatile.Read(ref _SeriesCount);

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
            Interlocked.Add(ref _RowsSeen, toIndexExclusive - fromIndex);
            if (cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series))
            {
                Volatile.Write(ref _SeriesCount, series.Count);
            }

            _ = session;
        }
    }

    /// <summary>Non-RA UDP source so the session must wrap it in <see cref="CachedFrameSource"/>.</summary>
    private sealed class ForwardOnlyUdpSource : IFrameSource
    {
        private readonly int _Count;
        private int _Next;
        private FrameInterfaceId _InterfaceId;
        private FrameInterfaceRegistry? _Registry;

        internal ForwardOnlyUdpSource(int count) => _Count = count;

        public string UiName => "ForwardOnlyUdp";

        public string? Description => null;

        public int? EstimatedFrameCount => _Count;

        public bool IsRunning => _Registry is not null;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
            _Registry = registry;
            _InterfaceId = registry.Register(sourceId, "fwd-udp", null, LinkType.Ethernet);
            _Next = 0;
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_Next >= _Count)
            {
                return null;
            }

            int idx = _Next;
            _Next++;
            return Frame.Create(
                new FrameId(idx),
                Timestamp.FromNanos(idx),
                TestHarness.GenerateUdpFrame(),
                LinkType.Ethernet,
                _InterfaceId,
                _Registry!).Value;
        }

        public void Dispose() => _Registry = null;
    }

    #endregion

    [Test]
    public async Task IngestUdpSrcPort_RowsMatchFrameCountAndTryGetPacketBuildsTree()
    {
        const int frameCount = 4;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = _UdpPortRequest(),
            });
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        int rows = ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(rows).IsEqualTo(frameCount);

        bool got = session.TryGetPacket(new PacketId(1), out Packet? packet);
        await Assert.That(got).IsTrue();
        await Assert.That(packet!.HasFieldTree).IsTrue();

        session.Shutdown();
    }

    [Test]
    public async Task IngestUdpSrcPort_ForwardOnlyWrappedSource_RowsMatchFrameCount()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using ForwardOnlyUdpSource source = new(frameCount);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = _UdpPortRequest(),
            });
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        int rows = ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(rows).IsEqualTo(frameCount);

        session.Shutdown();
    }

    [Test]
    public async Task DefaultStore_IngestValueCache_TryGetPacketHasFieldTree()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { ValueCache = _UdpPortRequest() });
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool got = session.TryGetPacket(new PacketId(0), out Packet? packet);
        await Assert.That(got).IsTrue();
        await Assert.That(packet!.HasFieldTree).IsTrue();

        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_StoreOn_UdpSrcPort_MatchesIngestRowCount()
    {
        const int frameCount = 5;
        using Stack ingestStack = TestHarness.CreateStack();
        using TestFrameSource ingestSource = TestFrameSource.WithUdpFrames(frameCount);
        using Session ingest = new(ingestStack, new SessionOptions { ValueCache = _UdpPortRequest() });
        ingest.TryAddFrameSource(ingestSource, out _);
        ingest.TryStart();
        ingest.WaitForCompletion();
        int ingestRows = ingest.IngestValueCache!.Value
            .GetSeries<ulong>(ingestStack.GetFieldId("udp.srcport")!.Value).Count;
        ingest.Shutdown();

        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        session.TryStart();
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);

        await Assert.That(added).IsTrue();
        int pullRows = info!.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(pullRows).IsEqualTo(ingestRows);
        bool stored = session.TryGetPacket(new PacketId(0), out Packet? packet);
        await Assert.That(stored).IsTrue();
        await Assert.That(packet!.HasFieldTree).IsTrue();

        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_StoreOn_RecordAllFields_MatchesIngestRowCount()
    {
        const int frameCount = 3;
        using Stack ingestStack = TestHarness.CreateStack();
        using TestFrameSource ingestSource = TestFrameSource.WithUdpFrames(frameCount);
        using Session ingest = new(ingestStack, new SessionOptions { ValueCache = _AllFieldsRequest() });
        ingest.TryAddFrameSource(ingestSource, out _);
        ingest.TryStart();
        ingest.WaitForCompletion();
        FieldId portId = ingestStack.GetFieldId("udp.srcport")!.Value;
        int ingestSeries = ingest.IngestValueCache!.Value.Series.Count;
        int ingestPortRows = ingest.IngestValueCache.Value.GetSeries<ulong>(portId).Count;
        ingest.Shutdown();

        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        RecordingValueCacheListener listener = new("all-fields");
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(listener, _AllFieldsRequest(), out ValueCacheInfo? info);
        session.TryStart();
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);

        await Assert.That(added).IsTrue();
        int pullSeries = info!.Cache.Series.Count;
        int pullPortRows = info.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(pullSeries).IsEqualTo(ingestSeries);
        await Assert.That(pullPortRows).IsEqualTo(ingestPortRows);

        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_DefaultSession_FillsFromFrame()
    {
        const int frameCount = 4;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        session.TryStart();
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);

        await Assert.That(added).IsTrue();
        int pullRows = info!.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(pullRows).IsEqualTo(frameCount);

        session.Shutdown();
    }

    [Test]
    public async Task TryGetFrame_StoreOn_EqualsStoredPacketFrame()
    {
        const int frameCount = 2;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool gotPacket = session.TryGetPacket(new PacketId(0), out Packet? packet);
        bool gotFrame = session.TryGetFrame(new PacketId(0), out Frame frame);

        await Assert.That(gotPacket).IsTrue();
        await Assert.That(gotFrame).IsTrue();
        await Assert.That(frame).IsEqualTo(packet!.Frame);

        session.Shutdown();
    }

    [Test]
    public async Task TryGetFrame_AfterIngest_ReturnsValidFrame()
    {
        const int frameCount = 2;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool gotPacket = session.TryGetPacket(new PacketId(0), out Packet? packet);
        bool gotFrame = session.TryGetFrame(new PacketId(0), out Frame frame);

        await Assert.That(gotPacket).IsTrue();
        await Assert.That(gotFrame).IsTrue();
        await Assert.That(frame.IsValid).IsTrue();
        await Assert.That(packet!.HasFieldTree).IsTrue();

        session.Shutdown();
    }

    [Test]
    public async Task ReadPackets_AfterIngest_ReturnsBuildPackets()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = _UdpPortRequest(),
            });
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        Packet?[] buffer = new Packet?[frameCount];
        int read = session.ReadPackets(0, buffer);
        await Assert.That(read).IsEqualTo(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            await Assert.That(buffer[i]).IsNotNull();
            await Assert.That(buffer[i]!.HasFieldTree).IsTrue();
        }

        session.Shutdown();
    }

    [Test]
    public async Task TryIsMatch_SkipPacketFromCoreParse_ReturnsNoFieldTree()
    {
        using Stack stack = TestHarness.CreateStack();
        Packet skip = _ParseSkip(stack);
        PacketFilter filter = PacketFilter.Compile("udp", stack).Value;

        bool evaluated = filter.TryIsMatch(skip, out bool matched, out FilterError? failure);

        await Assert.That(evaluated).IsFalse();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.NoFieldTree);
    }

    [Test]
    public async Task TryAddValueCache_WhileIngestProduces_FillsAllRows()
    {
        const int frameCount = 6;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(frameCount);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        WaitHelper.WaitUntil(() => session.PacketCount >= frameCount);

        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out _);
        await Assert.That(added).IsTrue();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= session.PacketCount);

        int packetCount = session.PacketCount;
        int seriesCount = listener.SeriesCount;
        await Assert.That(seriesCount).IsEqualTo(packetCount);
        source.Release();
        session.WaitForCompletion();

        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_FrameByIdHole_FailsSlotWithoutTenRows()
    {
        const int frameCount = 10;
        const int holeId = 5;
        using Stack stack = TestHarness.CreateStack();
        using HoleRandomAccessSource source = new(TestFrameSource.WithUdpFrames(frameCount), holeId);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        await Assert.That(added).IsTrue();
        session.TryStart();
        session.WaitForCompletion();

        WaitHelper.WaitUntil(() =>
            session.GetJobs().Any(job =>
                string.Equals(job.UiName, listener.UiName, StringComparison.Ordinal)
                && job.Status == JobStatus.Failed));

        int seriesCount = info!.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(seriesCount).IsEqualTo(holeId);
        await Assert.That(listener.RowsSeen).IsLessThan(frameCount);

        session.Shutdown();
    }
}
