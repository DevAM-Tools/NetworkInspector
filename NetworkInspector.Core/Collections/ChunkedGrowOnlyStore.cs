// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Sort key accessor for packed entries of <see cref="ChunkedAppendOnlyStore{T}"/>.
/// </summary>
public interface ISortKeyed
{
    /// <summary>
    /// Key compared by <see cref="ChunkedAppendOnlyStoreExtensions.BinarySearch{T}"/>.
    /// Entries must be appended in strictly ascending <see cref="SortKey"/> order for the
    /// search to be valid.
    /// </summary>
    int SortKey { get; }
}

/// <summary>
/// Extracts the integer sort key from a packed <see cref="ChunkedAppendOnlyStore{T}"/> entry.
/// Pass a cached static delegate into <see cref="ChunkedAppendOnlyStore{T}.BinarySearch"/>.
/// </summary>
/// <typeparam name="T">Entry type stored in the packed prefix.</typeparam>
/// <param name="item">Published entry.</param>
/// <returns>Sort key used by binary search.</returns>
public delegate int GetSortKey<T>(in T item);

/// <summary>
/// Returns a by-ref class field of a packed slot so <see cref="ChunkedAppendOnlyStore{T}.ReadVolatileRefField{TField}"/>
/// can <see cref="Volatile"/>-read the published location.
/// </summary>
/// <typeparam name="T">Packed entry type.</typeparam>
/// <typeparam name="TField">Class field type.</typeparam>
/// <param name="item">Live slot.</param>
/// <returns>Reference to the field inside <paramref name="item"/>.</returns>
public delegate ref TField GetRefField<T, TField>(ref T item) where TField : class?;

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
    #region Nested types

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

        // Writer-only cache of the chunk the last append wrote into (single writer by contract); readers never read
        // these two fields. The outer array returns the same chunk for an index for the whole generation, so a cached
        // chunk can never go stale; a new generation starts without a cache.
        internal T[]? Tail;
        internal int TailChunkIndex = -1;
    }

    #endregion

    #region Fields

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

    #endregion

    #region Constructors

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
        _FillUnset = !EqualityComparer<T>.Default.Equals(unsetValue, default!);
        _ChunkFactory = _CreateChunk;
    }

    #endregion

    #region Properties

    /// <summary>Number of published slots of the live generation (volatile read).</summary>
    internal int Count => _Current.Count;

    #endregion

    #region Public API

    /// <summary>Reads a published slot, or returns the unset sentinel when <paramref name="index"/> is not published.</summary>
    /// <param name="index">Slot index.</param>
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

                // Capacity check before any write. The checked sum only overflows for a huge span; that case is reported
                // as the same capacity error (InvalidValue is never a valid index) instead of a bare OverflowException.
                int last;
                try
                {
                    last = checked(index + values.Length - 1);
                }
                catch (OverflowException)
                {
                    Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(Ids.ArrayIndexIdRange.InvalidValue, "entry");
                    return;
                }

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
        int last;
        try
        {
            last = checked(startIndex + values.Length - 1);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                values.Length,
                "startIndex + values.Length - 1 overflows the valid index range.");
        }

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

    /// <summary>Drops every slot by swapping in an empty generation.</summary>
    internal void Clear()
    {
        // One volatile write replaces chunks and count together. Readers still on the old generation finish
        // on it; the GC reclaims it afterwards. A concurrent Append notices the swap and retries.
        // Rejected: resetting the count and clearing the chunks in place, which would let a reader pair a new
        // count with old chunk contents (or the reverse) and would need a lock or a version counter to detect it.
        _Current = new(_ChunkShift);
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

    #region Private helpers

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
    /// those are not supported (see the public type remarks). The branches are resolved per instantiation by the JIT,
    /// so only one remains in the compiled method.
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void _ThrowIndexOutOfRange(int index, int count) =>
        throw new ArgumentOutOfRangeException(
            nameof(index),
            index,
            $"Index must be in the range 0..{Math.Max(count - 1, 0).ToString(CultureInfo.InvariantCulture)} (published entries).");

    #endregion
}

