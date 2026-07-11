// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Cache;

/// <summary>
/// Append-only chunked store for all parsed packets. Single shared instance per session.
///
/// <para>
/// <b>Replaces:</b> <c>SmallPacketCache</c> + per-listener <c>Packet[]</c> batch copies.
/// All parsed packets are stored once; listeners read them directly by index.
/// </para>
///
/// <para>
/// <b>Layout:</b>
/// Chunked array: <c>_Chunks[packetId &gt;&gt; _ChunkShift][packetId &amp; _ChunkMask]</c>.
/// Each chunk holds <see cref="_ChunkSize"/> <see cref="Packet"/> reference slots.
/// Chunks are allocated lazily on first write.
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><b>Store:</b> Single writer per PacketId (source thread) — sequential IDs.</item>
///   <item><b>Get / ReadRange:</b> Any number of concurrent readers via <see cref="System.Threading.Volatile"/> Read.</item>
///   <item><b>Chunk allocation:</b> <see cref="Interlocked.CompareExchange{T}"/> — exactly one winner.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Memory:</b>
/// 16 384 slots × 8 bytes (reference) = 128 KB per chunk.
/// Up to 8 192 chunks = ~134 M packets maximum. Chunks are allocated on demand.
/// </para>
/// </summary>
internal sealed class PacketStore
{
    // 2^14 = 16 384 entries per chunk (128 KB per inner array of Packet?).
    private const int _ChunkShift = 14;
    private const int _ChunkSize = 1 << _ChunkShift;  // 16 384
    private const int _ChunkMask = _ChunkSize - 1;
    private const int _MaxChunks = 8192;              // 134 M packets max

    private readonly Packet?[]?[] _Chunks = new Packet?[]?[_MaxChunks];

    /// <summary>
    /// Stores a packet at its <see cref="PacketId"/> position.
    /// Called by the source thread after parsing — single writer per PacketId.
    /// <see cref="System.Threading.Volatile"/> Write ensures the reference is visible to concurrent readers.
    /// </summary>
    internal void Store(PacketId id, Packet packet)
    {
        int chunkIdx = id.Value >> _ChunkShift;
        int slotIdx = id.Value & _ChunkMask;

        Packet?[]? chunk = Volatile.Read(ref _Chunks[chunkIdx]);
        if (chunk is null)
        {
            // Lazily allocate chunk. CAS ensures exactly one allocation wins.
            Packet?[] newChunk = new Packet?[_ChunkSize];
            chunk = Interlocked.CompareExchange(ref _Chunks[chunkIdx], newChunk, null) ?? newChunk;
        }

        // Volatile.Write acts as a release fence — all preceding writes
        // (packet field population) are visible before the reference is published.
        Volatile.Write(ref chunk[slotIdx], packet);
    }

    /// <summary>
    /// Reads a packet by its <see cref="PacketId"/>. O(1), lock-free.
    /// Returns <see langword="null"/> if the packet has not been stored yet,
    /// the ID is invalid, or the chunk was cleared (e.g. session restart).
    /// </summary>
    internal Packet? Get(PacketId id)
    {
        if (!id.IsValid)
        {
            return null;
        }

        int chunkIdx = id.Value >> _ChunkShift;
        int slotIdx = id.Value & _ChunkMask;

        Packet?[]? chunk = Volatile.Read(ref _Chunks[chunkIdx]);
        if (chunk is null)
        {
            return null;
        }

        return Volatile.Read(ref chunk[slotIdx]);
    }

    /// <summary>
    /// Reads a contiguous range of packets into <paramref name="buffer"/>.
    /// Returns the number of slots actually read (may include <see langword="null"/> entries
    /// for cleared or not-yet-stored slots).
    /// </summary>
    /// <param name="fromIndex">
    /// First PacketId value (inclusive). Negative values are allowed and produce
    /// <see langword="null"/> entries in the corresponding buffer slots. This supports
    /// callers that compute a start index relative to a cursor that may temporarily
    /// underflow (e.g. <c>packetCount - windowSize</c> before enough packets arrive).
    /// </param>
    /// <param name="buffer">Destination buffer. Length determines how many to read.</param>
    /// <returns>Number of slots filled (always <c>min(buffer.Length, available)</c>).</returns>
    internal int ReadRange(long fromIndex, Span<Packet?> buffer)
    {
        int count = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            long idx = fromIndex + i;
            if (idx < 0)
            {
                buffer[i] = null;
                count++;
                continue;
            }

            int chunkIdx = (int)(idx >> _ChunkShift);
            int slotIdx = (int)(idx & _ChunkMask);

            if (chunkIdx >= _MaxChunks)
            {
                // Beyond maximum capacity — stop filling.
                break;
            }

            Packet?[]? chunk = Volatile.Read(ref _Chunks[chunkIdx]);
            buffer[i] = chunk is null ? null : Volatile.Read(ref chunk[slotIdx]);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Releases all chunks. Called on session restart.
    /// Must only be called when no source jobs are active.
    /// </summary>
    internal void Clear()
    {
        for (int i = 0; i < _MaxChunks; i++)
        {
            Volatile.Write(ref _Chunks[i], null);
        }
    }
}
