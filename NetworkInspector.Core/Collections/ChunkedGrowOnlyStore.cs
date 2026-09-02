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
internal sealed class ChunkedSlotStore<T>
{
    #region Fields

    private readonly ChunkedOuterArray<T[]> _Outer;
    private readonly T _UnsetValue;
    private readonly bool _FillUnset;
    private readonly Func<T[]> _ChunkFactory;

    private volatile int _Count;
    private volatile int _Epoch;
    private volatile int _AppendGate;

    #endregion

    #region Constructors

    internal ChunkedSlotStore(int chunkShift, T unsetValue)
    {
        _Outer = new(chunkShift);
        _UnsetValue = unsetValue;
        _FillUnset = !EqualityComparer<T>.Default.Equals(unsetValue, default!);
        _ChunkFactory = _CreateChunk;
    }

    #endregion

    #region Properties

    internal int Count => _Count;

    #endregion

    #region Public API

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int index, T value)
    {
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(index, nameof(index));
        _WriteLiveSlot(index, value);
    }

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
        if (!Ids.ArrayIndexIdRange.IsValidIndex(index))
        {
            value = _UnsetValue;
            return false;
        }

        (int chunkIdx, int slotIdx) = _Outer.DecomposeIndex(index);
        T[]? chunk = _Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            value = _UnsetValue;
            return false;
        }

        value = _ReadSlot(ref chunk[slotIdx]);
        return true;
    }

    internal void Append(in T item)
    {
        if (Interlocked.CompareExchange(ref _AppendGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent Append is not supported.");
        }

        try
        {
            while (true)
            {
                int index = _Count;
                Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(index, "entry");
                _WriteLiveSlot(index, item);
                if (Interlocked.CompareExchange(ref _Count, index + 1, index) == index)
                {
                    return;
                }
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _AppendGate, 0);
        }
    }

    internal ref T ItemRef(int index)
    {
        if ((uint)index >= (uint)_Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return ref _ItemRefUnchecked(index);
    }

    internal bool TryReadPublished(int index, out T value)
    {
        int epoch = _Epoch;
        if (_PackedSnapshotInvalid(epoch)
            || (uint)index >= (uint)_Count
            || !TryGet(index, out value)
            || _PackedSnapshotInvalid(epoch))
        {
            value = _UnsetValue;
            return false;
        }

        return true;
    }

    internal int BinarySearch(int sortKey, GetSortKey<T> getSortKey)
    {
        ArgumentNullException.ThrowIfNull(getSortKey);

        int epoch = _Epoch;
        if ((epoch & 1) != 0)
        {
            return -1;
        }

        int count = _Count;
        int lo = 0;
        int hi = count - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if ((uint)mid >= (uint)count || !_TryReadPublishedUnchecked(mid, out T published))
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

        if (found >= 0
            && ((uint)found >= (uint)count
                || !_TryReadPublishedUnchecked(found, out T check)
                || getSortKey(in check) != sortKey))
        {
            found = -1;
        }

        if (_PackedSnapshotInvalid(epoch))
        {
            return -1;
        }

        return found;
    }

    internal TField? ReadVolatileRefField<TField>(int index, GetRefField<T, TField> getter)
        where TField : class?
    {
        ArgumentNullException.ThrowIfNull(getter);

        int epoch = _Epoch;
        if (_PackedSnapshotInvalid(epoch) || (uint)index >= (uint)_Count)
        {
            return null;
        }

        (int chunkIdx, int slotIdx) = _Outer.DecomposeIndex(index);
        T[]? chunk = _Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            return null;
        }

        TField? value = Volatile.Read(ref getter(ref chunk[slotIdx]));
        if (_PackedSnapshotInvalid(epoch))
        {
            return null;
        }

        return value;
    }

    internal void Clear()
    {
        // Seqlock: odd epoch means Clear is in progress; even means a stable prefix.
        _ = Interlocked.Increment(ref _Epoch);
        Interlocked.Exchange(ref _Count, 0);
        _Outer.Clear();
        _ = Interlocked.Increment(ref _Epoch);
    }

    internal bool TryGetPublishedChunk(int chunkIndex, int publishedCount, out ReadOnlySpan<T> span)
    {
        if (publishedCount <= 0 || chunkIndex < 0)
        {
            span = default;
            return false;
        }

        int shift = _Outer.ChunkShift;
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

        T[]? chunk = _Outer.GetChunk(chunkIndex);
        if (chunk is null)
        {
            span = default;
            return false;
        }

        int length = Math.Min(_Outer.ChunkSize, publishedCount - start);
        span = chunk.AsSpan(0, length);
        return true;
    }

    #endregion

    #region Private helpers

    private T[] _CreateChunk()
    {
        T[] chunk = new T[_Outer.ChunkSize];
        if (_FillUnset)
        {
            Array.Fill(chunk, _UnsetValue);
        }

        return chunk;
    }

    private void _WriteLiveSlot(int index, T value)
    {
        (int chunkIdx, int slotIdx) = _Outer.DecomposeIndex(index);
        while (true)
        {
            T[] chunk = _Outer.GetOrAllocateChunk(chunkIdx, _ChunkFactory);
            _WriteSlot(ref chunk[slotIdx], value);
            if (ReferenceEquals(_Outer.GetChunk(chunkIdx), chunk))
            {
                return;
            }
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryReadPublishedUnchecked(int index, out T value)
    {
        (int chunkIdx, int slotIdx) = _Outer.DecomposeIndex(index);
        T[]? chunk = _Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            value = _UnsetValue;
            return false;
        }

        value = _ReadSlot(ref chunk[slotIdx]);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T _ItemRefUnchecked(int index)
    {
        (int chunkIdx, int slotIdx) = _Outer.DecomposeIndex(index);
        T[]? chunk = _Outer.GetChunk(chunkIdx);
        if (chunk is null)
        {
            _ThrowIndexOutOfRange(index);
        }

        return ref chunk[slotIdx];
    }

    /// <summary>
    /// Packed seqlock: odd epoch means <see cref="Clear"/> is in progress; a changed epoch means the
    /// sampled prefix is no longer the live one. Either case is a miss, not a hit from a new generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _PackedSnapshotInvalid(int epoch) =>
        (epoch & 1) != 0 || _Epoch != epoch;

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private void _ThrowIndexOutOfRange(int index) =>
        throw new ArgumentOutOfRangeException(
            nameof(index),
            index,
            $"Index must be in the range 0..{Math.Max(_Count - 1, 0).ToString(CultureInfo.InvariantCulture)} (published entries).");

    #endregion
}

/// <summary>
/// Grow-only chunked store for dense non-negative integer keys up to <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
/// Lazy inner-chunk allocation; outer chunk pointer array grows on demand via
/// <see cref="ChunkedOuterArray{TChunk}"/>.
/// </summary>
/// <typeparam name="T">
/// Value stored per slot. Unset dense slots hold the constructor sentinel
/// (default is <see langword="default"/> of <typeparamref name="T"/>).
/// </typeparam>
/// <remarks>
/// <para>
/// Chunk allocation uses <see cref="Interlocked.CompareExchange{T}"/> on
/// <see cref="ChunkedOuterArray{TChunk}"/>. Slot writes retry until the inner chunk is still the
/// live chunk, so a concurrent outer-array grow cannot drop a completed write.
/// </para>
/// <para>
/// <b>Dense <see cref="Set"/> / <see cref="Get"/>:</b> Concurrent disjoint writers (one index per
/// thread) and concurrent readers are supported for reference-type <typeparamref name="T"/> and
/// for blittable primitives of size 1, 2, 4, or 8 bytes (published with <see cref="Volatile"/>).
/// Larger structs, and structs that contain references, are assigned plainly; concurrent dense
/// readers of those slots are not supported.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> publishes an empty outer array. Concurrent <see cref="Set"/> retries
/// onto the live array. A concurrent <see cref="Get"/> / <see cref="TryGet"/> that already sampled
/// the previous outer array may still return that snapshot; a load that samples the outer array
/// after <see cref="Clear"/> observes unset / false.
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

    /// <summary>Stores a value at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, T value) =>
        _Store.Set(index, value);

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// For reference-type <typeparamref name="T"/>, missing/invalid/unallocated slots return
    /// <see langword="null"/>. For value-type <typeparamref name="T"/>, they return the constructor
    /// unset sentinel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    public T Get(int index) =>
        _Store.Get(index);

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// Returns <see langword="false"/> when the index is invalid or the inner chunk is unallocated.
    /// An allocated slot may still hold the unset sentinel.
    /// </summary>
    public bool TryGet(int index, out T value) =>
        _Store.TryGet(index, out value);

    /// <summary>Drops all chunk references.</summary>
    public void Clear() =>
        _Store.Clear();

    /// <summary>
    /// Returns a published inner chunk as a span clipped to <paramref name="publishedCount"/>.
    /// Readers pass their loaded committed count, not a packed store <c>Count</c>
    /// (dense <see cref="Set"/> does not publish a packed prefix).
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
/// Packed append-only log of <typeparamref name="T"/> entries with lock-free readers.
/// Dense <see cref="ChunkedGrowOnlyStore{T}.Set"/> is not available on this type.
/// </summary>
/// <typeparam name="T">Packed entry type.</typeparam>
/// <remarks>
/// <para>
/// One writer for <see cref="Count"/>; lock-free readers. The writer writes the live slot first, then
/// publishes with <see cref="Interlocked.CompareExchange(ref int, int, int)"/> of <see cref="Count"/>.
/// Concurrent <see cref="Append"/> throws <see cref="InvalidOperationException"/>.
/// Mutation through <see cref="ItemRef"/> is single-writer.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> seqlock on a packed epoch — odd means mutation in progress, even
/// means a stable prefix. Packed readers that sample an odd epoch, or whose epoch changes before
/// they return, observe a miss (<c>-1</c> / <see langword="false"/>). They do not return
/// post-refill entries under a stale snapshot. <see cref="ItemRef"/> throws
/// <see cref="ArgumentOutOfRangeException"/> when the index is no longer published.
/// A concurrent <see cref="Append"/> retries against the empty prefix.
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
    /// Concurrent <see cref="Append"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>
    /// Returns a reference to the packed entry at <paramref name="index"/>. Readers must treat the
    /// reference as read-only; only the single writer may mutate through it.
    /// </summary>
    /// <param name="index">Published entry index (0 … <see cref="Count"/> − 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not published.</exception>
    public ref T ItemRef(int index) =>
        ref _Store.ItemRef(index);

    /// <summary>
    /// Copies the packed entry at <paramref name="index"/> when it is still published in the same epoch.
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
    /// Drops all chunk references and resets the packed prefix. Concurrent readers that sampled a
    /// pre-clear epoch, or that observe an in-progress (odd) epoch, miss; they do not observe the
    /// refilled log under that snapshot.
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
            int holes = Math.Min(-start, buffer.Length);
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
/// Grow-only chunked store for packed <see cref="long"/> values (e.g. PacketId → frame mapping).
/// Unset slots contain a configurable sentinel (default <c>-1</c>).
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> Same dense concurrent model as <see cref="ChunkedGrowOnlyStore{T}"/>
/// for <see cref="long"/> slots (<see cref="Volatile"/> writes and reads).
/// A concurrent <see cref="TryGet"/> that already sampled the previous outer array may still
/// return that snapshot; a load after <see cref="Clear"/> observes unset. Concurrent
/// <see cref="Set"/> retries onto the live outer array.
/// </remarks>
public sealed class ChunkedGrowOnlyLongStore
{
    #region Fields

    private readonly ChunkedGrowOnlyStore<long> _Store;
    private readonly long _UnsetValue;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array).
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <param name="unsetValue">Sentinel written to unallocated slots (default <c>-1</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyLongStore(int chunkShift, long unsetValue = -1L)
    {
        _UnsetValue = unsetValue;
        _Store = new(chunkShift, unsetValue);
    }

    #endregion

    #region Public API

    /// <summary>Stores a value at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public void Set(int index, long value) =>
        _Store.Set(index, value);

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// Returns <see langword="false"/> when the slot is unset or the index is out of range.
    /// </summary>
    public bool TryGet(int index, out long value)
    {
        if (!_Store.TryGet(index, out value) || value == _UnsetValue)
        {
            value = _UnsetValue;
            return false;
        }

        return true;
    }

    /// <summary>Drops all chunk references.</summary>
    public void Clear() =>
        _Store.Clear();

    #endregion
}

/// <summary>
/// Shared outer-chunk growth helper. Each outer slot holds one inner chunk (e.g. <c>Frame[]</c>).
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> Concurrent disjoint chunk allocation and concurrent readers are supported.
/// Outer-array growth uses <see cref="Interlocked.CompareExchange{T}"/> with retry; inner-chunk
/// installation uses <see cref="Interlocked.CompareExchange{T}"/>. A losing inner-chunk race parks
/// the factory-produced chunk as a spare for the next allocation. <see cref="Clear"/> replaces the
/// outer array with empty; concurrent <see cref="GetChunk"/> may then observe
/// <see langword="null"/>, and <see cref="GetOrAllocateChunk"/> retries onto the live array.
/// Packed stores zero count and bump the packed epoch (odd while clearing, even when stable)
/// before calling this method so packed readers do not treat a dropped chunk as a published slot.
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
    private volatile TChunk? _Spare;

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
    /// The factory runs only after a null slot is observed and no spare is available. A losing
    /// install <see cref="Interlocked.CompareExchange{T}"/> parks the unused chunk for reuse.
    /// A chunk that won the slot CAS and then lost the live-outer check is not spared.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="chunkIndex"/> is negative or not less than the maximum outer-chunk count
    /// for this <see cref="ChunkShift"/>.
    /// </exception>
    public TChunk GetOrAllocateChunk(int chunkIndex, Func<TChunk> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        TChunk? localSpare = null;
        try
        {
            while (true)
            {
                _EnsureOuterCapacity(chunkIndex);

                TChunk?[] chunks = _Chunks;
                if ((uint)chunkIndex >= (uint)chunks.Length)
                {
                    continue;
                }

                TChunk? chunk = Volatile.Read(ref chunks[chunkIndex]);
                if (chunk is not null)
                {
                    return chunk;
                }

                TChunk created = localSpare ?? Interlocked.Exchange(ref _Spare, null) ?? factory();
                localSpare = null;
                TChunk? previous = Interlocked.CompareExchange(ref chunks[chunkIndex], created, null);
                if (previous is null)
                {
                    TChunk?[] live = _Chunks;
                    if ((uint)chunkIndex < (uint)live.Length
                        && ReferenceEquals(Volatile.Read(ref live[chunkIndex]), created))
                    {
                        return created;
                    }

                    continue;
                }

                localSpare = created;
                TChunk?[] liveAfter = _Chunks;
                if ((uint)chunkIndex < (uint)liveAfter.Length
                    && ReferenceEquals(Volatile.Read(ref liveAfter[chunkIndex]), previous))
                {
                    return previous;
                }
            }
        }
        finally
        {
            if (localSpare is not null)
            {
                _ = Interlocked.CompareExchange(ref _Spare, localSpare, null);
            }
        }
    }

    /// <summary>Drops all chunk references. A never-installed spare is kept for reuse.</summary>
    public void Clear() =>
        _Chunks = [];

    #endregion

    #region Private helpers

    private void _EnsureOuterCapacity(int chunkIndex)
    {
        if ((uint)chunkIndex >= (uint)_MaxOuterChunks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkIndex),
                chunkIndex,
                $"Chunk index must be in the range 0..{(_MaxOuterChunks - 1).ToString(CultureInfo.InvariantCulture)}.");
        }

        while (true)
        {
            TChunk?[] chunks = _Chunks;
            if ((uint)chunkIndex < (uint)chunks.Length)
            {
                return;
            }

            int doubled = chunks.Length == 0 ? 1 : checked(chunks.Length * 2);
            int newLength = Math.Min(Math.Max(chunkIndex + 1, doubled), _MaxOuterChunks);
            TChunk?[] resized = new TChunk[newLength];
            Array.Copy(chunks, resized, chunks.Length);
            if (Interlocked.CompareExchange(ref _Chunks, resized, chunks) == chunks)
            {
                return;
            }
        }
    }

    #endregion
}
