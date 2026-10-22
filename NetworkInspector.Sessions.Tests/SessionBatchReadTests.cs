// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// The session reads the frames of <c>ReadPackets</c> (both overloads) and of <c>Restart</c> in chunks of 64 packets, one
/// <see cref="IRandomAccessFrameSource.FramesById"/> call per source. The packets, the nulls, and the errors equal those of
/// the per-packet read.
/// </summary>
internal sealed class SessionBatchReadTests
{
    #region ReadPackets

    [Test]
    [Arguments("one-pcapng")]
    [Arguments("two-interleaved-pcapng")]
    [Arguments("gapped")]
    [Arguments("hole")]
    public async Task ReadPacketsEqualsAPerPacketLoopForBothOverloads(string kind)
    {
        // Arrange: the case's session after ingest, and the packets a per-id loop returns.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        int total = _AddCase(session, kind);
        await Assert.That(session.TryStart(out _)).IsTrue();
        _ = session.WaitForCompletion();
        await Assert.That(session.PacketCount).IsEqualTo(total);

        // Act / Assert: start ids around the edges and buffer lengths around the chunk size.
        List<string> problems = [];
        foreach (int start in new[] { -1, 0, 63, total - 1, total })
        {
            foreach (int length in new[] { 1, 63, 64, 65, 200 })
            {
                _CompareOverloads(session, start, length, total, FormattableString.Invariant($"{kind} start {start} length {length}"), problems);
            }
        }

        session.Shutdown();
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task RecycledPacketsInTheBufferAreReused()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(150);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();

        // Fill a buffer, then read the same range again into it: every slot keeps its packet object.
        Packet?[] buffer = new Packet?[100];
        int first = session.ReadPackets(10, buffer);
        Packet?[] before = [.. buffer];
        int second = session.ReadPackets(10, buffer);
        int reused = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            reused += ReferenceEquals(before[i], buffer[i]) && buffer[i] is not null ? 1 : 0;
        }

        PacketRef[] refs = new PacketRef[100];
        for (int i = 0; i < refs.Length; i++)
        {
            refs[i] = new PacketRef(new PacketId(10 + i), before[i]);
        }

        int third = session.ReadPackets(10, refs, out PacketIdLayout layout);
        int reusedRefs = 0;
        for (int i = 0; i < refs.Length; i++)
        {
            reusedRefs += ReferenceEquals(before[i], refs[i].Packet) && refs[i].Packet is not null ? 1 : 0;
        }

        session.Shutdown();
        await Assert.That(first).IsEqualTo(100);
        await Assert.That(second).IsEqualTo(100);
        await Assert.That(third).IsEqualTo(100);
        await Assert.That(reused).IsEqualTo(100);
        await Assert.That(reusedRefs).IsEqualTo(100);
        await Assert.That(layout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task ReadPacketsReturnsZeroWhileQueriesAreDisabled()
    {
        // A restart disables queries while it re-reads the frames; a read issued from the source hook sees them disabled.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using HookRandomAccessSource source = new(TestFrameSource.WithUdpFrames(20));
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();

        int plain = -1;
        int withRefs = -1;
        bool queriesEnabledInside = true;
        source.OnFrameById = frameId =>
        {
            queriesEnabledInside = session.QueriesEnabled;
            plain = session.ReadPackets(0, new Packet?[8]);
            withRefs = session.ReadPackets(0, new PacketRef[8], out _);
            source.OnFrameById = null;
        };

        session.Restart(static registry => TestHarness.CreateStack(registry));
        _ = session.WaitForCompletion();
        session.Shutdown();

        await Assert.That(queriesEnabledInside).IsFalse();
        await Assert.That(plain).IsEqualTo(0);
        await Assert.That(withRefs).IsEqualTo(0);
    }

    #endregion

    #region Restart

    [Test]
    [Arguments("one-pcapng")]
    [Arguments("two-interleaved-pcapng")]
    public async Task RestartReparsesEveryPacketWithTheSameIdsAndFrameBytes(string kind)
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        int total = _AddCase(session, kind);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();
        List<(int Id, byte[] Data)> before = _Snapshot(session, total);

        session.Restart(static registry => TestHarness.CreateStack(registry));
        _ = session.WaitForCompletion();
        List<(int Id, byte[] Data)> after = _Snapshot(session, session.PacketCount);

        session.Shutdown();
        await Assert.That(after.Count).IsEqualTo(before.Count);
        int differing = 0;
        for (int i = 0; i < before.Count; i++)
        {
            differing += before[i].Id == after[i].Id && before[i].Data.AsSpan().SequenceEqual(after[i].Data) ? 0 : 1;
        }

        await Assert.That(differing).IsEqualTo(0);
    }

