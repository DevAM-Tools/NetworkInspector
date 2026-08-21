// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Grow-only chunked store for dense non-negative integer keys up to <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
/// Lazy inner-chunk allocation; outer chunk pointer array grows on demand.
/// </summary>
/// <typeparam name="T">Reference type stored per slot. Unset slots hold <see langword="null"/>.</typeparam>
/// <remarks>
/// <b>Thread-safety:</b> Concurrent disjoint writers (one index per thread) and concurrent readers
/// are supported. Inner-chunk installation uses <see cref="Interlocked.CompareExchange{T}"/>.
/// Outer-array growth uses <see cref="Interlocked.CompareExchange{T}"/> with retry so concurrent
/// resizes cannot drop installed inner-chunk pointers. Slot writes use <see cref="Volatile.Write{T}"/>.
/// <see cref="Clear"/> is not safe concurrently with <see cref="Set"/> or <see cref="Get"/>.
/// </remarks>
public sealed class ChunkedGrowOnlyStore<T> where T : class?
{
    #region Constants

    private const int _MinChunkShift = 4;
    private const int _MaxChunkShift = 20;

    #endregion

    #region Fields

    private readonly int _ChunkShift;
    private readonly int _ChunkSize;
    private readonly int _ChunkMask;
    private readonly int _MaxOuterChunks;

    private volatile T?[][] _Chunks;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array).
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyStore(int chunkShift)
    {
        if ((uint)(chunkShift - _MinChunkShift) > (uint)(_MaxChunkShift - _MinChunkShift))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkShift),
                chunkShift,
                $"Chunk shift must be between {_MinChunkShift.ToString(CultureInfo.InvariantCulture)} " +
                $"and {_MaxChunkShift.ToString(CultureInfo.InvariantCulture)}.");
        }

        _ChunkShift = chunkShift;
        _ChunkSize = 1 << chunkShift;
        _ChunkMask = _ChunkSize - 1;
        _MaxOuterChunks = (Ids.ArrayIndexIdRange.MaxValue >> chunkShift) + 1;
        _Chunks = [];
    }

    #endregion

    #region Public API

    /// <summary>Stores a value at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, T value)
    {
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(index, nameof(index));

        int chunkIdx = index >> _ChunkShift;
        int slotIdx = index & _ChunkMask;

        T?[] chunk = _GetOrAllocateChunk(chunkIdx);
        Volatile.Write(ref chunk[slotIdx], value);
    }

    /// <summary>Reads a value at <paramref name="index"/>. Returns <see langword="null"/> when unset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Get(int index)
    {
        if (!Ids.ArrayIndexIdRange.IsValidIndex(index))
        {
            return null;
        }

        int chunkIdx = index >> _ChunkShift;
        int slotIdx = index & _ChunkMask;

        T?[][] chunks = _Chunks;
        if ((uint)chunkIdx >= (uint)chunks.Length)
        {
            return null;
        }

        T?[]? chunk = Volatile.Read(ref chunks[chunkIdx]);
        if (chunk is null)
        {
            return null;
        }

        return Volatile.Read(ref chunk[slotIdx]);
    }

    /// <summary>
    /// Reads a contiguous range into <paramref name="buffer"/>.
    /// Negative <paramref name="fromIndex"/> offsets produce <see langword="null"/> entries.
    /// </summary>
    public int ReadRange(int fromIndex, Span<T?> buffer)
    {
        int count = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            int idx = fromIndex + i;
            if (idx < 0)
            {
                buffer[i] = null;
                count++;
                continue;
            }

            if ((uint)idx > (uint)Ids.ArrayIndexIdRange.MaxValue)
            {
                break;
            }

            buffer[i] = Get(idx);
            count++;
        }

        return count;
    }

    /// <summary>Drops all chunk references.</summary>
    public void Clear() =>
        _Chunks = [];

    #endregion

    #region Private helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T?[] _GetOrAllocateChunk(int chunkIndex)
    {
        _EnsureOuterCapacity(chunkIndex);

        T?[][] chunks = _Chunks;
        T?[]? chunk = Volatile.Read(ref chunks[chunkIndex]);
        if (chunk is not null)
        {
            return chunk;
        }

        T?[] newChunk = new T?[_ChunkSize];
        chunk = Interlocked.CompareExchange(ref chunks[chunkIndex], newChunk, null) ?? newChunk;
        return chunk;
    }

    private void _EnsureOuterCapacity(int chunkIndex)
    {
        while (true)
        {
            if ((uint)chunkIndex >= (uint)_MaxOuterChunks)
            {
                Ids.ArrayIndexIdRange.ValidateIndexOrThrow(chunkIndex << _ChunkShift, nameof(chunkIndex));
            }

            T?[][] chunks = _Chunks;
            if ((uint)chunkIndex < (uint)chunks.Length)
            {
                return;
            }

            int doubled = chunks.Length == 0 ? 1 : checked(chunks.Length * 2);
            int newLength = Math.Min(Math.Max(chunkIndex + 1, doubled), _MaxOuterChunks);
            T?[][] resized = new T?[newLength][];
            Array.Copy(chunks, resized, chunks.Length);
            if (Interlocked.CompareExchange(ref _Chunks, resized, chunks) == chunks)
            {
                return;
            }
        }
    }

    #endregion
}

/// <summary>
/// Grow-only chunked store for packed <see cref="long"/> values (e.g. PacketId → frame mapping).
/// Unset slots contain a configurable sentinel (default <c>-1</c>).
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> Same concurrent model as <see cref="ChunkedGrowOnlyStore{T}"/>.
/// </remarks>
public sealed class ChunkedGrowOnlyLongStore
{
    #region Constants

