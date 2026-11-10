// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Roaring bitmap for 32-bit values.
/// Partitions values into 16-bit high chunks, each containing a typed container for the low 16 bits.
/// Supports AND, OR, XOR, ANDNOT, and in-place variants that update the receiver instead of building a new bitmap
/// (the containers of changed chunks are still new objects, so an in-place operation is not allocation-free).
/// <para>
/// <b>Aliasing model for set operations:</b> Immutable <see cref="Or"/>, <see cref="AndNot"/>,
/// and <see cref="Xor"/> produce detached results — single-side chunks are cloned before insert.
/// <see cref="And"/> always allocates fresh containers via <see cref="IContainer.And(IContainer)"/>.
/// <see cref="AndWith"/> and <see cref="AndNotWith"/> replace the receiver's chunks by the containers of the container-level
/// operations (the operands' containers are never mutated); <see cref="OrWith(RoaringBitmap)"/> and <see cref="XorWith"/> clone chunks
/// adopted from the other operand only.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. Concurrent <see cref="Add"/> from more
/// than one thread is not supported. Concurrent readers may call <see cref="Contains"/>,
/// <see cref="Clone"/>, cardinality and rank/select while a single writer is appending.
/// A seqlock retries the reader when it overlaps an in-flight mutation, so a held
/// <see cref="ReadOnlyRoaringBitmap"/> view of an index bitmap stays valid as the index grows.
/// The single writer makes the sequence odd with <see cref="Interlocked.Increment(ref int)"/> (a full fence, so its
/// data writes cannot become visible before the odd value) and even again with a release store (so its data writes
/// cannot become visible after the even value). Every reader issues <see cref="Interlocked.MemoryBarrier"/> before it
/// validates (so its data loads complete before the second sequence load). The fences at both ends of the writer
/// and the reader fence are required on weakly ordered CPUs such as ARM64. A read section that throws while
/// a writer changes the arrays under it (a half-grown array read with the new count) is a torn read and retries; an
/// exception in a section whose sequence did not move propagates.
/// Set operations that allocate a new bitmap (<see cref="And"/>, <see cref="Or"/>, …) copy
/// containers and are intended for a private result or a bitmap that is no longer growing.
/// </para>
/// <para>
/// <b>Set operations and concurrent growth:</b> Every set operation, immutable or in place, reads its
/// <i>operand</i> bitmap (<c>other</c>) directly, without the seqlock. The operand must therefore not be appended to
/// while the operation runs; only the receiver of an in-place operation is protected for concurrent readers.
/// </para>
/// <para>
/// <b>Why a seqlock:</b> A packet index is appended by one writer (the parser thread) while other threads keep
/// reading it. With a seqlock the writer pays two fences per mutation and readers never write shared state, so readers
/// neither block the writer nor each other. The price is that a reader retries when it overlaps a mutation and spins
/// (<see cref="Thread.SpinWait(int)"/>, no timeout) while one is in flight, which is short for <see cref="Add"/> but
/// can be long for <see cref="RunOptimize"/> or an in-place set operation on a large bitmap. The writer always makes
/// the sequence even again in a <c>finally</c> block, so a mutation that throws cannot leave readers spinning forever
/// (the data it half-changed stays as left; callers must treat a throwing in-place operation as a bug).
/// </para>
/// </summary>
public sealed class RoaringBitmap
{
    #region Configuration and state

    /// <summary>
    /// The chunk keys (high 16 bits of the values), strictly ascending; only the first <see cref="_Count"/> slots are valid.
    /// Parallel to <see cref="_Containers"/>. The binary search in <see cref="_FindChunk"/> and the merges in the set
    /// operations rely on the order. The writer may replace the array (growth, merge) or shift entries while a reader looks
    /// at it; readers therefore only touch it inside a seqlock read section (see <see cref="_SeqLock"/>). A plain field, no
    /// <see langword="volatile"/>: the seqlock fences provide the ordering.
    /// </summary>
    private ushort[] _Keys;

    /// <summary>
    /// The containers (low 16 bits of the values) parallel to <see cref="_Keys"/>; only the first <see cref="_Count"/> slots
    /// are valid. Same access rules as <see cref="_Keys"/>.
    /// </summary>
    private IContainer[] _Containers;

    /// <summary>Number of valid slots in <see cref="_Keys"/> and <see cref="_Containers"/>. Same access rules as <see cref="_Keys"/>.</summary>
    private int _Count;

    /// <summary>
    /// Cached sum of all container cardinalities so <see cref="Cardinality"/> is O(1). Maintained incrementally by
    /// <see cref="_AddUnsynchronized"/> and <see cref="_InsertChunk"/>, and recomputed after the in-place set operations.
    /// </summary>
    private long _Cardinality;

