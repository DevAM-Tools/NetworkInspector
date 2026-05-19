// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Runtime.CompilerServices;

namespace NetworkInspector.Core.Fields;

/// <summary>
/// A bump-pointer allocator for <see cref="FieldBody"/> storage.
/// Allocates contiguous regions from a shared backing array, reducing per-packet
/// GC object count by allowing multiple packets to share a single <see cref="FieldBody"/>
/// array instead of each packet allocating its own.
/// <para>
/// When a slab is full, a new slab is created. Old slabs are kept alive by the packets
/// that reference them via their <c>_Fields</c> array reference. Once all packets in a slab
/// are garbage collected, the slab's backing array becomes collectible as well.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Designed for single-threaded use during
/// packet parsing. Each parsing thread should use its own slab instance
/// (managed via <c>[ThreadStatic]</c> in <see cref="Packet"/>).
/// </para>
/// </summary>
internal sealed class FieldBodySlab
{
    /// <summary>
    /// Default slab capacity: 1024 slots × ~56 bytes/slot ≈ 57 KB.
    /// Stays under the 85 KB Large Object Heap (LOH) threshold to avoid LOH allocations.
    /// At 3 chunks per packet (48 slots), holds approximately 21 packets per slab.
    /// </summary>
    internal const int DefaultCapacity = 1024;

    /// <summary>
    /// Number of <see cref="FieldBody"/> slots per chunk. Each chunk is allocated
    /// as a contiguous region from the slab and referenced by a <see cref="FieldBodyChunk"/>.
    /// 16 slots × ~64 bytes = 1024 bytes per chunk.
    /// </summary>
    internal const int ChunkSize = 16;

    /// <summary>Log₂ of <see cref="ChunkSize"/> for bitwise division.</summary>
    internal const int ChunkShift = 4;

    /// <summary>Bitmask for modulo <see cref="ChunkSize"/> via bitwise AND.</summary>
    internal const int ChunkMask = ChunkSize - 1;

    private readonly FieldBody[] _Buffer;
    private int _Used;

    /// <summary>Creates a slab with the specified slot capacity.</summary>
    internal FieldBodySlab(int capacity)
    {
        _Buffer = new FieldBody[capacity];
    }

    /// <summary>
    /// Tries to allocate a contiguous region of <paramref name="count"/> slots.
    /// Returns true on success, providing the backing buffer and start offset.
    /// The caller stores the buffer reference and offset, so the slab itself does not
    /// need to track which ranges are in use.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAllocate(int count, out FieldBody[] buffer, out int offset)
    {
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