// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// A generic slab allocator for contiguous <typeparamref name="T"/> storage.
/// <para>
/// Pre-allocates a large <typeparamref name="T"/>[] backing array (the "slab") and hands out
/// contiguous slices via a simple bump pointer. Multiple consumers (e.g. <see cref="Packet"/>
/// instances) share the same backing array — no per-consumer heap allocation is needed.
/// Each allocation returns a <c>(T[] buffer, int offset)</c> pair into the shared array.
/// </para>
/// <para>
/// <b>Why:</b> many small, similarly sized regions (one field table per parsed packet, one buffer per captured frame) would
/// otherwise each be a separate array: one allocation and one more object for the GC to track per packet or frame. Carving
/// them from one large array turns that into one allocation per slab plus a pointer bump, and keeps neighbours adjacent in
/// memory. Callers size the slab below the large-object-heap threshold (about 85 KB) where possible so a slab is an ordinary
/// generation-0 object and does not force a full collection when it dies.
/// </para>
/// <para>
/// <b>Shared source:</b> this file is link-compiled into <c>NetworkInspector.Sources</c> (for frame slabs of the sequential
/// readers) as well as built in Core. Each assembly compiles its own internal copy, so the type stays internal in both and
/// must not depend on other Core types.
/// </para>
/// <para>
/// <b>Not an arena allocator.</b> Unlike a true arena (where all allocations share a single
/// lifetime and are freed together via <c>Reset()</c>), this allocator has no collective
/// deallocation. Each consumer independently references its slice of the backing array.
/// The slab stays alive as long as <em>any</em> consumer still holds a reference to the
/// backing array — the GC manages individual lifetimes, not the allocator.
/// </para>
/// <para>
/// <b>Memory layout example</b> (capacity=256, 4 slots per consumer):
/// <code>
///   Slab._Buffer:  [0..3] Consumer A  [4..7] Consumer B  [8..255] free...
///                   ▲                   ▲
///                   │                   └── B._Buffer = Slab._Buffer, B._Offset = 4
///                   └────────────────────── A._Buffer = Slab._Buffer, A._Offset = 0
/// </code>
/// Each consumer stores only a reference to the shared buffer and its start offset.
/// When consumer A needs to grow (e.g. from 4 to 8 slots), it allocates a new region
/// and copies its existing data — the old slots become orphaned (negligible waste).
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe, and does not need to be: every instance is used by one thread only
/// (Core keeps its slabs in <c>[ThreadStatic]</c> fields, so concurrent parses on different threads never share a slab;
/// each sequential frame reader owns its own instance). <c>_Used</c> is therefore a plain field with no volatile or
/// interlocked access, and sharing an instance between threads would hand out overlapping regions. Buffers already carved
/// out stay valid for readers of the resulting packets — a slab is only ever appended to, never compacted. Other threads may
/// read those regions concurrently because they never overlap the region a later allocation hands out.
/// </para>
/// </summary>
/// <typeparam name="T">The element type stored in the slab.
/// The backing array is zero-initialized (<c>default(T)</c>) unless the slab is created with the uninitialized policy.</typeparam>
internal sealed class SlabAllocator<T>
{
    /// <summary>The shared backing array from which all allocations are carved.</summary>
    private readonly T[] _Buffer;

    /// <summary>Number of slots consumed so far (bump pointer).</summary>
    private int _Used;

    /// <summary>Creates a slab allocator with the specified slot capacity.</summary>
    /// <param name="capacity">Total number of <typeparamref name="T"/> slots in the backing array.
    /// Choose a capacity that keeps the array below the 85 KB LOH threshold for value types.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    internal SlabAllocator(int capacity)
        : this(capacity, uninitialized: false)
    {
    }

    /// <summary>Creates a slab allocator with the specified slot capacity and initialisation policy.</summary>
    /// <param name="capacity">Total number of <typeparamref name="T"/> slots in the backing array.</param>
    /// <param name="uninitialized">
    /// True skips the zeroing pass (<see cref="GC.AllocateUninitializedArray{T}(int, bool)"/>). Only for consumers that
    /// write every slot of a region before anything reads it (frame byte copies); <c>T</c> with references is still
    /// zeroed by the runtime.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <remarks>
    /// The uninitialized policy saves the zeroing pass over the whole slab (a memory-bandwidth cost proportional to the slab
    /// size, paid once per slab). It is only safe when the consumer never reads a slot it has not written first, which is
    /// the contract of the frame byte slabs. The field-table and lazy-populator slabs are created through the zeroing
    /// constructor (<c>Packet.AllocateFromSlab</c>), so their unwritten slots read as <c>default</c>.
    /// </remarks>
    internal SlabAllocator(int capacity, bool uninitialized)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _Buffer = uninitialized ? GC.AllocateUninitializedArray<T>(capacity) : new T[capacity];
    }

    /// <summary>
    /// Tries to allocate a contiguous region of <paramref name="count"/> slots.
    /// <para>
    /// On success, returns <c>true</c> and provides the shared backing array and the
    /// start offset of the allocated region. The caller stores these two values —
    /// the allocator does not track individual allocations.
    /// </para>
    /// <para>
    /// On failure (not enough space), returns <c>false</c>. The caller should create
    /// a new <see cref="SlabAllocator{T}"/> instance and retry.
    /// </para>
    /// </summary>
    /// <param name="count">Number of slots to reserve. Zero succeeds without consuming space.</param>
    /// <param name="buffer">The shared backing array on success; <see langword="null"/> on failure (must not be used).</param>
    /// <param name="offset">Start of the reserved region within <paramref name="buffer"/>; 0 on failure.</param>
    /// <returns><see langword="true"/> if the region was reserved; <see langword="false"/> if the slab has too little space left.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative (a caller bug, not an expected failure).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAllocate(int count, out T[] buffer, out int offset)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Allocation count must be non-negative.");
        }

        // Out of space is an expected failure (the caller starts a new slab), so it is a false return, not an exception.
        // Overflow reasoning: _Used never exceeds the array length, so length - used is non-negative, and count is
        // non-negative after the check above. Comparing both as unsigned therefore decides "count fits" exactly, and the
        // test never computes used + count, which could overflow int for a huge count.
        int used = _Used;
        int length = _Buffer.Length;
        if ((uint)count > (uint)(length - used))
        {
            buffer = null!; // Invariant: callers never read buffer when false is returned.
            offset = 0;
            return false;
        }

        // Bump the pointer. Plain (non-atomic) store: the allocator is single-threaded by contract.
        buffer = _Buffer;
        offset = used;
        _Used = used + count;
        return true;
    }
}
