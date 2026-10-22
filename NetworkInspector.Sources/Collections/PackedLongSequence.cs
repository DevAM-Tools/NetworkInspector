// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Collections;

/// <summary>
/// Append-only sequence of <see cref="long"/> values stored in groups of 64 with frame-of-reference deltas in
/// bit-sliced planes. Memory per entry: 0.375 B + width/8 B, where width is the bit width of the largest residual of a
/// group (0 for a constant step, 64 for random values).
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout of a sealed group</b> (see SOURCE_GUIDE §11.6 for the worked example):
/// <c>word 0 = value[0]</c>, <c>word 1 = minDelta</c> (smallest signed step of the group), then <c>width</c> bit planes
/// of the residuals <c>(value[j] − value[j−1] − minDelta) &gt;&gt; shift</c>, where <c>shift</c> is the number of
/// trailing zero bits all residuals share (2 for 4-aligned PCAPNG block offsets). One group info word per group holds
/// <c>(wordOffset &lt;&lt; 13) | (shift &lt;&lt; 7) | width</c>. A lookup is
/// <c>value[j] = base + j·minDelta + (Σ_b 2^b · PopCount(plane_b &amp; mask(j))) &lt;&lt; shift</c>: <c>width</c>
/// hardware population counts, independent of <c>j</c>.
/// </para>
/// <para>
/// <b>Exactness:</b> deltas, residuals, and sums are computed unchecked modulo 2^64, so every <see cref="long"/>
/// sequence (including wrap-around jumps between <see cref="long.MinValue"/> and <see cref="long.MaxValue"/>) reads
/// back exactly.
/// </para>
/// <para>
/// <b>Thread-safety:</b> single writer (<see cref="Append"/>, <see cref="Complete"/>), any number of readers
/// (<see cref="TryGet"/>, <see cref="TryGetCached"/>, <see cref="Count"/>). Publish order: staging slot (volatile) → <c>_Count</c> (volatile);
/// group words → group info slot (plain writes; chunks are installed with a volatile write) → <c>_SealedGroups</c>
/// (volatile write, the release that publishes both). A reader that reads a staging slot
/// re-reads <c>_SealedGroups</c> afterwards; if the group was sealed meanwhile, the slot may already belong to the
/// next group and the reader decodes the sealed group instead. All three reads are acquires, so no torn value is ever
/// returned and neither path needs a lock or an allocation.
/// </para>
/// </remarks>
internal sealed class PackedLongSequence
{
    #region Constants

    /// <summary>Log₂ of the group size.</summary>
    internal const int GroupShift = 6;

    /// <summary>Values per group: 64, one lane per bit of a plane word.</summary>
    internal const int GroupSize = 1 << GroupShift;

    private const int _GroupMask = GroupSize - 1;
    private const int _HeaderWords = 2;
    private const int _InfoChunkShift = 13;
    private const int _InfoChunkMask = (1 << _InfoChunkShift) - 1;
    private const int _InfoOffsetShift = 13;
    private const int _InfoShiftShift = 7;
    private const long _InfoWidthMask = 0x7F;
    private const long _InfoShiftMask = 0x3F;

    #endregion

    #region Fields

    private readonly PackedWordStore _Words = new();

    /// <summary>
    /// One info word per sealed group in 64 KiB chunks. Plain slots: the sealed group count is their publish (a reader
    /// only reads groups below the count it acquired), so no per-slot volatile access or bounds re-check is needed.
    /// </summary>
    private readonly ChunkedOuterArray<long[]> _GroupInfo = new(_InfoChunkShift);
    private static readonly Func<long[]> _NewInfoChunk = static () => new long[1 << _InfoChunkShift];
    private int _GroupCount;

    /// <summary>Last issued sequence id; ids start at 1, so 0 marks an empty <see cref="DecodedGroupCache"/> way.</summary>
    private static long _LastId;

    /// <summary>Process-unique key of this sequence in the per-thread <see cref="DecodedGroupCache"/>.</summary>
    private readonly long _Id = Interlocked.Increment(ref _LastId);

    private readonly long[] _Staging = new long[GroupSize];
    private readonly ulong[] _Scratch = new ulong[GroupSize];
    private volatile int _Count;
    private volatile int _SealedGroups;
    private bool _Completed;

    #endregion

    #region Properties

    /// <summary>Number of published values. Any thread.</summary>
    internal int Count => _Count;

    /// <summary>Bytes used by words, group infos, and the staging buffer. Writer thread (tests, diagnostics).</summary>
    internal long UsedBytes =>
        (_Words.UsedWords * sizeof(ulong)) + ((long)_GroupCount * sizeof(long)) + (GroupSize * sizeof(long));

    #endregion

    #region Public API

    /// <summary>Appends one value. Single writer.</summary>
    /// <exception cref="InvalidOperationException">
    /// The sequence is complete, or the next index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    internal void Append(long value)
    {
        if (_Completed)
        {
            throw new InvalidOperationException("The sequence is complete.");
        }

        int index = _Count;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(index, "sequence entry");

        // Slot first, then the count: a reader that sees the count sees the slot.
        int lane = index & _GroupMask;
        Volatile.Write(ref _Staging[lane], value);
        _Count = index + 1;

        if (lane == _GroupMask)
        {
            _SealGroup(GroupSize);
        }
    }

