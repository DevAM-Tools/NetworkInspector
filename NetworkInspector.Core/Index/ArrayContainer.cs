// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Sorted ushort array container. Efficient for sparse data (≤4096 elements).
/// Automatically promotes to <see cref="BitmapContainer"/> at 4096 elements.
///
/// SIMD acceleration is applied to the following operations (Vector128, 8 ushorts / 128 bits):
/// <list type="bullet">
///   <item><see cref="Contains"/>: linear scan with early exit (sorted order) for small arrays.</item>
///   <item><see cref="And"/>: all-pairs comparison via cyclic rotations of the B window —
///         finds all matches in parallel, then advances the pointer with the smaller max.</item>
///   <item><see cref="Or"/>, <see cref="AndNot"/>, <see cref="Xor"/>: SIMD bulk-copy of
///         non-overlapping chunks; overlapping regions handled with the scalar two-pointer walk.</item>
/// </list>
/// Scalar fallbacks are provided for all code paths so that the container works correctly on
/// platforms without hardware SIMD support (e.g., WASM, older x86).
/// </summary>
internal sealed class ArrayContainer : IContainer
{
    internal const int MaxCapacity = 4096;

    // Elements ≤ this threshold use SIMD linear scan in Contains instead of binary search.
    // Below ~32 elements SIMD linear scan outperforms binary search because it avoids
    // branch-prediction overhead from the log2(n) comparisons.
    private const int SimdLinearScanThreshold = 32;

    private ushort[] _Values;
    private int _Count;

    /// <summary>Creates an empty array container with default capacity.</summary>
    internal ArrayContainer()
    {
        _Values = new ushort[4];
        _Count = 0;
    }

    /// <summary>Creates an array container from existing sorted values.</summary>
    internal ArrayContainer(ushort[] values, int count)
    {
        _Values = values;
        _Count = count;
    }

    public int Cardinality => _Count;
    /// <inheritdoc cref="IContainer.Min"/>
    public ushort Min => _Values[0]; // caller must ensure Cardinality > 0
    /// <inheritdoc cref="IContainer.Max"/>
    public ushort Max => _Values[_Count - 1]; // caller must ensure Cardinality > 0

    /// <summary>Returns the value at the given index (unchecked for speed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort ValueAt(int index) => _Values[index];

    /// <summary>
    /// Checks whether <paramref name="value"/> is present.
    /// Uses SIMD linear scan for small arrays (≤<see cref="SimdLinearScanThreshold"/> elements)
    /// and binary search for larger ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ushort value)
    {
        if (_Count <= SimdLinearScanThreshold)
        {
            return SimdLinearContains(_Values, _Count, value);
        }

        return Array.BinarySearch(_Values, 0, _Count, value) >= 0;
    }

    public IContainer Add(ushort value)
    {
        int idx = Array.BinarySearch(_Values, 0, _Count, value);
        if (idx >= 0)
        {
            return this;
        } // already present

        // Promote to bitmap at threshold
        if (_Count >= MaxCapacity)
        {
            BitmapContainer bitmap = new();
            for (int i = 0; i < _Count; i++)
            {
                bitmap.Add(_Values[i]);
            }
            bitmap.Add(value);
            return bitmap;
        }

        int insertAt = ~idx;

        // Grow if needed
        if (_Count == _Values.Length)
        {
            int newCap = Math.Min(_Values.Length * 2, MaxCapacity + 1);
            Array.Resize(ref _Values, newCap);
        }

        // Shift elements right
        if (insertAt < _Count)
        {
            Array.Copy(_Values, insertAt, _Values, insertAt + 1, _Count - insertAt);
        }
        _Values[insertAt] = value;
        _Count++;

        return this;
    }

    public IContainer Clone()
    {
        ushort[] valueCopy = new ushort[_Count];
        Array.Copy(_Values, valueCopy, _Count);
        return new ArrayContainer(valueCopy, _Count);
    }

