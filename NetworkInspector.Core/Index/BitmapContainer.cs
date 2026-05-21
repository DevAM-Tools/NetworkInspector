// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Bitmap container using 1024 ulongs (8KB) for O(1) insert/contains.
/// Uses hardware POPCNT via <see cref="BitOperations.PopCount(ulong)"/> and
/// SIMD (Vector256/Vector128) for bulk AND/OR/XOR/ANDNOT operations.
/// </summary>
internal sealed class BitmapContainer : IContainer
{
    internal const int BitmapSize = 1024; // 1024 * 64 = 65536 bits

    internal readonly ulong[] Bitmap = new ulong[BitmapSize];
    private int _Cardinality;

    public int Cardinality => _Cardinality;

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

    public IContainer Clone()
    {
        BitmapContainer copy = new();
        Array.Copy(Bitmap, copy.Bitmap, BitmapSize);
        copy._Cardinality = _Cardinality;
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ushort value)
    {
        int word = value >> 6;
        ulong bit = 1UL << (value & 63);
        return (Bitmap[word] & bit) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IContainer Add(ushort value)
    {
        int word = value >> 6;
        ulong bit = 1UL << (value & 63);
        if ((Bitmap[word] & bit) == 0)
        {
            Bitmap[word] |= bit;
            _Cardinality++;
        }
        return this;
    }

    public IContainer And(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result._Cardinality = SimdAnd(
                Bitmap, bmp.Bitmap, result.Bitmap);

            // Downgrade to array if sparse
            if (result._Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(result);
            }
            return result;
        }

        return other.And(this);
    }

    public IContainer Or(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result._Cardinality = SimdOr(
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
            cloneRun._Cardinality = PopCountAll(cloneRun.Bitmap);
            return cloneRun;
        }

        // Clone this, add other's elements
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone._Cardinality = _Cardinality;

        // Add all values from the other container into this bitmap
        if (other is ArrayContainer arr)
        {
            for (int i = 0; i < arr.Cardinality; i++)
            {
                clone.Add(arr.ValueAt(i));
            }
        }

        return clone;
    }

    /// <summary>Performs AND-NOT and writes result into <paramref name="other"/>. Counts popcount.</summary>
    public IContainer AndNot(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result._Cardinality = SimdAndNot(
                Bitmap, bmp.Bitmap, result.Bitmap);

            if (result._Cardinality <= ArrayContainer.MaxCapacity)
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
            cloneRun._Cardinality = PopCountAll(cloneRun.Bitmap);
            if (cloneRun._Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(cloneRun);
            }
            return cloneRun;
        }

        // Fallback: clone this and remove other's elements
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone._Cardinality = _Cardinality;
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
                    clone._Cardinality--;
                }
            }
        }

        if (clone._Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(clone);
        }
        return clone;
    }

    /// <summary>XOR with another container.</summary>
    public IContainer Xor(IContainer other)
    {
        if (other is BitmapContainer bmp)
        {
            BitmapContainer result = new();
            result._Cardinality = SimdXor(
                Bitmap, bmp.Bitmap, result.Bitmap);

            if (result._Cardinality <= ArrayContainer.MaxCapacity)
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
            cloneRun._Cardinality = PopCountAll(cloneRun.Bitmap);
            if (cloneRun._Cardinality <= ArrayContainer.MaxCapacity)
            {
                return BitmapToArray(cloneRun);
            }
            return cloneRun;
        }

        // Fallback: clone this and toggle other's elements
        BitmapContainer clone = new();
        Array.Copy(Bitmap, clone.Bitmap, BitmapSize);
        clone._Cardinality = _Cardinality;
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
                    clone._Cardinality--;
                }
                else
                {
                    clone.Bitmap[w] |= bit;
                    clone._Cardinality++;
                }
            }
        }

        if (clone._Cardinality <= ArrayContainer.MaxCapacity)
        {
            return BitmapToArray(clone);
        }
        return clone;
    }

    #region In-place mutation methods

    /// <summary>In-place AND. Returns recomputed cardinality.</summary>
    internal void AndWith(BitmapContainer other) => _Cardinality = SimdAndInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place OR. Returns recomputed cardinality.</summary>
    internal void OrWith(BitmapContainer other) => _Cardinality = SimdOrInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place ANDNOT. Returns recomputed cardinality.</summary>
    internal void AndNotWith(BitmapContainer other) => _Cardinality = SimdAndNotInPlace(Bitmap, other.Bitmap);

    /// <summary>In-place XOR. Returns recomputed cardinality.</summary>
    internal void XorWith(BitmapContainer other) => _Cardinality = SimdXorInPlace(Bitmap, other.Bitmap);

    #endregion

    #region SIMD bulk operations

    /// <summary>
    /// Total population count of all bits set in <paramref name="bitmap"/>. Used by the
    /// Bitmap×Run direct paths to recompute cardinality after applying range masks.
    /// </summary>
    private static int PopCountAll(ulong[] bitmap)
    {
        int total = 0;
        for (int i = 0; i < bitmap.Length; i++)
        {
            total += BitOperations.PopCount(bitmap[i]);
        }
        return total;
    }

    /// <summary>
    /// SIMD AND: dst[i] = a[i] &amp; b[i], returns total popcount.
    /// Uses Vector256 (processes 4 ulongs = 256 bits per iteration).
    /// Falls back to scalar loop on platforms without hardware vectors.
    /// </summary>
    private static int SimdAnd(ulong[] a, ulong[] b, ulong[] dst)
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
    private static int SimdOr(ulong[] a, ulong[] b, ulong[] dst)
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
    private static int SimdAndNot(ulong[] a, ulong[] b, ulong[] dst)
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
    private static int SimdXor(ulong[] a, ulong[] b, ulong[] dst)
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
    private static int SimdAndInPlace(ulong[] data, ulong[] other)
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
    private static int SimdOrInPlace(ulong[] data, ulong[] other)
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
    private static int SimdAndNotInPlace(ulong[] data, ulong[] other)
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
    private static int SimdXorInPlace(ulong[] data, ulong[] other)
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

    /// <summary>Converts a bitmap container to an array container by extracting all set bits.</summary>
    internal static ArrayContainer BitmapToArray(BitmapContainer bmp)
    {
        ushort[] values = new ushort[bmp._Cardinality];
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