    [Test]
    public async Task RestartWithAnUnreadableFrameFailsWithTheSameMessageAsBefore()
    {
        using Stack stack = TestHarness.CreateStack();
        using HoleRandomAccessSource source = new(TestFrameSource.WithUdpFrames(100), holeFrameIndex: 70);
        using Session session = new(stack);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();

        SessionException? thrown = null;
        try
        {
            session.Restart(static registry => TestHarness.CreateStack(registry));
        }
        catch (SessionException exception)
        {
            thrown = exception;
        }

        session.Shutdown();
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(thrown.Message).Contains("PacketId 70");
        await Assert.That(thrown.Message).Contains("FrameById returned null");
    }

    #endregion

    #region Batch calls

    [Test]
    public async Task ReadPacketsOverOneSourceMakesOneBatchCallPerChunkAndNoSingleReads()
    {
        List<(string Source, int[] Ids)> log = [];
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using BatchCountingRandomAccessSource source = new(TestFrameSource.WithUdpFrames(200), "a", log);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();
        log.Clear();
        int singleReadsBefore = source.FrameByIdCalls;

        int filled = session.ReadPackets(0, new Packet?[128]);

        session.Shutdown();
        await Assert.That(filled).IsEqualTo(128);
        await Assert.That(log.Count).IsEqualTo(2);
        await Assert.That(log[0].Ids.Length).IsEqualTo(64);
        await Assert.That(log[1].Ids.Length).IsEqualTo(64);
        await Assert.That(source.FrameByIdCalls).IsEqualTo(singleReadsBefore);
    }