    public IContainer And(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            ushort[] result = new ushort[Math.Min(_Count, arr._Count)];
            int k = SimdIntersect(_Values, _Count, arr._Values, arr._Count, result);
            return new ArrayContainer(result, k);
        }

        // Fallback: probe other container
        ushort[] res = new ushort[_Count];
        int cnt = 0;
        for (int i = 0; i < _Count; i++)
        {
            if (other.Contains(_Values[i]))
            {
                res[cnt++] = _Values[i];
            }
        }

        return new ArrayContainer(res, cnt);
    }

    public IContainer Or(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            ushort[] result = new ushort[_Count + arr._Count];
            int k = SimdMerge(_Values, _Count, arr._Values, arr._Count, result);

            if (k > MaxCapacity)
            {
                BitmapContainer bitmap = new();
                for (int m = 0; m < k; m++)
                {
                    bitmap.Add(result[m]);
                }

                return bitmap;
            }

            return new ArrayContainer(result, k);
        }

        // Fallback
        IContainer merged = other;
        for (int i = 0; i < _Count; i++)
        {
            merged = merged.Add(_Values[i]);
        }

        return merged;
    }

    public IContainer AndNot(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            ushort[] result = new ushort[_Count];
            int k = SimdDifference(_Values, _Count, arr._Values, arr._Count, result);
            return new ArrayContainer(result, k);
        }

        // Fallback: probe other container
        ushort[] res = new ushort[_Count];
        int cnt = 0;
        for (int i = 0; i < _Count; i++)
        {
            if (!other.Contains(_Values[i]))
            {
                res[cnt++] = _Values[i];
            }
        }

        return new ArrayContainer(res, cnt);
    }

    public IContainer Xor(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            ushort[] result = new ushort[_Count + arr._Count];
            int k = SimdSymmetricDifference(_Values, _Count, arr._Values, arr._Count, result);

            if (k > MaxCapacity)
            {
                BitmapContainer bitmap = new();
                for (int m = 0; m < k; m++)
                {
                    bitmap.Add(result[m]);
                }

                return bitmap;
            }

            return new ArrayContainer(result, k);
        }

        // Fallback: use bitmap for complex XOR
        BitmapContainer bmp = new();
        for (int i = 0; i < _Count; i++)
        {
            bmp.Add(_Values[i]);
        }

        return bmp.Xor(other);
    }

    #region ===================================================================
    // SIMD helper methods — Vector128<ushort> (8 elements / 128 bits)
    // All methods include scalar fallbacks for non-SIMD platforms (WASM, older x86).
    #endregion

    #region ===================================================================

    /// <summary>
    /// SIMD linear scan for contains on a small sorted ushort array.
    /// Processes 8 elements per iteration using Vector128. Exploits sorted order to exit
    /// early once a chunk's first element exceeds <paramref name="value"/>.
    /// Falls back to scalar loop on platforms without Vector128 support.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SimdLinearContains(ushort[] values, int count, ushort value)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            Vector128<ushort> target = Vector128.Create(value);
            while (i + vecSize <= count)
            {
                // Early exit: array is sorted — if the first element of this chunk is already
                // greater than value, value cannot be in this or any later chunk.
                if (values[i] > value)
                {
                    return false;
                }

                if (Vector128.EqualsAny(Vector128.LoadUnsafe(ref values[i]), target))
                {
                    return true;
                }

                i += vecSize;
            }
        }

        // Scalar tail / non-SIMD fallback — also uses sorted order for early exit
        for (; i < count; i++)
        {
            if (values[i] == value)
            {
                return true;
            }

            if (values[i] > value)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// SIMD vectorized intersection of two sorted ushort arrays (AND / set intersection).
    ///
    /// Algorithm: for each pair of 8-element windows (vA, vB), compute an all-pairs equality
    /// matrix by comparing vA against all 8 cyclic rotations of vB. Each rotation tests a
    /// different alignment, so the OR of all 8 comparisons yields a mask where bit i is set
    /// iff a[i] appears anywhere in the current vB window. Matching elements are extracted
    /// via the MSB mask. Pointer advance: the side with the smaller maximum is advanced
    /// (its elements cannot match any future window on the other side).
    ///
    /// Correctness proof snippet — advance invariant:
    ///   If maxA &lt;= maxB: all a[i..i+7] were compared against b[j..j+7]. Any future b[j+8..]
    ///   has value &gt; maxB >= maxA, so a[i..i+7] cannot match those. Advance A. ✓
    ///   If maxB &lt;= maxA: symmetric. Advance B. ✓
    ///
    /// Falls back to scalar two-pointer walk for the tail and on non-SIMD platforms.
    /// <paramref name="result"/> must have capacity ≥ Min(aCount, bCount).
    /// </summary>
    private static int SimdIntersect(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        int i = 0, j = 0, k = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            while (i + vecSize <= aCount && j + vecSize <= bCount)
            {
                Vector128<ushort> vA = Vector128.LoadUnsafe(ref a[i]);
                Vector128<ushort> vB = Vector128.LoadUnsafe(ref b[j]);

                // All-pairs comparison: mask[lane] = 0xFFFF iff a[i+lane] is in b[j..j+7]
                Vector128<ushort> matchMask = ComputeMatchMask(vA, vB);

                // Extract one bit per ushort lane (bit 15 of each ushort element)
                uint laneMask = matchMask.ExtractMostSignificantBits();

                // Write matching elements from the A window to result
                while (laneMask != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(laneMask);
                    result[k++] = a[i + lane];
                    laneMask &= laneMask - 1; // clear lowest set bit
                }

                // Advance the pointer with the smaller window maximum (see correctness note above).
                ushort maxA = a[i + vecSize - 1];
                ushort maxB = b[j + vecSize - 1];
                if (maxA <= maxB)
                {
                    i += vecSize;
                }

                if (maxB <= maxA)
                {
                    j += vecSize;
                }
            }
        }

        // Scalar two-pointer walk for the remaining tail / non-SIMD fallback
        while (i < aCount && j < bCount)
        {
            if (a[i] < b[j])
            {
                i++;
            }
            else if (a[i] > b[j])
            {
                j++;
            }
            else
            {
                result[k++] = a[i++];
                j++;
            }
        }

        return k;
    }

    /// <summary>
    /// SIMD-accelerated sorted merge of two sorted ushort arrays (OR / union).
    /// When a full 8-element chunk from one side lies entirely below the other side's minimum,
    /// the chunk is bulk-copied with a single SIMD store. Overlapping regions are handled by
    /// the scalar two-pointer walk (one element per step).
    /// <paramref name="result"/> must have capacity ≥ aCount + bCount.
    /// </summary>
    private static int SimdMerge(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        int i = 0, j = 0, k = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            while (i + vecSize <= aCount && j + vecSize <= bCount)
            {
                ushort lastA = a[i + vecSize - 1]; // max of current A chunk
                ushort firstB = b[j];              // min of current B chunk
                ushort lastB = b[j + vecSize - 1]; // max of current B chunk
                ushort firstA = a[i];              // min of current A chunk

                if (lastA < firstB)
                {
                    // A chunk is entirely below B: no overlap → SIMD bulk-copy A chunk
                    Vector128.LoadUnsafe(ref a[i]).StoreUnsafe(ref result[k]);
                    i += vecSize;
                    k += vecSize;
                    continue;
                }

                if (lastB < firstA)
                {
                    // B chunk is entirely below A: no overlap → SIMD bulk-copy B chunk
                    Vector128.LoadUnsafe(ref b[j]).StoreUnsafe(ref result[k]);
                    j += vecSize;
                    k += vecSize;
                    continue;
                }

                // Ranges overlap: scalar step (advances one element at a time)
                if (a[i] < b[j])
                {
                    result[k++] = a[i++];
                }
                else if (a[i] > b[j])
                {
                    result[k++] = b[j++];
                }
                else
                {
                    result[k++] = a[i++];
                    j++; // duplicate: emit once
                }
            }
        }

        // Scalar tail / non-SIMD fallback
        while (i < aCount && j < bCount)
        {
            if (a[i] < b[j])
            {
                result[k++] = a[i++];
            }
            else if (a[i] > b[j])
            {
                result[k++] = b[j++];
            }
            else
            {
                result[k++] = a[i++];
                j++;
            }
        }

        while (i < aCount)
        {
            result[k++] = a[i++];
        }

        while (j < bCount)
        {
            result[k++] = b[j++];
        }

        return k;
    }

    /// <summary>
    /// SIMD-accelerated sorted difference (A \ B) for ANDNOT.
    /// When an 8-element A chunk lies entirely below the current B minimum, all 8 elements
    /// are absent from B and are bulk-copied with a single SIMD store. When a B chunk lies
    /// entirely below the current A minimum, the whole B chunk is skipped (bulk-advance).
    /// Overlapping regions are handled by the scalar two-pointer walk.
    /// <paramref name="result"/> must have capacity ≥ aCount.
    /// </summary>
    private static int SimdDifference(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        int i = 0, j = 0, k = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            while (i + vecSize <= aCount && j + vecSize <= bCount)
            {
                ushort lastA = a[i + vecSize - 1]; // max of current A chunk
                ushort firstB = b[j];              // min of current B chunk
                ushort lastB = b[j + vecSize - 1]; // max of current B chunk
                ushort firstA = a[i];              // min of current A chunk

                if (lastA < firstB)
                {
                    // Entire A chunk < min(remaining B): all A elements absent from B → include all
                    Vector128.LoadUnsafe(ref a[i]).StoreUnsafe(ref result[k]);
                    i += vecSize;
                    k += vecSize;
                    continue;
                }

                if (lastB < firstA)
                {
                    // Entire B chunk < min(A chunk): none can match current A → skip B chunk
                    j += vecSize;
                    continue;
                }

                // Ranges overlap: scalar step
                if (a[i] < b[j])
                {
                    result[k++] = a[i++];
                }
                else if (a[i] > b[j])
                {
                    j++;
                }
                else
                {
                    i++;
                    j++; // in both: exclude from result
                }
            }
        }

        // Scalar tail / non-SIMD fallback
        while (i < aCount && j < bCount)
        {
            if (a[i] < b[j])
            {
                result[k++] = a[i++];
            }
            else if (a[i] > b[j])
            {
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }

        while (i < aCount)
        {
            result[k++] = a[i++];
        }

        return k;
    }

    /// <summary>
    /// SIMD-accelerated symmetric difference (A △ B) for XOR.
    /// Non-overlapping chunks from either side are bulk-copied with a single SIMD store.
    /// Overlapping regions are handled by the scalar two-pointer walk.
    /// <paramref name="result"/> must have capacity ≥ aCount + bCount.
    /// </summary>
    private static int SimdSymmetricDifference(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        int i = 0, j = 0, k = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            while (i + vecSize <= aCount && j + vecSize <= bCount)
            {
                ushort lastA = a[i + vecSize - 1]; // max of current A chunk
                ushort firstB = b[j];              // min of current B chunk
                ushort lastB = b[j + vecSize - 1]; // max of current B chunk
                ushort firstA = a[i];              // min of current A chunk

                if (lastA < firstB)
                {
                    // A chunk entirely before B: all A elements only in A → include all
                    Vector128.LoadUnsafe(ref a[i]).StoreUnsafe(ref result[k]);
                    i += vecSize;
                    k += vecSize;
                    continue;
                }

                if (lastB < firstA)
                {
                    // B chunk entirely before A: all B elements only in B → include all
                    Vector128.LoadUnsafe(ref b[j]).StoreUnsafe(ref result[k]);
                    j += vecSize;
                    k += vecSize;
                    continue;
                }

                // Ranges overlap: scalar step
                if (a[i] < b[j])
                {
                    result[k++] = a[i++];
                }
                else if (a[i] > b[j])
                {
                    result[k++] = b[j++];
                }
                else
                {
                    i++;
                    j++; // in both: exclude from result
                }
            }
        }

        // Scalar tail / non-SIMD fallback
        while (i < aCount && j < bCount)
        {
            if (a[i] < b[j])
            {
                result[k++] = a[i++];
            }
            else if (a[i] > b[j])
            {
                result[k++] = b[j++];
            }
            else
            {
                i++;
                j++;
            }
        }

        while (i < aCount)
        {
            result[k++] = a[i++];
        }

        while (j < bCount)
        {
            result[k++] = b[j++];
        }

        return k;
    }

    /// <summary>
    /// Computes an all-pairs equality mask for two 8-element ushort vectors.
    /// <c>result[i] = 0xFFFF</c> iff <c>vA[i]</c> equals any element in <c>vB</c>.
    ///
    /// Implementation: compares <c>vA</c> against all 8 cyclic rotations of <c>vB</c> and
    /// OR-accumulates the results. Each rotation shifts vB by one more ushort position,
    /// so the final mask captures every possible alignment between the two windows.
    /// Rotations are implemented via byte-level shuffles using the precomputed table
    /// <see cref="RotByteShuffles"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> ComputeMatchMask(Vector128<ushort> vA, Vector128<ushort> vB)
    {
        ref byte shuffleBase = ref MemoryMarshal.GetReference(RotByteShuffles);

        // Rotation 0: identity — compare vA against vB as-is
        Vector128<ushort> mask = Vector128.Equals(vA, vB);

        // Rotations 1–7: shift vB by 1…7 ushort positions and compare.
        // Each LoadUnsafe call reads 16 bytes from the precomputed shuffle table at the
        // offset corresponding to that rotation (16 bytes per rotation entry).
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 0)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 16)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 32)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 48)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 64)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 80)).AsUInt16());
        mask |= Vector128.Equals(vA, Vector128.Shuffle(vB.AsByte(), Vector128.LoadUnsafe(ref shuffleBase, 96)).AsUInt16());

        return mask;
    }

    /// <summary>
    /// Byte-level shuffle index table for cyclic rotations 1–7 of an 8-element ushort vector
    /// (7 × 16 bytes). Rotating by n ushort positions is equivalent to rotating by 2n byte
    /// positions within the 16-byte Vector128.
    ///
    /// Layout for rotation r (0-based ushort lane s → source lane (s+r) mod 8):
    ///   byte index 2s   = ((s+r) mod 8) * 2
    ///   byte index 2s+1 = ((s+r) mod 8) * 2 + 1
    ///
    /// Used by <see cref="ComputeMatchMask"/> via <c>Vector128.Shuffle</c>.
    /// </summary>
    private static ReadOnlySpan<byte> RotByteShuffles =>
    [
        // Rotation 1: [u1,u2,u3,u4,u5,u6,u7,u0] — byte indices [2,3,4,5,6,7,8,9,10,11,12,13,14,15,0,1]
        2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1,
        // Rotation 2: [u2,u3,u4,u5,u6,u7,u0,u1]
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3,
        // Rotation 3: [u3,u4,u5,u6,u7,u0,u1,u2]
        6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5,
        // Rotation 4: [u4,u5,u6,u7,u0,u1,u2,u3]
        8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7,
        // Rotation 5: [u5,u6,u7,u0,u1,u2,u3,u4]
        10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        // Rotation 6: [u6,u7,u0,u1,u2,u3,u4,u5]
        12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        // Rotation 7: [u7,u0,u1,u2,u3,u4,u5,u6]
        14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
    ];
    #endregion
}
