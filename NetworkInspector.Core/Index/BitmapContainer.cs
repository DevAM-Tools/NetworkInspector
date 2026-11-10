// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Bitmap container using 1024 ulongs (8KB) for O(1) insert/contains.
/// Uses hardware POPCNT via <see cref="BitOperations.PopCount(ulong)"/> and
/// SIMD (Vector256/Vector128) for bulk AND/OR/XOR/ANDNOT operations.
/// <para>
/// <b>Why a fixed 8 KiB bit set:</b> one bit per possible low-16-bit value (65536 bits) makes membership and insertion a
/// single word access, and it is the smaller representation once a chunk holds more than
/// <see cref="ArrayContainer.MaxCapacity"/> values (the array would need more than 8 KiB). The size is fixed by the
/// 16-bit chunk range, so it is a format constant and not a setting. 8 KiB is also far below the large-object-heap threshold (about 85 KB),
/// so a bitmap, including the per-operation result bitmaps, is allocated on the normal generational heap and not in the LOH.
/// </para>
/// <para>
/// <b>Cardinality bookkeeping:</b> <see cref="Cardinality"/> always equals the number of set bits. <see cref="Add"/> maintains
/// it incrementally; every bulk operation recomputes it from the words it writes (the SIMD loops count bits in the same pass
/// that produces them, so no second pass over the 8 KiB is needed).
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe; mutated in place only by <see cref="Add"/> (the set operations return new
/// containers and never write into an operand; the run container writes range masks into a fresh container that it still owns).
/// Concurrent readers are made safe one level up by the sequence lock of the owning <see cref="RoaringBitmap"/>, which
/// discards a read that overlapped a mutation.
/// </para>
/// </summary>
internal sealed class BitmapContainer : IContainer
{

    #region Configuration and state

    /// <summary>Number of 64-bit words in the bit set: 1024 * 64 = 65536 bits, one per possible 16-bit value.</summary>
    internal const int BitmapSize = 1024;

    /// <summary>
    /// The bit set: bit <c>v % 64</c> of word <c>v / 64</c> is set when the value <c>v</c> is present. Exposed internally so
    /// the run container can apply range masks directly; anything
    /// that writes it must keep <see cref="Cardinality"/> consistent afterwards.
    /// </summary>
    internal readonly ulong[] Bitmap = new ulong[BitmapSize];

    /// <summary>Number of set bits; see the class remarks for how it is kept in sync.</summary>
    public int Cardinality { get; private set; }

    #endregion

    #region Membership, adding and copy

    /// <inheritdoc cref="IContainer.Min"/>
    public ushort Min
    {
        // Caller must ensure Cardinality > 0 — an empty bitmap has no defined minimum.
        get
        {
            for (int i = 0; i < BitmapSize; i++)
            {
                if (Bitmap[i] != 0)
                {
                    return (ushort)(i * 64 + BitOperations.TrailingZeroCount(Bitmap[i]));
                }
            }
            return ushort.MinValue; // unreachable when Cardinality > 0
        }
    }

    /// <inheritdoc cref="IContainer.Max"/>
    public ushort Max
    {
        // Caller must ensure Cardinality > 0 — an empty bitmap has no defined maximum.
        get
        {
            for (int i = BitmapSize - 1; i >= 0; i--)
            {
                if (Bitmap[i] != 0)
                {
                    return (ushort)(i * 64 + 63 - BitOperations.LeadingZeroCount(Bitmap[i]));
                }
            }
            return ushort.MinValue; // unreachable when Cardinality > 0
        }
    }

    /// <summary>Deep copy: a new 8 KiB word array with the same bits and cardinality.</summary>
    /// <returns>A new bitmap container that shares no storage with this one.</returns>
    public IContainer Clone()
    {
        BitmapContainer copy = new();
        Array.Copy(Bitmap, copy.Bitmap, BitmapSize);
        copy.Cardinality = Cardinality;
        return copy;
    }

    /// <summary>O(1) membership test. Every <see cref="ushort"/> maps to a valid word (0..1023), so no range check is needed.</summary>
    /// <param name="value">The low 16 bits of the id to look up.</param>
    /// <returns><see langword="true"/> when the bit of <paramref name="value"/> is set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ushort value)
    {
        int word = value >> 6;
        ulong bit = 1UL << (value & 63);
        return (Bitmap[word] & bit) != 0;
    }

    /// <summary>Sets the bit for <paramref name="value"/> and keeps <see cref="Cardinality"/> in sync. Always returns <see langword="this"/>.</summary>
    /// <param name="value">The low 16 bits of the id to add.</param>
    /// <returns><see langword="this"/>; a bitmap container never changes its kind on an add.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IContainer Add(ushort value)
    {
        int word = value >> 6;
        ulong bit = 1UL << (value & 63);

        // Count only a newly set bit so adding a present value keeps Cardinality exact.
        if ((Bitmap[word] & bit) == 0)
        {
            Bitmap[word] |= bit;
            Cardinality++;
        }
        return this;
    }

    #endregion

    #region Set operations

    /// <summary>
    /// Intersection into a new container. Bitmap with bitmap is a SIMD word AND whose result is converted to an
    /// <see cref="ArrayContainer"/> when it has at most <see cref="ArrayContainer.MaxCapacity"/> values. Any other operand kind
    /// is delegated to that kind's own intersection (AND is commutative; the array and run containers handle a bitmap operand
    /// themselves without calling back, so this cannot recurse).
    /// </summary>
    /// <param name="other">The container to intersect with; not <see langword="null"/>; not modified.</param>
    /// <returns>A new container that holds the values present in both operands.</returns>
    public IContainer And(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result.Cardinality = _SimdAnd(
                Bitmap, bmp.Bitmap, result.Bitmap);

            // Downgrade to array if sparse: at most MaxCapacity values is the range where the array is the smaller form.
            if (result.Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(result);
            }
            return result;
        }

        return other.And(this);
    }

    /// <summary>
    /// Union into a new bitmap, returned without a density check (a union cannot hold fewer values than this bitmap, so
    /// converting it to an array is not attempted). Bitmap with bitmap is a SIMD word OR; a run operand is applied with
    /// word masks; an array operand is added value by value to a clone.
    /// </summary>
    /// <param name="other">The container to unite with; not <see langword="null"/>; not modified.</param>
    /// <returns>A new bitmap container that holds the values present in either operand.</returns>
    public IContainer Or(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result.Cardinality = _SimdOr(
                Bitmap, bmp.Bitmap, result.Bitmap);
            return result;
        }

        // Direct path for runs: clone our words, set every run's range with ulong-mask ops,
        // recompute cardinality once via PopCount. Avoids the value-by-value Add() loop.
        if (other is RunContainer run)
        {
            BitmapContainer cloneRun = new();
            Array.Copy(Bitmap, cloneRun.Bitmap, BitmapSize);
            run.SetRangesIn(cloneRun.Bitmap);
            cloneRun.Cardinality = _PopCountAll(cloneRun.Bitmap);
            return cloneRun;
        }

        // Clone this, add other's elements. The clone keeps the result detached from this container, which must not be
        // mutated by a set operation.
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone.Cardinality = Cardinality;

        // Add all values from the other container into this bitmap. Only ArrayContainer is handled here: the production
        // containers are array, bitmap and run, and the run case returned above. An operand of any other IContainer
        // implementation would be ignored by this branch (the clone is returned unchanged).
        if (other is ArrayContainer arr)
        {
            for (int i = 0; i < arr.Cardinality; i++)
            {
                clone.Add(arr.ValueAt(i));
            }
        }

        return clone;
    }

    /// <summary>
    /// Returns a new container with bits set in this and not in <paramref name="other"/>.
    /// Neither operand is mutated. Against another
    /// <see cref="BitmapContainer"/> the SIMD result is written into one new container in a single pass. The result is converted to an
    /// <see cref="ArrayContainer"/> when it has at most <see cref="ArrayContainer.MaxCapacity"/> values (a difference can
    /// be much sparser than this bitmap).
    /// </summary>
    /// <param name="other">The container whose values are removed; not <see langword="null"/>; not modified.</param>
    /// <returns>A new container that holds the values of this bitmap that are absent from <paramref name="other"/>.</returns>
    public IContainer AndNot(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result.Cardinality = _SimdAndNot(
                Bitmap, bmp.Bitmap, result.Bitmap);

            if (result.Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(result);
            }
            return result;
        }

        // Direct path for runs: clear every run's range with ulong-mask ops.
        if (other is RunContainer run)
        {
            BitmapContainer cloneRun = new();
            Array.Copy(Bitmap, cloneRun.Bitmap, BitmapSize);
            run.ClearRangesIn(cloneRun.Bitmap);
            cloneRun.Cardinality = _PopCountAll(cloneRun.Bitmap);
            if (cloneRun.Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(cloneRun);
            }
            return cloneRun;
        }

        // Fallback: clone this and remove other's elements
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone.Cardinality = Cardinality;
        // Remove elements that exist in other
        if (other is ArrayContainer arr)
        {
            for (int i = 0; i < arr.Cardinality; i++)
            {
                // Use Contains/Remove pattern on clone
                ushort val = arr.ValueAt(i);
                int w = val >> 6;
                ulong bit = 1UL << (val & 63);
                if ((clone.Bitmap[w] & bit) != 0)
                {
                    clone.Bitmap[w] &= ~bit;
                    clone.Cardinality--;
                }
            }
        }

        if (clone.Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(clone);
        }
        return clone;
    }

    /// <summary>
    /// Symmetric difference into a new container. The result is converted to an <see cref="ArrayContainer"/> when it has at most
    /// <see cref="ArrayContainer.MaxCapacity"/> values. Neither operand is mutated.
    /// </summary>
    /// <param name="other">The container to combine with; not <see langword="null"/>; not modified.</param>
    /// <returns>A new container that holds the values present in exactly one operand.</returns>
    public IContainer Xor(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result.Cardinality = _SimdXor(
                Bitmap, bmp.Bitmap, result.Bitmap);

            if (result.Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(result);
            }
            return result;
        }

        // Direct path for runs: toggle every run's range with ulong-mask ops.
        if (other is RunContainer run)
        {
            BitmapContainer cloneRun = new();
            Array.Copy(Bitmap, cloneRun.Bitmap, BitmapSize);
            run.ToggleRangesIn(cloneRun.Bitmap);
            cloneRun.Cardinality = _PopCountAll(cloneRun.Bitmap);
            if (cloneRun.Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(cloneRun);
            }
            return cloneRun;
        }

        // Fallback: clone this and toggle other's elements
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone.Cardinality = Cardinality;
        if (other is ArrayContainer arr)
        {
            for (int i = 0; i < arr.Cardinality; i++)
            {
                ushort val = arr.ValueAt(i);
                int w = val >> 6;
                ulong bit = 1UL << (val & 63);
                if ((clone.Bitmap[w] & bit) != 0)
                {
                    clone.Bitmap[w] &= ~bit;
                    clone.Cardinality--;
                }
                else
                {
                    clone.Bitmap[w] |= bit;
                    clone.Cardinality++;
                }
            }
        }

        if (clone.Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(clone);
        }
        return clone;
    }

    #endregion

    #region Cardinality recount

    /// <summary>
    /// Total population count of all bits set in <paramref name="bitmap"/>. Used by the
    /// Bitmap×Run direct paths to recompute cardinality after applying range masks.
    /// </summary>
    /// <param name="bitmap">The words to count; exactly <see cref="BitmapSize"/> entries.</param>
    /// <returns>The number of set bits.</returns>
    private static int _PopCountAll(ulong[] bitmap)
    {
        int total = 0;
        for (int i = 0; i < bitmap.Length; i++)
        {
            total += BitOperations.PopCount(bitmap[i]);
        }
        return total;
    }

    /// <summary>
    /// Recomputes cardinality from bitmap words and downgrades to <see cref="ArrayContainer"/> when sparse.
    /// </summary>
    /// <param name="bitmap">
    /// A container whose words were written directly (for example by the range masks of a run container); its
    /// <see cref="Cardinality"/> is overwritten with the real bit count.
    /// </param>
    /// <returns>
    /// <paramref name="bitmap"/> itself, or a new <see cref="ArrayContainer"/> when it holds at most
    /// <see cref="ArrayContainer.MaxCapacity"/> values.
    /// </returns>
    internal static IContainer FromPopcount(BitmapContainer bitmap)
    {
        bitmap.Cardinality = _PopCountAll(bitmap.Bitmap);
        if (bitmap.Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(bitmap);
        }
        return bitmap;
    }

    #endregion

    #region SIMD bulk operations

    /// <summary>
    /// SIMD AND: dst[i] = a[i] &amp; b[i], returns total popcount.
    /// Uses Vector256 (processes 4 ulongs = 256 bits per iteration).
    /// Falls back to scalar loop on platforms without hardware vectors.
    /// </summary>
    /// <remarks>
    /// This is the pattern shared by all the bulk operations below:
    /// <list type="bullet">
    /// <item>The 1024-word arrays are reinterpreted as vectors with <see cref="MemoryMarshal.Cast{TFrom, TTo}(ReadOnlySpan{TFrom})"/>
    /// (no copy). 1024 is a multiple of both 4 (Vector256) and 2 (Vector128), so the vector loop covers every word and the
    /// scalar tail only runs when no vector path is available.</item>
    /// <item>The population count of the result is accumulated in the same loop that produces the words, one scalar POPCNT per
    /// 64-bit lane, so <see cref="Cardinality"/> is known without a second pass over the 8 KiB.</item>
    /// <item><c>Vector256.IsHardwareAccelerated</c> is a JIT-time constant, so the unused branches are removed at compile time.</item>
    /// </list>
    /// The sources and the destination must be distinct arrays of exactly <see cref="BitmapSize"/> words (the container always
    /// allocates them that size). They may not partially overlap.
    /// </remarks>
    /// <param name="a">The first source words.</param>
    /// <param name="b">The second source words.</param>
    /// <param name="dst">The destination words; written completely; a distinct array.</param>
    /// <returns>The number of set bits in <paramref name="dst"/> afterwards.</returns>
    private static int _SimdAnd(ulong[] a, ulong[] b, ulong[] dst)
    {
        int cardinality = 0;
        ReadOnlySpan<ulong> spanA = a.AsSpan();
        ReadOnlySpan<ulong> spanB = b.AsSpan();
        Span<ulong> spanDst = dst.AsSpan();
        int i = 0;

        // Vector256 path: 4 ulongs per iteration (256 bits)
        if (Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector256<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanA);
            ReadOnlySpan<Vector256<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanB);
            Span<Vector256<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector256<ulong> result = vecA[vi] & vecB[vi];
                vecDst[vi] = result;
                // Sum popcount of each 64-bit lane
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecA.Length * 4; // elements already processed
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector128<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanA);
            ReadOnlySpan<Vector128<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanB);
            Span<Vector128<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector128<ulong> result = vecA[vi] & vecB[vi];
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecA.Length * 2;
        }

        // Scalar tail (handles remaining elements and non-SIMD platforms)
        for (; i < BitmapSize; i++)
        {
            ulong val = spanA[i] & spanB[i];
            spanDst[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>SIMD OR: dst[i] = a[i] | b[i], returns total popcount. Same shape and preconditions as <see cref="_SimdAnd"/>.</summary>
    /// <param name="a">The first source words.</param>
    /// <param name="b">The second source words.</param>
    /// <param name="dst">The destination words; written completely; a distinct array.</param>
    /// <returns>The number of set bits in <paramref name="dst"/> afterwards.</returns>
    private static int _SimdOr(ulong[] a, ulong[] b, ulong[] dst)
    {
        int cardinality = 0;
        ReadOnlySpan<ulong> spanA = a.AsSpan();
        ReadOnlySpan<ulong> spanB = b.AsSpan();
        Span<ulong> spanDst = dst.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector256<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanA);
            ReadOnlySpan<Vector256<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanB);
            Span<Vector256<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector256<ulong> result = vecA[vi] | vecB[vi];
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecA.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector128<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanA);
            ReadOnlySpan<Vector128<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanB);
            Span<Vector128<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector128<ulong> result = vecA[vi] | vecB[vi];
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecA.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanA[i] | spanB[i];
            spanDst[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>SIMD ANDNOT: dst[i] = a[i] &amp; ~b[i], returns total popcount. Same shape and preconditions as <see cref="_SimdAnd"/>.</summary>
    /// <param name="a">The source words to keep.</param>
    /// <param name="b">The source words whose bits are removed.</param>
    /// <param name="dst">The destination words; written completely; a distinct array.</param>
    /// <returns>The number of set bits in <paramref name="dst"/> afterwards.</returns>
    private static int _SimdAndNot(ulong[] a, ulong[] b, ulong[] dst)
    {
        int cardinality = 0;
        ReadOnlySpan<ulong> spanA = a.AsSpan();
        ReadOnlySpan<ulong> spanB = b.AsSpan();
        Span<ulong> spanDst = dst.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector256<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanA);
            ReadOnlySpan<Vector256<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanB);
            Span<Vector256<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector256<ulong> result = Vector256.AndNot(vecA[vi], vecB[vi]);
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecA.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector128<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanA);
            ReadOnlySpan<Vector128<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanB);
            Span<Vector128<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector128<ulong> result = Vector128.AndNot(vecA[vi], vecB[vi]);
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecA.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanA[i] & ~spanB[i];
            spanDst[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>SIMD XOR: dst[i] = a[i] ^ b[i], returns total popcount. Same shape and preconditions as <see cref="_SimdAnd"/>.</summary>
    /// <param name="a">The first source words.</param>
    /// <param name="b">The second source words.</param>
    /// <param name="dst">The destination words; written completely; a distinct array.</param>
    /// <returns>The number of set bits in <paramref name="dst"/> afterwards.</returns>
    private static int _SimdXor(ulong[] a, ulong[] b, ulong[] dst)
    {
        int cardinality = 0;
        ReadOnlySpan<ulong> spanA = a.AsSpan();
        ReadOnlySpan<ulong> spanB = b.AsSpan();
        Span<ulong> spanDst = dst.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector256<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanA);
            ReadOnlySpan<Vector256<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanB);
            Span<Vector256<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector256<ulong> result = vecA[vi] ^ vecB[vi];
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecA.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector128<ulong>> vecA = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanA);
            ReadOnlySpan<Vector128<ulong>> vecB = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanB);
            Span<Vector128<ulong>> vecDst = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanDst);

            for (int vi = 0; vi < vecA.Length; vi++)
            {
                Vector128<ulong> result = vecA[vi] ^ vecB[vi];
                vecDst[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecA.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanA[i] ^ spanB[i];
            spanDst[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    #endregion

    #region Conversion to an array container

    /// <summary>
    /// Converts a bitmap container to an array container by extracting all set bits in ascending order.
    /// <para>
    /// <b>Precondition:</b> <see cref="Cardinality"/> equals the number of set bits (the array is allocated with exactly that
    /// size) and is at most <see cref="ArrayContainer.MaxCapacity"/> in the normal use; callers invoke it right after a
    /// bulk operation that recomputed the cardinality. A cardinality smaller than the real bit count would overrun the array.
    /// </para>
    /// </summary>
    /// <param name="bmp">The bitmap to convert; not mutated.</param>
    /// <returns>A new array container with the same values.</returns>
    internal static ArrayContainer BitmapToArray(BitmapContainer bmp)
    {
        // Allocation is sized exactly from the maintained cardinality, so no counting pass or resize is needed.
        ushort[] values = new ushort[bmp.Cardinality];
        int idx = 0;
        for (int i = 0; i < BitmapSize; i++)
        {
            ulong word = bmp.Bitmap[i];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                values[idx++] = (ushort)(i * 64 + bit);
                word &= word - 1; // clear lowest set bit
            }
        }

        return new ArrayContainer(values, idx);
    }

    #endregion
}