/// <summary>
/// Thread-safety: one thread may call Append or AppendRange; a second append throws
/// InvalidOperationException. Get and TryGet of published slots are lock-free for reference
/// types and for blittable values of size 1, 2, 4, or 8 bytes. Concurrent readers of larger
/// structs, and of structs that contain references, are not supported. Clear may run during
/// Append; Append then retries onto the live prefix.
/// Grow-only chunked store: a packed prefix <c>0 .. Count-1</c> published by
/// <see cref="Append"/> / <see cref="AppendRange"/>. Lazy inner-chunk allocation; outer chunk
/// pointer array grows on demand via <see cref="ChunkedOuterArray{TChunk}"/>.
/// </summary>
/// <typeparam name="T">
/// Value stored per slot. Unset / unpublished slots hold the constructor sentinel
/// (<see langword="default"/> of <typeparamref name="T"/> unless the caller passes another one).
/// </typeparam>
/// <remarks>
/// <para>
/// Dense, index-addressed view of the shared slot engine (<c>ChunkedSlotStore</c>): values are read by index with
/// <see cref="Get"/> / <see cref="TryGet"/>. Chunking instead of one growing array means growth never copies existing
/// values and never moves them, so readers need no lock while the writer extends the store; a chunk is allocated lazily
/// on the first append that reaches it.
/// </para>
/// <para>
/// Chunks are allocated through <see cref="ChunkedOuterArray{TChunk}"/>. The single appender is its only
/// allocator, so a chunk it receives stays the live chunk of its index and a slot write needs no re-check.
/// </para>
/// <para>
/// <b>Single-writer <see cref="Append"/> / <see cref="AppendRange"/>:</b> Concurrent
/// <see cref="Append"/> / <see cref="AppendRange"/> throws <see cref="InvalidOperationException"/>.
/// Lock-free <see cref="Get"/> / <see cref="TryGet"/> of published slots <c>0 .. Count-1</c> is
/// supported for reference-type <typeparamref name="T"/> and for blittable primitives of size 1, 2,
/// 4, or 8 bytes (published with <see cref="Volatile"/>). Larger structs, and structs that contain
/// references, are assigned plainly; concurrent readers of those slots are not supported.
/// <see cref="Get"/> / <see cref="TryGet"/> of <c>index &gt;= Count</c> or an unallocated chunk
/// returns the unset sentinel / <see langword="false"/>.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> replaces the chunks and <see cref="Count"/> together with one empty
/// generation (one volatile write). A concurrent <see cref="Append"/> retries onto the live generation.
/// A concurrent <see cref="Get"/> / <see cref="TryGet"/> that already loaded the previous generation may
/// still return that snapshot; a load after <see cref="Clear"/> observes unset / false. An append costs
/// one interlocked operation (the single-writer gate); the publish is a volatile write of the count.
/// </para>
/// </remarks>
public sealed class ChunkedGrowOnlyStore<T>
{
    #region Fields

