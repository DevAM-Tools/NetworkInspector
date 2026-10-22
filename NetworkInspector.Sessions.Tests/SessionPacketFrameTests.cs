// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Every packet of a session resolves to the frame (id, source, bytes) recorded at ingest: one PCAPNG source, two
/// interleaved PCAPNG sources, and a source whose frame ids have gaps; a restart re-parses every packet from the same
/// frames.
/// </summary>
internal sealed class SessionPacketFrameTests
{
    [Test]
    public async Task OnePcapngSourceResolvesEveryPacketToItsFrame() =>
        await _AssertSession([PcapngSource.FromData(_Pcapng(source: 0, count: 500), "one")], expectedPackets: 500);

    [Test]
    public async Task TwoInterleavedPcapngSourcesResolveEveryPacketToItsFrame() =>
        await _AssertSession(
            [PcapngSource.FromData(_Pcapng(source: 0, count: 400), "a"), PcapngSource.FromData(_Pcapng(source: 1, count: 400), "b")],
            expectedPackets: 800);

    [Test]
    public async Task SourceWithFrameIdGapsResolvesEveryPacketToItsFrame() =>
        await _AssertSession([new GappedFrameSource(300)], expectedPackets: 300);

    #region Helpers

    /// <summary>A little-endian PCAPNG capture of marked UDP frames, for the batch read tests.</summary>
    internal static byte[] BuildPcapng(int source, int count) => _Pcapng(source, count);

    /// <summary>A source whose frame ids have gaps, for the batch read tests.</summary>
    internal static IRandomAccessFrameSource NewGappedSource(int count) => new GappedFrameSource(count);

    /// <summary>
    /// Ingests the sources, then checks for every packet: the frame bytes carry the (source, frame id) marker the frame
    /// was built with, every frame appears exactly once, and per source the frame ids ascend with the packet id. The
    /// same holds after a restart.
    /// </summary>
    private static async Task _AssertSession(IRandomAccessFrameSource[] sources, int expectedPackets)
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        foreach (IRandomAccessFrameSource source in sources)
        {
            await Assert.That(session.TryAddFrameSource(source, out _, out _)).IsTrue();
        }

        await Assert.That(session.TryStart(out _)).IsTrue();
        _ = session.WaitForCompletion();
        await Assert.That(session.PacketCount).IsEqualTo(expectedPackets);
        await Assert.That(_FindMismatch(session, expectedPackets)).IsNull();

        // Restart re-reads every packet's frame through the map and FrameById.
        session.Restart(static registry => TestHarness.CreateStack(registry));
        _ = session.WaitForCompletion();
        await Assert.That(session.PacketCount).IsEqualTo(expectedPackets);
        await Assert.That(_FindMismatch(session, expectedPackets)).IsNull();
        session.Shutdown();
    }

    private static string? _FindMismatch(Session session, int packets)
    {
        HashSet<(int Source, int Frame)> seen = [];
        Dictionary<int, int> lastFramePerSource = [];
        for (int p = 0; p < packets; p++)
        {
            if (!session.TryGetFrame(new PacketId(p), out Frame frame, out SessionFailure? failure))
            {
                return $"Packet {p}: {failure?.Message}";
            }

            // The marker in the last 8 bytes is the (source, frame id) the frame was built with.
            ReadOnlySpan<byte> data = frame.Data.Span;
            int source = BinaryPrimitives.ReadInt32LittleEndian(data[^8..]);
            int frameId = BinaryPrimitives.ReadInt32LittleEndian(data[^4..]);
            if (frameId != frame.Id.Value || !seen.Add((source, frameId)))
            {
                return $"Packet {p}: frame {frame.Id.Value} carries marker ({source}, {frameId}) or repeats.";
            }

            if (lastFramePerSource.TryGetValue(source, out int last) && frameId <= last)
            {
                return $"Packet {p}: frame {frameId} of source {source} does not follow frame {last}.";
            }

            lastFramePerSource[source] = frameId;
        }

        return null;
    }

    /// <summary>A UDP frame whose last 8 bytes are the little-endian source index and frame id.</summary>
    private static byte[] _MarkedFrame(int source, int frameId)
    {
        byte[] frame = TestHarness.GenerateUdpFrame(80);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(frame.Length - 8), source);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(frame.Length - 4), frameId);
        return frame;
    }

    /// <summary>A little-endian PCAPNG capture (SHB, Ethernet IDB, EPBs) of marked UDP frames.</summary>
    private static byte[] _Pcapng(int source, int count)
    {
        List<byte> bytes = [];
        byte[] shb = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(shb, 0x0A0D_0D0A);
        BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(8), 0x1A2B_3C4D);
        BinaryPrimitives.WriteUInt16LittleEndian(shb.AsSpan(12), 1);
        BinaryPrimitives.WriteInt64LittleEndian(shb.AsSpan(16), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(24), 28);
        bytes.AddRange(shb);

        byte[] idb = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(idb, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(idb.AsSpan(8), (ushort)LinkType.Ethernet);
        BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(12), 65_535);
        BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(16), 20);
        bytes.AddRange(idb);

        for (int i = 0; i < count; i++)
        {
            byte[] data = _MarkedFrame(source, i);
            int length = 32 + ((data.Length + 3) & ~3);
            byte[] epb = new byte[length];
            BinaryPrimitives.WriteUInt32LittleEndian(epb, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(4), (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(16), (uint)(i * 1_000));
            BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(20), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(24), (uint)data.Length);
            data.CopyTo(epb.AsSpan(28));
            BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(length - 4), (uint)length);
            bytes.AddRange(epb);
        }

        return [.. bytes];
    }

    /// <summary>Random-access source whose frame ids step by 1, 3, or 7, so the session map stores residuals.</summary>
    /// <remarks>Not thread-safe for NextFrame (one ingest thread); FrameById reads published frames only.</remarks>
    private sealed class GappedFrameSource(int count) : IRandomAccessFrameSource
    {
        private readonly Dictionary<int, byte[]> _Frames = [];
        private readonly int[] _Ids = _BuildIds(count);
        private FrameInterfaceRegistry? _Registry;
        private FrameInterfaceId _InterfaceId;
        private int _Next;

        public string UiName => "gapped";

        public string? Description => null;

        public int? EstimatedFrameCount => count;

        public bool IsRunning => _Registry is not null;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
            _Registry = registry;
            _InterfaceId = registry.Register(sourceId, "gapped", null, LinkType.Ethernet);
            foreach (int id in _Ids)
            {
                _Frames[id] = _MarkedFrame(2, id);
            }
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) =>
            _Next < _Ids.Length
                ? _Create(_Ids[_Next++])
                : null;

        public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) =>
            _Frames.ContainsKey(id.Value)
                ? _Create(id.Value)
                : null;

        public void Dispose() => _Registry = null;

        private Frame _Create(int id) =>
            Frame.Create(new FrameId(id), Timestamp.FromNanos(id * 1_000L), _Frames[id], LinkType.Ethernet, _InterfaceId, _Registry!).Value;

        private static int[] _BuildIds(int count)
        {
            int[] steps = [1, 3, 1, 7, 1, 1];
            int[] ids = new int[count];
            int id = 0;
            for (int i = 0; i < count; i++)
            {
                ids[i] = id;
                id += steps[i % steps.Length];
            }

            return ids;
        }
    }

    #endregion
}
