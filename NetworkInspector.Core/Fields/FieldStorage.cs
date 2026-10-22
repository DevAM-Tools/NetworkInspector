// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Field-slot storage embedded in <see cref="Packet"/>: a first chunk owned directly plus equal-size follow-up chunks.
/// <para><b>Layout:</b> slots <c>0 … FirstChunkSize − 1</c> live in the first chunk, a slice of a thread-local slab
/// that the packet keeps for its whole life; an access there is one compare, one array bounds check, and one load.
/// Slot <c>FirstChunkSize + k</c> lives in follow-up chunk <c>k / ChunkSize</c> at position <c>k % ChunkSize</c>.
/// Chunks are never copied or moved, so a <c>ref FieldBody</c> obtained before a growth stays valid. The first chunk
/// holds the root and every frame field, including a materialized frame (<see cref="FrameFieldBudget"/>), plus room
/// for protocol fields, so a small packet such as classic CAN fits in the first chunk alone.</para>
/// <para><b>Recycle:</b> <see cref="ResetForReuse"/> clears the used slots and keeps the chunks, so a recycled packet
/// that parses frames of similar size allocates nothing. Follow-up chunks beyond <see cref="RetainedSlots"/> go to the
/// thread's <see cref="FieldChunkPool"/> (at most <see cref="FieldChunkPool.MaxPooledSlots"/> slots); what does not fit
/// is left to the GC, and a descriptor table that grew beyond the retained chunks is replaced by a small one. A packet
/// therefore holds the surplus of one large frame only until its next recycle. Every slot of a kept or pooled chunk
/// that no field count covers is zero, so no field data crosses from one parse or packet to the next.</para>
/// <para><b>Thread-safety:</b> not synchronized by itself; <see cref="Packet"/> owns the protocol. Before Seal one
/// thread writes. After Seal, <see cref="Packet"/> serializes slot reservation; only follow-up chunks are added, each
/// published (a grown descriptor table first, then the volatile count store) before any field count covers it. The
/// first chunk never changes while the packet is readable. <see cref="ResetForReuse"/> requires exclusive access (the
/// packet's recycle gate). A reader that breaks this stays memory-safe: every slot access is a checked array access, so
/// a stale or torn state yields a slot of some slab array (possibly another packet's, type-safe) or an exception, never
/// a reference outside an array. The pool is thread-local and only its own thread touches it.</para>
/// </summary>
internal struct FieldStorage
{
    #region Constants

    /// <summary>
    /// Slots in the first chunk; at least <see cref="FrameFieldBudget"/> + 1. Chosen by measurement: 16 is the smallest
    /// first chunk that holds a classic CAN packet (13–16 slots with the packet node), so a stored CAN packet needs no
    /// follow-up chunk or descriptor table (A/B measured against 8- and 32-slot variants; 16 won on stored CAN memory
    /// without slowing recycled multi-threaded parsing).
    /// </summary>
    internal const int FirstChunkSize = 16;

    /// <summary>
    /// Slots per follow-up chunk. Chosen by measurement: 8-slot chunks stored less, but cost recycled multi-threaded
    /// parsing more than the agreed tolerance and pinned more slab memory for sparsely kept packets.
    /// </summary>
    internal const int ChunkSize = 16;

    /// <summary>Slots of the root plus a materialized frame without interface: root, frame node, five frame fields.</summary>
    internal const int FrameFieldBudget = 7;

    /// <summary>Follow-up slots a recycle keeps at most (64 × 64 B = 4 KiB); the surplus goes to the pool.</summary>
    internal const int RetainedSlots = 64;

    // Whole follow-up chunks a recycle keeps, so the kept slots never exceed RetainedSlots.
    private const int _RetainedChunks = RetainedSlots / ChunkSize;

    // Descriptors of the first table. Equal to _RetainedChunks, so the small table a recycle keeps is also the table a
    // fresh packet starts with, and a packet that stays within the retained chunks never grows it.
    private const int _InitialDescriptors = 4;

    // Descriptor capacity a recycle keeps: enough for the retained chunks, at least the initial table.
    private const int _RetainedDescriptors = _RetainedChunks > _InitialDescriptors ? _RetainedChunks : _InitialDescriptors;

    /// <summary>Slots per thread-local slab: 1 024 × 64 B = 64 KiB (below the large-object threshold).</summary>
    private const int _SlabCapacity = 1024;

    #endregion

    #region Fields

    // Per-thread bump allocator shared by all packets created or recycled on this thread.
    [ThreadStatic]
    private static SlabAllocator<FieldBody>? _Slab;

    // Per-thread pool of zeroed follow-up chunks; created by the first recycle that releases surplus.
    [ThreadStatic]
    private static FieldChunkPool? _Pool;

