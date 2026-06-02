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
/// <b>Thread-safety:</b> Not thread-safe. Designed for single-threaded use during
/// packet parsing. Each thread manages its own slab instance via <c>[ThreadStatic]</c>.
/// </para>
/// </summary>
/// <typeparam name="T">The element type stored in the slab. Must be a value or reference type.
/// For value types, the backing array is initialized to <c>default(T)</c> by the runtime.</typeparam>
internal sealed class SlabAllocator<T>
{
    /// <summary>The shared backing array from which all allocations are carved.</summary>
    private readonly T[] _Buffer;

    /// <summary>Number of slots consumed so far (bump pointer).</summary>
    private int _Used;

    /// <summary>Creates a slab allocator with the specified slot capacity.</summary>
    /// <param name="capacity">Total number of <typeparamref name="T"/> slots in the backing array.
    /// Choose a capacity that keeps the array below the 85 KB LOH threshold for value types.</param>
    internal SlabAllocator(int capacity)
    {
        _Buffer = new T[capacity];
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAllocate(int count, out T[] buffer, out int offset)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Allocation count must be non-negative.");
        }

        int used = _Used;
        if (used + count <= _Buffer.Length)
        {
            buffer = _Buffer;
            offset = used;
            _Used = used + count;
            return true;
        }

        buffer = null!;
        offset = 0;
        return false;
    }
}