    private const int _MinChunkShift = 4;
    private const int _MaxChunkShift = 20;

    #endregion

    #region Fields

    private readonly int _ChunkShift;
    private readonly int _ChunkSize;
    private readonly int _ChunkMask;
    private readonly int _MaxOuterChunks;
    private readonly long _UnsetValue;

    private volatile long[]?[] _Chunks;

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
        if ((uint)(chunkShift - _MinChunkShift) > (uint)(_MaxChunkShift - _MinChunkShift))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkShift),
                chunkShift,
                $"Chunk shift must be between {_MinChunkShift.ToString(CultureInfo.InvariantCulture)} " +
                $"and {_MaxChunkShift.ToString(CultureInfo.InvariantCulture)}.");
        }

        _ChunkShift = chunkShift;
        _ChunkSize = 1 << chunkShift;
        _ChunkMask = _ChunkSize - 1;
        _MaxOuterChunks = (Ids.ArrayIndexIdRange.MaxValue >> chunkShift) + 1;
        _UnsetValue = unsetValue;
        _Chunks = [];
    }

    #endregion

    #region Public API

    /// <summary>Stores a value at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public void Set(int index, long value)
    {
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(index, nameof(index));

        int chunkIdx = index >> _ChunkShift;
        int slotIdx = index & _ChunkMask;

        long[] chunk = _GetOrAllocateChunk(chunkIdx);
        Volatile.Write(ref chunk[slotIdx], value);
    }

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// Returns <see langword="false"/> when the slot is unset or the index is out of range.
    /// </summary>
    public bool TryGet(int index, out long value)
    {
        if (!Ids.ArrayIndexIdRange.IsValidIndex(index))
        {
            value = _UnsetValue;
            return false;
        }

        int chunkIdx = index >> _ChunkShift;
        int slotIdx = index & _ChunkMask;

        long[]?[] chunks = _Chunks;
        if ((uint)chunkIdx >= (uint)chunks.Length)
        {
            value = _UnsetValue;
            return false;
        }

        long[]? chunk = Volatile.Read(ref chunks[chunkIdx]);
        if (chunk is null)
        {
            value = _UnsetValue;
            return false;
        }

        value = Volatile.Read(ref chunk[slotIdx]);
        if (value == _UnsetValue)
        {
            return false;
        }

        return true;
    }

    /// <summary>Drops all chunk references.</summary>
    public void Clear() =>
        _Chunks = [];

    #endregion

    #region Private helpers

    private long[] _GetOrAllocateChunk(int chunkIndex)
    {
        _EnsureOuterCapacity(chunkIndex);

        long[]?[] chunks = _Chunks;
        long[]? chunk = Volatile.Read(ref chunks[chunkIndex]);
        if (chunk is not null)
        {
            return chunk;
        }

        long[] newChunk = new long[_ChunkSize];
        Array.Fill(newChunk, _UnsetValue);

        chunk = Interlocked.CompareExchange(ref chunks[chunkIndex], newChunk, null) ?? newChunk;
        return chunk;
    }

    private void _EnsureOuterCapacity(int chunkIndex)
    {
        while (true)
        {
            if ((uint)chunkIndex >= (uint)_MaxOuterChunks)
            {
                Ids.ArrayIndexIdRange.ValidateIndexOrThrow(chunkIndex << _ChunkShift, nameof(chunkIndex));
            }

            long[]?[] chunks = _Chunks;
            if ((uint)chunkIndex < (uint)chunks.Length)
            {
                return;
            }

            int doubled = chunks.Length == 0 ? 1 : checked(chunks.Length * 2);
            int newLength = Math.Min(Math.Max(chunkIndex + 1, doubled), _MaxOuterChunks);
            long[]?[] resized = new long[]?[newLength];
            Array.Copy(chunks, resized, chunks.Length);
            if (Interlocked.CompareExchange(ref _Chunks, resized, chunks) == chunks)
            {
                return;
            }
        }
    }

    #endregion
}

/// <summary>
/// Shared outer-chunk growth helper. Each outer slot holds one inner chunk (e.g. <c>Frame[]</c>).
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> Concurrent disjoint chunk allocation and concurrent readers are supported.
/// Outer-array growth uses <see cref="Interlocked.CompareExchange{T}"/> with retry; inner-chunk
/// installation uses <see cref="Interlocked.CompareExchange{T}"/>. A losing inner-chunk race
/// discards the factory-produced chunk (acceptable rare waste). <see cref="Clear"/> is not safe
/// concurrently with allocation or reads.
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
    /// The factory runs only after a null slot is observed; if another thread wins the install
    /// <see cref="Interlocked.CompareExchange{T}"/>, the factory-produced chunk is discarded.
    /// </remarks>
    public TChunk GetOrAllocateChunk(int chunkIndex, Func<TChunk> factory)
    {
        _EnsureOuterCapacity(chunkIndex);

        TChunk?[] chunks = _Chunks;
        TChunk? chunk = Volatile.Read(ref chunks[chunkIndex]);
        if (chunk is not null)
        {
            return chunk;
        }

        TChunk newChunk = factory();
        chunk = Interlocked.CompareExchange(ref chunks[chunkIndex], newChunk, null) ?? newChunk;
        return chunk;
    }

    /// <summary>Drops all chunk references.</summary>
    public void Clear() =>
        _Chunks = [];

    #endregion

    #region Private helpers

    private void _EnsureOuterCapacity(int chunkIndex)
    {
        while (true)
        {
            if ((uint)chunkIndex >= (uint)_MaxOuterChunks)
            {
                Ids.ArrayIndexIdRange.ValidateIndexOrThrow(chunkIndex << ChunkShift, nameof(chunkIndex));
            }

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