    /// <summary>
    /// The sequence of the seqlock. Even = stable; odd = the writer is in <see cref="Add"/> or an in-place operation.
    /// Readers retry while it is odd or changed. Single writer: made odd with <see cref="Interlocked.Increment(ref int)"/>
    /// (full fence), made even with a release store; readers use <c>Volatile.Read</c> plus a full
    /// fence before validation (see <see cref="_BeginWrite"/>, <see cref="_EndWrite"/>, <see cref="_ReadStillValid"/>).
    /// </summary>
    private int _SeqLock;

    /// <summary>Creates an empty Roaring bitmap.</summary>
    public RoaringBitmap()
    {
        _Keys = new ushort[4];
        _Containers = new IContainer[4];
        _Count = 0;
    }

    #endregion

    #region Sequence lock

    /// <summary>An odd sequence value means a writer is between <see cref="_BeginWrite"/> and <see cref="_EndWrite"/>.</summary>
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

    /// <summary>
    /// Makes the sequence even again after the data writes. A release store is enough here: it keeps every earlier
    /// data write before the even value, and a reader validates with a full fence before its second sequence load
    /// (<see cref="_ReadStillValid"/>). The plain read of the current value is safe because this single writer is the
    /// only thread that changes the sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EndWrite() => Volatile.Write(ref _SeqLock, _SeqLock + 1);

    #endregion

    #region Cardinality

    /// <summary>
    /// Total number of values stored (O(1), cached). Safe for concurrent readers while a single writer appends; the
    /// seqlock also guards against a torn 64-bit read on 32-bit processes.
    /// </summary>
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

    #endregion

    #region Adding values

    /// <summary>
    /// Adds a 32-bit value to the bitmap. Single writer: concurrent <see cref="Add"/> calls from several threads are not
    /// supported. Adding a value that is already present changes nothing.
    /// </summary>
    /// <param name="value">The value to add.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint value)
    {
        // Every mutation sits between _BeginWrite and _EndWrite so readers can detect it. The finally block restores an
        // even sequence even if the insert throws (for example OutOfMemoryException), so readers never wait forever.
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
    /// The add itself, without the write section: callers must hold one (<see cref="Add"/>). The name refers to the
    /// absence of synchronization inside this method.
    /// </summary>
    /// <param name="value">The value to add.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AddUnsynchronized(uint value)
    {
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);

        // Packet ids are indexed in ascending order, so an add almost always targets the container of the previous add.
        // Check the last key first and skip the binary search; any other value takes the search below and stays correct.
        int last = _Count - 1;
        int idx = last >= 0 && _Keys[last] == high
            ? last
            : _FindChunk(high);
        if (idx >= 0)
        {
            // IContainer.Add mutates in place and returns either the same container or an upgraded one (array to
            // bitmap at ArrayContainer.MaxCapacity). Comparing cardinalities tells whether the value was new, which
            // keeps the cached total exact for a duplicate add; the slot is only rewritten when something changed.
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
            // First value of a new chunk: every container starts as a small array container. ~idx is the sorted
            // insertion point returned by the binary search (for an append it equals _Count, so nothing shifts).
            IContainer newContainer = new ArrayContainer().Add(low);
            _InsertChunk(~idx, high, newContainer);
        }
    }

    /// <summary>Binary search over the valid prefix of the key array.</summary>
    /// <param name="key">The high 16 bits of a value.</param>
    /// <returns>The slot index of the chunk when found, otherwise the bitwise complement of the sorted insertion point.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _FindChunk(ushort key) => Array.BinarySearch(_Keys, 0, _Count, key);