    [Test]
    public async Task ReadPacketsOverTwoSourcesMakesOneAscendingCallPerSourceAndChunk()
    {
        List<(string Source, int[] Ids)> log = [];
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using BatchCountingRandomAccessSource first = new(TestFrameSource.WithUdpFrames(100), "a", log);
        using BatchCountingRandomAccessSource second = new(TestFrameSource.WithUdpFrames(100), "b", log);
        _ = session.TryAddFrameSource(first, out _, out _);
        _ = session.TryAddFrameSource(second, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();
        log.Clear();

        int filled = session.ReadPackets(0, new Packet?[128]);

        // Calls arrive chunk after chunk: the ids of one chunk add up to 64, with at most one call per source.
        List<string> problems = [];
        int chunkTotal = 0;
        HashSet<string> chunkSources = [];
        int chunks = 0;
        foreach ((string name, int[] ids) in log)
        {
            if (!chunkSources.Add(name))
            {
                problems.Add("a source was called twice in one chunk");
            }

            for (int i = 1; i < ids.Length; i++)
            {
                if (ids[i] <= ids[i - 1])
                {
                    problems.Add("ids of one call do not ascend");
                }
            }

            chunkTotal += ids.Length;
            if (chunkTotal == 64)
            {
                chunkTotal = 0;
                chunkSources.Clear();
                chunks++;
            }
            else if (chunkTotal > 64)
            {
                problems.Add("a chunk holds more than 64 ids");
            }
        }

        session.Shutdown();
        await Assert.That(filled).IsEqualTo(128);
        await Assert.That(chunks).IsEqualTo(2);
        await Assert.That(chunkTotal).IsEqualTo(0);
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task RestartMakesOneBatchCallPerChunkOfSixtyFourPackets()
    {
        List<(string Source, int[] Ids)> log = [];
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using BatchCountingRandomAccessSource source = new(TestFrameSource.WithUdpFrames(200), "a", log);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        _ = session.WaitForCompletion();
        log.Clear();
        int singleReadsBefore = source.FrameByIdCalls;

        session.Restart(static registry => TestHarness.CreateStack(registry));
        _ = session.WaitForCompletion();
        session.Shutdown();

        // 200 packets: ceil(200 / 64) = 4 calls (64, 64, 64, 8).
        await Assert.That(log.Count).IsEqualTo(4);
        await Assert.That(log.Sum(static entry => entry.Ids.Length)).IsEqualTo(200);
        await Assert.That(source.FrameByIdCalls).IsEqualTo(singleReadsBefore);
    }

    #endregion

    #region Helpers

    /// <summary>Adds the sources of a case to the session and returns the number of packets it will hold.</summary>
    private static int _AddCase(Session session, string kind)
    {
        switch (kind)
        {
            case "one-pcapng":
                _ = session.TryAddFrameSource(PcapngSource.FromData(SessionPacketFrameTests.BuildPcapng(0, 200), "one"), out _, out _);
                return 200;
            case "two-interleaved-pcapng":
                _ = session.TryAddFrameSource(PcapngSource.FromData(SessionPacketFrameTests.BuildPcapng(0, 100), "a"), out _, out _);
                _ = session.TryAddFrameSource(PcapngSource.FromData(SessionPacketFrameTests.BuildPcapng(1, 100), "b"), out _, out _);
                return 200;
            case "gapped":
                _ = session.TryAddFrameSource(SessionPacketFrameTests.NewGappedSource(150), out _, out _);
                return 150;
            default:
                _ = session.TryAddFrameSource(new HoleRandomAccessSource(TestFrameSource.WithUdpFrames(150), holeFrameIndex: 70), out _, out _);
                return 150;
        }
    }

    /// <summary>Every packet's id and frame bytes through <c>TryGetPacket</c>.</summary>
    private static List<(int Id, byte[] Data)> _Snapshot(Session session, int count)
    {
        List<(int, byte[])> snapshot = [];
        for (int i = 0; i < count; i++)
        {
            if (session.TryGetPacket(new PacketId(i), out Packet? packet, out _))
            {
                snapshot.Add((packet.Id.Value, packet.Frame.Data.ToArray()));
            }
        }

        return snapshot;
    }

    /// <summary>Reads a range with both overloads and with a per-packet loop; records every difference.</summary>
    private static void _CompareOverloads(Session session, int start, int length, int total, string label, List<string> problems)
    {
        // Reference: the per-id loop of the old implementation.
        int expectedCount = start >= 0 && start < total
            ? Math.Min(length, total - start)
            : 0;
        Packet?[] expected = new Packet?[length];
        for (int i = 0; i < expectedCount; i++)
        {
            expected[i] = session.TryGetPacket(new PacketId(start + i), out Packet? packet, out _)
                ? packet
                : null;
        }

        Packet?[] buffer = new Packet?[length];
        int plain = session.ReadPackets(start, buffer);
        PacketRef[] refs = new PacketRef[length];
        int withRefs = session.ReadPackets(start, refs, out PacketIdLayout layout);

        if (plain != expectedCount || withRefs != expectedCount || layout != PacketIdLayout.Contiguous)
        {
            problems.Add(FormattableString.Invariant($"{label}: counts {plain}/{withRefs}, expected {expectedCount}"));
        }

        for (int i = 0; i < expectedCount; i++)
        {
            if (!_Same(expected[i], buffer[i]) || !_Same(expected[i], refs[i].Packet) || refs[i].Id.Value != start + i)
            {
                problems.Add(FormattableString.Invariant($"{label}: slot {i} differs"));
            }
        }
    }

    private static bool _Same(Packet? expected, Packet? actual) =>
        expected is null
            ? actual is null
            : actual is not null && expected.Id.Value == actual.Id.Value && expected.Frame.Data.Span.SequenceEqual(actual.Frame.Data.Span);

    #endregion
}
