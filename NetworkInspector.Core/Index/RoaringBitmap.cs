// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Roaring bitmap for 32-bit values.
/// Partitions values into 16-bit high chunks, each containing a typed container for the low 16 bits.
/// Supports AND, OR, XOR, ANDNOT and in-place variants for zero-allocation query paths.
/// <para>
/// <b>Aliasing model for set operations:</b> <see cref="Or"/>, <see cref="AndNot"/> and
/// <see cref="Xor"/> may share <see cref="IContainer"/> references with the operands for
/// chunks that exist in only one of the two operands (single-side branches). <see cref="And"/>
/// always allocates fresh containers via <see cref="IContainer.And(IContainer)"/>, so its
/// result is fully detached. The shared-reference policy keeps the common case zero-allocation;
/// the result is safe to read concurrently with the operands. However, mutating either the
/// result or any operand (via <see cref="Add"/>, <c>Remove</c>, or any <c>*With</c>
/// in-place variant) after such a set operation may corrupt the other side because containers
/// such as <see cref="ArrayContainer"/> mutate in place. Call <see cref="Clone"/> first when
/// an independent, fully detached copy is required.
/// </para>
/// <para>
/// <b>Thread-safety:</b> instances are not thread-safe for concurrent mutation. After all
/// mutations have happened-before the publication, multiple threads may read concurrently
/// (cardinality, contains, set operations producing fresh results).
/// </para>
/// </summary>
public sealed class RoaringBitmap
{
    // Sorted parallel arrays: keys (high 16 bits) + containers (low 16 bits)
    private ushort[] _Keys;
    private IContainer[] _Containers;
    private int _Count;

    /// <summary>Creates an empty Roaring bitmap.</summary>
    public RoaringBitmap()
    {
        _Keys = new ushort[4];
        _Containers = new IContainer[4];
        _Count = 0;
    }

