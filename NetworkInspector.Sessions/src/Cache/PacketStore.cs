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
/// Chunked array backed by <see cref="Core.Collections.ChunkedGrowOnlyStore{T}"/> with
/// configurable chunk shift (default 14 → 16 384 slots per chunk).
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><b>Store:</b> Single writer per PacketId (source thread) — sequential IDs.</item>
///   <item><b>Get / ReadRange:</b> Any number of concurrent readers via <see cref="Volatile"/> Read.</item>
///   <item><b>Chunk allocation:</b> <see cref="Interlocked.CompareExchange{T}"/> — exactly one winner.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Capacity:</b> Supports all valid <see cref="PacketId"/> values
/// (<c>0 … Array.MaxLength - 1</c>). Chunks are allocated on demand.
/// </para>
/// </summary>
internal sealed class PacketStore
{
    private const int _DefaultChunkShift = 14;

    private readonly Core.Collections.ChunkedGrowOnlyStore<Packet> _Store = new(_DefaultChunkShift);

    /// <summary>
    /// Stores a packet at its <see cref="PacketId"/> position.
    /// Called by the source thread after parsing — single writer per PacketId.
    /// </summary>
    internal void Store(PacketId id, Packet packet) =>
        _Store.Set(id.Value, packet);

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

        return _Store.Get(id.Value);
    }

    /// <summary>
    /// Reads a contiguous range of packets into <paramref name="buffer"/>.
    /// Returns the number of slots actually read (may include <see langword="null"/> entries
    /// for cleared or not-yet-stored slots).
    /// </summary>
    internal int ReadRange(int fromIndex, Span<Packet?> buffer) =>
        _Store.ReadRange(fromIndex, buffer);

    /// <summary>
    /// Reads a contiguous range of packets into <paramref name="buffer"/>, pairing each packet with
    /// the id it was read from. Stops early at the end of the valid id range.
    /// Returns the number of slots written.
    /// </summary>
    internal int ReadRange(int fromIndex, Span<PacketRef> buffer)
    {
        int count = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            int id = fromIndex + i;
            if (id < 0 || id > Core.Ids.ArrayIndexIdRange.MaxValue)
            {
                break;
            }

            PacketId packetId = new(id);
            buffer[i] = new PacketRef(packetId, _Store.Get(id));
            count++;
        }

        return count;
    }

    /// <summary>
    /// Releases all chunks. Called on session restart.
    /// Must only be called when no source jobs are active.
    /// </summary>
    internal void Clear() =>
        _Store.Clear();
}