    /// <summary>Shared engine; this type only narrows its surface to the dense, index-addressed operations.</summary>
    private readonly ChunkedSlotStore<T> _Store;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array)
    /// and <see langword="default"/> as the unset slot value.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyStore(int chunkShift)
        : this(chunkShift, unsetValue: default!)
    {
    }

    /// <summary>
    /// Creates a store with the given chunk size and unset sentinel.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <param name="unsetValue">
    /// Value written into every slot of a newly allocated chunk when it differs from
    /// <see langword="default"/> of <typeparamref name="T"/>. Also returned by <see cref="Get"/>
    /// and <see cref="TryGet"/> when the index is invalid or the inner chunk is unallocated.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyStore(int chunkShift, T unsetValue) =>
        _Store = new(chunkShift, unsetValue);

    #endregion

    #region Public API

    /// <summary>Number of entries published by <see cref="Append"/> / <see cref="AppendRange"/>.</summary>
    public int Count => _Store.Count;

    /// <summary>Appends one entry at <see cref="Count"/>. Single-writer only.</summary>
    /// <param name="item">The entry to store; passed by <c>in</c> reference to avoid copying large structs.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>
    /// <see cref="Append"/> without the concurrent-writer check. The caller guarantees that every append and
    /// <see cref="Clear"/> of this store is serialized by the caller; <c>ValueCacheSeries</c> calls it while holding
    /// its series gate. Readers stay lock-free. The gate is skipped because the series already serializes its writers,
    /// so a second compare-exchange per appended value would be pure overhead; without that serialization the call is unsafe.
    /// </summary>
    /// <param name="item">The entry to store.</param>
    /// <exception cref="InvalidOperationException">The store reached <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendOwned(in T item) =>
        _Store.AppendOwned(in item);

    /// <summary>
    /// Appends <paramref name="values"/> as one publish (readers see all or none of them). Empty span is a no-op.
    /// </summary>
    /// <param name="values">Entries to append in order.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store would exceed
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void AppendRange(ReadOnlySpan<T> values) =>
        _Store.AppendRange(values);

    /// <summary>
    /// Reads the value at <paramref name="index"/>.
    /// An invalid index, an index <c>&gt;= Count</c>, or an unallocated chunk returns the
    /// unset sentinel from the constructor. That sentinel is null for a reference type
    /// only when the caller left it at the default.
    /// </summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The stored value, or the unset sentinel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    public T Get(int index) =>
        _Store.Get(index);

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// Returns <see langword="false"/> when the index is invalid, <c>index &gt;= Count</c>,
    /// or the inner chunk is unallocated.
    /// An allocated slot may still hold the unset sentinel.
    /// </summary>
    /// <param name="index">Slot index.</param>
    /// <param name="value">The stored value, or the unset sentinel on a miss.</param>
    public bool TryGet(int index, out T value) =>
        _Store.TryGet(index, out value);

    /// <summary>
    /// Drops all chunk references by swapping in an empty generation (readers that already loaded the old one finish on it).
    /// </summary>
    public void Clear() =>
        _Store.Clear();

    /// <summary>
    /// Returns a published inner chunk as a span clipped to the lesser of
    /// <paramref name="publishedCount"/> and <see cref="Count"/>.
    /// Readers pass their loaded committed count so staged-but-uncommitted slots stay hidden.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index.</param>
    /// <param name="publishedCount">Exclusive upper bound of readable slots (typically a series committed count).</param>
    /// <param name="span">Slice of the inner array covering published slots in this chunk.</param>
    /// <returns><see langword="true"/> when the chunk exists and overlaps the published range.</returns>
    public bool TryGetPublishedChunk(int chunkIndex, int publishedCount, out ReadOnlySpan<T> span) =>
        _Store.TryGetPublishedChunk(chunkIndex, publishedCount, out span);

    #endregion
}

/// <summary>
/// Thread-safety: one thread may call Append or AppendRange; a second append throws
/// InvalidOperationException. TryReadPublished and ItemRef of published slots are lock-free
/// for reference types and for blittable values of size 1, 2, 4, or 8 bytes. Concurrent readers
/// of larger structs, and of structs that contain references, are not supported. Clear may run
/// during Append; Append then retries onto the live prefix.
/// Packed append-only log of <typeparamref name="T"/> entries with lock-free readers.
/// Random-index slot writes are not available on this type; packed growth is
/// <see cref="Append"/> / <see cref="AppendRange"/> only.
/// </summary>
/// <typeparam name="T">Packed entry type.</typeparam>
/// <remarks>
/// <para>
/// Packed, ordered view of the shared slot engine (<c>ChunkedSlotStore</c>): entries are addressed by their position
/// and, when appended in ascending key order, found by <see cref="BinarySearch"/>. Readers get a copy of an entry
/// (<see cref="TryReadPublished"/>) or a reference into the live slot (<see cref="ItemRef"/>) so large entry structs
/// need not be copied to read one field.
/// </para>
/// <para>
/// One writer for <see cref="Count"/>; lock-free readers. The writer writes the live slot first, then
/// publishes with a volatile write of <see cref="Count"/>.
/// Concurrent <see cref="Append"/> / <see cref="AppendRange"/> throws <see cref="InvalidOperationException"/>.
/// Mutation through <see cref="ItemRef"/> is single-writer.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> replaces the chunks and <see cref="Count"/> together with one empty
/// generation (one volatile write). Packed readers read one generation; when <see cref="Clear"/>
/// replaced it before they return, they observe a miss (<c>-1</c> / <see langword="false"/>). They never
/// mix entries of the cleared log with entries appended after <see cref="Clear"/>. <see cref="ItemRef"/>
/// throws <see cref="ArgumentOutOfRangeException"/> when the index is not published in the live generation.
/// A concurrent <see cref="Append"/> retries onto the empty generation.
/// </para>
/// <para>
/// Reference-type fields inside a value-type <typeparamref name="T"/> that the writer mutates after
/// <see cref="Append"/> must be published by the caller with <see cref="Volatile"/> writes
/// on the live slot; readers must <see cref="ReadVolatileRefField{TField}"/> that location.
/// </para>
/// </remarks>
public sealed class ChunkedAppendOnlyStore<T>
{
    #region Fields

