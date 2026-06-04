// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Cache;

/// <summary>
/// Maps <see cref="PacketId"/> → (<see cref="FrameId"/>, <see cref="FrameSourceId"/>) via
/// a lock-free chunked array.
///
/// <para>
/// <b>Why a chunked array instead of a hash map:</b>
/// PacketIds are dense sequential integers (0, 1, 2, …). No hashing or collision
/// resolution needed. Direct indexing is O(1) with zero overhead.
/// </para>
///
/// <para>
/// <b>Data layout:</b>
/// <c>_Chunks[packetId &gt;&gt; ChunkShift][packetId &amp; ChunkMask]</c>
/// Each slot is a packed <c>long</c>:
/// <list type="bullet">
///   <item>bits 63..32 → <c>FrameId.Value</c> (cast via uint to avoid sign-extension)</item>
///   <item>bits 31..0  → <c>FrameSourceId.Value</c></item>
/// </list>
/// Unset slots hold <see cref="UnsetEntry"/> (−1L = both IDs invalid).
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// Single writer per packetId sequence (source job thread writes 0, 1, 2, …).
/// Multiple concurrent readers (UI, export jobs) via <see cref="System.Threading.Volatile"/> Read.
/// Chunk allocation uses <see cref="Interlocked.CompareExchange{T}"/> — exactly one winner.
/// </para>
///
/// <para>
/// <b>Performance:</b>
/// Write: ~2 ns — Volatile.Write to pre-allocated slot.
/// Read:  ~2 ns — one Volatile.Read + bit-unpack.
/// No lock, no allocation after warm-up, hardware-prefetcher-friendly.
/// </para>
///
/// <para>
/// <b>Memory (allocated on demand, 512 KB per chunk):</b>
/// <list type="bullet">
///   <item>1 M packets  →   16 chunks ≈   8 MB</item>
///   <item>10 M packets →  153 chunks ≈  76 MB</item>
///   <item>100 M packets → 1526 chunks ≈ 762 MB</item>
///   <item>Hard limit: 2048 chunks = 134 M packets</item>
/// </list>
/// </para>
/// </summary>
internal sealed class PacketToFrameMap
{
    // 2^16 = 65 536 entries per chunk (512 KB per inner array of long).
    private const int ChunkShift = 16;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxChunks = 2048; // 2048 × 65 536 = 134 M packets
    private const long UnsetEntry = -1L; // both IDs packed as -1 (Invalid)

    /// <summary>Maximum number of packets the map can hold (2048 × 65 536 = 134 217 728).</summary>
    internal const int MaxEntries = MaxChunks * ChunkSize;

    // Outer reference array: 2048 × 8 bytes = 16 KB, always allocated.
    // Inner arrays: 65 536 × 8 bytes = 512 KB each, lazily allocated.
    private readonly long[]?[] _Chunks = new long[]?[MaxChunks];

    /// <summary>
    /// Records the mapping from <paramref name="packetId"/> to the frame that produced it.
    /// Called only from the source job thread — single writer per packetId sequence.
    /// Lock-free: <see cref="System.Threading.Volatile"/> Write publishes atomically on 64-bit platforms.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the mapping was recorded successfully;
    /// <see langword="false"/> if the <paramref name="packetId"/> is invalid or
    /// exceeds the maximum capacity (<see cref="MaxChunks"/> × <see cref="ChunkSize"/>).
    /// </returns>
    internal bool Record(PacketId packetId, FrameId frameId, FrameSourceId sourceId)
    {
        if (!packetId.IsValid)
        {
            return false;
        }

        int chunkIndex = packetId.Value >> ChunkShift;
        int slotIndex = packetId.Value & ChunkMask;

        // Guard against exceeding the fixed chunk array capacity.
        if (chunkIndex >= MaxChunks)
        {
            return false;
        }

        long[]? chunk = Volatile.Read(ref _Chunks[chunkIndex]);
        if (chunk is null)
        {
            // Lazily allocate: fill with UnsetEntry so readers never observe stale zeros.
            long[] newChunk = new long[ChunkSize];
            Array.Fill(newChunk, UnsetEntry);

            // CAS: one thread wins; the loser discards its allocation (GC reclaims it).
            chunk = Interlocked.CompareExchange(ref _Chunks[chunkIndex], newChunk, null) ?? newChunk;
        }

        // Pack: cast to uint before widening to prevent sign-extension into the high bits.
        long entry = (long)(uint)frameId.Value << 32 | (uint)sourceId.Value;
        Volatile.Write(ref chunk[slotIndex], entry);
        return true;
    }

    /// <summary>
    /// Looks up the frame that produced <paramref name="packetId"/>.
    /// Thread-safe via <see cref="System.Threading.Volatile"/> Read.
    /// Returns <see langword="false"/> if the packet has not yet been recorded or the ID is invalid.
    /// </summary>
    internal bool TryGet(
        PacketId packetId,
        out FrameId frameId,
        out FrameSourceId sourceId)
    {
        if (!packetId.IsValid)
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        int chunkIndex = packetId.Value >> ChunkShift;
        int slotIndex = packetId.Value & ChunkMask;

        long[]? chunk = Volatile.Read(ref _Chunks[chunkIndex]);
        if (chunk is null)
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        long entry = Volatile.Read(ref chunk[slotIndex]);
        if (entry == UnsetEntry)
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        // Unpack: arithmetic right-shift sign-extends the high 32 bits back to int.
        frameId = new FrameId((int)(entry >> 32));
        sourceId = new FrameSourceId((int)(entry & 0xFFFF_FFFFL));
        return true;
    }

    /// <summary>
    /// Drops all chunk references, resetting the map to empty.
    /// Must only be called when no concurrent source-job threads are active.
    /// </summary>
    internal void Clear()
    {
        for (int i = 0; i < MaxChunks; i++)
        {
            Volatile.Write(ref _Chunks[i], null);
        }
    }
}