    /// <summary>Seals the partial last group so the staging buffer is no longer read. Idempotent. Single writer.</summary>
    internal void Complete()
    {
        if (_Completed)
        {
            return;
        }

        int partial = _Count & _GroupMask;
        if (partial != 0)
        {
            _SealGroup(partial);
        }

        _Completed = true;
    }

    /// <summary>Reads the value at <paramref name="index"/>. Any thread. False (and 0) for an index outside 0 … Count−1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGet(int index, out long value)
    {
        if ((uint)index >= (uint)_Count)
        {
            value = 0;
            return false;
        }

        int group = index >> GroupShift;
        int lane = index & _GroupMask;
        if (group < _SealedGroups)
        {
            value = _ReadSealed(group, lane);
            return true;
        }

        // Open group: read the slot, then re-check that the group was not sealed (and the slot reused) meanwhile.
        long staged = Volatile.Read(ref _Staging[lane]);
        if (group < _SealedGroups)
        {
            value = _ReadSealed(group, lane);
            return true;
        }

        value = staged;
        return true;
    }

    /// <summary>
    /// Reads the value at <paramref name="index"/> like <see cref="TryGet"/>, but through the calling thread's
    /// <see cref="DecodedGroupCache"/>: the second read of a sealed group in a row decodes all 64 values once, and later
    /// reads of that group are an array load. Use it where callers walk ids in order (frame ids of
    /// <c>FrameById</c>); a binary search should call <see cref="TryGet"/>, which leaves the cache alone. Any thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetCached(int index, out long value)
    {
        // Out of range and the open group: the uncached path (bounds, staging re-check) is already exact.
        int group = index >> GroupShift;
        if ((uint)index >= (uint)_Count || group >= _SealedGroups)
        {
            return TryGet(index, out value);
        }

        int lane = index & _GroupMask;
        DecodedGroupCache cache = DecodedGroupCache.Current;
        int way = cache.Find(_Id, group, out bool decoded);
        if (decoded)
        {
            value = cache.Value(way, lane);
            return true;
        }

        if (way >= 0)
        {
            // Second touch of the group: the walk stays here for a while, so decode it in full once.
            _DecodeGroup(group, cache.Slots(way));
            cache.MarkDecoded(way);
            value = cache.Value(way, lane);
            return true;
        }

        // First touch: remember the group, decode only this lane (random access stops here every time).
        cache.Touch(_Id, group);
        value = _ReadSealed(group, lane);
        return true;
    }

    /// <summary>
    /// Reads the value at <paramref name="index"/> like <see cref="TryGetCached"/>, and the value at
    /// <paramref name="index"/> + 1 when it is published (<paramref name="hasNext"/>; otherwise <paramref name="next"/>
    /// is 0). The second value costs no extra plane load: a decoded group yields both from the cache, and a first touch
    /// decodes both lanes in one pass. Any thread.
    /// </summary>
    internal bool TryGetCachedWithNext(int index, out long value, out long next, out bool hasNext)
    {
        int count = _Count;
        if ((uint)index >= (uint)count)
        {
            value = 0;
            next = 0;
            hasNext = false;
            return false;
        }

        hasNext = index + 1 < count;
        int group = index >> GroupShift;
        int lane = index & _GroupMask;

        // Open group, or the last lane of a group (the next value lives in the next group): two plain reads.
        if (group >= _SealedGroups || lane == _GroupMask)
        {
            _ = TryGetCached(index, out value);
            next = 0;
            if (hasNext)
            {
                _ = TryGet(index + 1, out next);
            }

            return true;
        }

        // Sealed group, lane ≤ 62: index + 1 lies in the same sealed group. A lane past the end of a partial group is
        // never returned because hasNext is false there.
        DecodedGroupCache cache = DecodedGroupCache.Current;
        int way = cache.Find(_Id, group, out bool decoded);
        if (!decoded && way >= 0)
        {
            _DecodeGroup(group, cache.Slots(way));
            cache.MarkDecoded(way);
            decoded = true;
        }

        if (decoded)
        {
            value = cache.Value(way, lane);
            next = hasNext
                ? cache.Value(way, lane + 1)
                : 0;
            return true;
        }

        cache.Touch(_Id, group);
        value = _ReadSealedPair(group, lane, out long pairNext);
        next = hasNext
            ? pairNext
            : 0;
        return true;
    }

    #endregion

    #region Private helpers

    /// <summary>Decodes lanes <paramref name="lane"/> and <paramref name="lane"/> + 1 (≤ 63) of a sealed group in one pass.</summary>
    private long _ReadSealedPair(int group, int lane, out long next)
    {
        long info = _GroupInfo.GetChunk(group >> _InfoChunkShift)![group & _InfoChunkMask];
        int width = (int)(info & _InfoWidthMask);
        int shift = (int)((info >> _InfoShiftShift) & _InfoShiftMask);
        ReadOnlySpan<ulong> words = _Words.Get(info >>> _InfoOffsetShift, _HeaderWords + width);

        ulong sum = BitPlanes.PrefixSumPair(
            words[_HeaderWords..], BitPlanes.InclusivePrefixMask(lane), BitPlanes.InclusivePrefixMask(lane + 1), out ulong sumNext);
        next = unchecked((long)(words[0] + ((ulong)(lane + 1) * words[1]) + (sumNext << shift)));
        return unchecked((long)(words[0] + ((ulong)lane * words[1]) + (sum << shift)));
    }

