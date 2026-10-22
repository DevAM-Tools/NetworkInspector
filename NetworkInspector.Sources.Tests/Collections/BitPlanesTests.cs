// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Collections;

/// <summary>Scatter, extract, prefix sum, and equal mask of <see cref="BitPlanes"/>.</summary>
internal sealed class BitPlanesTests
{
    #region Tests

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(33)]
    [Arguments(64)]
    public async Task ScatterExtractPrefixSumAndEqualMaskRoundTrip(int width)
    {
        // Arrange: 64 random lanes limited to the width; two lanes share a value so the equal mask has several bits.
        System.Random random = new(width + 17);
        ulong[] values = new ulong[64];
        ulong widthMask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        for (int lane = 0; lane < 64; lane++)
        {
            values[lane] = (ulong)random.NextInt64() & widthMask;
        }

        values[40] = values[3];
        ulong[] planes = new ulong[width];

        // Act
        BitPlanes.Scatter(values, width, planes);

        // Assert: every lane extracts; prefix sums wrap modulo 2^64; equal mask marks exactly the matching lanes.
        for (int lane = 0; lane < 64; lane++)
        {
            await Assert.That(BitPlanes.Extract(planes, lane)).IsEqualTo(values[lane]);
        }

        foreach (int lane in new[] { 0, 1, 31, 62, 63 })
        {
            ulong expected = 0;
            for (int j = 0; j <= lane; j++)
            {
                expected = unchecked(expected + values[j]);
            }

            await Assert.That(BitPlanes.PrefixSum(planes, BitPlanes.InclusivePrefixMask(lane))).IsEqualTo(expected);
        }

        ulong expectedMask = 0;
        for (int lane = 0; lane < 64; lane++)
        {
            expectedMask |= values[lane] == values[3] ? 1UL << lane : 0;
        }

        await Assert.That(BitPlanes.EqualMask(planes, values[3])).IsEqualTo(expectedMask);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(64)]
    public async Task ExtractAllIsTheInverseOfScatter(int width)
    {
        // Arrange: random lanes limited to the width, and a target buffer full of garbage from an earlier group.
        System.Random random = new(width + 31);
        ulong widthMask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong[] values = new ulong[64];
        for (int lane = 0; lane < 64; lane++)
        {
            values[lane] = (ulong)random.NextInt64() & widthMask;
        }

        ulong[] planes = new ulong[width];
        BitPlanes.Scatter(values, width, planes);
        ulong[] lanes = Enumerable.Repeat(ulong.MaxValue, 64).ToArray();

        // Act
        BitPlanes.ExtractAll(planes, lanes);

        // Assert
        await Assert.That(lanes).IsEquivalentTo(values, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task InclusivePrefixMaskCoversLanesUpToAndIncludingTheLane()
    {
        await Assert.That(BitPlanes.InclusivePrefixMask(0)).IsEqualTo(1UL);
        await Assert.That(BitPlanes.InclusivePrefixMask(1)).IsEqualTo(3UL);
        await Assert.That(BitPlanes.InclusivePrefixMask(62)).IsEqualTo(ulong.MaxValue >> 1);
        await Assert.That(BitPlanes.InclusivePrefixMask(63)).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task ScatterClearsStalePlaneBits()
    {
        // Arrange: planes that hold garbage from an earlier group.
        ulong[] planes = [ulong.MaxValue, ulong.MaxValue, ulong.MaxValue];
        ulong[] values = [1, 2, 3];

        // Act
        BitPlanes.Scatter(values, 2, planes);

        // Assert: only the requested width is rewritten; lanes beyond the values are zero.
        await Assert.That(BitPlanes.Extract(planes.AsSpan(0, 2), 0)).IsEqualTo(1UL);
        await Assert.That(BitPlanes.Extract(planes.AsSpan(0, 2), 2)).IsEqualTo(3UL);
        await Assert.That(BitPlanes.Extract(planes.AsSpan(0, 2), 5)).IsEqualTo(0UL);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(33)]
    [Arguments(64)]
    public async Task PrefixSumPairEqualsTwoSingleSums(int width)
    {
        // Arrange: 64 random lanes limited to the width, scattered into planes.
        System.Random random = new(width + 53);
        ulong widthMask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong[] values = new ulong[64];
        for (int lane = 0; lane < 64; lane++)
        {
            values[lane] = (ulong)random.NextInt64() & widthMask;
        }

        ulong[] planes = new ulong[width];
        BitPlanes.Scatter(values, width, planes);
        (int First, int Second)[] pairs = [(0, 1), (31, 32), (61, 62), (62, 63)];
        List<string> mismatches = [];

        // Act / Assert: the paired pass returns the same two sums as two single passes (a second sum computed with the
        // first mask would differ for every pair).
        foreach ((int first, int second) in pairs)
        {
            ulong maskA = BitPlanes.InclusivePrefixMask(first);
            ulong maskB = BitPlanes.InclusivePrefixMask(second);
            ulong sumA = BitPlanes.PrefixSumPair(planes, maskA, maskB, out ulong sumB);
            if (sumA != BitPlanes.PrefixSum(planes, maskA) || sumB != BitPlanes.PrefixSum(planes, maskB))
            {
                mismatches.Add(Invariant($"width {width}, lanes {first}/{second}"));
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task EqualMaskOfNoPlanesMatchesEveryLane()
    {
        await Assert.That(BitPlanes.EqualMask(ReadOnlySpan<ulong>.Empty, 0)).IsEqualTo(ulong.MaxValue);
    }

    #endregion
}
