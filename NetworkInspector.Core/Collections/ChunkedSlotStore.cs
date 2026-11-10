// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Shared slot engine for dense <see cref="ChunkedGrowOnlyStore{T}"/> and packed
/// <see cref="ChunkedAppendOnlyStore{T}"/>. Not part of the public API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generations:</b> the chunks and the published count between two <see cref="Clear"/> calls form one
/// <see cref="Generation"/>. <see cref="Clear"/> replaces the whole generation with one volatile write, so a
/// reader always pairs a count with the chunks it belongs to, and the count of a generation never goes back.
/// The GC keeps a replaced generation alive while a reader still uses it; no epoch or retire step is needed.
/// </para>
/// <para>
/// <b>Append cost:</b> the gate compare-exchange is the only full fence. The publish is a volatile write of the
/// generation's count (a plain store on x64, a store-release on ARM64), and nothing is undone after a racing
/// <see cref="Clear"/>: an entry written into a replaced generation is unreachable for every reader that loads
/// the store after that <see cref="Clear"/>.
/// </para>
/// <para>
/// <b>Why one engine for two public types:</b> the dense and the packed store need the same lock-free publication
/// protocol (write the slot, then publish the count with a release store). Keeping the protocol in one place means a
/// memory-ordering fix cannot be applied to only one of them. Neither type takes a lock on the read or append path.
/// </para>
/// <para>
/// <b>Thread-safety:</b> a single writer (enforced by <see cref="Append"/> and <see cref="AppendRange"/> with a
/// compare-exchange gate, or guaranteed by the caller for <see cref="AppendOwned"/>), any number of lock-free readers.
/// </para>
/// </remarks>
internal sealed class ChunkedSlotStore<T>
{
    #region Configuration and state

    /// <summary>
    /// The slots between two <see cref="Clear"/> calls: their chunks and their published count.
    /// Only the single writer changes <see cref="Count"/>, and only upward.
    /// </summary>
    private sealed class Generation(int chunkShift)
    {
        /// <summary>Chunk table of this generation. Its only allocator is the single writer.</summary>
        internal readonly ChunkedOuterArray<T[]> Outer = new(chunkShift);

        /// <summary>
        /// Number of published slots. <see langword="volatile"/>: the writer's release store makes every slot below the
        /// count visible to a reader that acquires the count.
        /// </summary>
        internal volatile int Count;

        /// <summary>
        /// Writer-only cache of the chunk the last append wrote into (single writer by contract); readers never read it.
        /// The outer array returns the same chunk for an index for the whole generation, so a cached chunk can never go
        /// stale; a new generation starts without a cache.
        /// </summary>
        internal T[]? Tail;

        /// <summary>Index of the chunk held in <see cref="Tail"/>; -1 while there is no cache.</summary>
        internal int TailChunkIndex = -1;
    }

    /// <summary>Log₂ of slots per chunk; passed on to every new <see cref="Generation"/>.</summary>
    private readonly int _ChunkShift;

    /// <summary>Slots per chunk (<c>1 &lt;&lt; _ChunkShift</c>); a power of two so index math is shift and mask.</summary>
    private readonly int _ChunkSize;

    /// <summary>Value of a slot that was never written; also what failed reads return.</summary>
    private readonly T _UnsetValue;

    /// <summary>
    /// Whether a new chunk must be filled with <see cref="_UnsetValue"/>. When the sentinel is <c>default</c>, the
    /// zeroed memory of a fresh array already holds it and the fill is skipped.
    /// </summary>
    private readonly bool _FillUnset;

    /// <summary>
    /// <see cref="_CreateChunk"/> converted to a delegate once. Converting the instance method group on every call would
    /// allocate a delegate each time a chunk is requested.
    /// </summary>
    private readonly Func<T[]> _ChunkFactory;

    /// <summary>
    /// The live generation. <see langword="volatile"/> so a reader or the writer always sees the latest swap by
    /// <see cref="Clear"/>; the object it points to is internally consistent (see the type remarks).
    /// </summary>
    private volatile Generation _Current;

    /// <summary>1 while an <see cref="Append"/> or <see cref="AppendRange"/> runs. A misuse detector, not a lock.</summary>
    private volatile int _AppendGate;

