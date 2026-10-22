// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the append fast paths of <see cref="RoaringBitmap.Add"/> (last-container shortcut, array append without
/// search) and for the release-store end of its seqlock. Every result is compared with a <see cref="SortedSet{T}"/>.
/// <para><b>Thread safety:</b> each test owns its bitmap; the concurrency test shares it between its own threads.</para>
/// </summary>
internal sealed class RoaringBitmapAppendTests
{
    #region Helpers

    private static async Task _AssertEqualsReference(RoaringBitmap bitmap, SortedSet<uint> reference, IEnumerable<uint> probes)
    {
        await Assert.That(bitmap.Cardinality).IsEqualTo((long)reference.Count);
        await Assert.That(bitmap.Min).IsEqualTo(reference.Min);
        await Assert.That(bitmap.Max).IsEqualTo(reference.Max);
        bool containsMatch = true;
        bool rankMatch = true;
        foreach (uint probe in probes)
        {
            containsMatch &= bitmap.Contains(probe) == reference.Contains(probe);
            rankMatch &= bitmap.Rank(probe) == reference.GetViewBetween(0, probe).Count;
        }

        bool selectMatch = true;
        long position = 0;
        foreach (uint value in reference)
        {
            selectMatch &= bitmap.Select(position) == value;
            position++;
        }

        await Assert.That(containsMatch).IsTrue();
        await Assert.That(rankMatch).IsTrue();
        await Assert.That(selectMatch).IsTrue();
        await Assert.That(bitmap.Select(reference.Count)).IsNull();
    }

    #endregion

    #region Append path

    [Test]
    public async Task MonotonicAddsAcrossPromotionAndKeyEdgesMatchReference()
    {
        RoaringBitmap bitmap = new();
        SortedSet<uint> reference = [];

        // 0..10 000 crosses the array → bitmap promotion at 4 096 values in container 0.
        for (uint v = 0; v <= 10_000; v++)
        {
            bitmap.Add(v);
            _ = reference.Add(v);
        }

        // 65 530..65 545 crosses the high-key edge into a second container; 131 072 opens a third.
        for (uint v = 65_530; v <= 65_545; v++)
        {
            bitmap.Add(v);
            _ = reference.Add(v);
        }

        bitmap.Add(131_072);
        _ = reference.Add(131_072);

        uint[] probes = [0, 1, 4_095, 4_096, 4_097, 10_000, 10_001, 65_529, 65_530, 65_535, 65_536, 65_545, 65_546, 131_071, 131_072, 131_073];
        await _AssertEqualsReference(bitmap, reference, probes);
    }

    [Test]
    public async Task DuplicateAndDescendingAddsStayCorrect()
    {
        RoaringBitmap bitmap = new();
        SortedSet<uint> reference = [];

        // Duplicates, values below the current maximum, and a key below the last container's key.
        foreach (uint v in new uint[] { 10, 5, 10, 7, 70_000, 3, 70_000, 65_536, 4 })
        {
            bitmap.Add(v);
            _ = reference.Add(v);
        }

        uint[] probes = [0, 3, 4, 5, 6, 7, 8, 10, 11, 65_535, 65_536, 69_999, 70_000, 70_001];
        await _AssertEqualsReference(bitmap, reference, probes);
        await Assert.That(bitmap.Cardinality).IsEqualTo(7L);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ReadersDuringAppendsSeeConsistentContainsAndCardinality()
    {
        const uint limit = 2_000_000;
        RoaringBitmap bitmap = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using Barrier start = new(3);
        int done = 0;
        int failures = 0;

        Task writer = Task.Run(() =>
        {
            start.SignalAndWait(timeout.Token);
            for (uint v = 0; v < limit; v += 3)
            {
                bitmap.Add(v);
            }

            Volatile.Write(ref done, 1);
        });

        Task[] readers = new Task[2];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = Task.Run(() =>
            {
                start.SignalAndWait(timeout.Token);
                Random random = new(seed);
                long lastCardinality = 0;
                while (Volatile.Read(ref done) == 0 && !timeout.IsCancellationRequested)
                {
                    if (!bitmap.TryGetMax(out uint max))
                    {
                        continue;
                    }

                    // Every multiple of 3 up to an observed maximum was added before it.
                    uint probe = (uint)random.NextInt64(0, (max / 3) + 1) * 3;
                    if (!bitmap.Contains(probe) || bitmap.Contains(probe + 1))
                    {
                        _ = Interlocked.Increment(ref failures);
                    }

                    long cardinality = bitmap.Cardinality;
                    if (cardinality < lastCardinality)
                    {
                        _ = Interlocked.Increment(ref failures);
                    }

                    lastCardinality = cardinality;
                }
            });
        }

        await writer.WaitAsync(timeout.Token);
        await Task.WhenAll(readers).WaitAsync(timeout.Token);
        await Assert.That(Volatile.Read(ref failures)).IsEqualTo(0);
        await Assert.That(bitmap.Cardinality).IsEqualTo((long)((limit + 2) / 3));
    }

    #endregion
}
