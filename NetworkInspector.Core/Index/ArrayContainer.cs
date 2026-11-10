// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Sorted ushort array container. Efficient for sparse data (≤4096 elements).
/// Automatically promotes to <see cref="BitmapContainer"/> when a distinct 4097th element would be added.
/// <para>
/// <b>Why 4096:</b> an array of <c>n</c> values takes <c>2n</c> bytes and a <see cref="BitmapContainer"/> always takes 8 KiB
/// (8192 bytes), so the array is the smaller representation up to exactly 4096 values (the standard Roaring bitmap
/// crossover); beyond that the bitmap is smaller and has O(1) membership tests.
/// </para>
/// <para>
/// <b>Invariants:</b> the first <see cref="Cardinality"/> slots of the backing array are strictly ascending (sorted, no
/// duplicates). Every operation below relies on that: binary search, the early exit in <see cref="Contains"/> and the
/// merge-style set operations. Members that take a pre-built array (the second constructor) trust the caller for it.
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe. <see cref="Add"/> mutates in place (growth, shifting). Concurrent readers are
/// made safe one level up by the sequence lock of the owning <see cref="RoaringBitmap"/>, which discards a read that
/// overlapped a mutation; a racing read here may therefore return a wrong answer or throw.
/// </para>
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

    #region Configuration and state

    /// <summary>
    /// Largest number of values an array container holds before it becomes a <see cref="BitmapContainer"/>. A format fact
    /// of Roaring bitmaps (array bytes 2n versus the 8 KiB bitmap), so a constant and not a setting. Other containers
    /// also use it as the density threshold when a set-operation result is converted back to an array.
    /// </summary>
    internal const int MaxCapacity = 4096;

    /// <summary>
    /// Largest cardinality for which <see cref="Contains"/> uses the SIMD linear scan instead of binary search.
    /// Below ~32 elements the linear scan outperforms binary search because it avoids the branch-prediction cost of the
    /// log2(n) comparisons.
    /// </summary>
    private const int _SimdLinearScanThreshold = 32;

    /// <summary>
    /// Backing store; only the first <see cref="Cardinality"/> slots are valid (strictly ascending). Longer than
    /// <see cref="Cardinality"/> after growth, exactly <see cref="Cardinality"/> after a set operation or <see cref="Clone"/>.
    /// </summary>
    private ushort[] _Values;

    /// <summary>Number of values stored.</summary>
    public int Cardinality { get; private set; }

    /// <summary>Creates an empty array container with default capacity.</summary>
    internal ArrayContainer()
    {
        // Small initial capacity; Add doubles it when full (see Add).
        _Values = new ushort[4];
        Cardinality = 0;
    }

    /// <summary>
    /// Creates an array container that takes ownership of <paramref name="values"/> (no copy).
    /// The caller guarantees that the first <paramref name="count"/> entries are strictly ascending and that
    /// <c>count</c> is at most <c>values.Length</c> and at most <see cref="MaxCapacity"/>; none of this is validated here
    /// because the callers are the set operations, which produce sorted output by construction.
    /// </summary>
    /// <param name="values">Backing array; the container keeps and mutates it, so the caller must not use it afterwards.</param>
    /// <param name="count">Number of valid leading entries.</param>
    internal ArrayContainer(ushort[] values, int count)
    {
        _Values = values;
        Cardinality = count;
    }

    #endregion

    #region Membership, bounds and copy

    /// <inheritdoc cref="IContainer.Min"/>
    public ushort Min => _Values[0]; // caller must ensure Cardinality > 0

    /// <inheritdoc cref="IContainer.Max"/>
    public ushort Max => _Values[Cardinality - 1]; // caller must ensure Cardinality > 0

    /// <summary>Returns the value at the given index (unchecked for speed).</summary>
    /// <param name="index">The 0-based position in ascending order, 0 to <see cref="Cardinality"/> - 1; not range-checked.</param>
    /// <returns>The value stored at <paramref name="index"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort ValueAt(int index) => _Values[index];

    /// <summary>
    /// Checks whether <paramref name="value"/> is present.
    /// Uses SIMD linear scan for small arrays (≤<see cref="_SimdLinearScanThreshold"/> elements)
    /// and binary search for larger ones.
    /// </summary>
    /// <param name="value">The low 16 bits of the id to look up.</param>
    /// <returns><see langword="true"/> when the container holds <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ushort value)
    {
        if (Cardinality <= _SimdLinearScanThreshold)
        {
            return _SimdLinearContains(_Values, Cardinality, value);
        }

        return Array.BinarySearch(_Values, 0, Cardinality, value) >= 0;
    }

    /// <summary>Counts the values that are at most <paramref name="threshold"/>. Binary search, O(log n).</summary>
    /// <param name="threshold">The largest value to count.</param>
    /// <returns>The number of contained values in <c>0..threshold</c>.</returns>
    internal int Rank(ushort threshold)
    {
        int index = Array.BinarySearch(_Values, 0, Cardinality, threshold);
        return index >= 0
            ? index + 1
            : ~index;
    }

    /// <summary>
    /// Deep copy sized exactly to <see cref="Cardinality"/> (no spare capacity), so the first <see cref="Add"/> on the copy
    /// regrows the array. An empty container clones to a zero-length backing array, which <see cref="Add"/> handles.
    /// </summary>
    /// <returns>A new array container that holds the same values and shares no storage with this one.</returns>
    public IContainer Clone()
    {
        ushort[] valueCopy = new ushort[Cardinality];
        Array.Copy(_Values, valueCopy, Cardinality);
        return new ArrayContainer(valueCopy, Cardinality);
    }

    #endregion

    #region Adding values

    /// <summary>
    /// Inserts <paramref name="value"/> keeping the array sorted. Returns <see langword="this"/>, or a new
    /// <see cref="BitmapContainer"/> when the array already holds <see cref="MaxCapacity"/> values and the value is new
    /// (the caller must continue with the returned container). Adding a present value is a no-op.
    /// </summary>
    /// <param name="value">The low 16 bits of the id to add.</param>
    /// <returns>
    /// <see langword="this"/> when the value was added or already present; a new <see cref="BitmapContainer"/> that holds the
    /// old values and <paramref name="value"/> when the array was full.
    /// </returns>
    public IContainer Add(ushort value)
    {
        // Append shortcut: ids arrive in ascending order, so the value is usually above the current maximum. That needs
        // neither a search nor a shift. Any smaller value takes the binary search and the insert below, so unordered adds
        // stay correct.
        int count = Cardinality;
        int insertAt;
        if (count == 0 || value > _Values[count - 1])
        {
            insertAt = count;
        }
        else
        {
            int idx = Array.BinarySearch(_Values, 0, count, value);
            // Already present: nothing to change.
            if (idx >= 0)
            {
                return this;
            }

            insertAt = ~idx;
        }

        // Promote to bitmap at threshold. Reached only for a value that is not present yet (a duplicate returned above), so
        // the array really would exceed MaxCapacity. The array stays unchanged and is simply dropped by the caller.
        if (Cardinality >= MaxCapacity)
        {
            BitmapContainer bitmap = new();
            for (int i = 0; i < Cardinality; i++)
            {
                bitmap.Add(_Values[i]);
            }

            bitmap.Add(value);
            return bitmap;
        }

        // Grow if needed: doubling gives amortized O(1) appends, capped at MaxCapacity + 1 slots. A zero-length backing
        // array (Clone of an empty container) grows to the default capacity 4, because doubling 0 would stay 0 and the
        // store below would run past the array.
        if (Cardinality == _Values.Length)
        {
            int newCap = Math.Min(Math.Max(_Values.Length * 2, 4), MaxCapacity + 1);
            Array.Resize(ref _Values, newCap);
        }

        // Open a gap by shifting the larger elements right; an append (insertAt == Cardinality) shifts nothing.
        if (insertAt < Cardinality)
        {
            Array.Copy(_Values, insertAt, _Values, insertAt + 1, Cardinality - insertAt);
        }

        _Values[insertAt] = value;
        Cardinality++;

        return this;
    }

    #endregion

    #region Set operations

    /// <summary>
    /// Intersection. Array with array uses the SIMD intersection; against any other kind it probes <paramref name="other"/> for
    /// each of this container's values (this side is the smaller one, at most <see cref="MaxCapacity"/> values).
    /// The result is always a new array container (an intersection cannot exceed this container's size). Neither operand is mutated.
    /// </summary>
    /// <param name="other">The container to intersect with; not <see langword="null"/>; not modified.</param>
    /// <returns>A new array container that holds the values present in both operands.</returns>
    public IContainer And(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            // One read of each array and count: the result buffer is sized from the same numbers the intersection gets. The result
            // cannot be larger than the smaller operand, so the buffer is allocated once at that size.
            int thisCount = Cardinality;
            ushort[] otherValues = arr._Values;
            int otherCount = arr.Cardinality;
            ushort[] result = new ushort[Math.Min(thisCount, otherCount)];
            int k = _SimdIntersect(_Values, thisCount, otherValues, otherCount, result);
            return new ArrayContainer(result, k);
        }

        // Fallback: probe other container
        ushort[] res = new ushort[Cardinality];
        int cnt = 0;
        for (int i = 0; i < Cardinality; i++)
        {
            if (other.Contains(_Values[i]))
            {
                res[cnt++] = _Values[i];
            }
        }

        return new ArrayContainer(res, cnt);
    }

    /// <summary>
    /// Union. Array with array merges into a buffer sized for the worst case (no overlap) and becomes a
    /// <see cref="BitmapContainer"/> if the merged count exceeds <see cref="MaxCapacity"/>. Against any other kind it clones
    /// <paramref name="other"/> and adds this container's values to the clone. Neither operand is mutated.
    /// </summary>
    /// <param name="other">The container to unite with; not <see langword="null"/>; not modified.</param>
    /// <returns>
    /// A new container that holds the values present in either operand: an array container, a <see cref="BitmapContainer"/> for a
    /// dense array-with-array result, or the kind of <paramref name="other"/> for any other operand.
    /// </returns>
    public IContainer Or(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            // One read of each array and count (see And). Worst-case size buffer (disjoint inputs); the merge reports how many
            // entries it really wrote.
            int thisCount = Cardinality;
            ushort[] otherValues = arr._Values;
            int otherCount = arr.Cardinality;
            ushort[] result = new ushort[thisCount + otherCount];
            int k = _SimdMerge(_Values, thisCount, otherValues, otherCount, result);

            // Same density rule as Add: more than MaxCapacity distinct values is cheaper as a bitmap.
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

        // Fallback: clone first — BitmapContainer.Add and RunContainer.Add mutate in place.
        IContainer merged = other.Clone();
        for (int i = 0; i < Cardinality; i++)
        {
            merged = merged.Add(_Values[i]);
        }

        return merged;
    }

    /// <summary>
    /// Difference (this AND NOT <paramref name="other"/>). The result is a new array container (it cannot be larger than this
    /// container). Neither operand is mutated.
    /// </summary>
    /// <param name="other">The container whose values are removed; not <see langword="null"/>; not modified.</param>
    /// <returns>A new array container that holds the values of this container that are absent from <paramref name="other"/>.</returns>
    public IContainer AndNot(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            // One read of each array and count (see And).
            int thisCount = Cardinality;
            ushort[] otherValues = arr._Values;
            int otherCount = arr.Cardinality;
            ushort[] result = new ushort[thisCount];
            int k = _SimdDifference(_Values, thisCount, otherValues, otherCount, result);
            return new ArrayContainer(result, k);
        }

        // Fallback: probe other container
        ushort[] res = new ushort[Cardinality];
        int cnt = 0;
        for (int i = 0; i < Cardinality; i++)
        {
            if (!other.Contains(_Values[i]))
            {
                res[cnt++] = _Values[i];
            }
        }

        return new ArrayContainer(res, cnt);
    }

    /// <summary>
    /// Symmetric difference. Array with array merges and becomes a <see cref="BitmapContainer"/> when the result exceeds
    /// <see cref="MaxCapacity"/>. Against any other kind this container is converted to a bitmap and the bitmap's XOR does the
    /// work (it picks the right result kind). Neither operand is mutated.
    /// </summary>
    /// <param name="other">The container to combine with; not <see langword="null"/>; not modified.</param>
    /// <returns>A new container that holds the values present in exactly one operand.</returns>
    public IContainer Xor(IContainer other)
    {
        if (other is ArrayContainer arr)
        {
            // One read of each array and count (see And).
            int thisCount = Cardinality;
            ushort[] otherValues = arr._Values;
            int otherCount = arr.Cardinality;
            ushort[] result = new ushort[thisCount + otherCount];
            int k = _SimdSymmetricDifference(_Values, thisCount, otherValues, otherCount, result);

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
        for (int i = 0; i < Cardinality; i++)
        {
            bmp.Add(_Values[i]);
        }

        return bmp.Xor(other);
    }

    #endregion

    #region SIMD membership scan

    // SIMD helpers use Vector128<ushort> (8 elements / 128 bits). Every kernel has a scalar fallback for platforms without
    // hardware SIMD (WASM, older x86).

    /// <summary>
    /// SIMD linear scan for contains on a small sorted ushort array.
    /// Processes 8 elements per iteration using Vector128. Exploits sorted order to exit
    /// early once a chunk's first element exceeds <paramref name="value"/>.
    /// Falls back to scalar loop on platforms without Vector128 support.
    /// </summary>
    /// <param name="values">The sorted values.</param>
    /// <param name="count">The number of valid leading entries; bounded to <c>values.Length</c> inside, see the comment in the body.</param>
    /// <param name="value">The value to look for.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is among the first <paramref name="count"/> entries.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _SimdLinearContains(ushort[] values, int count, ushort value)
    {
        // A concurrent reader loads the array and the count at different moments, so it can hold an old, shorter array together
        // with the count of a later insert (the writer grows the array before it raises the count). The wrong answer of such a
        // torn read is discarded by the sequence lock of the bitmap, but the unchecked 8-lane load below must still never run
        // past the array: bound the count to the array we actually hold.
        count = Math.Min(count, values.Length);

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

                // LoadUnsafe does no bounds check on the other seven lanes; the loop condition (i + 8 <= count, with count
                // bounded to values.Length above) keeps all eight lanes inside the array.
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

    #endregion

    #region SIMD sorted-array kernels

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
    /// <param name="a">The first sorted operand.</param>
    /// <param name="aCount">The number of valid leading entries of <paramref name="a"/>; bounded to <c>a.Length</c> inside.</param>
    /// <param name="b">The second sorted operand.</param>
    /// <param name="bCount">The number of valid leading entries of <paramref name="b"/>; bounded to <c>b.Length</c> inside.</param>
    /// <param name="result">The output buffer; receives the common values in ascending order.</param>
    /// <returns>The number of values written to <paramref name="result"/>.</returns>
    private static int _SimdIntersect(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        // The operand of a set operation is read without the sequence lock, so its array and its count can come from different
        // moments (the writer grows the array before it raises the count). The result of such a torn read is wrong by contract,
        // but the unchecked 8-lane loads and stores below must still stay inside the arrays held here: bound both counts. The
        // clamped counts are never larger than the counts the result was sized from, so the store bound
        // (k + 8 <= aCount + bCount <= result.Length) holds.
        aCount = Math.Min(aCount, a.Length);
        bCount = Math.Min(bCount, b.Length);

        int i = 0, j = 0, k = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            int vecSize = Vector128<ushort>.Count; // 8
            while (i + vecSize <= aCount && j + vecSize <= bCount)
            {
                Vector128<ushort> vA = Vector128.LoadUnsafe(ref a[i]);
                Vector128<ushort> vB = Vector128.LoadUnsafe(ref b[j]);

                // All-pairs comparison: mask[lane] = 0xFFFF iff a[i+lane] is in b[j..j+7]
                Vector128<ushort> matchMask = _ComputeMatchMask(vA, vB);

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
    /// <param name="a">The first sorted operand.</param>
    /// <param name="aCount">The number of valid leading entries of <paramref name="a"/>; bounded to <c>a.Length</c> inside.</param>
    /// <param name="b">The second sorted operand.</param>
    /// <param name="bCount">The number of valid leading entries of <paramref name="b"/>; bounded to <c>b.Length</c> inside.</param>
    /// <param name="result">The output buffer; receives the union in ascending order, each value once.</param>
    /// <returns>The number of values written to <paramref name="result"/>.</returns>
    private static int _SimdMerge(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        // The operand of a set operation is read without the sequence lock, so its array and its count can come from different
        // moments (the writer grows the array before it raises the count). The result of such a torn read is wrong by contract,
        // but the unchecked 8-lane loads and stores below must still stay inside the arrays held here: bound both counts. The
        // clamped counts are never larger than the counts the result was sized from, so the store bound
        // (k + 8 <= aCount + bCount <= result.Length) holds.
        aCount = Math.Min(aCount, a.Length);
        bCount = Math.Min(bCount, b.Length);

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
                    // A chunk is entirely below B: no overlap → SIMD bulk-copy A chunk. The unchecked 8-lane store stays
                    // inside result because these 8 values are all part of the output (capacity is aCount + bCount, both clamped at the top).
                    Vector128.LoadUnsafe(ref a[i]).StoreUnsafe(ref result[k]);
                    i += vecSize;
                    k += vecSize;
                    continue;
                }

                if (lastB < firstA)
                {
                    // B chunk is entirely below A: no overlap → SIMD bulk-copy B chunk. Store bound: k + 8 <= aCount + bCount <= result.Length (counts are
                    // clamped to the arrays at the top, and the result was sized from the same counts).
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
    /// <param name="a">The minuend: the sorted values to keep unless they appear in <paramref name="b"/>.</param>
    /// <param name="aCount">The number of valid leading entries of <paramref name="a"/>; bounded to <c>a.Length</c> inside.</param>
    /// <param name="b">The sorted values to remove.</param>
    /// <param name="bCount">The number of valid leading entries of <paramref name="b"/>; bounded to <c>b.Length</c> inside.</param>
    /// <param name="result">The output buffer; receives the values of <paramref name="a"/> that are not in <paramref name="b"/>.</param>
    /// <returns>The number of values written to <paramref name="result"/>.</returns>
    private static int _SimdDifference(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        // The operand of a set operation is read without the sequence lock, so its array and its count can come from different
        // moments (the writer grows the array before it raises the count). The result of such a torn read is wrong by contract,
        // but the unchecked 8-lane loads and stores below must still stay inside the arrays held here: bound both counts. The
        // clamped counts are never larger than the counts the result was sized from, so the store bound
        // (k + 8 <= aCount + bCount <= result.Length) holds.
        aCount = Math.Min(aCount, a.Length);
        bCount = Math.Min(bCount, b.Length);

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
                    // Entire A chunk < min(remaining B): all A elements absent from B → include all. Store bound: k + 8 <= i + 8 <= aCount <=
                    // result.Length (the result was sized from the same, clamped count).
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
    /// <param name="a">The first sorted operand.</param>
    /// <param name="aCount">The number of valid leading entries of <paramref name="a"/>; bounded to <c>a.Length</c> inside.</param>
    /// <param name="b">The second sorted operand.</param>
    /// <param name="bCount">The number of valid leading entries of <paramref name="b"/>; bounded to <c>b.Length</c> inside.</param>
    /// <param name="result">The output buffer; receives the values present in exactly one operand, in ascending order.</param>
    /// <returns>The number of values written to <paramref name="result"/>.</returns>
    private static int _SimdSymmetricDifference(ushort[] a, int aCount, ushort[] b, int bCount, ushort[] result)
    {
        // The operand of a set operation is read without the sequence lock, so its array and its count can come from different
        // moments (the writer grows the array before it raises the count). The result of such a torn read is wrong by contract,
        // but the unchecked 8-lane loads and stores below must still stay inside the arrays held here: bound both counts. The
        // clamped counts are never larger than the counts the result was sized from, so the store bound
        // (k + 8 <= aCount + bCount <= result.Length) holds.
        aCount = Math.Min(aCount, a.Length);
        bCount = Math.Min(bCount, b.Length);

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
                    // A chunk entirely before B: all A elements only in A → include all. Store bound: k + 8 <= aCount + bCount <= result.Length
                    // (the clamped counts never exceed the counts the result was sized from).
                    Vector128.LoadUnsafe(ref a[i]).StoreUnsafe(ref result[k]);
                    i += vecSize;
                    k += vecSize;
                    continue;
                }

                if (lastB < firstA)
                {
                    // B chunk entirely before A: all B elements only in B → include all. Same store bound as the A chunk above.
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

    #endregion

    #region All-pairs match mask

    /// <summary>
    /// Computes an all-pairs equality mask for two 8-element ushort vectors.
    /// <c>result[i] = 0xFFFF</c> iff <c>vA[i]</c> equals any element in <c>vB</c>.
    ///
    /// Implementation: compares <c>vA</c> against all 8 cyclic rotations of <c>vB</c> and
    /// OR-accumulates the results. Each rotation shifts vB by one more ushort position,
    /// so the final mask captures every possible alignment between the two windows.
    /// Rotations are implemented via byte-level shuffles using the precomputed table
    /// <see cref="_RotByteShuffles"/>.
    /// </summary>
    /// <param name="vA">The window of the first operand.</param>
    /// <param name="vB">The window of the second operand.</param>
    /// <returns>A vector whose lane is all ones where the lane of <paramref name="vA"/> occurs in <paramref name="vB"/>, else zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> _ComputeMatchMask(Vector128<ushort> vA, Vector128<ushort> vB)
    {
        ref byte shuffleBase = ref MemoryMarshal.GetReference(_RotByteShuffles);

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
    /// Used by <see cref="_ComputeMatchMask"/> via <c>Vector128.Shuffle</c>.
    /// </summary>
    private static ReadOnlySpan<byte> _RotByteShuffles =>
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
