// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Collections;

/// <summary>
/// Bit-sliced storage of up to 64 unsigned values ("lanes"): plane <c>b</c> holds bit <c>b</c> of every lane, lane
/// <c>j</c> in bit <c>j</c>. A prefix sum over lanes is <c>Σ_b 2^b · PopCount(plane_b &amp; mask)</c>: one hardware
/// population count per plane (<c>POPCNT</c> on x64, <c>CNT</c> on ARM64), independent of the lane position.
/// </summary>
/// <remarks>
/// Stateless; all members are thread-safe. Sums are modulo 2^64 by design: the callers store wrap-around deltas and rely
/// on unchecked arithmetic to read every <see cref="long"/> back exactly.
/// </remarks>
internal static class BitPlanes
{
    #region Public API

    /// <summary>Mask of lanes 0..<paramref name="lane"/> inclusive. Lane 63 yields all ones (2 &lt;&lt; 63 wraps to 0).</summary>
    /// <remarks>Caller guarantees <c>0 ≤ lane ≤ 63</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong InclusivePrefixMask(int lane) => unchecked((2UL << lane) - 1);

    /// <summary>Writes the low <paramref name="width"/> bits of each value into the first <paramref name="width"/> planes.</summary>
    /// <remarks>
    /// Caller guarantees <c>values.Length ≤ 64</c>, <c>planes.Length ≥ width</c>, and that no value has bits at or above
    /// <paramref name="width"/>; the planes are cleared first, so stale words of a reused buffer never leak into a group.
    /// </remarks>
    internal static void Scatter(ReadOnlySpan<ulong> values, int width, Span<ulong> planes)
    {
        planes[..width].Clear();

        // Visit only set bits: constant-size workloads have few, so the loop cost follows the data.
        for (int lane = 0; lane < values.Length; lane++)
        {
            ulong laneBit = 1UL << lane;
            ulong remaining = values[lane];
            while (remaining != 0)
            {
                int bit = BitOperations.TrailingZeroCount(remaining);
                planes[bit] |= laneBit;
                remaining &= remaining - 1;
            }
        }
    }

    /// <summary>Value of one lane.</summary>
    /// <remarks>Caller guarantees <c>0 ≤ lane ≤ 63</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Extract(ReadOnlySpan<ulong> planes, int lane)
    {
        ulong value = 0;
        for (int bit = 0; bit < planes.Length; bit++)
        {
            value |= ((planes[bit] >> lane) & 1UL) << bit;
        }

        return value;
    }

    /// <summary>Values of all 64 lanes (the inverse of <see cref="Scatter"/>).</summary>
    /// <remarks>
    /// Caller guarantees <c>lanes.Length ≥ 64</c>. Visits only set plane bits, so the cost follows the data: a constant
    /// step (no planes) costs one clear, mixed sizes a few hundred bit operations per 64 lanes.
    /// </remarks>
    internal static void ExtractAll(ReadOnlySpan<ulong> planes, Span<ulong> lanes)
    {
        lanes[..64].Clear();
        for (int bit = 0; bit < planes.Length; bit++)
        {
            ulong bitValue = 1UL << bit;
            ulong remaining = planes[bit];
            while (remaining != 0)
            {
                lanes[BitOperations.TrailingZeroCount(remaining)] |= bitValue;
                remaining &= remaining - 1;
            }
        }
    }

    /// <summary>Sum of the lanes selected by <paramref name="laneMask"/>, modulo 2^64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong PrefixSum(ReadOnlySpan<ulong> planes, ulong laneMask)
    {
        ulong sum = 0;
        for (int bit = 0; bit < planes.Length; bit++)
        {
            sum += unchecked((ulong)BitOperations.PopCount(planes[bit] & laneMask) << bit);
        }

        return sum;
    }

    /// <summary>
    /// Sums of the lanes selected by <paramref name="maskA"/> and by <paramref name="maskB"/> in one pass over the
    /// planes (each plane is loaded once), modulo 2^64.
    /// </summary>
    /// <returns>The sum for <paramref name="maskA"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong PrefixSumPair(ReadOnlySpan<ulong> planes, ulong maskA, ulong maskB, out ulong sumB)
    {
        ulong sumA = 0;
        ulong second = 0;
        for (int bit = 0; bit < planes.Length; bit++)
        {
            ulong plane = planes[bit];
            sumA += unchecked((ulong)BitOperations.PopCount(plane & maskA) << bit);
            second += unchecked((ulong)BitOperations.PopCount(plane & maskB) << bit);
        }

        sumB = second;
        return sumA;
    }

    /// <summary>Mask of the lanes whose value equals <paramref name="value"/> (only the plane bits are compared).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong EqualMask(ReadOnlySpan<ulong> planes, ulong value)
    {
        ulong mask = ulong.MaxValue;
        for (int bit = 0; bit < planes.Length; bit++)
        {
            mask &= ((value >> bit) & 1UL) != 0 ? planes[bit] : ~planes[bit];
        }

        return mask;
    }

    #endregion
}
