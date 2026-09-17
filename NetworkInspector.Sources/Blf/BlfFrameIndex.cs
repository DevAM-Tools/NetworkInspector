// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Compact per-frame metadata for the BLF frame index.
/// Stores the information needed to locate and reconstruct a frame's data
/// from the BLF file: which container holds it, where within the container,
/// and its bus/channel classification.
///
/// Layout (32 bytes, sequential):
///   ContainerOffset  (8B) — file offset of the container holding this object
///   ObjectOffset     (4B) — byte offset within the decompressed container
///   ObjectLength     (4B) — total object byte length in the container
///   ObjectType       (4B) — BLF object type (CAN, Ethernet, etc.)
///   Channel          (2B) — BLF channel number
///   HeaderSize       (2B) — total header size (block + log object)
///   TimestampNanos   (8B) — resolved timestamp in nanoseconds
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BlfFrameEntry
{
    /// <summary>File offset of the container (or raw object) that holds this frame.</summary>
    internal long ContainerOffset;

    /// <summary>
    /// Byte offset of this object within the decompressed container data,
    /// or <c>-1</c> if this entry represents a raw (non-container) object stored
    /// directly in the file at <see cref="ContainerOffset"/>.
    /// </summary>
    internal int ObjectOffset;

    /// <summary>Total object length in the container (headers + payload).</summary>
    internal int ObjectLength;

    /// <summary>BLF object type identifier.</summary>
    internal uint ObjectType;

    /// <summary>Channel number.</summary>
    internal ushort Channel;

    /// <summary>Total header size (block header + log object header).</summary>
    internal ushort HeaderSize;

    /// <summary>Resolved timestamp in nanoseconds (relative or absolute).</summary>
    internal long TimestampNanos;
}

/// <summary>
/// Growable index of BLF frame entries stored in a <see cref="ChunkedGrowOnlyStore{T}"/>.
/// Each entry contains both the location metadata and the timestamp, so the store is the
/// sole authoritative storage — there is no split-array layout.
/// </summary>
/// <remarks>
/// <para>
/// Chunked storage (chunkShift 12 → 4096 entries × 32 B = 128 KiB chunks) avoids
/// doubling-array copies of the whole table on grow and matches Session's dense
/// append-only frame maps. Random access does not need a single contiguous array.
/// </para>
/// <para>
/// <b>Thread-safety:</b> A single writer (the scanner) calls <see cref="Push"/>; concurrent
/// readers may call <see cref="Count"/> and <see cref="GetEntry"/> at any time. The store
/// publishes <see cref="ChunkedGrowOnlyStore{T}.Count"/> after the slot is written, so any
/// index <c>i &lt; Count</c> observed by a reader is a fully initialised entry.
/// </para>
/// </remarks>
internal sealed class BlfFrameIndex
{
    #region Fields

    private readonly ChunkedGrowOnlyStore<BlfFrameEntry> _Entries = new(chunkShift: 12);

    #endregion

    #region Constructors

    /// <summary>Creates a new BLF frame index. <paramref name="initialCapacity"/> is ignored; chunks grow on demand.</summary>
    internal BlfFrameIndex(int initialCapacity = 1024)
    {
        _ = initialCapacity;
    }

    #endregion

    #region Properties

    /// <summary>Number of indexed frames. Safe for concurrent readers.</summary>
    internal int Count => _Entries.Count;

    #endregion

    #region Public API

    /// <summary>
    /// Appends a frame entry to the index. Single-writer only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Push(in BlfFrameEntry entry)
    {
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_Entries.Count, "frame");
        _Entries.Append(in entry);
        return true;
    }

    /// <summary>Returns a copy of the entry at the given frame index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal BlfFrameEntry GetEntry(int index) => _Entries.Get(index);

    #endregion
}
