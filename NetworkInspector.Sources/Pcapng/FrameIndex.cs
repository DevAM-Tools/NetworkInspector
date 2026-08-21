// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Compact metadata for a single frame's location within the capture file.
/// Stored in a flat array for cache-friendly sequential iteration.
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
/// Index mapping frame IDs to their file positions and timestamps.
/// Uses a split-array design: offsets and timestamps are stored in separate arrays
/// for cache-friendly access patterns — sequential iteration accesses timestamps
/// together, and random access reads offsets together.
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> A single writer (the scanner) calls <see cref="Push"/>; concurrent
/// readers may call <see cref="Count"/>, <see cref="GetOffset"/>, and
/// <see cref="GetTimestamp"/> at any time.
/// <para>
/// <b>Atomic array publication:</b> Both the <c>Offsets</c> and
/// <c>Timestamps</c> arrays are wrapped in a single <see cref="IndexArrays"/> object.
/// When the backing storage grows, the writer constructs a new <see cref="IndexArrays"/>,
/// copies all existing entries into it, and publishes the new object via a single
/// <see cref="System.Threading.Volatile.Write{T}(ref T, T)"/>. This guarantees that
/// any reader who takes a <see cref="System.Threading.Volatile"/> snapshot
/// of <c>_Arrays</c> sees both arrays from the same generation — a reader can never
/// observe the new offsets array paired with the old timestamps array or vice versa.
/// After the arrays are published, each entry is written and then the count is
/// incremented with <see cref="System.Threading.Volatile.Write{T}(ref T, T)"/>, so
/// any index <c>i &lt; Count</c> observed by a reader is guaranteed to be initialised
/// on the snapshot the reader holds.
/// </para>
/// </remarks>
internal sealed class FrameIndex
{
    #region Nested Types

    /// <summary>
    /// Holds the two parallel arrays that back the index. Published atomically as
    /// a single reference to ensure readers always see a consistent pair.
    /// </summary>
    private sealed class IndexArrays
    {
        /// <summary>Frame offsets (file position + metadata).</summary>
        internal readonly FrameOffset[] Offsets;

        /// <summary>Timestamps in nanoseconds since Unix epoch.</summary>
        internal readonly long[] Timestamps;

        /// <summary>Creates a pair of arrays with the given capacity.</summary>
        internal IndexArrays(int capacity)
        {
            Offsets = capacity > 0 ? new FrameOffset[capacity] : Array.Empty<FrameOffset>();
            Timestamps = capacity > 0 ? new long[capacity] : Array.Empty<long>();
        }
    }

    #endregion

    #region Fields

    /// <summary>
    /// Current backing arrays, published atomically.
    /// Writers: create a new <see cref="IndexArrays"/>, populate, then
    /// <see cref="System.Threading.Volatile"/> Write here.
    /// Readers: take one <see cref="System.Threading.Volatile"/> Read snapshot
    /// per logical operation and use that snapshot throughout.
    /// </summary>
    private volatile IndexArrays _Arrays;

    /// <summary>Number of frames currently stored. Published with <see cref="System.Threading.Volatile"/> Write.</summary>
    private volatile int _Count;

    #endregion

    #region Properties

    /// <summary>Gets the number of frames in the index. Safe to call from any thread.</summary>
    internal int Count => _Count;

    #endregion

    #region Constructors

    /// <summary>Creates an empty frame index with optional initial capacity.</summary>
    internal FrameIndex(int capacity = 0)
    {
        _Arrays = new IndexArrays(capacity);
        _Count = 0;
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
    internal int Push(FrameOffset offset, long timestampNanos)
    {
        int count = _Count;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(count, "frame");

        // Snapshot the current arrays (single-writer; no concurrent write possible here,
        // but Volatile.Read is used for consistency with the reader-side contract).
        IndexArrays arrays = _Arrays;
        if (count >= arrays.Offsets.Length)
        {
            arrays = _Grow(count);
        }

        arrays.Offsets[count] = offset;
        arrays.Timestamps[count] = timestampNanos;
        // Publish the new count after the entry is fully written so concurrent readers
        // never observe a count that exceeds initialised data.
        _Count = count + 1;
        return count;
    }

    /// <summary>Gets the frame offset at the given index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly FrameOffset GetOffset(int index) => ref _Arrays.Offsets[index];

    /// <summary>Gets the timestamp (nanoseconds) at the given index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long GetTimestamp(int index) => _Arrays.Timestamps[index];

    /// <summary>Trims excess capacity to match the actual count. Single-writer only.</summary>
    internal void ShrinkToFit()
    {
        int count = _Count;
        IndexArrays arrays = _Arrays;
        if (count < arrays.Offsets.Length)
        {
            IndexArrays trimmed = new(count);
            Array.Copy(arrays.Offsets, trimmed.Offsets, count);
            Array.Copy(arrays.Timestamps, trimmed.Timestamps, count);
            _Arrays = trimmed;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// _Grows the backing arrays to fit at least <paramref name="required"/> entries.
    /// Creates a new <see cref="IndexArrays"/>, copies all existing data, and
    /// publishes atomically via a single <see cref="Volatile.Write{T}"/>.
    /// _Growth uses doubling (saturating at <see cref="ArrayIndexIdRange.MaxCount"/>).
    /// </summary>
    /// <param name="required">Minimum number of entries the new arrays must accommodate.</param>
    /// <returns>The newly published <see cref="IndexArrays"/>.</returns>
    private IndexArrays _Grow(int required)
    {
        IndexArrays old = _Arrays;
        int oldCapacity = old.Offsets.Length;

        // Doubling with overflow guard: a 64-bit intermediate keeps the math safe for
        // the upper bound of ArrayIndexIdRange.MaxCount entries.
        long doubled = (long)oldCapacity * 2;
        long target = Math.Max(Math.Max(doubled, required), 1024);
        int newCapacity = (int)Math.Min(target, ArrayIndexIdRange.MaxCount);

        // Build new arrays, copy existing data, then publish atomically as a unit.
        // Readers who concurrently take a snapshot will see either the old or the new
        // pair — never a mix of one old and one new array.
        IndexArrays grown = new(newCapacity);
        int count = _Count;
        Array.Copy(old.Offsets, grown.Offsets, count);
        Array.Copy(old.Timestamps, grown.Timestamps, count);
        _Arrays = grown;
        return grown;
    }

    #endregion
}
