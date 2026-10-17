// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Roaring bitmap for 32-bit values.
/// Partitions values into 16-bit high chunks, each containing a typed container for the low 16 bits.
/// Supports AND, OR, XOR, ANDNOT and in-place variants for zero-allocation query paths.
/// <para>
/// <b>Aliasing model for set operations:</b> Immutable <see cref="Or"/>, <see cref="AndNot"/>,
/// and <see cref="Xor"/> produce detached results — single-side chunks are cloned before insert.
/// <see cref="And"/> always allocates fresh containers via <see cref="IContainer.And(IContainer)"/>.
/// In-place <see cref="AndWith"/> and <see cref="AndNotWith"/> clone <see cref="BitmapContainer"/>
/// operands before SIMD mutation; <see cref="OrWith(RoaringBitmap)"/> and <see cref="XorWith"/> clone chunks
/// adopted from the other operand only.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. Concurrent <see cref="Add"/> from more
/// than one thread is not supported. Concurrent readers may call <see cref="Contains"/>,
/// <see cref="Clone"/>, cardinality and rank/select while a single writer is appending.
/// A seqlock retries the reader when it overlaps an in-flight mutation, so a held
/// <see cref="ReadOnlyRoaringBitmap"/> view of an index bitmap stays valid as the index grows.
/// The writer moves the sequence with <see cref="Interlocked.Increment(ref int)"/> (a full fence, so its data
/// writes cannot become visible before the odd value), and every reader issues
/// <see cref="Interlocked.MemoryBarrier"/> before it validates (so its data loads complete before the second
/// sequence load). Both edges are required on weakly ordered CPUs such as ARM64. A read section that throws while
/// a writer changes the arrays under it (a half-grown array read with the new count) is a torn read and retries; an
/// exception in a section whose sequence did not move propagates.
/// Set operations that allocate a new bitmap (<see cref="And"/>, <see cref="Or"/>, …) copy
/// containers and are intended for a private result or a bitmap that is no longer growing.
/// </para>
/// </summary>
public sealed class RoaringBitmap
{
    #region Fields

    // Sorted parallel arrays: keys (high 16 bits) + containers (low 16 bits)
    private ushort[] _Keys;
    private IContainer[] _Containers;
    private int _Count;
    private long _Cardinality;

    // Even = stable; odd = writer in Add / in-place set op. Readers retry while odd or changed.
    // Written only with Interlocked.Increment, read with Volatile.Read (see _BeginWrite / _ReadStillValid).
    private int _SeqLock;

    #endregion

    #region Lifecycle

    /// <summary>Creates an empty Roaring bitmap.</summary>
    public RoaringBitmap()
    {
        _Keys = new ushort[4];
        _Containers = new IContainer[4];
        _Count = 0;
    }

    #endregion

    #region Public API

    /// <summary>Total number of values stored.</summary>
    public long Cardinality
    {
        get
        {
            while (true)
            {
                int seq = _ReadSeqLock();
                if (_IsWriteInProgress(seq))
                {
                    Thread.SpinWait(1);
                    continue;
                }

                long cardinality = _Cardinality;
                if (_ReadStillValid(seq))
                {
                    return cardinality;
                }
            }
        }
    }

    /// <summary>Whether the bitmap is empty.</summary>
    public bool IsEmpty => Cardinality == 0L;

    /// <summary>Adds a 32-bit value to the bitmap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint value)
    {
        _BeginWrite();
        try
        {
            _AddUnsynchronized(value);
        }
        finally
        {
            _EndWrite();
        }
    }

    /// <summary>
    /// Converts every array or bitmap container whose run-length form is smaller to a run container (Roaring
    /// serialized sizes: runs <c>2 + 4·runs</c> bytes, arrays <c>2 + 2·cardinality</c>, bitmaps 8192). The values do
    /// not change. Call after bulk adds, not per add; later adds to a run container keep it a run container.
    /// </summary>
    /// <returns><see langword="true"/> when at least one container was converted.</returns>
    /// <remarks>Single writer, like <see cref="Add"/>; concurrent readers retry through the seqlock.</remarks>
    public bool RunOptimize() => RunOptimizeKeys(0, ushort.MaxValue + 1);