    /// <summary>Creates an empty store.</summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; validated by <see cref="ChunkedOuterArray{TChunk}"/>.</param>
    /// <param name="unsetValue">Sentinel for unwritten slots and failed reads.</param>
    internal ChunkedSlotStore(int chunkShift, T unsetValue)
    {
        // ChunkedOuterArray validates chunkShift and throws ArgumentOutOfRangeException.
        _Current = new(chunkShift);
        _ChunkShift = chunkShift;
        _ChunkSize = _Current.Outer.ChunkSize;
        _UnsetValue = unsetValue;
        _FillUnset = !EqualityComparer<T>.Default.Equals(unsetValue, default!); // default! only feeds the comparer; default(T) is a valid T value.
        _ChunkFactory = _CreateChunk;
    }

    /// <summary>Number of published slots of the live generation (volatile read).</summary>
    internal int Count => _Current.Count;

    #endregion

    #region Copying reads

    /// <summary>Reads a published slot, or returns the unset sentinel when <paramref name="index"/> is not published.</summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The slot value, or the unset sentinel when the index is not published.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    internal T Get(int index)
    {
        if (!TryGet(index, out T value))
        {
            return _UnsetValue;
        }

        return value;
    }

    /// <summary>Reads a published slot of the live generation.</summary>
    /// <param name="index">Slot index; anything outside <c>0 .. Count-1</c> is a miss.</param>
    /// <param name="value">The slot value, or the unset sentinel on a miss.</param>
    /// <returns><see langword="false"/> when the index is not published or its chunk does not exist.</returns>
    internal bool TryGet(int index, out T value)
    {
        // One generation snapshot: count and chunks always belong together.
        Generation generation = _Current;
        if (!Ids.ArrayIndexIdRange.IsValidIndex(index) || (uint)index >= (uint)generation.Count)
        {
            value = _UnsetValue;
            return false;
        }

        return _TryReadPublishedUnchecked(generation, index, out value);
    }

    #endregion

    #region Appending and clearing

    /// <summary>
    /// Appends one entry and publishes it. Single writer: a second concurrent call throws.
    /// </summary>
    /// <param name="item">Entry stored at the current count.</param>
    /// <exception cref="InvalidOperationException">
    /// A concurrent <see cref="Append"/> / <see cref="AppendRange"/> is running, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/> entries.
    /// </exception>
    internal void Append(in T item)
    {
        _EnterAppendGate();
        try
        {
            // Loop only repeats when Clear replaced the generation while the entry was being published.
            while (true)
            {
                // Volatile read (acquire). A Clear after this read leaves the write below in a replaced,
                // unreachable generation; the re-check after the publish moves the entry to the live one.
                Generation generation = _Current;
                int index = generation.Count;
                Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(index, "entry");
                _WriteLiveSlot(generation, index, item);

                // Volatile write (release): readers that see the new count see the written slot. No CAS and
                // no undo: the count of a generation is never reset, only the generation is replaced.
                generation.Count = index + 1;
                if (ReferenceEquals(_Current, generation))
                {
                    return;
                }
            }
        }
        finally
        {
            _AppendGate = 0;
        }
    }