    /// <summary>
    /// Inserts a chunk at sorted position <paramref name="idx"/> and adds its cardinality to the cached total. Doubles the
    /// arrays when full (amortized O(1) appends). Used inside write sections on the receiver, and on private result
    /// bitmaps that are still being built (appending at <c>_Count</c>).
    /// </summary>
    /// <param name="idx">The sorted insertion slot, 0 to <c>_Count</c>; <c>_Count</c> appends without shifting.</param>
    /// <param name="key">The high 16 bits of the chunk; must keep <c>_Keys</c> strictly ascending at <paramref name="idx"/>.</param>
    /// <param name="container">The container of the chunk; ownership passes to this bitmap.</param>
    private void _InsertChunk(int idx, ushort key, IContainer container)
    {
        // Array.Resize allocates a new array and copies; a concurrent reader may still hold the old arrays, which is
        // exactly what the seqlock retry covers.
        if (_Count == _Keys.Length)
        {
            int newCap = _Keys.Length * 2;
            Array.Resize(ref _Keys, newCap);
            Array.Resize(ref _Containers, newCap);
        }

        // Open a gap only for a real insert; an append (idx == _Count) shifts nothing.
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

    #endregion

    #region Run optimization

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
        // Locate the first chunk at or above firstKey with one binary search (a miss yields the insertion point, ~index).
        // Plain reads of _Count / _Keys are fine here: this is the single writer. The early return when the range holds
        // no chunk happens before _BeginWrite, so an empty range never makes readers retry.
        int index = _FindChunk((ushort)Math.Clamp(firstKey, 0, ushort.MaxValue));
        if (index < 0)
        {
            index = ~index;
        }

        if (index >= _Count || _Keys[index] < firstKey || _Keys[index] >= endKey)
        {
            return false;
        }

        // Convert chunk by chunk, but only when the run form is strictly smaller in the Roaring serialized sizes; a run
        // container is never converted again. Containers are replaced, never mutated: the old container object is left
        // unchanged for anyone still holding it, and the swap of the array slot is covered by the write section.
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

    #endregion

    #region Point queries and bounds

    /// <summary>
    /// Checks if a 32-bit value is present. Safe for concurrent readers while a single writer appends; allocation-free.
    /// </summary>
    /// <param name="value">The value to look up.</param>
    /// <returns><see langword="true"/> when the bitmap holds <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(uint value)
    {
        // Split into the chunk key (high 16 bits) and the position inside the chunk's container (low 16 bits).
        ushort high = (ushort)(value >> 16);
        ushort low = (ushort)(value & 0xFFFF);

        // Seqlock read section: wait out an in-flight write, read, then validate that no write started or finished
        // meanwhile. Otherwise the result may be torn and is discarded.
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
    /// <param name="value">The smallest value on success; 0 when the bitmap is empty.</param>
    /// <returns><see langword="true"/> when the bitmap holds at least one value.</returns>
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
    /// <param name="value">The largest value on success; 0 when the bitmap is empty.</param>
    /// <returns><see langword="true"/> when the bitmap holds at least one value.</returns>
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

    /// <summary>Throws the exception of <see cref="Min"/> and <see cref="Max"/> for an empty bitmap; kept out of line so the getters stay small.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowEmpty() =>
        throw new InvalidOperationException("Bitmap is empty.");

    #endregion

    #region Copy and view

    /// <summary>
    /// Creates a deep copy of this bitmap. All containers are copied independently.
    /// Mutations to either the original or the copy do not affect the other.
    /// This is O(cardinality).
    /// </summary>
    /// <returns>A new bitmap that holds the values this bitmap held when the copy was validated; it shares no storage.</returns>
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
    /// <returns>A live read-only view that wraps this instance.</returns>
    public ReadOnlyRoaringBitmap AsReadOnly() => new(this);

    #endregion

    #region Immutable set operations

    // These build a new private result by a sorted merge over both key arrays (O(chunks of both)), appending chunks at the
    // end of the result (_InsertChunk with idx == _Count, so no shifting). Neither operand is modified. Both operands are
    // read without the seqlock, so neither may be appended to concurrently (see the type remarks). Chunks present on one
    // side only are cloned, so a later in-place Add to the result (containers mutate in place) cannot corrupt an operand.

    /// <summary>AND (intersection) with another bitmap. Neither operand is modified.</summary>
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
    /// <returns>A new bitmap that holds the values present in both operands; it shares no container with either.</returns>
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

    /// <summary>OR (union) with another bitmap. Neither operand is modified.</summary>
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
    /// <returns>A new bitmap that holds the values present in either operand; it shares no container with either.</returns>
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

    /// <summary>ANDNOT (difference): this AND NOT other. Neither operand is modified.</summary>
    /// <param name="other">The operand whose values are removed; must not be appended to while this runs.</param>
    /// <returns>A new bitmap that holds the values of this bitmap that are absent from <paramref name="other"/>.</returns>
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

    /// <summary>XOR (symmetric difference) with another bitmap. Neither operand is modified.</summary>
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
    /// <returns>A new bitmap that holds the values present in exactly one operand; it shares no container with either.</returns>
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

    // All in-place operations run inside one seqlock write section on the receiver, so concurrent readers of the receiver
    // retry instead of seeing a half-merged key/container array pair. The operand `other` is read without any
    // synchronization and must not be appended to concurrently.

    /// <summary>
    /// In-place AND: removes all values not present in <paramref name="other"/>.
    /// Avoids allocating a new RoaringBitmap. Containers that become empty are removed.
    /// Two bitmap chunks are combined by the one-pass SIMD container operation into a new container (sparse results become arrays).
    /// </summary>
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
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
                    // One path for every container pair. For two bitmaps, BitmapContainer.And writes the SIMD result into a new
                    // container in one pass and demotes a sparse result to an array; the receiver's container is never mutated, so
                    // aliased operands stay intact.
                    IContainer intersected = _Containers[i].And(other._Containers[j]);

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
            // The merge compacted the surviving chunks to the front; null the unused tail so the dropped containers
            // can be collected (the array itself is kept, its capacity is reused by later adds).
            for (int c = writePos; c < _Count; c++)
            {
                _Containers[c] = null!; // Invariant: slots at or above _Count are not read by a valid read section; a torn reader fails validation and retries.
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
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
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

            // Merge into fresh arrays and swap them in (O(chunks of both)); inserting chunk by chunk into the current arrays
            // would shift entries once per new chunk. This-side containers are moved by reference (not copied: they stay
            // this bitmap's own containers), chunks only in `other` are cloned so the two bitmaps never share a mutable
            // container, and chunks present in both are united into a new container.
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
                containers[c] = null!; // Invariant: the old array is replaced below; only a torn reader still sees it, fails validation, and retries.
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
    /// Two bitmap chunks are combined by the one-pass SIMD container operation into a new container (sparse results become arrays).
    /// </summary>
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
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
                    // One path for every container pair (see AndWith): BitmapContainer.AndNot is a single pass into a new container.
                    IContainer diff = _Containers[i].AndNot(other._Containers[j]);

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
                _Containers[c] = null!; // Invariant: slots at or above _Count are not read by a valid read section; a torn reader fails validation and retries.
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
    /// <param name="other">The other operand; must not be appended to while this runs.</param>
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

            // Same merge-into-fresh-arrays strategy as OrWith. A chunk present in both whose XOR is empty is dropped from the
            // result, so newCount can end below the old count (hence the cleanup of the old tail below).
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
                containers[c] = null!; // Invariant: the old array is replaced below; only a torn reader still sees it, fails validation, and retries.
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
    /// Replaces this (empty) bitmap's content with deep copies of <paramref name="other"/>'s chunks, so an in-place OR /
    /// XOR into an empty receiver needs no merge. Must run inside a write section.
    /// </summary>
    /// <param name="other">The bitmap to copy; not appended to while this runs; not modified.</param>
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

    /// <summary>
    /// Rebuilds the cached total after an in-place set operation changed many containers at once (O(chunks)); the
    /// incremental update used by <see cref="Add"/> does not apply there.
    /// </summary>
    private void _RecomputeCachedCardinality()
    {
        long total = 0;
        for (int i = 0; i < _Count; i++)
        {
            total += _Containers[i].Cardinality;
        }
        _Cardinality = total;
    }

    #endregion

    #region Rank and select

    /// <summary>
    /// Returns the number of values in the bitmap that are ≤ <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The upper bound, inclusive; it need not be present.</param>
    /// <returns>The count of stored values that are less than or equal to <paramref name="value"/>.</returns>
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
    /// <param name="position">The 0-based position in ascending order; a negative position selects nothing.</param>
    /// <returns>The value at <paramref name="position"/>, or <see langword="null"/> when no such value exists.</returns>
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

    /// <summary>Counts the values of a container that are ≤ <paramref name="threshold"/>.</summary>
    /// <param name="container">The container to count in.</param>
    /// <param name="threshold">The upper bound, inclusive, as a low 16-bit value.</param>
    /// <returns>The number of values of <paramref name="container"/> at or below <paramref name="threshold"/>.</returns>
    private static int _ContainerRank(IContainer container, ushort threshold)
    {
        if (container is ArrayContainer arr)
        {
            return arr.Rank(threshold);
        }

        if (container is RunContainer run)
        {
            return run.Rank(threshold);
        }

        if (container is BitmapContainer bmp)
        {
            // Population count of all whole words below the threshold's word, then the masked partial word.
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
                ulong mask = bitPos == 63
                    ? ulong.MaxValue
                    : (1UL << (bitPos + 1)) - 1;
                count += BitOperations.PopCount(bmp.Bitmap[fullWords] & mask);
            }
            return count;
        }

        // Fallback for an unknown container kind: probe every value up to the threshold. The v == MaxValue guard
        // is required: for threshold 65535 the ushort counter would wrap to 0 and the loop would never end.
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

    /// <summary>Selects the value of a container that has a given 0-based position in ascending order.</summary>
    /// <param name="container">The container to select from.</param>
    /// <param name="n">The 0-based position, 0 to <c>Cardinality - 1</c>; the caller checks the range.</param>
    /// <returns>The low 16-bit value at position <paramref name="n"/>; 0 when <paramref name="n"/> is out of range.</returns>
    private static ushort _ContainerSelect(IContainer container, int n)
    {
        if (container is ArrayContainer arr)
        {
            return arr.ValueAt(n);
        }

        if (container is RunContainer selectRun)
        {
            return selectRun.Select(n);
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

        // Fallback for an unknown container kind: scan all 65 536 values and count set ones. Returns 0 when n is
        // out of range, which Select's cardinality check upstream prevents.
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