    /// <summary>
    /// <see cref="RunOptimize"/> limited to the chunks whose high 16-bit key lies in
    /// [<paramref name="firstKey"/>, <paramref name="endKey"/>). Used by <see cref="PacketIndex"/> for chunks that
    /// are complete.
    /// </summary>
    /// <param name="firstKey">First high key, inclusive (0 … 65535).</param>
    /// <param name="endKey">Last high key, exclusive (≤ 65536).</param>
    /// <returns><see langword="true"/> when at least one container was converted.</returns>
    internal bool RunOptimizeKeys(int firstKey, int endKey)
    {
        // Containers are replaced, never mutated: a clone or a set-operation result that shares the old container
        // keeps it unchanged.
        int index = _FindChunk((ushort)Math.Clamp(firstKey, 0, ushort.MaxValue));
        if (index < 0)
        {
            index = ~index;
        }

        if (index >= _Count || _Keys[index] < firstKey || _Keys[index] >= endKey)
        {
            return false;
        }

        bool converted = false;
        _BeginWrite();
        try
        {
            for (; index < _Count && _Keys[index] < endKey; index++)
            {
                IContainer current = _Containers[index];
                if (current is RunContainer
                    || RunContainer.SerializedSize(RunContainer.CountRuns(current)) >= RunContainer.SerializedSizeOf(current))
                {
                    continue;
                }

                _Containers[index] = RunContainer.FromContainer(current);
                converted = true;
            }
        }
        finally
        {
            _EndWrite();
        }

        return converted;
    }

