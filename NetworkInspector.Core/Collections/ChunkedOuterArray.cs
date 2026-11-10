// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

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
    #region Configuration and state

    /// <summary>
    /// Smallest allowed chunk shift (16 slots per chunk). The allowed range is 4 to 20, i.e. 16 to 1 048 576 slots per chunk.
    /// Constant, not configurable: these are fixed limits of this type (a caller tunes the shift, not the limits); the
    /// constructor rejects anything outside.
    /// </summary>
    private const int _MinChunkShift = 4;

    /// <summary>Largest allowed chunk shift (1 048 576 slots per chunk); see <see cref="_MinChunkShift"/>.</summary>
    private const int _MaxChunkShift = 20;

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
                Invariant($"Chunk shift must be between {_MinChunkShift.ToString(CultureInfo.InvariantCulture)} ") +
                Invariant($"and {_MaxChunkShift.ToString(CultureInfo.InvariantCulture)}."));
        }

        ChunkShift = chunkShift;
        ChunkSize = 1 << chunkShift;
        ChunkMask = ChunkSize - 1;
        _MaxOuterChunks = (Ids.ArrayIndexIdRange.MaxValue >> chunkShift) + 1;
        _Chunks = [];
    }

    /// <summary>Log₂ of indices per inner chunk.</summary>
    public int ChunkShift { get; }

    /// <summary>Number of indices per inner chunk (<c>1 &lt;&lt; ChunkShift</c>).</summary>
    public int ChunkSize { get; }

    /// <summary>Bitmask for the slot index within a chunk (<c>ChunkSize - 1</c>).</summary>
    public int ChunkMask { get; }

    #endregion

    #region Chunk access and allocation

    /// <summary>
    /// Splits a dense index into outer chunk index and inner slot index with one shift and one mask (the chunk size is
    /// a power of two, which is why no division is needed).
    /// </summary>
    /// <param name="index">Non-negative dense index; not validated (hot path, callers check the index range first).</param>
    /// <returns>The outer chunk index and the slot index inside that chunk.</returns>
    public (int ChunkIndex, int SlotIndex) DecomposeIndex(int index) =>
        (index >> ChunkShift, index & ChunkMask);

    /// <summary>Returns the inner chunk at <paramref name="chunkIndex"/>, or <see langword="null"/> when unallocated.</summary>
    /// <param name="chunkIndex">Outer index; a negative or beyond-the-current-array value is simply "unallocated".</param>
    /// <returns>The installed chunk, or <see langword="null"/>.</returns>
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
    /// <returns>The chunk at <paramref name="chunkIndex"/>; never <see langword="null"/>.</returns>
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

    #region Allocation gate and growth

    /// <summary>
    /// Allocating path of <see cref="GetOrAllocateChunk"/>: grows the outer array when needed and installs a new chunk.
    /// Rare (once per chunk), so it is kept out of line.
    /// </summary>
    /// <param name="chunkIndex">Outer index of the chunk; already range-checked by the caller.</param>
    /// <param name="factory">Creates the chunk.</param>
    /// <returns>The chunk now installed at <paramref name="chunkIndex"/>.</returns>
    /// <exception cref="InvalidOperationException">Another allocating call or <see cref="Clear"/> runs at the same time.</exception>
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
    /// <param name="chunks">The live outer array.</param>
    /// <param name="chunkIndex">The index that must fit into the result.</param>
    /// <returns>The new, published outer array.</returns>
    private TChunk?[] _Grow(TChunk?[] chunks, int chunkIndex)
    {
        // chunks.Length <= chunkIndex < _MaxOuterChunks <= (int.MaxValue >> 4) + 1, so the doubling cannot overflow.
        int doubled = chunks.Length == 0
            ? 1
            : chunks.Length * 2;
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

    /// <summary>Throws the misuse error of the allocation gate; kept out of line so the inlined gate check stays small.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowConcurrentAllocation() =>
        throw new InvalidOperationException(
            "Concurrent allocation is not supported: allocating GetOrAllocateChunk calls and Clear must not overlap.");

    /// <summary>Throws the range error of <see cref="GetOrAllocateChunk"/>; kept out of line like the other throw helper.</summary>
    /// <param name="chunkIndex">The rejected chunk index.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always; the message names the valid range.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowChunkIndexOutOfRange(int chunkIndex) =>
        throw new ArgumentOutOfRangeException(
            nameof(chunkIndex),
            chunkIndex,
            Invariant($"Chunk index must be in the range 0..{(_MaxOuterChunks - 1).ToString(CultureInfo.InvariantCulture)}."));

    #endregion
}