    /// <summary>Shared engine; this type only narrows its surface to the packed, append-only operations.</summary>
    private readonly ChunkedSlotStore<T> _Store;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array)
    /// and <see langword="default"/> as the unset slot value.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedAppendOnlyStore(int chunkShift)
        : this(chunkShift, unsetValue: default!)
    {
    }

    /// <summary>
    /// Creates a store with the given chunk size and unset sentinel.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <param name="unsetValue">Sentinel returned by miss paths.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedAppendOnlyStore(int chunkShift, T unsetValue) =>
        _Store = new(chunkShift, unsetValue);

    #endregion

    #region Public API

    /// <summary>
    /// Number of entries published by <see cref="Append"/>. Volatile read; acquires the packed prefix.
    /// </summary>
    public int Count => _Store.Count;

    /// <summary>
    /// Appends one entry to the packed prefix. Single-writer only.
    /// A concurrent <see cref="Clear"/> causes the publish to fail and this method retries from the live prefix.
    /// </summary>
    /// <param name="item">
    /// Entry stored at the current <see cref="Count"/>. When <see cref="BinarySearch"/> is used,
    /// keys must be appended in strictly ascending order.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>
    /// Appends every value in <paramref name="values"/> to the packed prefix as one publish.
    /// Single-writer only. Concurrent <see cref="Append"/> or <see cref="AppendRange"/> throws.
    /// The span length is independent of the inner chunk size. An empty span is a no-op.
    /// A concurrent <see cref="Clear"/> causes the publish to fail and this method retries from the live prefix.
    /// </summary>
    /// <param name="values">Entries to append in order; readers see all or none of them.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store would exceed
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void AppendRange(ReadOnlySpan<T> values) =>
        _Store.AppendRange(values);

    /// <summary>
    /// Returns a reference to the packed entry at <paramref name="index"/>. Readers must treat the
    /// reference as read-only; only the single writer may mutate through it.
    /// </summary>
    /// <param name="index">Published entry index (0 … <see cref="Count"/> − 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not published.</exception>
    public ref T ItemRef(int index) =>
        ref _Store.ItemRef(index);

    /// <summary>
    /// Copies the packed entry at <paramref name="index"/> when it is still published in the live generation.
    /// Returns <see langword="false"/> when the index is outside <see cref="Count"/>, the chunk is
    /// missing, or <see cref="Clear"/> raced.
    /// </summary>
    /// <param name="index">Published entry index.</param>
    /// <param name="value">A copy of the entry, or the unset sentinel on a miss.</param>
    public bool TryReadPublished(int index, out T value) =>
        _Store.TryReadPublished(index, out value);

    /// <summary>
    /// Binary-searches the packed prefix for the entry whose sort key equals
    /// <paramref name="sortKey"/>. Valid only when entries were appended in strictly ascending
    /// key order. Returns <c>-1</c> when the key is missing or <see cref="Clear"/> raced.
    /// </summary>
    /// <param name="sortKey">Key to locate.</param>
    /// <param name="getSortKey">
    /// Extracts the sort key from an entry. Pass a cached static delegate; do not allocate per call.
    /// </param>
    /// <returns>Entry index, or <c>-1</c> when no published entry has the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getSortKey"/> is <see langword="null"/>.</exception>
    public int BinarySearch(int sortKey, GetSortKey<T> getSortKey) =>
        _Store.BinarySearch(sortKey, getSortKey);

    /// <summary>
    /// <see cref="Volatile"/> read of a class field on the live packed slot.
    /// Use this instead of copying the surrounding struct when the field is published with
    /// <see cref="Volatile"/> writes after <see cref="Append"/>.
    /// </summary>
    /// <typeparam name="TField">Class type of the field.</typeparam>
    /// <param name="index">Published entry index.</param>
    /// <param name="getter">Returns a reference to the field inside the live slot; pass a cached static delegate.</param>
    /// <returns>The field, or <see langword="null"/> when the index is unpublished or <see cref="Clear"/> raced.</returns>
    public TField? ReadVolatileRefField<TField>(int index, GetRefField<T, TField> getter)
        where TField : class? =>
        _Store.ReadVolatileRefField(index, getter);

    /// <summary>
    /// Drops all chunk references and resets the packed prefix in one generation swap. Concurrent packed
    /// readers that loaded the pre-clear generation miss; they do not observe the refilled log under that snapshot.
    /// </summary>
    public void Clear() =>
        _Store.Clear();

    #endregion
}

