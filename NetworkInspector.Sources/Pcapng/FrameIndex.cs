// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Compact metadata for a single frame's location within the capture file.
/// </summary>
internal struct FrameOffset
{
    /// <summary>File offset where the packet DATA starts (past the block header).</summary>
    internal long FileOffset;

    /// <summary>Zero-based section index in which this frame was found.</summary>
    internal ushort SectionIndex;

    /// <summary>Zero-based interface ID within the section.</summary>
    internal ushort InterfaceId;

    /// <summary>Number of octets captured and stored.</summary>
    internal int CapturedLength;
}

/// <summary>
/// One indexed PCAPNG frame: location plus timestamp.
/// Stored in a chunked grow-only table so appending does not copy earlier frames.
/// </summary>
internal struct PcapFrameEntry
{
    /// <summary>File offset where the packet DATA starts (past the block header).</summary>
    internal long FileOffset;

    /// <summary>Zero-based section index in which this frame was found.</summary>
    internal ushort SectionIndex;

    /// <summary>Zero-based interface ID within the section.</summary>
    internal ushort InterfaceId;

    /// <summary>Number of octets captured and stored.</summary>
    internal int CapturedLength;

    /// <summary>Timestamp in nanoseconds since the Unix epoch.</summary>
    internal long TimestampNanos;
}

/// <summary>
/// Index mapping frame IDs to their file positions and timestamps.
/// Entries live in a <see cref="ChunkedGrowOnlyStore{T}"/> (4096 entries per chunk)
/// so growth allocates a new chunk and leaves earlier chunks in place.
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> A single writer (the scanner) calls <see cref="Push"/>; concurrent
/// readers may call <see cref="Count"/>, <see cref="GetOffset"/>, and
/// <see cref="GetTimestamp"/> at any time. The store publishes
/// <see cref="ChunkedGrowOnlyStore{T}.Count"/> after the slot is written, so any
/// index <c>i &lt; Count</c> observed by a reader is a fully initialised entry.
/// </remarks>
internal sealed class FrameIndex
{
    #region Fields

    /// <summary>Chunk shift 12 → 4096 entries per chunk. Same shape as the BLF frame index.</summary>
    private readonly ChunkedGrowOnlyStore<PcapFrameEntry> _Entries = new(chunkShift: 12);

    #endregion

    #region Properties

    /// <summary>Gets the number of frames in the index. Safe to call from any thread.</summary>
    internal int Count => _Entries.Count;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates an empty frame index.
    /// <paramref name="capacity"/> is ignored; chunks grow on demand.
    /// </summary>
    internal FrameIndex(int capacity = 0)
    {
        _ = capacity;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Adds a frame to the index. Returns the zero-based frame index.
    /// Single-writer only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    internal int Push(in PcapFrameEntry entry)
    {
        int count = _Entries.Count;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(count, "frame");
        _Entries.Append(in entry);
        return count;
    }

    /// <summary>Gets the frame offset at the given index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FrameOffset GetOffset(int index)
    {
        PcapFrameEntry entry = _Entries.Get(index);
        return new FrameOffset
        {
            FileOffset = entry.FileOffset,
            SectionIndex = entry.SectionIndex,
            InterfaceId = entry.InterfaceId,
            CapturedLength = entry.CapturedLength,
        };
    }

    /// <summary>Gets the timestamp (nanoseconds) at the given index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long GetTimestamp(int index) => _Entries.Get(index).TimestampNanos;

    #endregion
}