    // First chunk [_FirstOffset, _FirstOffset + FirstChunkSize) of _First. Written only by Initialize (construction, or
    // the exclusive recycle that switches a never-built packet to Build).
    private FieldBody[]? _First;
    private int _FirstOffset;

    // Follow-up descriptors; null until the first follow-up chunk. Volatile: a grown table is published as a new object.
    private volatile ChunkTable? _Chunks;

    #endregion

    #region Nested types

    /// <summary>
    /// Follow-up chunk descriptors. A grown table is a new object whose <see cref="Count"/> is set before it is
    /// published, so readers never see a torn (array, count) pair or fewer visible chunks than before.
    /// </summary>
    private sealed class ChunkTable(FieldBodyChunk[] chunks)
    {
        /// <summary>Descriptor array; entries below <see cref="Allocated"/> own a slab slice.</summary>
        internal readonly FieldBodyChunk[] Chunks = chunks;

        /// <summary>Chunks visible to readers (release store by the appender, acquire load by readers).</summary>
        internal volatile int Count;

        /// <summary>
        /// Chunks with a buffer, kept for the next parse. Written by the appender and by the exclusive recycle; read by
        /// the next appender after it observed <see cref="Count"/> (acquire), which orders this plain field.
        /// </summary>
        internal int Allocated;
    }

    #endregion

    #region Properties

    /// <summary>Whether <see cref="Initialize"/> ran (Build-mode packets; a Skip-only packet never initializes).</summary>
    internal readonly bool IsInitialized => _First is not null;

    /// <summary>Follow-up chunks that own a buffer. For tests and diagnostics; not synchronized.</summary>
    internal readonly int AllocatedChunks
    {
        get
        {
            ChunkTable? table = _Chunks;
            return table is null
                ? 0
                : table.Allocated;
        }
    }

    #endregion

    #region Lifecycle

    /// <summary>Allocates the first chunk of a fresh packet from the thread-local slab.</summary>
    internal void Initialize()
    {
        Packet.AllocateFromSlab(ref _Slab, _SlabCapacity, FirstChunkSize, out FieldBody[] buffer, out int offset);
        _First = buffer;
        _FirstOffset = offset;
        _Chunks = null;
    }

    /// <summary>
    /// Clears every used slot, keeps the chunks for the next parse, and passes follow-up chunks beyond
    /// <see cref="RetainedSlots"/> to the thread's pool. Caller holds the recycle gate; no reader exists.
    /// </summary>
    /// <param name="usedSlots">Reserved slots of the previous parse (root included); at least 1.</param>
    internal void ResetForReuse(int usedSlots)
    {
        // 1. Clear GC-visible references in the used first-chunk slots (unused tail slots are still zero).
        int firstUsed = Math.Min(usedSlots, FirstChunkSize);
        Array.Clear(_First!, _FirstOffset, firstUsed);
        ChunkTable? table = _Chunks;
        if (table is null)
        {
            return;
        }

        // 2. Clear the used slots of the visible follow-up chunks. Chunks past Count were cleared by an earlier recycle,
        //    so after this loop every slot of every kept chunk is zero.
        int remaining = usedSlots - firstUsed;
        for (int c = 0; c < table.Count && remaining > 0; c++)
        {
            ref FieldBodyChunk chunk = ref table.Chunks[c];
            int clear = Math.Min(remaining, ChunkSize);
            Array.Clear(chunk.Buffer, chunk.Offset, clear);
            remaining -= clear;
        }

        table.Count = 0;

        // 3. Keep at most RetainedSlots of follow-up storage; the surplus goes to the thread's pool.
        if (table.Allocated > _RetainedChunks)
        {
            _ReleaseSurplus(table);
        }
    }

    #endregion

    #region Access

    /// <summary>
    /// Reference to slot <paramref name="index"/>. Callers pass only reserved or published indices (below
    /// <c>Packet._AllocatedFieldCount</c>); <see cref="EnsureSlot"/> made follow-up indices addressable first.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ref FieldBody Slot(int index)
    {
        if ((uint)index < FirstChunkSize)
        {
            // Checked array access on purpose: a torn or stale state stays inside the array or throws.
            return ref _First![_FirstOffset + index];
        }

        return ref _ChunkSlot(index);
    }

    /// <summary>
    /// Makes <paramref name="reservedIndex"/> addressable. Pre-Seal the single writer appends the next chunk. Post-Seal
    /// the thread that reserved a chunk's first slot appends it after the previous chunk is visible; others spin.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureSlot(int reservedIndex, bool postSeal)
    {
        if (reservedIndex < FirstChunkSize)
        {
            return;
        }

        // reservedIndex ≥ FirstChunkSize here, so the uint division is exact and cheap (shift for a power of two).
        int chunkIndex = (int)((uint)(reservedIndex - FirstChunkSize) / ChunkSize);
        ChunkTable? table = _Chunks;
        if (table is not null && chunkIndex < table.Count)
        {
            return;
        }

        _EnsureChunk(chunkIndex, reservedIndex, postSeal);
    }

