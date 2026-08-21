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
/// Each slot is a packed <c>long</c>:
/// <list type="bullet">
///   <item>bits 63..32 → <c>FrameId.Value</c> (cast via uint to avoid sign-extension)</item>
///   <item>bits 31..0  → <c>FrameSourceId.Value</c></item>
/// </list>
/// Unset slots hold <see cref="_UnsetEntry"/> (−1L = both IDs invalid).
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// Single writer per packetId sequence (source job thread writes 0, 1, 2, …).
/// Multiple concurrent readers (UI, export jobs) via <see cref="Volatile"/> Read.
/// </para>
///
/// <para>
/// <b>Capacity:</b> All valid <see cref="PacketId"/> values
/// (<c>0 … Array.MaxLength - 1</c>).
/// </para>
/// </summary>
internal sealed class PacketToFrameMap
{
    private const int _DefaultChunkShift = 16;
    private const long _UnsetEntry = -1L;

    private readonly Core.Collections.ChunkedGrowOnlyLongStore _Store = new(_DefaultChunkShift, _UnsetEntry);

    /// <summary>
    /// Records the mapping from <paramref name="packetId"/> to the frame that produced it.
    /// Called only from the source job thread — single writer per packetId sequence.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the mapping was recorded successfully;
    /// <see langword="false"/> if the <paramref name="packetId"/> is invalid.
    /// </returns>
    internal bool Record(PacketId packetId, FrameId frameId, FrameSourceId sourceId)
    {
        if (!packetId.IsValid)
        {
            return false;
        }

        long entry = (long)(uint)frameId.Value << 32 | (uint)sourceId.Value;
        _Store.Set(packetId.Value, entry);
        return true;
    }

    /// <summary>
    /// Looks up the frame that produced <paramref name="packetId"/>.
    /// Thread-safe via <see cref="Volatile"/> Read.
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

        if (!_Store.TryGet(packetId.Value, out long entry))
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        frameId = new FrameId((int)(entry >> 32));
        sourceId = new FrameSourceId((int)(entry & 0xFFFF_FFFFL));
        return true;
    }

    /// <summary>
    /// Drops all chunk references, resetting the map to empty.
    /// Must only be called when no concurrent source-job threads are active.
    /// </summary>
    internal void Clear() =>
        _Store.Clear();
}
