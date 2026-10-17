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
        internal readonly ChunkedOuterArray<T[]> Outer = new(chunkShift);
        internal volatile int Count;
    }

    #endregion

    #region Fields

    private readonly int _ChunkShift;
    private readonly int _ChunkSize;
    private readonly T _UnsetValue;
    private readonly bool _FillUnset;
    private readonly Func<T[]> _ChunkFactory;

    private volatile Generation _Current;
    private volatile int _AppendGate;

    #endregion

    #region Constructors

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

    internal int Count => _Current.Count;

    #endregion

    #region Public API

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

    internal void Append(in T item)
    {
        _EnterAppendGate();
        try
        {
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

    private void _SetRange(Generation generation, int startIndex, ReadOnlySpan<T> values)
    {
        if (values.Length == 0)
        {
            return;
        }

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
        ChunkedOuterArray<T[]> outer = generation.Outer;
        int offset = 0;
        while (offset < values.Length)
        {
            int index = startIndex + offset;
            (int chunkIdx, int slotIdx) = outer.DecomposeIndex(index);
            T[] chunk = outer.GetOrAllocateChunk(chunkIdx, _ChunkFactory);
            int run = Math.Min(outer.ChunkSize - slotIdx, values.Length - offset);
            ReadOnlySpan<T> slice = values.Slice(offset, run);
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

    internal bool TryReadPublished(int index, out T value)
    {
        // A generation replaced before this read returns is a miss, not a hit from the cleared log
        // (the contract the packed epoch gave before).
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
            int mid = lo + ((hi - lo) >> 1);
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

        TField? value = Volatile.Read(ref getter(ref chunk[slotIdx]));
        if (!ReferenceEquals(_Current, generation))
        {
            return null;
        }

        return value;
    }

    internal void Clear()
    {
        // One volatile write replaces chunks and count together. Readers still on the old generation finish
        // on it; the GC reclaims it afterwards. A concurrent Append notices the swap and retries.
        _Current = new(_ChunkShift);
    }

    internal bool TryGetPublishedChunk(int chunkIndex, int publishedCount, out ReadOnlySpan<T> span)
    {
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void _ThrowConcurrentAppend() =>
        throw new InvalidOperationException("Concurrent Append is not supported.");

    private T[] _CreateChunk()
    {
        T[] chunk = new T[_ChunkSize];
        if (_FillUnset)
        {
            Array.Fill(chunk, _UnsetValue);
        }

        return chunk;
    }

    private void _WriteLiveSlot(Generation generation, int index, T value)
    {
        // The generation is the only allocator of its outer array and never clears it, so the returned chunk stays
        // the live chunk of its index (single-allocator contract of ChunkedOuterArray); no re-check is needed.
        ChunkedOuterArray<T[]> outer = generation.Outer;
        (int chunkIdx, int slotIdx) = outer.DecomposeIndex(index);
        T[] chunk = outer.GetOrAllocateChunk(chunkIdx, _ChunkFactory);
        _WriteSlot(ref chunk[slotIdx], value);
    }

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
/// (default is <see langword="default"/> of <typeparamref name="T"/>).
/// </typeparam>
/// <remarks>
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
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>Appends <paramref name="values"/> as one publish. Empty span is a no-op.</summary>
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
    public bool TryGet(int index, out T value) =>
        _Store.TryGet(index, out value);

    /// <summary>Drops all chunk references.</summary>
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
    /// <returns>Entry index, or <c>-1</c> when no published entry has the key.</returns>
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
        /// <summary>Cached sort-key getter so packed binary search does not allocate a delegate per call.</summary>
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
    /// </summary>
    /// <returns>Number of slots written (may include <see langword="null"/> holes).</returns>
    public static int ReadRange<T>(this ChunkedGrowOnlyStore<T> store, int fromIndex, Span<T?> buffer)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(store);

        int count = 0;
        int start = fromIndex;
        int bufferOffset = 0;

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

    private const int _MinChunkShift = 4;
    private const int _MaxChunkShift = 20;

    #endregion

    #region Fields

    private readonly int _MaxOuterChunks;

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

    /// <summary>Splits a dense index into outer chunk index and inner slot index.</summary>
    public (int ChunkIndex, int SlotIndex) DecomposeIndex(int index) =>
        (index >> ChunkShift, index & ChunkMask);

    /// <summary>Returns the inner chunk at <paramref name="chunkIndex"/>, or <see langword="null"/> when unallocated.</summary>
    public TChunk? GetChunk(int chunkIndex)
    {
        TChunk?[] chunks = _Chunks;
        if ((uint)chunkIndex >= (uint)chunks.Length)
        {
            return null;
        }

        return Volatile.Read(ref chunks[chunkIndex]);
    }

    /// <summary>
    /// Returns the inner chunk at <paramref name="chunkIndex"/>, allocating it with
    /// <paramref name="factory"/> when absent.
    /// </summary>
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