/// <summary>
/// Packed-prefix helpers for <see cref="ChunkedAppendOnlyStore{T}"/> entries that expose
/// <see cref="ISortKeyed"/>.
/// </summary>
public static class ChunkedAppendOnlyStoreExtensions
{
    #region Public API

    /// <summary>
    /// Binary-searches the packed prefix for the entry whose <see cref="ISortKeyed.SortKey"/>
    /// equals <paramref name="sortKey"/>. Valid only when entries were appended in strictly
    /// ascending key order.
    /// </summary>
    /// <typeparam name="T">Entry type exposing its key through <see cref="ISortKeyed"/>.</typeparam>
    /// <param name="store">The store to search.</param>
    /// <param name="sortKey">Key to locate.</param>
    /// <returns>Entry index, or <c>-1</c> when no published entry has the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public static int BinarySearch<T>(this ChunkedAppendOnlyStore<T> store, int sortKey)
        where T : ISortKeyed
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.BinarySearch(sortKey, SortKeyCache<T>.Getter);
    }

    #endregion

    #region Nested types

    private static class SortKeyCache<T>
        where T : ISortKeyed
    {
        /// <summary>
        /// Cached sort-key getter so packed binary search does not allocate a delegate per call (a per-call lambda or
        /// method-group conversion would allocate on every search). One instance per entry type, created on first use.
        /// </summary>
        public static readonly GetSortKey<T> Getter = static (in T item) => item.SortKey;
    }

    #endregion
}

/// <summary>
/// Dense-store helpers for <see cref="ChunkedGrowOnlyStore{T}"/> reference entries.
/// </summary>
public static class ChunkedGrowOnlyStoreExtensions
{
    #region Public API

    /// <summary>
    /// Reads a contiguous range into <paramref name="buffer"/>.
    /// Negative <paramref name="fromIndex"/> offsets produce <see langword="null"/> entries in the
    /// prefix of <paramref name="buffer"/> only; indices past
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/> stop the copy.
    /// Each slot is one lock-free <see cref="ChunkedGrowOnlyStore{T}.Get"/>, so a concurrent writer may be
    /// observed part-way through the range: slots below the published count at the time of their read are returned.
    /// </summary>
    /// <typeparam name="T">Reference entry type; unset slots read as <see langword="null"/>.</typeparam>
    /// <param name="store">The store to read.</param>
    /// <param name="fromIndex">Index of the first slot; may be negative to pad the start of the buffer with holes.</param>
    /// <param name="buffer">Destination; the method never writes beyond its length.</param>
    /// <returns>Number of slots written (may include <see langword="null"/> holes).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public static int ReadRange<T>(this ChunkedGrowOnlyStore<T> store, int fromIndex, Span<T?> buffer)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(store);