    /// <summary>
    /// <see cref="Append"/> without the misuse gate. The caller guarantees that every <see cref="Append"/>,
    /// <see cref="AppendOwned"/>, <see cref="AppendRange"/>, and <see cref="Clear"/> of this store is serialized by
    /// the caller (the value-cache series holds its series gate around them). Readers stay lock-free: the slot is
    /// written before the count is published with a release store, exactly as in <see cref="Append"/>.
    /// Motivation: a series that already serializes its writers under its own gate would otherwise pay a second
    /// compare-exchange (the full fence of <see cref="Append"/>) per appended value; skipping it removes that fence
    /// from the per-value path. Calling this without the caller-side serialization removes the misuse detection and
    /// can corrupt the published count, so it must never be used without such a gate.
    /// </summary>
    /// <param name="item">Entry stored at the current count.</param>
    /// <exception cref="InvalidOperationException">The store reached <see cref="Ids.ArrayIndexIdRange.MaxValue"/> entries.</exception>
    internal void AppendOwned(in T item)
    {
        while (true)
        {
            Generation generation = _Current;
            int index = generation.Count;
            Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(index, "entry");
            _WriteLiveSlot(generation, index, item);

            // Release: readers that see the count see the slot. Re-check as in Append for a Clear that raced.
            generation.Count = index + 1;
            if (ReferenceEquals(_Current, generation))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Appends all values as one publish: a reader sees either none or all of them. Single writer, like
    /// <see cref="Append"/>. An empty span is a no-op.
    /// </summary>
    /// <param name="values">Entries stored from the current count onward.</param>
    /// <exception cref="InvalidOperationException">
    /// A concurrent <see cref="Append"/> / <see cref="AppendRange"/> is running, or the store would exceed
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/> entries.
    /// </exception>
    internal void AppendRange(ReadOnlySpan<T> values)
    {
        if (values.Length == 0)
        {
            return;
        }

        _EnterAppendGate();
        try
        {
            while (true)
            {
                // Same protocol as Append: one generation per attempt, publish once, retry on a replaced generation.
                Generation generation = _Current;
                int index = generation.Count;

                // Capacity check before any write. Summed in long: index and Length are non-negative ints, so the long sum cannot
                // wrap. A sum above int.MaxValue is reported as the same capacity error as any other index beyond the range
                // (InvalidValue is never a valid index) instead of a bare OverflowException.
                long lastLong = (long)index + values.Length - 1;
                int last = lastLong > int.MaxValue
                    ? Ids.ArrayIndexIdRange.InvalidValue
                    : (int)lastLong;
                Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(last, "entry");

                // Write all slots first, then publish the new count once (release): readers never see a partial range.
                _SetRange(generation, index, values);

                generation.Count = checked(index + values.Length);
                if (ReferenceEquals(_Current, generation))
                {
                    return;
                }
            }
        }
        finally
        {
            _AppendGate = 0;
        }
    }

    /// <summary>
    /// Writes <paramref name="values"/> into the slots starting at <paramref name="startIndex"/> of
    /// <paramref name="generation"/> without publishing them. The caller publishes the count afterwards.
    /// </summary>
    /// <param name="generation">The generation to write into.</param>
    /// <param name="startIndex">First slot index to write.</param>
    /// <param name="values">Values to copy; may span several chunks.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range does not fit the valid index range.</exception>
    private void _SetRange(Generation generation, int startIndex, ReadOnlySpan<T> values)
    {
        if (values.Length == 0)
        {
            return;
        }

        // Repeats the capacity checks of AppendRange; these only fire if a caller bypasses it.
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(startIndex, nameof(startIndex));
        // Summed in long: startIndex is validated non-negative and Length is non-negative, so the long sum cannot wrap.
        long lastLong = (long)startIndex + values.Length - 1;
        if (lastLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                values.Length,
                "startIndex + values.Length - 1 overflows the valid index range.");
        }

        int last = (int)lastLong;

        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(last, nameof(values));

        // The generation is the only allocator of its outer array and never clears it, so a chunk returned by
        // GetOrAllocateChunk stays the live chunk of its index (single-allocator contract of ChunkedOuterArray).
        // Copy chunk by chunk: each iteration fills the rest of the current chunk (a "run"), so the outer-array lookup
        // happens once per chunk instead of once per value.
        ChunkedOuterArray<T[]> outer = generation.Outer;
        int offset = 0;
        while (offset < values.Length)
        {
            int index = startIndex + offset;
            (int chunkIdx, int slotIdx) = outer.DecomposeIndex(index);
            T[] chunk = outer.GetOrAllocateChunk(chunkIdx, _ChunkFactory);
            int run = Math.Min(outer.ChunkSize - slotIdx, values.Length - offset);
            ReadOnlySpan<T> slice = values.Slice(offset, run);

            // Bulk memmove only for plain 1/2/4/8-byte values; every other T is written element by element through
            // _WriteSlot so reference and large-struct slots get the same store semantics as a single Append.
            if (_CanBulkCopy())
            {
                slice.CopyTo(chunk.AsSpan(slotIdx, run));
            }
            else
            {
                for (int i = 0; i < run; i++)
                {
                    _WriteSlot(ref chunk[slotIdx + i], slice[i]);
                }
            }

            offset += run;
        }
    }

    /// <summary>Drops every slot by swapping in an empty generation.</summary>
    /// <remarks>
    /// Safe against readers (they finish on the generation they loaded) and against a running <see cref="Append"/>
    /// (it notices the swap and retries); the count of the new generation starts at 0.
    /// </remarks>
    internal void Clear()
    {
        // One volatile write replaces chunks and count together. Readers still on the old generation finish
        // on it; the GC reclaims it afterwards. A concurrent Append notices the swap and retries.
        // Rejected: resetting the count and clearing the chunks in place, which would let a reader pair a new
        // count with old chunk contents (or the reverse) and would need a lock or a version counter to detect it.
        _Current = new(_ChunkShift);
    }

    #endregion

    #region In-place reads and searching

    /// <summary>
    /// Returns a reference to a published slot of the live generation. Avoids copying a large entry struct. The
    /// reference stays valid memory after a <see cref="Clear"/> (it then points into the replaced generation), but only
    /// the single writer may mutate through it.
    /// </summary>
    /// <param name="index">Published slot index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not published.</exception>
    internal ref T ItemRef(int index)
    {
        Generation generation = _Current;
        int count = generation.Count;
        if ((uint)index >= (uint)count)
        {
            _ThrowIndexOutOfRange(index, count);
        }

        return ref _ItemRefUnchecked(generation, index, count);
    }

    /// <summary>
    /// Copies a published slot, treating a racing <see cref="Clear"/> as a miss.
    /// </summary>
    /// <param name="index">Published slot index.</param>
    /// <param name="value">The slot value, or the unset sentinel on a miss.</param>
    /// <returns><see langword="false"/> when the index is not published or the generation was replaced meanwhile.</returns>
    internal bool TryReadPublished(int index, out T value)
    {
        // A generation replaced before this read returns is a miss, not a hit from the cleared log: the caller
        // must never receive a row of the old log as if it belonged to the refilled one.
        Generation generation = _Current;
        if ((uint)index >= (uint)generation.Count
            || !_TryReadPublishedUnchecked(generation, index, out value)
            || !ReferenceEquals(_Current, generation))
        {
            value = _UnsetValue;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Binary-searches the published prefix; valid only for strictly ascending keys.
    /// </summary>
    /// <param name="sortKey">Key to find.</param>
    /// <param name="getSortKey">
    /// Key extractor. A delegate (not an interface constraint) keeps this engine generic over any <typeparamref name="T"/>;
    /// callers pass a cached static delegate so a search allocates nothing.
    /// </param>
    /// <returns>The entry index, or -1 when no entry has the key or a <see cref="Clear"/> raced the search.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getSortKey"/> is <see langword="null"/>.</exception>
    internal int BinarySearch(int sortKey, GetSortKey<T> getSortKey)
    {
        ArgumentNullException.ThrowIfNull(getSortKey);

        // Every probe reads the same generation, so the search never mixes entries of a cleared log
        // with entries appended after the Clear.
        Generation generation = _Current;
        int count = generation.Count;
        int lo = 0;
        int hi = count - 1;
        int found = -1;
        while (lo <= hi)
        {
            // lo + (hi - lo) / 2 cannot overflow, unlike (lo + hi) / 2 for counts near int.MaxValue.
            int mid = lo + ((hi - lo) >> 1);

            // A missing chunk below the published count cannot happen for an intact generation; treat it as a miss
            // instead of reading garbage.
            if (!_TryReadPublishedUnchecked(generation, mid, out T published))
            {
                found = -1;
                break;
            }

            int midKey = getSortKey(in published);
            if (midKey == sortKey)
            {
                found = mid;
                break;
            }

            if (midKey < sortKey)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // A Clear that replaced the generation during the search makes the result a miss.
        if (!ReferenceEquals(_Current, generation))
        {
            return -1;
        }

        return found;
    }

    /// <summary>
    /// Acquire-reads one class field of a published slot in place. Reading the field through the live slot, instead of
    /// copying the whole entry struct first, is what lets the writer publish that field later with a release write.
    /// </summary>
    /// <typeparam name="TField">Class type of the field.</typeparam>
    /// <param name="index">Published slot index.</param>
    /// <param name="getter">Returns a reference to the field inside the live slot.</param>
    /// <returns>The field value, or <see langword="null"/> when the index is not published or a <see cref="Clear"/> raced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getter"/> is <see langword="null"/>.</exception>
    internal TField? ReadVolatileRefField<TField>(int index, GetRefField<T, TField> getter)
        where TField : class?
    {
        ArgumentNullException.ThrowIfNull(getter);

        Generation generation = _Current;
        if ((uint)index >= (uint)generation.Count)
        {
            return null;
        }

        (int chunkIdx, int slotIdx) = generation.Outer.DecomposeIndex(index);
        T[]? chunk = generation.Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            return null;
        }

        // Volatile.Read pairs with the writer's Volatile.Write of the field: a reader that sees the reference also
        // sees the object it points to fully constructed.
        TField? value = Volatile.Read(ref getter(ref chunk[slotIdx]));
        if (!ReferenceEquals(_Current, generation))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// Exposes one chunk as a read-only span clipped to the published slots, so a caller can scan a whole chunk
    /// without a per-element lookup.
    /// </summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="publishedCount">Exclusive upper bound of readable slots; clipped to <see cref="Count"/>.</param>
    /// <param name="span">The slots of the chunk below the bound.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one readable slot.</returns>
    internal bool TryGetPublishedChunk(int chunkIndex, int publishedCount, out ReadOnlySpan<T> span)
    {
        // Clip the caller's bound to the count of the generation read here, so the span can never expose a slot
        // that this generation has not published.
        Generation generation = _Current;
        int count = generation.Count;
        if (publishedCount > count)
        {
            publishedCount = count;
        }

        if (publishedCount <= 0 || chunkIndex < 0)
        {
            span = default;
            return false;
        }

        // Guard the shift below against overflow: chunkIndex << shift must stay within int.
        ChunkedOuterArray<T[]> outer = generation.Outer;
        int shift = outer.ChunkShift;
        if (chunkIndex > (int.MaxValue >> shift))
        {
            span = default;
            return false;
        }

        int start = chunkIndex << shift;
        if (start >= publishedCount)
        {
            span = default;
            return false;
        }

        T[]? chunk = outer.GetChunk(chunkIndex);
        if (chunk is null)
        {
            span = default;
            return false;
        }

        int length = Math.Min(outer.ChunkSize, publishedCount - start);
        span = chunk.AsSpan(0, length);
        return true;
    }

    #endregion

    #region Write gate and slot access

    /// <summary>
    /// Takes the single-writer gate. It is a misuse detector, not a lock: a second concurrent writer is a
    /// caller bug and gets <see cref="InvalidOperationException"/>. The compare-exchange is the only full
    /// fence of an append; the release in <c>finally</c> is a volatile write.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EnterAppendGate()
    {
        if (Interlocked.CompareExchange(ref _AppendGate, 1, 0) != 0)
        {
            _ThrowConcurrentAppend();
        }
    }

    /// <summary>Throw helper kept out of line so the inlined gate check stays small.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void _ThrowConcurrentAppend() =>
        throw new InvalidOperationException("Concurrent Append is not supported.");

    /// <summary>Allocates one chunk, filled with the unset sentinel when it is not <c>default</c>. Used through <see cref="_ChunkFactory"/>.</summary>
    private T[] _CreateChunk()
    {
        T[] chunk = new T[_ChunkSize];
        if (_FillUnset)
        {
            Array.Fill(chunk, _UnsetValue);
        }

        return chunk;
    }

    /// <summary>
    /// Writes one slot of the live append position, allocating its chunk on first use. Does not publish the count.
    /// </summary>
    /// <param name="generation">The generation being appended to.</param>
    /// <param name="index">Slot index, already validated against the index range by the caller.</param>
    /// <param name="value">Value to store.</param>
    private void _WriteLiveSlot(Generation generation, int index, T value)
    {
        // The generation is the only allocator of its outer array and never clears it, so a chunk it returned stays
        // the live chunk of its index (single-allocator contract of ChunkedOuterArray); no re-check is needed. The
        // tail cache skips the outer-array lookup for every append but the first of a chunk.
        int chunkIndex = index >> _ChunkShift;
        T[]? chunk = generation.Tail;
        if (chunk is null || generation.TailChunkIndex != chunkIndex)
        {
            chunk = generation.Outer.GetOrAllocateChunk(chunkIndex, _ChunkFactory);
            generation.Tail = chunk;
            generation.TailChunkIndex = chunkIndex;
        }

        _WriteSlot(ref chunk[index & (_ChunkSize - 1)], value);
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> is a reference-free value type of 1, 2, 4, or 8 bytes, i.e. one of the shapes
    /// <see cref="_WriteSlot"/> stores as a single primitive. The JIT evaluates these checks per instantiation, so the
    /// call folds to a constant.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _CanBulkCopy()
    {
        if (!typeof(T).IsValueType || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return false;
        }

        int size = Unsafe.SizeOf<T>();
        return size is 1 or 2 or 4 or 8;
    }

    /// <summary>
    /// Stores one value with the strongest ordering the shape of <typeparamref name="T"/> allows. Reference types and
    /// reference-free 1/2/4/8-byte values are stored with a single <see cref="Volatile"/> write of one primitive of the same size (release);
    /// reference-containing structs and larger structs fall back to a plain copy, which is why concurrent readers of
    /// those need a count gate: a reader may read such a slot only below a count it loaded before the read (see the public
    /// type remarks). The branches are resolved per instantiation by the JIT, so only one remains in the compiled method.
    /// </summary>
    /// <param name="slot">Destination slot.</param>
    /// <param name="value">Value to store.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteSlot(ref T slot, T value)
    {
        if (!typeof(T).IsValueType)
        {
            Volatile.Write(ref Unsafe.As<T, object?>(ref slot), Unsafe.As<T, object?>(ref value));
            return;
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            slot = value;
            return;
        }

        switch (Unsafe.SizeOf<T>())
        {
            case 1:
                Volatile.Write(ref Unsafe.As<T, byte>(ref slot), Unsafe.As<T, byte>(ref value));
                return;
            case 2:
                Volatile.Write(ref Unsafe.As<T, short>(ref slot), Unsafe.As<T, short>(ref value));
                return;
            case 4:
                Volatile.Write(ref Unsafe.As<T, int>(ref slot), Unsafe.As<T, int>(ref value));
                return;
            case 8:
                Volatile.Write(ref Unsafe.As<T, long>(ref slot), Unsafe.As<T, long>(ref value));
                return;
            default:
                slot = value;
                return;
        }
    }

    /// <summary>
    /// Counterpart of <see cref="_WriteSlot"/>: a single <see cref="Volatile"/> read (acquire) for the shapes that were
    /// stored with one, a plain read otherwise. The value is reinterpreted through a same-size primitive local, which
    /// avoids boxing and keeps the access a single primitive load.
    /// </summary>
    /// <param name="slot">Source slot.</param>
    /// <returns>The value of the slot, read with acquire ordering when the shape of <typeparamref name="T"/> allows it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T _ReadSlot(ref T slot)
    {
        if (!typeof(T).IsValueType)
        {
            object? boxed = Volatile.Read(ref Unsafe.As<T, object?>(ref slot));
            return Unsafe.As<object?, T>(ref boxed);
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return slot;
        }

        switch (Unsafe.SizeOf<T>())
        {
            case 1:
            {
                byte bits = Volatile.Read(ref Unsafe.As<T, byte>(ref slot));
                return Unsafe.As<byte, T>(ref bits);
            }
            case 2:
            {
                short bits = Volatile.Read(ref Unsafe.As<T, short>(ref slot));
                return Unsafe.As<short, T>(ref bits);
            }
            case 4:
            {
                int bits = Volatile.Read(ref Unsafe.As<T, int>(ref slot));
                return Unsafe.As<int, T>(ref bits);
            }
            case 8:
            {
                long bits = Volatile.Read(ref Unsafe.As<T, long>(ref slot));
                return Unsafe.As<long, T>(ref bits);
            }
            default:
                return slot;
        }
    }

    /// <summary>Reads a published slot of <paramref name="generation"/>; the caller checked the index against its count.</summary>
    /// <param name="generation">The generation to read from.</param>
    /// <param name="index">Slot index below the count of <paramref name="generation"/>.</param>
    /// <param name="value">The slot value, or the unset sentinel when the chunk does not exist.</param>
    /// <returns><see langword="false"/> when the chunk of the slot does not exist.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryReadPublishedUnchecked(Generation generation, int index, out T value)
    {
        (int chunkIdx, int slotIdx) = generation.Outer.DecomposeIndex(index);
        T[]? chunk = generation.Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            value = _UnsetValue;
            return false;
        }

        value = _ReadSlot(ref chunk[slotIdx]);
        return true;
    }

    /// <summary>Returns a reference to a slot whose index the caller already checked against <paramref name="count"/>.</summary>
    /// <param name="generation">The generation to read from.</param>
    /// <param name="index">Slot index below <paramref name="count"/>.</param>
    /// <param name="count">The count that the caller loaded from <paramref name="generation"/>; used for the error message.</param>
    /// <returns>A reference to the slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The chunk of the slot does not exist.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T _ItemRefUnchecked(Generation generation, int index, int count)
    {
        (int chunkIdx, int slotIdx) = generation.Outer.DecomposeIndex(index);
        T[]? chunk = generation.Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            _ThrowIndexOutOfRange(index, count);
        }

        return ref chunk[slotIdx];
    }

    /// <summary>Throw helper kept out of line so the inlined accessors stay small.</summary>
    /// <param name="index">The rejected index.</param>
    /// <param name="count">The published count that the index was checked against.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always; the message names the valid range.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void _ThrowIndexOutOfRange(int index, int count) =>
        throw new ArgumentOutOfRangeException(
            nameof(index),
            index,
            Invariant($"Index must be in the range 0..{Math.Max(count - 1, 0).ToString(CultureInfo.InvariantCulture)} (published entries)."));

    #endregion
}