    /// <summary>Checks if a 32-bit value is present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);
        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            bool present;
            try
            {
                int idx = _FindChunk(high);
                present = idx >= 0 && _Containers[idx].Contains(low);
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                return present;
            }
        }
    }

    /// <summary>
    /// Minimum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Min
    {
        get
        {
            while (true)
            {
                int seq = _ReadSeqLock();
                if (_IsWriteInProgress(seq))
                {
                    Thread.SpinWait(1);
                    continue;
                }

                if (_Count == 0)
                {
                    if (_ReadStillValid(seq))
                    {
                        _ThrowEmpty();
                    }

                    continue;
                }

                uint min;
                try
                {
                    min = ((uint)_Keys[0] << 16) | _Containers[0].Min;
                }
                catch (Exception) when (!_ReadStillValid(seq))
                {
                    // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                    continue;
                }

                if (_ReadStillValid(seq))
                {
                    return min;
                }
            }
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
            while (true)
            {
                int seq = _ReadSeqLock();
                if (_IsWriteInProgress(seq))
                {
                    Thread.SpinWait(1);
                    continue;
                }

                if (_Count == 0)
                {
                    if (_ReadStillValid(seq))
                    {
                        _ThrowEmpty();
                    }

                    continue;
                }

                uint max;
                try
                {
                    max = ((uint)_Keys[_Count - 1] << 16) | _Containers[_Count - 1].Max;
                }
                catch (Exception) when (!_ReadStillValid(seq))
                {
                    // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                    continue;
                }

                if (_ReadStillValid(seq))
                {
                    return max;
                }
            }
        }
    }

    /// <summary>
    /// Tries to get the minimum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMin(out uint value)
    {
        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            if (_Count == 0)
            {
                if (_ReadStillValid(seq))
                {
                    value = 0;
                    return false;
                }

                continue;
            }

            uint min;
            try
            {
                min = ((uint)_Keys[0] << 16) | _Containers[0].Min;
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                value = min;
                return true;
            }
        }
    }

    /// <summary>
    /// Tries to get the maximum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMax(out uint value)
    {
        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            if (_Count == 0)
            {
                if (_ReadStillValid(seq))
                {
                    value = 0;
                    return false;
                }

                continue;
            }

            uint max;
            try
            {
                max = ((uint)_Keys[_Count - 1] << 16) | _Containers[_Count - 1].Max;
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                value = max;
                return true;
            }
        }
    }

    /// <summary>
    /// Creates a deep copy of this bitmap. All containers are copied independently.
    /// Mutations to either the original or the copy do not affect the other.
    /// This is O(cardinality).
    /// </summary>
    public RoaringBitmap Clone()
    {
        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            RoaringBitmap copy = new();
            try
            {
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
                    copy._Cardinality = _Cardinality;
                }
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                return copy;
            }
        }
    }

    /// <summary>
    /// Returns a read-only view over this bitmap (zero-allocation; wraps this instance).
    /// All queries are delegated to this bitmap — mutations made to this bitmap
    /// after calling <see cref="AsReadOnly"/> are visible through the returned view.
    /// To create an isolated snapshot, call <see cref="Clone"/> first.
    /// </summary>
    public ReadOnlyRoaringBitmap AsReadOnly() => new(this);

    #endregion

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
                    result._InsertChunk(result._Count, _Keys[i], intersected);
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
                result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                result._InsertChunk(result._Count, other._Keys[j], other._Containers[j].Clone());
                j++;
            }
            else
            {
                result._InsertChunk(result._Count, _Keys[i], _Containers[i].Or(other._Containers[j]));
                i++;
                j++;
            }
        }
        while (i < _Count)
        {
            result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
            i++;
        }
        while (j < other._Count)
        {
            result._InsertChunk(result._Count, other._Keys[j], other._Containers[j].Clone());
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
                result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
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
                    result._InsertChunk(result._Count, _Keys[i], diff);
                }
                i++;
                j++;
            }
        }
        // Remaining chunks in this have no match in other — keep them
        while (i < _Count)
        {
            result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
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
                result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
                i++;
            }
            else if (_Keys[i] > other._Keys[j])
            {
                result._InsertChunk(result._Count, other._Keys[j], other._Containers[j].Clone());
                j++;
            }
            else
            {
                IContainer xored = _Containers[i].Xor(other._Containers[j]);
                if (xored.Cardinality > 0)
                {
                    result._InsertChunk(result._Count, _Keys[i], xored);
                }
                i++;
                j++;
            }
        }
        while (i < _Count)
        {
            result._InsertChunk(result._Count, _Keys[i], _Containers[i].Clone());
            i++;
        }
        while (j < other._Count)
        {
            result._InsertChunk(result._Count, other._Keys[j], other._Containers[j].Clone());
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
        _BeginWrite();
        try
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
                        // Clone before in-place mutation to avoid corrupting aliased operands from Or/AndNot/Xor.
                        BitmapContainer mutableBmp = (BitmapContainer)thisBmp.Clone();
                        mutableBmp.AndWith(otherBmp);
                        intersected = mutableBmp.Cardinality <= ArrayContainer.MaxCapacity
                            ? BitmapContainer.BitmapToArray(mutableBmp)
                            : mutableBmp;
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
            for (int c = writePos; c < _Count; c++)
            {
                _Containers[c] = null!;
            }
            _Count = writePos;
            _RecomputeCachedCardinality();
        }
        finally
        {
            _EndWrite();
        }
    }

    /// <summary>
    /// In-place OR: adds all values from <paramref name="other"/>.
    /// Clones chunks adopted from <paramref name="other"/>; this-side chunks are kept in place.
    /// </summary>
    public void OrWith(RoaringBitmap other)
    {
        _BeginWrite();
        try
        {
            if (other._Count == 0)
            {
                return;
            }

            if (_Count == 0)
            {
                _AdoptClonedChunks(other);
                return;
            }

        ushort[] keys = _Keys;
        IContainer[] containers = _Containers;
        int count = _Count;
        ushort[] newKeys = new ushort[Math.Max(keys.Length, count + other._Count)];
        IContainer[] newContainers = new IContainer[newKeys.Length];
        int newCount = 0;
        int i = 0;
        int j = 0;

        while (i < count && j < other._Count)
        {
            if (keys[i] < other._Keys[j])
            {
                newKeys[newCount] = keys[i];
                newContainers[newCount] = containers[i];
                newCount++;
                i++;
            }
            else if (keys[i] > other._Keys[j])
            {
                newKeys[newCount] = other._Keys[j];
                newContainers[newCount] = other._Containers[j].Clone();
                newCount++;
                j++;
            }
            else
            {
                newKeys[newCount] = keys[i];
                newContainers[newCount] = containers[i].Or(other._Containers[j]);
                newCount++;
                i++;
                j++;
            }
        }

        while (i < count)
        {
            newKeys[newCount] = keys[i];
            newContainers[newCount] = containers[i];
            newCount++;
            i++;
        }

        while (j < other._Count)
        {
            newKeys[newCount] = other._Keys[j];
            newContainers[newCount] = other._Containers[j].Clone();
            newCount++;
            j++;
        }

        for (int c = newCount; c < count; c++)
        {
            containers[c] = null!;
        }

        _Keys = newKeys;
        _Containers = newContainers;
        _Count = newCount;
        _RecomputeCachedCardinality();
        }
        finally
        {
            _EndWrite();
        }
    }

    /// <summary>
    /// In-place OR with a read-only view: adds all values from <paramref name="other"/> without first cloning the
    /// viewed bitmap (<see cref="ReadOnlyRoaringBitmap.ToBitmap"/> would copy every chunk only for
    /// <see cref="OrWith(RoaringBitmap)"/> to clone the adopted chunks again). An empty view is a no-op.
    /// </summary>
    /// <remarks>
    /// Same precondition as <see cref="OrWith(RoaringBitmap)"/>: the bitmap behind <paramref name="other"/> must
    /// not be appended to while this runs (for example a <see cref="PacketIndex"/> that is no longer growing).
    /// </remarks>
    public void OrWith(ReadOnlyRoaringBitmap other)
    {
        if (other.IsEmpty)
        {
            return;
        }

        OrWith(other.Inner);
    }

    /// <summary>
    /// In-place ANDNOT: removes all values present in <paramref name="other"/>.
    /// Uses SIMD in-place path for BitmapContainer×BitmapContainer.
    /// </summary>
    public void AndNotWith(RoaringBitmap other)
    {
        _BeginWrite();
        try
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
                    // Clone before in-place mutation to avoid corrupting aliased operands from Or/AndNot/Xor.
                    BitmapContainer mutableBmp = (BitmapContainer)thisBmp.Clone();
                    mutableBmp.AndNotWith(otherBmp);
                    diff = mutableBmp.Cardinality <= ArrayContainer.MaxCapacity
                        ? BitmapContainer.BitmapToArray(mutableBmp)
                        : mutableBmp;
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
        _RecomputeCachedCardinality();
        }
        finally
        {
            _EndWrite();
        }
    }

    /// <summary>
    /// In-place XOR: toggles all values from <paramref name="other"/>.
    /// Clones chunks adopted from <paramref name="other"/>; this-side chunks are kept in place.
    /// </summary>
    public void XorWith(RoaringBitmap other)
    {
        _BeginWrite();
        try
        {
        if (other._Count == 0)
        {
            return;
        }

        if (_Count == 0)
        {
            _AdoptClonedChunks(other);
            return;
        }

        ushort[] keys = _Keys;
        IContainer[] containers = _Containers;
        int count = _Count;
        ushort[] newKeys = new ushort[Math.Max(keys.Length, count + other._Count)];
        IContainer[] newContainers = new IContainer[newKeys.Length];
        int newCount = 0;
        int i = 0;
        int j = 0;

        while (i < count && j < other._Count)
        {
            if (keys[i] < other._Keys[j])
            {
                newKeys[newCount] = keys[i];
                newContainers[newCount] = containers[i];
                newCount++;
                i++;
            }
            else if (keys[i] > other._Keys[j])
            {
                newKeys[newCount] = other._Keys[j];
                newContainers[newCount] = other._Containers[j].Clone();
                newCount++;
                j++;
            }
            else
            {
                IContainer xored = containers[i].Xor(other._Containers[j]);
                if (xored.Cardinality > 0)
                {
                    newKeys[newCount] = keys[i];
                    newContainers[newCount] = xored;
                    newCount++;
                }
                i++;
                j++;
            }
        }

        while (i < count)
        {
            newKeys[newCount] = keys[i];
            newContainers[newCount] = containers[i];
            newCount++;
            i++;
        }

        while (j < other._Count)
        {
            newKeys[newCount] = other._Keys[j];
            newContainers[newCount] = other._Containers[j].Clone();
            newCount++;
            j++;
        }

        for (int c = newCount; c < count; c++)
        {
            containers[c] = null!;
        }

        _Keys = newKeys;
        _Containers = newContainers;
        _Count = newCount;
        _RecomputeCachedCardinality();
        }
        finally
        {
            _EndWrite();
        }
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
        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            long rank = 0;
            try
            {
                for (int i = 0; i < _Count; i++)
                {
                    if (_Keys[i] < high)
                    {
                        rank += _Containers[i].Cardinality;
                    }
                    else if (_Keys[i] == high)
                    {
                        rank += _ContainerRank(_Containers[i], low);
                        break;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                return rank;
            }
        }
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

        while (true)
        {
            int seq = _ReadSeqLock();
            if (_IsWriteInProgress(seq))
            {
                Thread.SpinWait(1);
                continue;
            }

            long remaining = position;
            uint? selected = null;
            bool found = false;
            try
            {
                for (int i = 0; i < _Count; i++)
                {
                    int card = _Containers[i].Cardinality;
                    if (remaining < card)
                    {
                        ushort low = _ContainerSelect(_Containers[i], (int)remaining);
                        selected = ((uint)_Keys[i] << 16) | low;
                        found = true;
                        break;
                    }
                    remaining -= card;
                }
            }
            catch (Exception) when (!_ReadStillValid(seq))
            {
                // Torn read: a writer grew or replaced an array between two loads of this section. Retry.
                continue;
            }

            if (_ReadStillValid(seq))
            {
                if (found)
                {
                    return selected;
                }

                return null;
            }
        }
    }

    #endregion

    #region Internal helpers

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowEmpty() =>
        throw new InvalidOperationException("Bitmap is empty.");

    private void _AdoptClonedChunks(RoaringBitmap other)
    {
        _Keys = new ushort[other._Count];
        _Containers = new IContainer[other._Count];
        for (int i = 0; i < other._Count; i++)
        {
            _Keys[i] = other._Keys[i];
            _Containers[i] = other._Containers[i].Clone();
        }
        _Count = other._Count;
        _Cardinality = other._Cardinality;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsWriteInProgress(int seq) => (seq & 1) != 0;

    /// <summary>Acquire load of the sequence; an odd value means a write is in progress.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ReadSeqLock() => Volatile.Read(ref _SeqLock);

    /// <summary>
    /// Validates a read section that started at <paramref name="seq"/>. The full fence keeps the section's data
    /// loads before the second sequence load; an acquire load alone orders only later loads, so on ARM64 a reader
    /// could otherwise validate against the old even value after reading half-updated data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _ReadStillValid(int seq)
    {
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _SeqLock) == seq;
    }

    /// <summary>
    /// Makes the sequence odd. <see cref="Interlocked.Increment(ref int)"/> is a full fence, so the data writes that
    /// follow cannot become visible before the odd value (a release store would order only the earlier writes).
    /// The sequence wraps after 2^31 writes; parity and equality stay correct under wrap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _BeginWrite() => _ = Interlocked.Increment(ref _SeqLock);

    /// <summary>Makes the sequence even again after the data writes; full fence on both sides.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EndWrite() => _ = Interlocked.Increment(ref _SeqLock);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AddUnsynchronized(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);

        int idx = _FindChunk(high);
        if (idx >= 0)
        {
            IContainer oldContainer = _Containers[idx];
            int oldCardinality = oldContainer.Cardinality;
            IContainer newContainer = oldContainer.Add(low);
            if (newContainer.Cardinality != oldCardinality)
            {
                _Containers[idx] = newContainer;
                _Cardinality += newContainer.Cardinality - oldCardinality;
            }
        }
        else
        {
            IContainer newContainer = new ArrayContainer().Add(low);
            _InsertChunk(~idx, high, newContainer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _FindChunk(ushort key) => Array.BinarySearch(_Keys, 0, _Count, key);

    private void _InsertChunk(int idx, ushort key, IContainer container)
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
        _Cardinality += container.Cardinality;
    }

    private void _RecomputeCachedCardinality()
    {
        long total = 0;
        for (int i = 0; i < _Count; i++)
        {
            total += _Containers[i].Cardinality;
        }
        _Cardinality = total;
    }

    /// <summary>Counts values ≤ threshold in a container.</summary>
    private static int _ContainerRank(IContainer container, ushort threshold)
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
    private static ushort _ContainerSelect(IContainer container, int n)
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
