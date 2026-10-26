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
/// <b>Thread-safety:</b> not thread-safe; mutated in place by <see cref="Add"/> and the <c>...With</c> methods. Concurrent
/// readers are made safe one level up by the sequence lock of the owning <see cref="RoaringBitmap"/>, which discards a read
/// that overlapped a mutation.
/// </para>
/// </summary>
internal sealed class BitmapContainer : IContainer
{
    /// <summary>Number of 64-bit words in the bit set: 1024 * 64 = 65536 bits, one per possible 16-bit value.</summary>
    internal const int BitmapSize = 1024;

    /// <summary>
    /// The bit set: bit <c>v % 64</c> of word <c>v / 64</c> is set when the value <c>v</c> is present. Exposed internally so
    /// the run container can apply range masks directly and the owning bitmap can run in-place word operations; anything
    /// that writes it must keep <see cref="Cardinality"/> consistent afterwards.
    /// </summary>
    internal readonly ulong[] Bitmap = new ulong[BitmapSize];

    /// <summary>Number of set bits; see the class remarks for how it is kept in sync.</summary>
    public int Cardinality { get; private set; }

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
    public IContainer Clone()
    {
        BitmapContainer copy = new();
        Array.Copy(Bitmap, copy.Bitmap, BitmapSize);
        copy.Cardinality = Cardinality;
        return copy;
    }

    /// <summary>O(1) membership test. Every <see cref="ushort"/> maps to a valid word (0..1023), so no range check is needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ushort value)
    {
        int word = value >> 6;
        ulong bit = 1UL << (value & 63);
        return (Bitmap[word] & bit) != 0;
    }

    /// <summary>Sets the bit for <paramref name="value"/> and keeps <see cref="Cardinality"/> in sync. Always returns <see langword="this"/>.</summary>
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

    /// <summary>
    /// Intersection into a new container. Bitmap with bitmap is a SIMD word AND whose result is converted to an
    /// <see cref="ArrayContainer"/> when it has at most <see cref="ArrayContainer.MaxCapacity"/> values. Any other operand kind
    /// is delegated to that kind's own intersection (AND is commutative; the array and run containers handle a bitmap operand
    /// themselves without calling back, so this cannot recurse).
    /// </summary>
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
    /// Neither operand is mutated. For in-place AND-NOT against another
    /// <see cref="BitmapContainer"/>, use <see cref="AndNotWith"/>. The result is converted to an
    /// <see cref="ArrayContainer"/> when it has at most <see cref="ArrayContainer.MaxCapacity"/> values (a difference can
    /// be much sparser than this bitmap).
    /// </summary>
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

    #region In-place mutation methods

    // The In-place variants write the result into this container's words instead of allocating a new 8 KiB result, which
    // is what lets query accumulations (PresenceQuery, RoaringBitmap's *With operations) chain many operations without
    // allocating. They never change the container kind: the caller decides whether a sparse result should become an array
    // (RoaringBitmap does so afterwards via BitmapToArray). The operand is only read, so it may be a shared container.

    /// <summary>In-place AND (this &amp;= other). Updates <see cref="Cardinality"/> from the written words.</summary>
    internal void AndWith(BitmapContainer other) => Cardinality = _SimdAndInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place OR (this |= other). Updates <see cref="Cardinality"/> from the written words.</summary>
    internal void OrWith(BitmapContainer other) => Cardinality = _SimdOrInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place ANDNOT (this &amp;= ~other). Updates <see cref="Cardinality"/> from the written words.</summary>
    internal void AndNotWith(BitmapContainer other) => Cardinality = _SimdAndNotInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place XOR (this ^= other). Updates <see cref="Cardinality"/> from the written words.</summary>
    internal void XorWith(BitmapContainer other) => Cardinality = _SimdXorInPlace(Bitmap, other.Bitmap);

    #endregion

    #region SIMD bulk operations

    /// <summary>
    /// Total population count of all bits set in <paramref name="bitmap"/>. Used by the
    /// Bitmap×Run direct paths to recompute cardinality after applying range masks.
    /// </summary>
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
    internal static IContainer FromPopcount(BitmapContainer bitmap)
    {
        bitmap.Cardinality = _PopCountAll(bitmap.Bitmap);
        if (bitmap.Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(bitmap);
        }
        return bitmap;
    }

    /// <summary>
    /// SIMD AND: dst[i] = a[i] &amp; b[i], returns total popcount.
    /// Uses Vector256 (processes 4 ulongs = 256 bits per iteration).
    /// Falls back to scalar loop on platforms without hardware vectors.
    /// </summary>
    /// <remarks>
    /// This is the pattern shared by all the bulk operations below (and their in-place twins):
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

