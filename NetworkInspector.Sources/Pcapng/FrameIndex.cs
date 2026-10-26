// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Frame id → start offset of the accepted block (PCAPNG) or record header (legacy PCAP). Nothing else is stored:
/// interface, captured length, and timestamp are re-read from the block by the scanner's own parser.
/// </summary>
/// <remarks>
/// <para>
/// The index only ever holds blocks the scanner accepted as frames, so frame ids cannot shift when a block is read
/// again: acceptance is never decided a second time. Offsets of 4-aligned blocks with few distinct sizes compress to
/// well under 2 B per frame (<see cref="PackedLongSequence"/>).
/// </para>
/// <para><b>Thread-safety:</b> single writer (the scanner); any number of readers (see <see cref="PackedLongSequence"/>).</para>
/// </remarks>
internal sealed class FrameIndex
{
    #region Fields

    /// <summary>
    /// Block offsets in frame order (non-decreasing in practice, but the encoding does not rely on it: it stores signed
    /// deltas modulo 2^64). The compact encoding is what keeps the index small for very large captures.
    /// </summary>
    private readonly PackedLongSequence _BlockOffsets = new();

    #endregion

    #region Properties

    /// <summary>Number of indexed frames. Any thread.</summary>
    internal int Count => _BlockOffsets.Count;

    /// <summary>Bytes the index uses. Writer thread (tests, diagnostics).</summary>
    internal long UsedBytes => _BlockOffsets.UsedBytes;

    #endregion

    #region Public API

    /// <summary>Adds the block offset of the next accepted frame and returns its frame index. Single writer.</summary>
    /// <exception cref="InvalidOperationException">The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.</exception>
    internal int Push(long blockOffset)
    {
        int index = _BlockOffsets.Count;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(index, "frame");
        _BlockOffsets.Append(blockOffset);
        return index;
    }

    /// <summary>Seals the last partial group once the scan ended. Idempotent. Single writer.</summary>
    internal void Complete() => _BlockOffsets.Complete();

    /// <summary>
    /// Start offset of the block of frame <paramref name="index"/>. Any thread; false for an id not indexed yet.
    /// Reads through the calling thread's decoded-group cache, because callers (<c>FrameById</c>, the index-driven
    /// sequential path) mostly walk ids in order: the second read inside one group of 64 decodes it once and the rest are
    /// array loads. A binary search over offsets would use the uncached <c>PackedLongSequence.TryGet</c> instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetBlockOffset(int index, out long blockOffset) => _BlockOffsets.TryGetCached(index, out blockOffset);

    /// <summary>
    /// Start offset of the block of frame <paramref name="index"/>, and the start of the next indexed block as an upper
    /// bound of its end (−1 when <paramref name="index"/> is the newest indexed frame; the caller then bounds by the file
    /// end). Non-packet blocks between two frames only make the bound larger. Any thread; false for an id not indexed
    /// yet. The upper bound feeds the cache prefetch of the block (<c>DataBackend.PrefetchLength</c>); both values come
    /// from one cached group read, so the neighbouring offset costs no second lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetBlockSpan(int index, out long blockOffset, out long nextBlockOffset)
    {
        if (!_BlockOffsets.TryGetCachedWithNext(index, out blockOffset, out long next, out bool hasNext))
        {
            nextBlockOffset = -1;
            return false;
        }

        nextBlockOffset = hasNext
            ? next
            : -1;
        return true;
    }

    #endregion
}
