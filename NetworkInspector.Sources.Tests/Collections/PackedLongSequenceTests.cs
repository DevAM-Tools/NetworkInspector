// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Collections;

/// <summary>
/// Exactness, open-group reads, bounds, concurrency, and memory of <see cref="PackedLongSequence"/>.
/// </summary>
internal sealed class PackedLongSequenceTests
{
    #region Round trip

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(1_000)]
    public async Task ConstantValuesRoundTrip(int count) =>
        await _AssertRoundTrip(_Generate(count, static _ => 123_456_789L));

    [Test]
    [Arguments(65)]
    [Arguments(1_000)]
    public async Task ConstantStepRoundTrips(int count) =>
        await _AssertRoundTrip(_Generate(count, static i => 24L + (i * 4L)));

    [Test]
    [Arguments(129)]
    [Arguments(1_000)]
    public async Task ImixStepsRoundTrip(int count)
    {
        int[] steps = [96, 96, 628, 96, 96, 628, 96, 1552];
        long value = 28;
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = value;
            value += steps[i % steps.Length];
        }

        await _AssertRoundTrip(values);
    }

    [Test]
    public async Task AlternatingUpAndDownStepsRoundTrip() =>
        await _AssertRoundTrip(_Generate(300, static i => 1_000 + (i % 2 == 0 ? 0 : 1) - (i / 7)));

    [Test]
    public async Task ExtremesAndWrapAroundDeltasRoundTrip()
    {
        long[] values = [long.MinValue, long.MaxValue, long.MinValue, 0, long.MaxValue, -1, 1, long.MinValue + 1, long.MaxValue - 1];
        long[] repeated = _Generate(200, i => values[i % values.Length]);

        await _AssertRoundTrip(repeated);
    }

    [Test]
    public async Task RandomValuesRoundTrip()
    {
        System.Random random = new(4711);
        long[] values = _Generate(10_000, _ => random.NextInt64(long.MinValue, long.MaxValue));

        await _AssertRoundTrip(values);
    }

    #endregion

    #region Open group

    [Test]
    [Arguments(1)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(130)]
    public async Task OpenGroupReadsMatchAfterEveryAppend(int count)
    {
        long[] values = _Generate(count, static i => (i * 1_000L) - (i % 3));
        PackedLongSequence sequence = new();

        // Every prefix is readable while the last group is still open, then once more after Complete.
        for (int appended = 0; appended < count; appended++)
        {
            sequence.Append(values[appended]);
            for (int i = 0; i <= appended; i++)
            {
                bool found = sequence.TryGet(i, out long value);
                if (!found || value != values[i])
                {
                    await Assert.That(value).IsEqualTo(values[i]);
                }
            }
        }

        sequence.Complete();
        await _AssertReads(sequence, values);
    }

    #endregion

    #region Bounds

    [Test]
    public async Task EmptySequenceHasNoValues()
    {
        PackedLongSequence sequence = new();

        bool found = sequence.TryGet(0, out long value);

        await Assert.That(sequence.Count).IsEqualTo(0);
        await Assert.That(found).IsFalse();
        await Assert.That(value).IsEqualTo(0L);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(3)]
    [Arguments(int.MaxValue)]
    [Arguments(int.MinValue)]
    public async Task OutOfRangeIndexReturnsFalseAndZero(int index)
    {
        PackedLongSequence sequence = new();
        sequence.Append(7);
        sequence.Append(8);
        sequence.Append(9);

        bool found = sequence.TryGet(index, out long value);

        await Assert.That(found).IsFalse();
        await Assert.That(value).IsEqualTo(0L);
    }

    [Test]
    public async Task AppendAfterCompleteThrowsAndCompleteIsIdempotent()
    {
        PackedLongSequence sequence = new();
        sequence.Append(1);
        sequence.Complete();
        sequence.Complete();

        await Assert.That(() => sequence.Append(2)).Throws<InvalidOperationException>();
        await Assert.That(sequence.Count).IsEqualTo(1);
        await Assert.That(sequence.TryGet(0, out long value) && value == 1).IsTrue();
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ReadersDuringAppendsNeverSeeAWrongValue()
    {
        // Arrange: mixed steps with one wrap-around per 1 000 values.
        const int count = 200_000;
        long[] expected = new long[count];
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            expected[i] = value;
            value = i % 1_000 == 999 ? long.MaxValue - i : unchecked(value + ((i % 5) * 37) + 1);
        }

        PackedLongSequence sequence = new();
        int done = 0;
        int failures = 0;
        int reads = 0;

        // Act: four readers race one writer; each successful read must equal the value of that index.
        Thread[] readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = new Thread(() =>
            {
                System.Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = sequence.Count;
                    if (published == 0)
                    {
                        continue;
                    }

                    int index = random.Next(published);
                    if (!sequence.TryGet(index, out long read) || read != expected[index])
                    {
                        Interlocked.Increment(ref failures);
                    }

                    Interlocked.Increment(ref reads);
                }
            })
            { Name = $"PackedLongSequence reader {r}", IsBackground = true };
            readers[r].Start();
        }

        for (int i = 0; i < count; i++)
        {
            sequence.Append(expected[i]);
        }

        sequence.Complete();
        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(reads).IsGreaterThan(0);
        await _AssertReads(sequence, expected);
    }

    [Test]
    public async Task CachedReadersOnSeveralThreadsDuringAppendsNeverSeeAWrongValue()
    {
        // Arrange: IMIX-like steps with one wrap-around per 1 000 values.
        const int count = 200_000;
        int[] steps = [96, 96, 628, 96, 96, 628, 96, 1552];
        long[] expected = new long[count];
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            expected[i] = value;
            value = i % 1_000 == 999 ? long.MinValue + i : unchecked(value + steps[i % steps.Length]);
        }

        PackedLongSequence sequence = new();
        int done = 0;
        int failures = 0;
        int reads = 0;

        // Act: forward, backward, and random cached readers race one writer, each on its own thread and cache.
        Thread[] readers = new Thread[6];
        for (int r = 0; r < readers.Length; r++)
        {
            int mode = r % 3;
            int seed = r;
            readers[r] = new Thread(() =>
            {
                System.Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = sequence.Count;
                    if (published == 0)
                    {
                        continue;
                    }

                    int start = random.Next(published);
                    for (int step = 0; step < 300; step++)
                    {
                        int index = mode switch
                        {
                            0 => Math.Min(start + step, published - 1),
                            1 => Math.Max(start - step, 0),
                            _ => random.Next(published),
                        };
                        if (!sequence.TryGetCached(index, out long read) || read != expected[index])
                        {
                            Interlocked.Increment(ref failures);
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
            })
            { Name = $"PackedLongSequence cached reader {r}", IsBackground = true };
            readers[r].Start();
        }

        for (int i = 0; i < count; i++)
        {
            sequence.Append(expected[i]);
        }

        sequence.Complete();
        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(reads).IsGreaterThan(0);
        await _AssertCachedReads(sequence, expected, forward: true);
        await _AssertCachedReads(sequence, expected, forward: false);
    }

    #endregion

    #region Cached reads

    [Test]
    [Arguments(1)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(1_000)]
    public async Task CachedReadsMatchForwardBackwardAndRandomly(int count)
    {
        // Arrange: extremes and wrap-around deltas, Complete seals a partial last group.
        long[] pattern = [long.MinValue, 3, long.MaxValue, -1, 0, 1_000_000_007, long.MinValue + 5];
        PackedLongSequence sequence = _Fill(count, i => pattern[i % pattern.Length] + (i % 11));
        long[] expected = _Generate(count, i => pattern[i % pattern.Length] + (i % 11));
        System.Random random = new(count);

        // Act / Assert: every order reads every value exactly; out-of-range ids stay false.
        await _AssertCachedReads(sequence, expected, forward: true);
        await _AssertCachedReads(sequence, expected, forward: false);
        for (int n = 0; n < 2_000; n++)
        {
            int index = random.Next(count);
            bool found = sequence.TryGetCached(index, out long value);
            if (!found || value != expected[index])
            {
                await Assert.That((index, found, value)).IsEqualTo((index, true, expected[index]));
            }
        }

        await Assert.That(sequence.TryGetCached(count, out long beyond)).IsFalse();
        await Assert.That(beyond).IsEqualTo(0L);
        await Assert.That(sequence.TryGetCached(-1, out _)).IsFalse();
    }

    [Test]
    public async Task CachedReadsOfMoreSequencesThanWaysStayApart()
    {
        // Arrange: six sequences (more than the cache ways) whose values differ only by sequence.
        const int count = 640;
        PackedLongSequence[] sequences = new PackedLongSequence[DecodedGroupCache.Ways + 2];
        for (int s = 0; s < sequences.Length; s++)
        {
            int offset = s * 1_000_000;
            sequences[s] = _Fill(count, i => offset + (i * 7L));
        }

        int failures = 0;

        // Act: one thread interleaves the sequences id by id, so entries are evicted and reused constantly.
        for (int i = 0; i < count; i++)
        {
            for (int s = 0; s < sequences.Length; s++)
            {
                if (!sequences[s].TryGetCached(i, out long value) || value != (s * 1_000_000) + (i * 7L))
                {
                    failures++;
                }
            }
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task CachedReadsOfSequencesWithTheSameGroupIndexStayApart()
    {
        // Arrange: two sequences whose group indexes are equal at every id, read alternately one value at a time (the MRU
        // way of the cache then belongs to the other sequence on every second call).
        const int count = 640;
        PackedLongSequence first = _Fill(count, static i => 10_000L + (i * 7L));
        PackedLongSequence second = _Fill(count, static i => 5_000_000L + (i * 13L));
        int failures = 0;

        // Act
        for (int i = 0; i < count; i++)
        {
            if (!first.TryGetCached(i, out long a) || a != 10_000L + (i * 7L)
                || !second.TryGetCached(i, out long b) || b != 5_000_000L + (i * 13L))
            {
                failures++;
            }
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task FindNeverReturnsAnotherSequencesGroup()
    {
        // Arrange: sequence 1 touches group 5; sequence 2 asks for group 5.
        DecodedGroupCache cache = DecodedGroupCache.Current;
        cache.Touch(owner: 9_001, group: 5);
        int ownHit = cache.Find(9_001, 5, out _);

        // Act: the previous hit is the MRU way, but the owner differs.
        int foreign = cache.Find(9_002, 5, out bool foreignDecoded);
        int foreignAfterOwn = cache.Find(9_002, 5, out _);

        // Assert
        await Assert.That(ownHit).IsGreaterThanOrEqualTo(0);
        await Assert.That(foreign).IsEqualTo(-1);
        await Assert.That(foreignDecoded).IsFalse();
        await Assert.That(foreignAfterOwn).IsEqualTo(-1);
    }

    [Test]
    public async Task CachedReadsOfTheOpenGroupFollowAppends()
    {
        // Arrange: a sequence that stays open, read through the cache after every append.
        PackedLongSequence sequence = new();
        int failures = 0;

        // Act: the open group is never cached, so every new value is visible at once.
        for (int i = 0; i < 300; i++)
        {
            sequence.Append(i * 3L);
            for (int j = Math.Max(0, i - 70); j <= i; j++)
            {
                if (!sequence.TryGetCached(j, out long value) || value != j * 3L)
                {
                    failures++;
                }
            }
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
    }

    #endregion

    #region Cached reads with the next value

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(128)]
    [Arguments(130)]
    public async Task CachedReadsWithNextEqualTwoPlainReads(int count)
    {
        // Arrange: constant step, IMIX steps, and extremes with wrap-around; sealed (Complete) and unsealed.
        int[] imix = [96, 96, 628, 96, 96, 628, 96, 1552];
        long[] extremes = [long.MinValue, long.MaxValue, 0, -1, long.MaxValue - 1, long.MinValue + 1];
        long imixValue = 28;
        long[] imixValues = new long[count];
        for (int i = 0; i < count; i++)
        {
            imixValues[i] = imixValue;
            imixValue += imix[i % imix.Length];
        }

        long[][] patterns =
        [
            _Generate(count, static i => 24L + (i * 4L)),
            imixValues,
            _Generate(count, i => extremes[i % extremes.Length]),
        ];
        List<string> mismatches = [];
        System.Random random = new(count);

        // Act / Assert
        foreach (long[] expected in patterns)
        {
            foreach (bool sealedSequence in new[] { false, true })
            {
                PackedLongSequence sequence = new();
                foreach (long value in expected)
                {
                    sequence.Append(value);
                }

                if (sealedSequence)
                {
                    sequence.Complete();
                }

                // Forward, backward, and random walks reach the first-touch path, the decoded-cache path, and lane 63.
                for (int walk = 0; walk < 3; walk++)
                {
                    for (int n = 0; n < count; n++)
                    {
                        int index = walk switch
                        {
                            0 => n,
                            1 => count - 1 - n,
                            _ => random.Next(count),
                        };
                        string? problem = _CheckWithNext(sequence, expected, index);
                        if (problem is not null)
                        {
                            mismatches.Add(problem);
                        }
                    }
                }

                // Out-of-range indexes are false and report nothing.
                foreach (int outside in new[] { -1, count, int.MaxValue })
                {
                    if (sequence.TryGetCachedWithNext(outside, out long value, out long next, out bool hasNext)
                        || value != 0 || next != 0 || hasNext)
                    {
                        mismatches.Add(Invariant($"index {outside} out of range was accepted"));
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task CachedReadsWithNextDuringAppendsAreExact()
    {
        // Arrange: IMIX-like steps; one writer appends while four readers read random published pairs.
        const int count = 200_000;
        int[] steps = [96, 96, 628, 96, 96, 628, 96, 1552];
        long[] expected = new long[count];
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            expected[i] = value;
            value += steps[i % steps.Length];
        }

        PackedLongSequence sequence = new();
        int done = 0;
        int failures = 0;
        int reads = 0;
        Thread[] readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = new Thread(() =>
            {
                System.Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = sequence.Count;
                    if (published == 0)
                    {
                        continue;
                    }

                    int index = random.Next(published);
                    bool found = sequence.TryGetCachedWithNext(index, out long read, out long next, out bool hasNext);
                    if (!found || read != expected[index] || (hasNext && next != expected[index + 1]) || (!hasNext && next != 0))
                    {
                        _ = Interlocked.Increment(ref failures);
                    }

                    _ = Interlocked.Increment(ref reads);
                }
            })
            { Name = Invariant($"PackedLongSequence pair reader {r}"), IsBackground = true };
            readers[r].Start();
        }

        // Act
        for (int i = 0; i < count; i++)
        {
            sequence.Append(expected[i]);
        }

        sequence.Complete();
        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(reads).IsGreaterThan(0);
    }

    #endregion

    #region Footprint

    [Test]
    public async Task ConstantStepCostsAtMostFortyHundredthsOfAByte()
    {
        PackedLongSequence sequence = _Fill(1_000_000, static i => 1_000 + (i * 92L));

        await Assert.That(sequence.UsedBytes / (double)sequence.Count).IsLessThanOrEqualTo(0.40);
    }

    [Test]
    public async Task ImixStepsCostAtMostOnePointFiftyFiveBytes()
    {
        int[] steps = [96, 96, 628, 96, 96, 628, 96, 1552, 96, 628, 96, 628];
        long value = 0;
        PackedLongSequence sequence = new();
        for (int i = 0; i < 1_000_000; i++)
        {
            sequence.Append(value);
            value += steps[i % steps.Length];
        }

        sequence.Complete();

        await Assert.That(sequence.UsedBytes / (double)sequence.Count).IsLessThanOrEqualTo(1.55);
    }

    [Test]
    public async Task RandomValuesCostAtMostEightAndAHalfBytes()
    {
        System.Random random = new(99);
        PackedLongSequence sequence = _Fill(1_000_000, _ => random.NextInt64(long.MinValue, long.MaxValue));

        await Assert.That(sequence.UsedBytes / (double)sequence.Count).IsLessThanOrEqualTo(8.5);
    }

    [Test]
    public async Task AppendAllocatesOnlyWordChunks()
    {
        // Arrange: warm the first chunks so the measured window allocates new chunks only (the sequence stays open).
        PackedLongSequence sequence = new();
        for (int i = 0; i < 1_000; i++)
        {
            sequence.Append(i * 92L);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act: 64 000 constant-step appends need two words per group, far below one 64 KiB chunk per call.
        for (int i = 1_000; i < 65_000; i++)
        {
            sequence.Append(i * 92L);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert: fewer than two chunk sizes (word chunks plus group-info chunks).
        await Assert.That(allocated).IsLessThan(2L * PackedWordStore.ChunkWords * sizeof(ulong));
    }

    #endregion

    #region Helpers

    private static long[] _Generate(int count, Func<int, long> valueAt)
    {
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = valueAt(i);
        }

        return values;
    }

    private static PackedLongSequence _Fill(int count, Func<int, long> valueAt)
    {
        PackedLongSequence sequence = new();
        for (int i = 0; i < count; i++)
        {
            sequence.Append(valueAt(i));
        }

        sequence.Complete();
        return sequence;
    }

    private static async Task _AssertRoundTrip(long[] values)
    {
        PackedLongSequence sequence = new();
        foreach (long value in values)
        {
            sequence.Append(value);
        }

        sequence.Complete();
        await _AssertReads(sequence, values);
    }

    /// <summary>The first difference between <c>TryGetCachedWithNext</c> and the expected values at <paramref name="index"/>, or null.</summary>
    private static string? _CheckWithNext(PackedLongSequence sequence, long[] expected, int index)
    {
        bool found = sequence.TryGetCachedWithNext(index, out long value, out long next, out bool hasNext);
        bool expectNext = index + 1 < expected.Length;
        long expectedNext = expectNext
            ? expected[index + 1]
            : 0;
        return found && value == expected[index] && hasNext == expectNext && next == expectedNext
            ? null
            : Invariant($"index {index} of {expected.Length}: found={found} value={value} next={next} hasNext={hasNext}");
    }

    private static async Task _AssertCachedReads(PackedLongSequence sequence, long[] values, bool forward)
    {
        for (int n = 0; n < values.Length; n++)
        {
            int i = forward ? n : values.Length - 1 - n;
            bool found = sequence.TryGetCached(i, out long value);
            if (!found || value != values[i])
            {
                await Assert.That((i, found, value)).IsEqualTo((i, true, values[i]));
            }
        }
    }

    private static async Task _AssertReads(PackedLongSequence sequence, long[] values)
    {
        await Assert.That(sequence.Count).IsEqualTo(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            bool found = sequence.TryGet(i, out long value);
            if (!found || value != values[i])
            {
                // Name the failing index in the assertion so the first divergence is visible.
                await Assert.That((i, found, value)).IsEqualTo((i, true, values[i]));
            }
        }
    }

    #endregion
}