    /// <summary>Encodes the first <paramref name="length"/> staging slots as the next sealed group and publishes it.</summary>
    private void _SealGroup(int length)
    {
        ReadOnlySpan<long> values = _Staging.AsSpan(0, length);

        // Frame of reference: the smallest signed delta; residuals are unsigned distances to it (exact mod 2^64).
        long minDelta = 0;
        for (int j = 1; j < length; j++)
        {
            long delta = unchecked(values[j] - values[j - 1]);
            minDelta = j == 1 || delta < minDelta ? delta : minDelta;
        }

        Span<ulong> residuals = _Scratch.AsSpan(0, length);
        residuals[0] = 0;
        ulong any = 0;
        for (int j = 1; j < length; j++)
        {
            residuals[j] = unchecked((ulong)(values[j] - values[j - 1]) - (ulong)minDelta);
            any |= residuals[j];
        }

        // Common trailing zeros (alignment) are shifted out once per group, e.g. 2 bits for PCAPNG block offsets.
        int shift = any == 0 ? 0 : BitOperations.TrailingZeroCount(any);
        ulong max = 0;
        for (int j = 1; j < length; j++)
        {
            residuals[j] >>= shift;
            max = residuals[j] > max ? residuals[j] : max;
        }

        int width = max == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(max);

        // Words first; the info publish and the sealed count make them visible.
        long offset = _Words.Reserve(_HeaderWords + width);
        Span<ulong> words = _Words.GetWritable(offset, _HeaderWords + width);
        words[0] = unchecked((ulong)values[0]);
        words[1] = unchecked((ulong)minDelta);
        BitPlanes.Scatter(residuals, width, words[_HeaderWords..]);

        // offset < 2^31 · 2^13 words, so offset << 13 stays below 2^57; width ≤ 64 and shift ≤ 63 fit their fields.
        // The group count is below 2^25 (2^31 values / 64), so the chunk index fits the outer array.
        int group = _GroupCount;
        long[] infoChunk = _GroupInfo.GetOrAllocateChunk(group >> _InfoChunkShift, _NewInfoChunk);
        infoChunk[group & _InfoChunkMask] = (offset << _InfoOffsetShift) | ((long)shift << _InfoShiftShift) | (uint)width;
        _GroupCount = group + 1;

        // The sealed group is the one holding the last published value; a plain volatile write publishes it.
        _SealedGroups = ((_Count - 1) >> GroupShift) + 1;
    }

    /// <summary>Decodes lane <paramref name="lane"/> of the sealed group <paramref name="group"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long _ReadSealed(int group, int lane)
    {
        // The caller acquired a sealed count above group, so the chunk and the slot are published.
        long info = _GroupInfo.GetChunk(group >> _InfoChunkShift)![group & _InfoChunkMask];
        int width = (int)(info & _InfoWidthMask);
        int shift = (int)((info >> _InfoShiftShift) & _InfoShiftMask);
        ReadOnlySpan<ulong> words = _Words.Get(info >>> _InfoOffsetShift, _HeaderWords + width);

        ulong residualSum = BitPlanes.PrefixSum(words[_HeaderWords..], BitPlanes.InclusivePrefixMask(lane)) << shift;
        return unchecked((long)(words[0] + ((ulong)lane * words[1]) + residualSum));
    }

    /// <summary>Decodes all 64 lanes of the sealed group <paramref name="group"/> into <paramref name="target"/>.</summary>
    /// <remarks>
    /// Lanes past the end of a partial last group decode to values no index reaches (their residuals are 0). The caller
    /// acquired a sealed count above <paramref name="group"/>, so the words are published.
    /// </remarks>
    private void _DecodeGroup(int group, Span<long> target)
    {
        long info = _GroupInfo.GetChunk(group >> _InfoChunkShift)![group & _InfoChunkMask];
        int width = (int)(info & _InfoWidthMask);
        int shift = (int)((info >> _InfoShiftShift) & _InfoShiftMask);
        ReadOnlySpan<ulong> words = _Words.Get(info >>> _InfoOffsetShift, _HeaderWords + width);

        // Residuals first, then a running sum: value[j] = value[j−1] + minDelta + (residual[j] << shift), modulo 2^64.
        Span<ulong> lanes = MemoryMarshal.Cast<long, ulong>(target);
        BitPlanes.ExtractAll(words[_HeaderWords..], lanes);
        ulong value = words[0];
        ulong minDelta = words[1];
        lanes[0] = value;
        for (int lane = 1; lane < GroupSize; lane++)
        {
            value = unchecked(value + minDelta + (lanes[lane] << shift));
            lanes[lane] = value;
        }
    }

    #endregion
}