    #endregion

    #region Private helpers

    // Follow-up path, inlined: with small first chunks most protocol fields live here. A negative index wraps to a huge
    // uint and fails the descriptor bounds check; a missing table or buffer throws NullReferenceException.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref FieldBody _ChunkSlot(int index)
    {
        uint relative = (uint)(index - FirstChunkSize);
        ChunkTable table = _Chunks!;
        ref FieldBodyChunk chunk = ref table.Chunks[relative / ChunkSize];
        return ref chunk.Buffer[chunk.Offset + (int)(relative % ChunkSize)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _EnsureChunk(int chunkIndex, int reservedIndex, bool postSeal)
    {
        SpinWait waiter = default;
        while (true)
        {
            if (chunkIndex < _VisibleChunks())
            {
                return;
            }

            // Pre-Seal: reservations are sequential, so chunkIndex is exactly the next chunk.
            if (!postSeal)
            {
                _AppendChunk();
                return;
            }

            // Post-Seal: the owner of the chunk's first slot appends after the previous chunk became visible.
            if ((reservedIndex - FirstChunkSize) % ChunkSize == 0)
            {
                SpinWait spin = default;
                while (_VisibleChunks() < chunkIndex)
                {
                    spin.SpinOnce();
                }

                _AppendChunk();
                return;
            }

            waiter.SpinOnce();
        }
    }

    private readonly int _VisibleChunks()
    {
        ChunkTable? table = _Chunks;
        return table is null
            ? 0
            : table.Count;
    }

    /// <summary>Appends one follow-up chunk. Callers are serialized (single writer pre-Seal, chunk owner post-Seal).</summary>
    private void _AppendChunk()
    {
        // Count first (acquire), then the plain fields it orders.
        ChunkTable? table = _Chunks;
        int count = table is null ? 0 : table.Count;
        if (table is null || count == table.Chunks.Length)
        {
            // Doubling from 4: a packet at the field limit (65 535 slots) needs 4 095 follow-up chunks of 16 slots, so the table
            // grows to 4 096 descriptors × 16 B = 64 KiB — still below the large-object threshold.
            int capacity = table is null ? _InitialDescriptors : table.Chunks.Length * 2;
            FieldBodyChunk[] chunks = new FieldBodyChunk[capacity];
            int allocated = 0;
            if (table is not null)
            {
                table.Chunks.AsSpan(0, table.Allocated).CopyTo(chunks);
                allocated = table.Allocated;
            }

            table = new ChunkTable(chunks)
            {
                Allocated = allocated,
                Count = count,
            };
        }

        // A kept buffer first; else a zeroed chunk from this thread's pool; else a new slice from the slab.
        if (count == table.Allocated)
        {
            ref FieldBodyChunk chunk = ref table.Chunks[count];
            FieldChunkPool? pool = _Pool;
            if (pool is null || !pool.TryRent(out chunk))
            {
                Packet.AllocateFromSlab(ref _Slab, _SlabCapacity, ChunkSize, out chunk.Buffer, out chunk.Offset);
            }

            table.Allocated = count + 1;
        }

        // Publish: the table reference first (when new), then the count that makes the chunk visible.
        if (!ReferenceEquals(table, _Chunks))
        {
            _Chunks = table;
        }

        table.Count = count + 1;
    }

    /// <summary>
    /// Passes kept chunks beyond the retention limit to this thread's pool and replaces a grown descriptor table with a
    /// small one. Caller holds the recycle gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ReleaseSurplus(ChunkTable table)
    {
        // Every slot of these chunks is zero (ResetForReuse step 2), so the pool hands out clean chunks only.
        FieldChunkPool pool = _Pool ??= new FieldChunkPool(ChunkSize);
        for (int c = _RetainedChunks; c < table.Allocated; c++)
        {
            ref FieldBodyChunk chunk = ref table.Chunks[c];

            // A full pool returns false: the chunk is dropped and the GC reclaims it with its slab.
            _ = pool.TryReturn(in chunk);

            // The descriptor table must not pin the buffer any longer.
            chunk = default;
        }

        table.Allocated = _RetainedChunks;

        // A large frame grew the descriptor table (up to 4 096 entries, 64 KiB, at the field limit). Do not keep it: a small
        // table with the retained chunks replaces it, and the next large frame grows it again.
        if (table.Chunks.Length > _RetainedDescriptors)
        {
            FieldBodyChunk[] chunks = new FieldBodyChunk[_RetainedDescriptors];
            table.Chunks.AsSpan(0, _RetainedChunks).CopyTo(chunks);
            _Chunks = new ChunkTable(chunks)
            {
                Allocated = _RetainedChunks,
            };
        }
    }

    #endregion
}
