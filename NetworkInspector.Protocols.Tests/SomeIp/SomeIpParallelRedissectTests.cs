// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The parallel re-dissection proof for the one parser that keeps state across packets: a mixed capture of 2,000 frames is ingested in
/// order, and then re-parsed by eight readers at once, in two phases, with every tree compared with the baseline of the first parse.
/// <para>
/// <b>Why this cannot flake:</b> the tree of a packet never changes after its first parse (every cross-packet fact is stored with the
/// packet), so no interleaving of readers and ingest can change the expected tree. A reader that sees a different tree has found a
/// real shared write. Phase A re-parses after the ingest finished. Phase B starts the readers before the ingest and lets each take only
/// packets whose first parse has completed, which is the contract of a replay.
/// </para>
/// <para><b>Thread-safety:</b> the ingest thread is the only writer of the published count; readers use volatile reads.</para>
/// </summary>
internal sealed class SomeIpParallelRedissectTests
{
    private const int _Readers = 8;

    private static SomeIpTestStack _NewStack() => SomeIpTestStack.Create(SomeIpMixedCapture.Config, SomeIpMixedCapture.Settings);

    [Test]
    public async Task EightReadersReproduceEveryTreeAfterTheIngestAndWhileItRuns()
    {
        SomeIpMixedCapture capture = SomeIpMixedCapture.Build();
        await Assert.That(capture.Frames.Count).IsEqualTo(SomeIpMixedCapture.FrameCount);

        // Baseline: the first parse of every packet, on one thread, materialized and captured.
        using SomeIpTestStack baselineStack = _NewStack();
        PacketFieldComparer.PacketFieldSnapshot[] baseline = new PacketFieldComparer.PacketFieldSnapshot[capture.Frames.Count];
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            baseline[i] = PacketFieldComparer.CaptureFields(baselineStack.ParseAt(capture.Frames[i], SomeIpMixedCapture.TimeOf(i)));
        }

        // Phase A: after the ingest, eight readers walk all ids in their own seeded random order.
        await Task.WhenAll(Enumerable.Range(0, _Readers).Select(reader => Task.Run(
            () => _ReadAll(baselineStack, capture, baseline, reader, capture.Frames.Count, published: null)))).ConfigureAwait(false);

        // Phase B: a fresh stack; the readers start before the ingest and only take ids the ingest has published.
        using SomeIpTestStack liveStack = _NewStack();
        int publishedCount = 0;
        using CancellationTokenSource stop = new();
        Task[] readers = [.. Enumerable.Range(0, _Readers).Select(reader => Task.Run(
            () => _ReadWhileIngesting(liveStack, capture, baseline, reader, () => Volatile.Read(ref publishedCount), stop.Token)))];
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            _ = liveStack.ParseAt(capture.Frames[i], SomeIpMixedCapture.TimeOf(i));
            Volatile.Write(ref publishedCount, i + 1);
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(readers).ConfigureAwait(false);

        // After the ingest every id is published, so a final pass of one reader covers what the running readers did not reach.
        await _ReadAll(liveStack, capture, baseline, reader: 99, capture.Frames.Count, published: null).ConfigureAwait(false);
    }
    [Test]
    public async Task TheMixedCaptureReallyExercisesTheFeaturesItIsBuiltFor()
    {
        SomeIpMixedCapture capture = SomeIpMixedCapture.Build();
        using SomeIpTestStack s = _NewStack();
        Dictionary<string, int> seen = [];
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            Packet packet = s.ParseAt(capture.Frames[i], SomeIpMixedCapture.TimeOf(i));
            packet.MaterializeAll();
            foreach (Field field in packet.IterFieldsDfs(materialize: true))
            {
                string name = s.Stack.GetField(field.FieldId)?.Name ?? "";
                seen[name] = seen.GetValueOrDefault(name) + 1;
            }
        }

        string[] expected =
        [
            "someip_sd.entry.type", "someip.timing.request_in", "someip.timing.request_missing", "someip.tp.segment_count",
            "someip.tp.partial", "someip.tp.error.reset", "someip.tp.error.overlap", "someip.tp.error.evicted", "someip.e2e.crc_valid",
            "someip.payload.tlv.tag", "someip.payload.union.type", "tcp.error.resync",
        ];
        foreach (string name in expected)
        {
            await Assert.That(seen.GetValueOrDefault(name)).IsGreaterThan(0).Because($"the capture contains '{name}'");
        }

        int someIpPackets = seen.GetValueOrDefault("someip");
        await Assert.That(someIpPackets).IsGreaterThan(1000);
    }


    private static async Task _ReadAll(
        SomeIpTestStack stack,
        SomeIpMixedCapture capture,
        PacketFieldComparer.PacketFieldSnapshot[] baseline,
        int reader,
        int count,
        Func<int>? published)
    {
        int[] order = [.. Enumerable.Range(0, count)];
        new Random(reader).Shuffle(order);
        foreach (int id in order)
        {
            Packet again = stack.ReparseAt(capture.Frames[id], id, SomeIpMixedCapture.TimeOf(id));
            await PacketFieldComparer.AssertMatchesSnapshot(stack.Stack, baseline[id], again).ConfigureAwait(false);
        }

        _ = published;
    }

    private static async Task _ReadWhileIngesting(
        SomeIpTestStack stack,
        SomeIpMixedCapture capture,
        PacketFieldComparer.PacketFieldSnapshot[] baseline,
        int reader,
        Func<int> published,
        CancellationToken stop)
    {
        Random random = new(reader);
        while (!stop.IsCancellationRequested)
        {
            int available = published();
            if (available == 0)
            {
                // Nothing is published yet; give the ingest thread the processor instead of spinning.
                _ = Thread.Yield();
                continue;
            }

            int id = random.Next(available);
            Packet again = stack.ReparseAt(capture.Frames[id], id, SomeIpMixedCapture.TimeOf(id));
            await PacketFieldComparer.AssertMatchesSnapshot(stack.Stack, baseline[id], again).ConfigureAwait(false);
        }
    }
}