    /// <summary>SIMD OR: dst[i] = a[i] | b[i], returns total popcount.</summary>
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

    /// <summary>SIMD ANDNOT: dst[i] = a[i] &amp; ~b[i], returns total popcount.</summary>
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

    /// <summary>SIMD XOR: dst[i] = a[i] ^ b[i], returns total popcount.</summary>
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

    #region In-place SIMD operations

    /// <summary>In-place AND: this[i] &amp;= other[i], returns total popcount.</summary>
    private static int _SimdAndInPlace(ulong[] data, ulong[] other)
    {
        int cardinality = 0;
        Span<ulong> spanData = data.AsSpan();
        ReadOnlySpan<ulong> spanOther = other.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            Span<Vector256<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanData);
            ReadOnlySpan<Vector256<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector256<ulong> result = vecData[vi] & vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecData.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Span<Vector128<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanData);
            ReadOnlySpan<Vector128<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector128<ulong> result = vecData[vi] & vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecData.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanData[i] & spanOther[i];
            spanData[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>In-place OR: this[i] |= other[i], returns total popcount.</summary>
    private static int _SimdOrInPlace(ulong[] data, ulong[] other)
    {
        int cardinality = 0;
        Span<ulong> spanData = data.AsSpan();
        ReadOnlySpan<ulong> spanOther = other.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            Span<Vector256<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanData);
            ReadOnlySpan<Vector256<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector256<ulong> result = vecData[vi] | vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecData.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Span<Vector128<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanData);
            ReadOnlySpan<Vector128<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector128<ulong> result = vecData[vi] | vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecData.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanData[i] | spanOther[i];
            spanData[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>In-place ANDNOT: this[i] &amp;= ~other[i], returns total popcount.</summary>
    private static int _SimdAndNotInPlace(ulong[] data, ulong[] other)
    {
        int cardinality = 0;
        Span<ulong> spanData = data.AsSpan();
        ReadOnlySpan<ulong> spanOther = other.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            Span<Vector256<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanData);
            ReadOnlySpan<Vector256<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector256<ulong> result = Vector256.AndNot(vecData[vi], vecOther[vi]);
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecData.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Span<Vector128<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanData);
            ReadOnlySpan<Vector128<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector128<ulong> result = Vector128.AndNot(vecData[vi], vecOther[vi]);
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecData.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanData[i] & ~spanOther[i];
            spanData[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>In-place XOR: this[i] ^= other[i], returns total popcount.</summary>
    private static int _SimdXorInPlace(ulong[] data, ulong[] other)
    {
        int cardinality = 0;
        Span<ulong> spanData = data.AsSpan();
        ReadOnlySpan<ulong> spanOther = other.AsSpan();
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            Span<Vector256<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanData);
            ReadOnlySpan<Vector256<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector256<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector256<ulong> result = vecData[vi] ^ vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
                cardinality += BitOperations.PopCount(result.GetElement(2));
                cardinality += BitOperations.PopCount(result.GetElement(3));
            }
            i = vecData.Length * 4;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Span<Vector128<ulong>> vecData = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanData);
            ReadOnlySpan<Vector128<ulong>> vecOther = MemoryMarshal.Cast<ulong, Vector128<ulong>>(spanOther);

            for (int vi = 0; vi < vecData.Length; vi++)
            {
                Vector128<ulong> result = vecData[vi] ^ vecOther[vi];
                vecData[vi] = result;
                cardinality += BitOperations.PopCount(result.GetElement(0));
                cardinality += BitOperations.PopCount(result.GetElement(1));
            }
            i = vecData.Length * 2;
        }

        for (; i < BitmapSize; i++)
        {
            ulong val = spanData[i] ^ spanOther[i];
            spanData[i] = val;
            cardinality += BitOperations.PopCount(val);
        }
        return cardinality;
    }

    /// <summary>
    /// Converts a bitmap container to an array container by extracting all set bits in ascending order.
    /// <para>
    /// <b>Precondition:</b> <see cref="Cardinality"/> equals the number of set bits (the array is allocated with exactly that
    /// size) and is at most <see cref="ArrayContainer.MaxCapacity"/> in the normal use; callers invoke it right after a
    /// bulk operation that recomputed the cardinality. A cardinality smaller than the real bit count would overrun the array.
    /// </para>
    /// </summary>
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