        int count = 0;
        int start = fromIndex;
        int bufferOffset = 0;

        // Negative start: the leading part of the buffer corresponds to indices below zero, which do not exist, so
        // it is filled with null holes and the copy starts at index 0 behind them.
        if (start < 0)
        {
            // -int.MinValue overflows in int. The prefix is entirely holes for any
            // buffer this method can fill.
            long magnitude = -(long)start;
            int holes = magnitude >= buffer.Length ? buffer.Length : (int)magnitude;
            for (int h = 0; h < holes; h++)
            {
                buffer[h] = null;
            }

            count = holes;
            bufferOffset = holes;
            start = 0;
            if (bufferOffset == buffer.Length)
            {
                return count;
            }
        }

        // A start above MaxValue has no slots at all. Without this check MaxValue - start is negative, the unsigned compare
        // below never breaks, the buffer is filled with null holes and reported as read (and start + offset could overflow).
        if (start > Ids.ArrayIndexIdRange.MaxValue)
        {
            return count;
        }

        // Copy the remaining buffer. For start <= MaxValue the unsigned compare stops before start + offset can exceed
        // MaxValue (and so before the int addition below could overflow).
        for (int i = bufferOffset; i < buffer.Length; i++)
        {
            int offset = i - bufferOffset;
            if ((uint)offset > (uint)(Ids.ArrayIndexIdRange.MaxValue - start))
            {
                break;
            }

            int idx = start + offset;
            buffer[i] = store.Get(idx);
            count++;
        }

        return count;
    }

    #endregion
}

/// <summary>
/// Shared outer-chunk growth helper. Each outer slot holds one inner chunk (e.g. <c>Frame[]</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safety:</b> one allocator, any number of readers, no locks. Calls of <see cref="GetOrAllocateChunk"/>
/// that allocate and calls of <see cref="Clear"/> must not overlap; they may come from different threads one after
/// the other. <see cref="GetChunk"/>, and <see cref="GetOrAllocateChunk"/> for an installed chunk, may run on any
/// number of threads at any time, also while the allocator grows the outer array or installs a chunk. Growth copies
/// the outer array and publishes the copy with one volatile write; a chunk is installed with one volatile write into
/// the live outer array. A reader sees the old or the new outer array, and each holds every chunk installed before
/// it was published.
/// </para>
/// <para>
/// <b>Why one allocator:</b> copy-then-publish growth cannot absorb a concurrent install. A chunk written into the
/// old array after the copy and before the publish would be lost. Every user allocates from one thread at a time
/// (the chunked stores under their single-writer gate, <c>CachedFrameSource</c> from its source thread), so the
/// contract costs nothing and keeps reads and installs free of locks.
/// </para>
/// <para>
/// <b>Misuse detection:</b> the allocating path takes a compare-exchange gate and throws
/// <see cref="InvalidOperationException"/> when a second allocating call or <see cref="Clear"/> overlaps it. It is a
/// misuse detector, not a lock: nobody waits, an installed chunk is returned without touching it, and an overlap
/// fails loudly instead of losing a chunk. Gate entry (full fence) and release (volatile write) also order calls that
/// follow one another on different threads.
/// </para>
/// <para>
/// <see cref="Clear"/> replaces the outer array with an empty one; a concurrent <see cref="GetChunk"/> may still
/// return a chunk of the replaced array. The chunked stores do not call <see cref="Clear"/>: they replace a whole
/// generation (a new outer array plus its count), so a reader never pairs a count with chunks of another generation.
/// </para>
/// </remarks>
public sealed class ChunkedOuterArray<TChunk> where TChunk : class
{
    #region Constants

    // Allowed range of the caller-chosen chunk shift: 4 to 20, i.e. 16 to 1 048 576 slots per chunk. These are fixed
    // limits of this type (a caller tunes the shift, not the limits); the constructor rejects anything outside.
    private const int _MinChunkShift = 4;
    private const int _MaxChunkShift = 20;

    #endregion

    #region Fields

