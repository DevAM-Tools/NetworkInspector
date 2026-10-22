// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Exactness, memory, and concurrency of the packed packet → frame map.</summary>
internal sealed class PacketFrameMapTests
{
    #region Round trip

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(129)]
    public async Task OneSourceWithConsecutiveFramesRoundTrips(int count) =>
        await _AssertRoundTrip(_Pairs(count, static i => (i, 0)));

    [Test]
    public async Task TwoAlternatingSourcesRoundTrip() =>
        await _AssertRoundTrip(_Pairs(1_000, static i => (i / 2, i % 2)));

    [Test]
    public async Task ThreeSourcesInBlocksOfFiveRoundTrip() =>
        await _AssertRoundTrip(_Pairs(1_000, static i => ((i / 15 * 5) + (i % 5), i / 5 % 3)));

    [Test]
    public async Task OneSourceWithGapsRoundTrips()
    {
        int[] steps = [3, 1, 6, 1, 1, 200, 1];
        int frame = 0;
        (int, int)[] pairs = new (int, int)[700];
        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i] = (frame, 0);
            frame += steps[i % steps.Length];
        }

        await _AssertRoundTrip(pairs);
    }

    [Test]
    public async Task ExtremeIdsRoundTrip() =>
        await _AssertRoundTrip(_Pairs(130, static i => (ArrayIndexIdRange.MaxValue - 130 + i, i % 2 == 0 ? 0 : ArrayIndexIdRange.MaxValue - 1)));

    [Test]
    public async Task NonAscendingFrameIdsFallBackToRawGroups() =>
        await _AssertRoundTrip(_Pairs(300, static i => ((i * 37) % 101, i % 3)));

    [Test]
    public async Task InvalidFrameIdRoundTrips() =>
        await _AssertRoundTrip(_Pairs(70, static i => (i == 5 ? -1 : i, 0)));

    [Test]
    public async Task MissingAndOutOfRangeIndexesReturnFalse()
    {
        PacketFrameMap map = new();
        map.Record(0, new FrameId(1), new FrameSourceId(0));

        bool beyond = map.TryGet(1, out FrameId beyondFrame, out FrameSourceId beyondSource);
        bool negative = map.TryGet(-1, out _, out _);

        await Assert.That(beyond).IsFalse();
        await Assert.That(negative).IsFalse();
        await Assert.That(beyondFrame).IsEqualTo(FrameId.Invalid);
        await Assert.That(beyondSource).IsEqualTo(FrameSourceId.Invalid);
        await Assert.That(() => map.Record(5, new FrameId(2), new FrameSourceId(0))).Throws<InvalidOperationException>();
    }

    #endregion

    #region Footprint

    [Test]
    public async Task OneSourceCostsAtMostHalfAByte() =>
        await Assert.That(_BytesPerPacket(static i => (i, 0))).IsLessThanOrEqualTo(0.5);

    [Test]
    public async Task TwoAlternatingSourcesCostAtMostOneAndAHalfBytes() =>
        await Assert.That(_BytesPerPacket(static i => (i / 2, i % 2))).IsLessThanOrEqualTo(1.5);

    [Test]
    public async Task NonAscendingFrameIdsCostAtMostEightAndAHalfBytes()
    {
        Random random = new(7);
        int[] frames = [.. Enumerable.Range(0, 1_000_000).Select(_ => random.Next())];
        await Assert.That(_BytesPerPacket(i => (frames[i], 0))).IsLessThanOrEqualTo(8.5);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ReadersDuringRecordsNeverSeeAWrongMapping()
    {
        // Arrange: two interleaved sources with gaps.
        const int count = 200_000;
        (int Frame, int Source)[] pairs = _Pairs(count, static i => ((i / 2 * 3) + (i % 7 == 0 ? 1 : 0), i % 2));
        PacketFrameMap map = new();
        int done = 0;
        int failures = 0;
        int reads = 0;

        // Act
        Thread[] readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = new Thread(() =>
            {
                Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = map.Count;
                    if (published == 0)
                    {
                        continue;
                    }

                    int index = random.Next(published);
                    if (!map.TryGet(index, out FrameId frame, out FrameSourceId source)
                        || frame.Value != pairs[index].Frame
                        || source.Value != pairs[index].Source)
                    {
                        Interlocked.Increment(ref failures);
                    }

                    Interlocked.Increment(ref reads);
                }
            })
            { Name = $"PacketFrameMap reader {r}", IsBackground = true };
            readers[r].Start();
        }

        for (int i = 0; i < count; i++)
        {
            map.Record(i, new FrameId(pairs[i].Frame), new FrameSourceId(pairs[i].Source));
        }

        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(reads).IsGreaterThan(0);
        await _AssertReads(map, pairs);
    }

    [Test]
    public async Task InOrderReadersOnSeveralThreadsDuringRecordsNeverSeeAWrongMapping()
    {
        // Arrange: three sources in blocks with gaps, plus a raw (non-ascending) stretch every 5 000 packets.
        const int count = 150_000;
        (int Frame, int Source)[] pairs = _Pairs(
            count,
            static i => i % 5_000 < 70 ? ((i * 37) % 101, i % 3) : ((i / 15 * 6) + (i % 5), i / 5 % 3));
        PacketFrameMap map = new();
        int done = 0;
        int failures = 0;
        int reads = 0;

        // Act: forward and backward walkers on their own threads (each with its own decoded-group cache).
        Thread[] readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            bool backward = r % 2 == 1;
            int seed = r;
            readers[r] = new Thread(() =>
            {
                Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = map.Count;
                    if (published == 0)
                    {
                        continue;
                    }

                    int start = random.Next(published);
                    for (int step = 0; step < 200; step++)
                    {
                        int index = backward ? Math.Max(start - step, 0) : Math.Min(start + step, published - 1);
                        if (!map.TryGet(index, out FrameId frame, out FrameSourceId source)
                            || frame.Value != pairs[index].Frame
                            || source.Value != pairs[index].Source)
                        {
                            Interlocked.Increment(ref failures);
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
            })
            { Name = $"PacketFrameMap in-order reader {r}", IsBackground = true };
            readers[r].Start();
        }

        for (int i = 0; i < count; i++)
        {
            map.Record(i, new FrameId(pairs[i].Frame), new FrameSourceId(pairs[i].Source));
        }

        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(reads).IsGreaterThan(0);
        await _AssertReads(map, pairs, backward: true);
    }

    #endregion

    #region Helpers

    private static (int Frame, int Source)[] _Pairs(int count, Func<int, (int Frame, int Source)> pairAt)
    {
        (int Frame, int Source)[] pairs = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            pairs[i] = pairAt(i);
        }

        return pairs;
    }

    private static double _BytesPerPacket(Func<int, (int Frame, int Source)> pairAt)
    {
        const int count = 1_000_000;
        PacketFrameMap map = new();
        for (int i = 0; i < count; i++)
        {
            (int frame, int source) = pairAt(i);
            map.Record(i, new FrameId(frame), new FrameSourceId(source));
        }

        return map.UsedBytes / (double)count;
    }

    private static async Task _AssertRoundTrip((int Frame, int Source)[] pairs)
    {
        PacketFrameMap map = new();
        for (int i = 0; i < pairs.Length; i++)
        {
            map.Record(i, new FrameId(pairs[i].Frame), new FrameSourceId(pairs[i].Source));
        }

        // Forward and backward: both walks decode every sealed group in full through the per-thread cache.
        await _AssertReads(map, pairs);
        await _AssertReads(map, pairs, backward: true);
        await Assert.That(map.TryGet(pairs.Length, out _, out _)).IsFalse();
    }

    private static async Task _AssertReads(PacketFrameMap map, (int Frame, int Source)[] pairs, bool backward = false)
    {
        await Assert.That(map.Count).IsEqualTo(pairs.Length);
        for (int n = 0; n < pairs.Length; n++)
        {
            int i = backward ? pairs.Length - 1 - n : n;
            bool found = map.TryGet(i, out FrameId frame, out FrameSourceId source);
            if (!found || frame.Value != pairs[i].Frame || source.Value != pairs[i].Source)
            {
                // Name the first wrong packet so the failing group is visible.
                await Assert.That((i, found, frame.Value, source.Value)).IsEqualTo((i, true, pairs[i].Frame, pairs[i].Source));
            }
        }
    }

    #endregion
}