    /// <summary>Total number of values stored.</summary>
    public long Cardinality
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _Count; i++)
            {
                total += _Containers[i].Cardinality;
            }
            return total;
        }
    }

    /// <summary>Whether the bitmap is empty.</summary>
    public bool IsEmpty => _Count == 0;

    /// <summary>Adds a 32-bit value to the bitmap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);

        int idx = FindChunk(high);
        if (idx >= 0)
        {
            _Containers[idx] = _Containers[idx].Add(low);
        }
        else
        {
            InsertChunk(~idx, high, new ArrayContainer().Add(low));
        }
    }

    /// <summary>Checks if a 32-bit value is present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);

        int idx = FindChunk(high);
        return idx >= 0 && _Containers[idx].Contains(low);
    }

    /// <summary>
    /// Minimum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Min
    {
        get
        {
            if (_Count == 0)
            {
                ThrowEmpty();
            }
            return ((uint)_Keys[0] << 16) | _Containers[0].Min;
        }
    }

    /// <summary>
    /// Maximum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Max
    {
        get
        {
            if (_Count == 0)
            {
                ThrowEmpty();
            }
            return ((uint)_Keys[_Count - 1] << 16) | _Containers[_Count - 1].Max;
        }
    }

    /// <summary>
    /// Tries to get the minimum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMin(out uint value)
    {
        if (_Count == 0)
        {
            value = 0;
            return false;
        }
        value = ((uint)_Keys[0] << 16) | _Containers[0].Min;
        return true;
    }

    /// <summary>
    /// Tries to get the maximum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMax(out uint value)
    {
        if (_Count == 0)
        {
            value = 0;
            return false;
        }
        value = ((uint)_Keys[_Count - 1] << 16) | _Containers[_Count - 1].Max;
        return true;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEmpty() =>
        throw new InvalidOperationException("Bitmap is empty.");

    /// <summary>
    /// Creates a deep copy of this bitmap. All containers are copied independently.
    /// Mutations to either the original or the copy do not affect the other.
    /// This is O(cardinality).
    /// </summary>
    public RoaringBitmap Clone()
    {
        RoaringBitmap copy = new();
        if (_Count > 0)
        {
            copy._Keys = new ushort[_Count];
            copy._Containers = new IContainer[_Count];
            Array.Copy(_Keys, copy._Keys, _Count);
            for (int i = 0; i < _Count; i++)
            {
                copy._Containers[i] = _Containers[i].Clone();
            }
            copy._Count = _Count;
        }
        return copy;
    }

    #region Immutable set operations

    /// <summary>AND (intersection) with another bitmap.</summary>
    public RoaringBitmap And(RoaringBitmap other)
    {
        RoaringBitmap result = new();
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                j++;
            }
            else
            {
                IContainer intersected = _Containers[i].And(other._Containers[j]);
                if (intersected.Cardinality > 0)
                {
                    result.InsertChunk(result._Count, _Keys[i], intersected);
                }
                i++;
                j++;
            }
        }
        return result;
    }

    /// <summary>OR (union) with another bitmap.</summary>
    public RoaringBitmap Or(RoaringBitmap other)
    {
        RoaringBitmap result = new();
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                result.InsertChunk(result._Count, other._Keys[j], other._Containers[j]);
                j++;
            }
            else
            {
                result.InsertChunk(result._Count, _Keys[i], _Containers[i].Or(other._Containers[j]));
                i++;
                j++;
            }
        }
        while (i < _Count)
        {
            result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
            i++;
        }
        while (j < other._Count)
        {
            result.InsertChunk(result._Count, other._Keys[j], other._Containers[j]);
            j++;
        }
        return result;
    }

    /// <summary>ANDNOT (difference): this AND NOT other.</summary>
    public RoaringBitmap AndNot(RoaringBitmap other)
    {
        RoaringBitmap result = new();
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                // Chunk only in this — keep it
                result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                j++;
            }
            else
            {
                IContainer diff = _Containers[i].AndNot(other._Containers[j]);
                if (diff.Cardinality > 0)
                {
                    result.InsertChunk(result._Count, _Keys[i], diff);
                }
                i++;
                j++;
            }
        }
        // Remaining chunks in this have no match in other — keep them
        while (i < _Count)
        {
            result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
            i++;
        }
        return result;
    }

    /// <summary>XOR (symmetric difference) with another bitmap.</summary>
    public RoaringBitmap Xor(RoaringBitmap other)
    {
        RoaringBitmap result = new();
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                result.InsertChunk(result._Count, other._Keys[j], other._Containers[j]);
                j++;
            }
            else
            {
                IContainer xored = _Containers[i].Xor(other._Containers[j]);
                if (xored.Cardinality > 0)
                {
                    result.InsertChunk(result._Count, _Keys[i], xored);
                }
                i++;
                j++;
            }
        }
        while (i < _Count)
        {
            result.InsertChunk(result._Count, _Keys[i], _Containers[i]);
            i++;
        }
        while (j < other._Count)
        {
            result.InsertChunk(result._Count, other._Keys[j], other._Containers[j]);
            j++;
        }
        return result;
    }

    #endregion

    #region In-place set operations

    /// <summary>
    /// In-place AND: removes all values not present in <paramref name="other"/>.
    /// Avoids allocating a new RoaringBitmap. Containers that become empty are removed.
    /// Uses SIMD in-place path for BitmapContainer×BitmapContainer.
    /// </summary>
    public void AndWith(RoaringBitmap other)
    {
        int writePos = 0;
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                j++;
            }
            else
            {
                IContainer intersected;
                if (_Containers[i] is BitmapContainer thisBmp
                    && other._Containers[j] is BitmapContainer otherBmp)
                {
                    // SIMD in-place AND on the 8KB bitmap
                    thisBmp.AndWith(otherBmp);
                    intersected = thisBmp.Cardinality <= ArrayContainer.MaxCapacity
                        ? BitmapContainer.BitmapToArray(thisBmp)
                        : thisBmp;
                }
                else
                {
                    intersected = _Containers[i].And(other._Containers[j]);
                }

                if (intersected.Cardinality > 0)
                {
                    _Keys[writePos] = _Keys[i];
                    _Containers[writePos] = intersected;
                    writePos++;
                }
                i++;
                j++;
            }
        }
        // Clear removed slots to allow GC of old containers
        for (int c = writePos; c < _Count; c++)
        {
            _Containers[c] = null!;
        }
        _Count = writePos;
    }

    /// <summary>
    /// In-place OR: adds all values from <paramref name="other"/>.
    /// </summary>
    public void OrWith(RoaringBitmap other)
    {
        if (other._Count == 0)
        {
            return;
        }

        // OR may add new chunks — use immutable OR then swap
        RoaringBitmap merged = Or(other);
        _Keys = merged._Keys;
        _Containers = merged._Containers;
        _Count = merged._Count;
    }

    /// <summary>
    /// In-place ANDNOT: removes all values present in <paramref name="other"/>.
    /// Uses SIMD in-place path for BitmapContainer×BitmapContainer.
    /// </summary>
    public void AndNotWith(RoaringBitmap other)
    {
        int writePos = 0;
        int i = 0, j = 0;
        while (i < _Count && j < other._Count)
        {
            if (_Keys[i] < other._Keys[j])
            {
                _Keys[writePos] = _Keys[i];
                _Containers[writePos] = _Containers[i];
                writePos++;
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                j++;
            }
            else
            {
                IContainer diff;
                if (_Containers[i] is BitmapContainer thisBmp
                    && other._Containers[j] is BitmapContainer otherBmp)
                {
                    thisBmp.AndNotWith(otherBmp);
                    diff = thisBmp.Cardinality <= ArrayContainer.MaxCapacity
                        ? BitmapContainer.BitmapToArray(thisBmp)
                        : thisBmp;
                }
                else
                {
                    diff = _Containers[i].AndNot(other._Containers[j]);
                }

                if (diff.Cardinality > 0)
                {
                    _Keys[writePos] = _Keys[i];
                    _Containers[writePos] = diff;
                    writePos++;
                }
                i++;
                j++;
            }
        }
        while (i < _Count)
        {
            _Keys[writePos] = _Keys[i];
            _Containers[writePos] = _Containers[i];
            writePos++;
            i++;
        }
        for (int c = writePos; c < _Count; c++)
        {
            _Containers[c] = null!;
        }
        _Count = writePos;
    }

    /// <summary>In-place XOR: toggles all values from <paramref name="other"/>.</summary>
    public void XorWith(RoaringBitmap other)
    {
        RoaringBitmap xored = Xor(other);
        _Keys = xored._Keys;
        _Containers = xored._Containers;
        _Count = xored._Count;
    }

    #endregion

    #region Rank / Select

    /// <summary>
    /// Returns the number of values in the bitmap that are ≤ <paramref name="value"/>.
    /// </summary>
    public long Rank(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);
        long rank = 0;

        for (int i = 0; i < _Count; i++)
        {
            if (_Keys[i] < high)
            {
                rank += _Containers[i].Cardinality;
            }
            else if (_Keys[i] == high)
            {
                rank += ContainerRank(_Containers[i], low);
                break;
            }
            else
            {
                break;
            }
        }
        return rank;
    }

    /// <summary>
    /// Returns the 0-based <paramref name="position"/>-th smallest value,
    /// or null if fewer than (position + 1) values exist.
    /// </summary>
    public uint? Select(long position)
    {
        if (position < 0)
        {
            return null;
        }

        long remaining = position;
        for (int i = 0; i < _Count; i++)
        {
            int card = _Containers[i].Cardinality;
            if (remaining < card)
            {
                ushort low = ContainerSelect(_Containers[i], (int)remaining);
                return ((uint)_Keys[i] << 16) | low;
            }
            remaining -= card;
        }
        return null;
    }

    #endregion

    /// <summary>
    /// Returns a read-only view over this bitmap.
    /// All queries are delegated to this bitmap — mutations made to this bitmap
    /// after calling <see cref="AsReadOnly"/> are visible through the returned view.
    /// To create an isolated snapshot, call <see cref="Clone"/> first.
    /// </summary>
    public ReadOnlyRoaringBitmap AsReadOnly() => new(this);

    #region Internal helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindChunk(ushort key) => Array.BinarySearch(_Keys, 0, _Count, key);

    private void InsertChunk(int idx, ushort key, IContainer container)
    {
        if (_Count == _Keys.Length)
        {
            int newCap = _Keys.Length * 2;
            Array.Resize(ref _Keys, newCap);
            Array.Resize(ref _Containers, newCap);
        }
        if (idx < _Count)
        {
            Array.Copy(_Keys, idx, _Keys, idx + 1, _Count - idx);
            Array.Copy(_Containers, idx, _Containers, idx + 1, _Count - idx);
        }
        _Keys[idx] = key;
        _Containers[idx] = container;
        _Count++;
    }

    /// <summary>Counts values ≤ threshold in a container.</summary>
    private static int ContainerRank(IContainer container, ushort threshold)
    {
        if (container is ArrayContainer arr)
        {
            // Values are sorted — find insertion point
            int count = 0;
            for (int i = 0; i < arr.Cardinality; i++)
            {
                if (arr.ValueAt(i) <= threshold)
                {
                    count++;
                }
                else
                {
                    break;
                }
            }
            return count;
        }

        if (container is BitmapContainer bmp)
        {
            int count = 0;
            int fullWords = threshold >> 6;
            for (int i = 0; i < fullWords && i < BitmapContainer.BitmapSize; i++)
            {
                count += BitOperations.PopCount(bmp.Bitmap[i]);
            }
            // Partial word: mask bits ≤ threshold
            if (fullWords < BitmapContainer.BitmapSize)
            {
                int bitPos = threshold & 63;
                ulong mask = bitPos == 63 ? ulong.MaxValue : (1UL << (bitPos + 1)) - 1;
                count += BitOperations.PopCount(bmp.Bitmap[fullWords] & mask);
            }
            return count;
        }

        // Fallback: linear scan
        int cnt = 0;
        for (ushort v = 0; v <= threshold; v++)
        {
            if (container.Contains(v))
            {
                cnt++;
            }
            if (v == ushort.MaxValue)
            {
                break;
            }
        }
        return cnt;
    }

    /// <summary>Selects the n-th value from a container.</summary>
    private static ushort ContainerSelect(IContainer container, int n)
    {
        if (container is ArrayContainer arr)
        {
            return arr.ValueAt(n);
        }

        if (container is BitmapContainer bmp)
        {
            int remaining = n;
            for (int i = 0; i < BitmapContainer.BitmapSize; i++)
            {
                ulong word = bmp.Bitmap[i];
                int popcount = BitOperations.PopCount(word);
                if (remaining < popcount)
                {
                    // The n-th bit is within this word — clear lowest bits until we reach it
                    while (remaining > 0)
                    {
                        word &= word - 1;
                        remaining--;
                    }
                    return (ushort)(i * 64 + BitOperations.TrailingZeroCount(word));
                }
                remaining -= popcount;
            }
        }

        // Fallback
        int count = 0;
        for (int v = 0; v <= ushort.MaxValue; v++)
        {
            if (container.Contains((ushort)v))
            {
                if (count == n)
                {
                    return (ushort)v;
                }
                count++;
            }
        }
        return 0;
    }
    #endregion
}