    /// <summary>
    /// Number of outer slots needed to address every valid index: <c>(MaxValue >> ChunkShift) + 1</c>. The outer array
    /// never grows beyond this, which also bounds <see cref="GetOrAllocateChunk"/> against a hostile chunk index.
    /// </summary>
    private readonly int _MaxOuterChunks;

    /// <summary>
    /// The outer array. <see langword="volatile"/> because <c>_Grow</c> and <see cref="Clear"/> publish a new array
    /// object that readers pick up without a lock; elements are read and written with <see cref="Volatile"/>.
    /// </summary>
    private volatile TChunk?[] _Chunks;

    /// <summary>Single-allocator gate: 1 while an allocating call or <see cref="Clear"/> runs. A misuse detector, not a lock.</summary>
    private volatile int _AllocationGate;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates an outer-chunk array with the given chunk size (<c>1 &lt;&lt; chunkShift</c> indices per inner chunk).
    /// </summary>
    /// <param name="chunkShift">Log₂ of indices per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedOuterArray(int chunkShift)
    {
        if ((uint)(chunkShift - _MinChunkShift) > (uint)(_MaxChunkShift - _MinChunkShift))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkShift),
                chunkShift,
                $"Chunk shift must be between {_MinChunkShift.ToString(CultureInfo.InvariantCulture)} " +
                $"and {_MaxChunkShift.ToString(CultureInfo.InvariantCulture)}.");
        }

        ChunkShift = chunkShift;
        ChunkSize = 1 << chunkShift;
        ChunkMask = ChunkSize - 1;
        _MaxOuterChunks = (Ids.ArrayIndexIdRange.MaxValue >> chunkShift) + 1;
        _Chunks = [];
    }

    #endregion

    #region Properties

    /// <summary>Log₂ of indices per inner chunk.</summary>
    public int ChunkShift { get; }

    /// <summary>Number of indices per inner chunk (<c>1 &lt;&lt; ChunkShift</c>).</summary>
    public int ChunkSize { get; }

    /// <summary>Bitmask for the slot index within a chunk (<c>ChunkSize - 1</c>).</summary>
    public int ChunkMask { get; }

    #endregion

    #region Public API

    /// <summary>
    /// Splits a dense index into outer chunk index and inner slot index with one shift and one mask (the chunk size is
    /// a power of two, which is why no division is needed).
    /// </summary>
    /// <param name="index">Non-negative dense index; not validated (hot path, callers check the index range first).</param>
    public (int ChunkIndex, int SlotIndex) DecomposeIndex(int index) =>
        (index >> ChunkShift, index & ChunkMask);

    /// <summary>Returns the inner chunk at <paramref name="chunkIndex"/>, or <see langword="null"/> when unallocated.</summary>
    /// <param name="chunkIndex">Outer index; a negative or beyond-the-current-array value is simply "unallocated".</param>
    public TChunk? GetChunk(int chunkIndex)
    {
        // Load the array once: a concurrent growth swaps in a longer copy, and bounds check and element read must
        // refer to the same array.
        TChunk?[] chunks = _Chunks;
        if ((uint)chunkIndex >= (uint)chunks.Length)
        {
            return null;
        }

        // Acquire read pairs with the release write that installed the chunk, so a non-null chunk is fully constructed.
        return Volatile.Read(ref chunks[chunkIndex]);
    }

    /// <summary>
    /// Returns the inner chunk at <paramref name="chunkIndex"/>, allocating it with
    /// <paramref name="factory"/> when absent.
    /// </summary>
    /// <param name="chunkIndex">Outer index of the chunk.</param>
    /// <param name="factory">Creates a new chunk; called at most once per call and only when the chunk is absent.</param>
    /// <remarks>
    /// An installed chunk is returned without calling the factory and without touching the allocation gate; this is
    /// the per-item path of the stores and may run on any thread. Otherwise the call allocates: it takes the gate,
    /// grows the outer array when needed, calls the factory once, and installs the result. Allocating calls must not
    /// overlap (see the type remarks). The factory must not call back into this instance; such a call overlaps the
    /// running one. A factory exception propagates; the array then keeps any growth but no new chunk.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="chunkIndex"/> is negative or not less than the maximum outer-chunk count
    /// for this <see cref="ChunkShift"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Another allocating call or <see cref="Clear"/> runs at the same time (single-allocator contract).
    /// </exception>
    public TChunk GetOrAllocateChunk(int chunkIndex, Func<TChunk> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if ((uint)chunkIndex >= (uint)_MaxOuterChunks)
        {
            _ThrowChunkIndexOutOfRange(chunkIndex);
        }

        // Installed chunk: lock-free read, no gate, no factory.
        TChunk? existing = GetChunk(chunkIndex);
        if (existing is not null)
        {
            return existing;
        }

        return _AllocateChunk(chunkIndex, factory);
    }

    /// <summary>Drops all chunk references.</summary>
    /// <exception cref="InvalidOperationException">An allocating <see cref="GetOrAllocateChunk"/> call runs at the same time.</exception>
    public void Clear()
    {
        _EnterAllocationGate();
        try
        {
            _Chunks = [];
        }
        finally
        {
            _AllocationGate = 0;
        }
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Allocating path of <see cref="GetOrAllocateChunk"/>: grows the outer array when needed and installs a new chunk.
    /// Rare (once per chunk), so it is kept out of line.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TChunk _AllocateChunk(int chunkIndex, Func<TChunk> factory)
    {
        _EnterAllocationGate();
        try
        {
            // Re-read under the gate: a call that finished before this one entered may have installed the chunk.
            TChunk?[] chunks = _Chunks;
            if ((uint)chunkIndex >= (uint)chunks.Length)
            {
                chunks = _Grow(chunks, chunkIndex);
            }

            TChunk? installed = chunks[chunkIndex];
            if (installed is not null)
            {
                return installed;
            }

            // Install with a volatile write (release): a reader that sees the chunk sees it fully constructed. No
            // compare-exchange is needed, because nothing else writes slots or the outer array while the gate is held.
            TChunk created = factory();
            Volatile.Write(ref chunks[chunkIndex], created);
            return created;
        }
        finally
        {
            _AllocationGate = 0;
        }
    }

    /// <summary>
    /// Copies <paramref name="chunks"/> into an outer array long enough for <paramref name="chunkIndex"/> (at least
    /// doubled, at most <see cref="_MaxOuterChunks"/>) and publishes it. Caller holds the allocation gate, so no install
    /// can land in <paramref name="chunks"/> between the copy and the publish.
    /// </summary>
    private TChunk?[] _Grow(TChunk?[] chunks, int chunkIndex)
    {
        // chunks.Length <= chunkIndex < _MaxOuterChunks <= (int.MaxValue >> 4) + 1, so the doubling cannot overflow.
        int doubled = chunks.Length == 0 ? 1 : chunks.Length * 2;
        int newLength = Math.Min(Math.Max(chunkIndex + 1, doubled), _MaxOuterChunks);
        TChunk?[] resized = new TChunk[newLength];
        Array.Copy(chunks, resized, chunks.Length);

        // Volatile write (release): a reader that loads the new array sees every copied chunk.
        _Chunks = resized;
        return resized;
    }

    /// <summary>
    /// Takes the single-allocator gate. A misuse detector, not a lock: an overlapping allocating call or
    /// <see cref="Clear"/> is a caller bug and gets <see cref="InvalidOperationException"/>; nobody waits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EnterAllocationGate()
    {
        if (Interlocked.CompareExchange(ref _AllocationGate, 1, 0) != 0)
        {
            _ThrowConcurrentAllocation();
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowConcurrentAllocation() =>
        throw new InvalidOperationException(
            "Concurrent allocation is not supported: allocating GetOrAllocateChunk calls and Clear must not overlap.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowChunkIndexOutOfRange(int chunkIndex) =>
        throw new ArgumentOutOfRangeException(
            nameof(chunkIndex),
            chunkIndex,
            $"Chunk index must be in the range 0..{(_MaxOuterChunks - 1).ToString(CultureInfo.InvariantCulture)}.");

    #endregion
}
