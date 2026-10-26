// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Run-length encoded container. Efficient for dense sequential ranges.
/// Stores sorted (start, length) pairs where each run covers [start, start+length], both ends inclusive, so a run
/// of <c>Length + 1</c> values fits a <see cref="ushort"/> length even for a full 65 536-value container.
/// Runs never overlap and never touch: <see cref="Add"/> merges adjacent runs, which keeps the run count minimal.
/// <para>
/// Created by <see cref="FromContainer"/> when a completed chunk is smaller as runs (see
/// <see cref="RoaringBitmap.RunOptimizeKeys"/>); never created by plain adds to a new chunk, which start as array
/// containers. A run container stays a run container when values are added later.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. <see cref="Add"/> mutates in place and returns
/// <see langword="this"/>. Set operations must clone before calling <see cref="Add"/>
/// on a result that aliases an operand. Concurrent readers are made safe one level up, by the seqlock of the owning
/// <see cref="RoaringBitmap"/>.
/// </para>
/// </summary>
internal sealed class RunContainer : IContainer
{
    #region Fields

    // Sorted by Start; only the first RunCount entries are valid. Length is the run length minus one (inclusive end).
    private (ushort Start, ushort Length)[] _Runs;

    /// <summary>Number of valid runs in the backing array.</summary>
    internal int RunCount { get; private set; }

    #endregion

    #region Constructors

    /// <summary>Creates an empty run-length encoded container.</summary>
    internal RunContainer()
    {
        _Runs = new (ushort, ushort)[4];
        RunCount = 0;
    }

    /// <summary>Creates a run container from existing runs (used by Clone).</summary>
    private RunContainer((ushort Start, ushort Length)[] runs, int count)
    {
        _Runs = runs;
        RunCount = count;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    /// <remarks>O(runs): summed on every call, not cached (unlike the array and bitmap containers).</remarks>
    public int Cardinality
    {
        get
        {
            int total = 0;
            for (int i = 0; i < RunCount; i++)
            {
                total += _Runs[i].Length + 1;
            }
            return total;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns 0 for an empty container (the interface leaves that case undefined).</remarks>
    public ushort Min
    {
        get
        {
            if (RunCount > 0)
            {
                return _Runs[0].Start;
            }
            return ushort.MinValue;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns 0 for an empty container (the interface leaves that case undefined).</remarks>
    public ushort Max
    {
        get
        {
            if (RunCount == 0)
            {
                return ushort.MinValue;
            }
            (ushort Start, ushort Length) = _Runs[RunCount - 1];
            return (ushort)(Start + Length);
        }
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public bool Contains(ushort value)
    {
        // Binary search over the sorted runs for the one whose [start, end] covers value: O(log runs), no allocation.
        // The unsigned shift averages lo and hi without signed overflow.
        int lo = 0, hi = RunCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ushort start = _Runs[mid].Start;
            ushort end = (ushort)(start + _Runs[mid].Length);
            if (value < start)
            {
                hi = mid - 1;
            }
            else if (value > end)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>The copy's backing array is sized exactly to <see cref="RunCount"/> and may have length 0.</remarks>
    public IContainer Clone()
    {
        (ushort Start, ushort Length)[] runsCopy = new (ushort, ushort)[RunCount];
        Array.Copy(_Runs, runsCopy, RunCount);
        return new RunContainer(runsCopy, RunCount);
    }

    /// <inheritdoc/>
    /// <remarks>Mutates in place and always returns <see langword="this"/>; a run container never changes its type.</remarks>
    public IContainer Add(ushort value)
    {
        // Duplicate add: nothing to change (callers rely on an unchanged cardinality to detect this).
        if (Contains(value))
        {
            return this;
        }

        // Find the insertion point: the first run that starts after value (linear scan, O(runs)). A run container is
        // only built when its run list is smaller than the array or bitmap form, so the run count starts small, but
        // later adds can grow it.
        int idx = 0;
        while (idx < RunCount && _Runs[idx].Start < value)
        {
            idx++;
        }

        // Check if can extend previous run
        if (idx > 0)
        {
            ref (ushort Start, ushort Length) prev = ref _Runs[idx - 1];
            ushort prevEnd = (ushort)(prev.Start + prev.Length);
            if (prevEnd + 1 == value)
            {
                prev.Length++;
                // Check if we can merge with next run
                _TryMergeAt(idx - 1);
                return this;
            }
        }

        // Check if can extend next run
        if (idx < RunCount && _Runs[idx].Start == value + 1)
        {
            _Runs[idx].Start = value;
            _Runs[idx].Length++;
            // Check merge with prev
            if (idx > 0)
            {
                _TryMergeAt(idx - 1);
            }
            return this;
        }

        // Insert new run
        _InsertRunAt(idx, value, 0);
        return this;
    }

    /// <inheritdoc/>
    public IContainer And(IContainer other)
    {
        // Pick the cheapest strategy per operand type: run-vs-run is O(runs) on the run lists, run-vs-array binary-searches
        // each run's bounds in the array, run-vs-bitmap expands this side into a bitmap with word masks and uses the
        // bitmap AND. Only an unknown IContainer implementation takes the value-by-value fallback.
        if (other is RunContainer otherRun)
        {
            return _AndRunContainer(otherRun);
        }

        if (other is BitmapContainer bmp)
        {
            BitmapContainer selfBmp = new();
            SetRangesIn(selfBmp.Bitmap);
            return selfBmp.And(bmp);
        }

        if (other is ArrayContainer arr)
        {
            return _AndArrayContainer(arr);
        }

        // Fallback: probe other container value-by-value
        ArrayContainer result = new();
        IContainer r = result;
        for (int i = 0; i < RunCount; i++)
        {
            ushort start = _Runs[i].Start;
            ushort end = (ushort)(start + _Runs[i].Length);
            for (ushort v = start; v <= end; v++)
            {
                if (other.Contains(v))
                {
                    r = r.Add(v);
                }
                if (v == ushort.MaxValue)
                {
                    break;
                }
            }
        }
        return r;
    }

    /// <summary>
    /// Range-based intersection for two sorted run lists — O(runs) without per-value iteration.
    /// </summary>
    private RunContainer _AndRunContainer(RunContainer other)
    {
        // Two-pointer sweep: intersect the current pair of runs, then advance the run that ends first (both when they end
        // together). Ends are widened to int so start + length cannot wrap at 65535. Overlaps of non-adjacent input runs
        // are themselves non-adjacent, so appending with _InsertRunAt keeps the "no touching runs" invariant.
        RunContainer result = new();
        int i = 0;
        int j = 0;
        while (i < RunCount && j < other.RunCount)
        {
            (ushort aStart, ushort aLen) = _Runs[i];
            (ushort bStart, ushort bLen) = other._Runs[j];
            int aEnd = aStart + aLen;
            int bEnd = bStart + bLen;
            int intersectStart = Math.Max(aStart, bStart);
            int intersectEnd = Math.Min(aEnd, bEnd);
            if (intersectStart <= intersectEnd)
            {
                result._InsertRunAt(result.RunCount, (ushort)intersectStart, (ushort)(intersectEnd - intersectStart));
            }

            if (aEnd < bEnd)
            {
                i++;
            }
            else if (bEnd < aEnd)
            {
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }

        return result;
    }

    /// <summary>
    /// Intersect each run with a sorted array via binary search on run bounds — avoids full run expansion.
    /// </summary>
    private IContainer _AndArrayContainer(ArrayContainer arr)
    {
        ArrayContainer result = new();
        IContainer r = result;
        for (int i = 0; i < RunCount; i++)
        {
            ushort start = _Runs[i].Start;
            ushort end = (ushort)(start + _Runs[i].Length);
            int lo = 0;
            int hi = arr.Cardinality - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (arr.ValueAt(mid) < start)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            for (int k = lo; k < arr.Cardinality && arr.ValueAt(k) <= end; k++)
            {
                r = r.Add(arr.ValueAt(k));
            }
        }
        return r;
    }

    /// <inheritdoc/>
    public IContainer Or(IContainer other)
    {
        // Convert to bitmap for large unions, add both sides. The sum of cardinalities is an upper bound of the union; above
        // ArrayContainer.MaxCapacity (4096) the result could not be a valid array container, so go through a bitmap.
        if (Cardinality + other.Cardinality > ArrayContainer.MaxCapacity)
        {
            BitmapContainer bitmap = new();
            IContainer b = bitmap;
            for (int i = 0; i < RunCount; i++)
            {
                ushort start = _Runs[i].Start;
                ushort end = (ushort)(start + _Runs[i].Length);
                for (ushort v = start; v <= end; v++)
                {
                    b = b.Add(v);
                    if (v == ushort.MaxValue)
                    {
                        break;
                    }
                }
            }
            return b.Or(other);
        }

        // Small union: add other's elements to a clone of this.
        // Must NOT start from `this` directly: RunContainer.Add is in-place and
        // returns `this`, so any mutation would corrupt the left operand.
        IContainer result = Clone();
        if (other is ArrayContainer arr)
        {
            for (int i = 0; i < arr.Cardinality; i++)
            {
                result = result.Add(arr.ValueAt(i));
            }
        }
        else if (other is RunContainer run)
        {
            // Iterate all runs and add each value
            for (int i = 0; i < run.RunCount; i++)
            {
                ushort start = run._Runs[i].Start;
                ushort end = (ushort)(start + run._Runs[i].Length);
                for (ushort v = start; v <= end; v++)
                {
                    result = result.Add(v);
                    if (v == ushort.MaxValue)
                    {
                        break;
                    }
                }
            }
        }
        else if (other is BitmapContainer bmpOther)
        {
            // Bitmap has all values encoded; iterate set bits
            for (int w = 0; w < BitmapContainer.BitmapSize; w++)
            {
                ulong word = bmpOther.Bitmap[w];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    result = result.Add((ushort)(w * 64 + bit));
                    word &= word - 1; // clear lowest set bit
                }
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public IContainer AndNot(IContainer other)
    {
        // Run-vs-run: set this side's ranges in a bitmap with word masks, clear the other side's ranges, then let
        // FromPopcount pick the container type from the cardinality. Run-vs-bitmap reuses the bitmap AND NOT.
        if (other is RunContainer otherRun)
        {
            BitmapContainer bitmap = new();
            SetRangesIn(bitmap.Bitmap);
            otherRun.ClearRangesIn(bitmap.Bitmap);
            return BitmapContainer.FromPopcount(bitmap);
        }

        if (other is BitmapContainer bmp)
        {
            BitmapContainer selfBmp = new();
            SetRangesIn(selfBmp.Bitmap);
            return selfBmp.AndNot(bmp);
        }

        // Fallback: materialize as array, removing elements present in other
        ArrayContainer result = new();
        IContainer r = result;
        for (int i = 0; i < RunCount; i++)
        {
            ushort start = _Runs[i].Start;
            ushort end = (ushort)(start + _Runs[i].Length);
            for (ushort v = start; v <= end; v++)
            {
                if (!other.Contains(v))
                {
                    r = r.Add(v);
                }
                if (v == ushort.MaxValue)
                {
                    break;
                }
            }
        }
        return r;
    }

    /// <inheritdoc/>
    public IContainer Xor(IContainer other)
    {
        // Materialize to bitmap for XOR — symmetric difference is complex with runs. The fill below goes value by value;
        // SetRangesIn (word masks, as used by AndNot) fills a bitmap faster for long runs.
        BitmapContainer bitmap = new();
        for (int i = 0; i < RunCount; i++)
        {
            ushort start = _Runs[i].Start;
            ushort end = (ushort)(start + _Runs[i].Length);
            for (ushort v = start; v <= end; v++)
            {
                bitmap.Add(v);
                if (v == ushort.MaxValue)
                {
                    break;
                }
            }
        }
        return bitmap.Xor(other);
    }

    #endregion

    #region Internal helpers (Bitmap interop)

    /// <summary>
    /// Returns the i-th run as <c>(start, end)</c> where <c>end</c> is inclusive.
    /// </summary>
    internal (ushort Start, ushort End) RunAt(int index)
    {
        (ushort start, ushort length) = _Runs[index];
        return (start, (ushort)(start + length));
    }

    /// <summary>
    /// Sets all bits covered by this container's runs in <paramref name="bitmap"/>
    /// (must be sized <see cref="BitmapContainer.BitmapSize"/>). Used by
    /// <c>BitmapContainer.Or(RunContainer)</c> to avoid the value-by-value loop.
    /// </summary>
    internal void SetRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < RunCount; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            _ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.Or);
        }
    }

    /// <summary>
    /// Clears all bits covered by this container's runs in <paramref name="bitmap"/>.
    /// Used by <c>BitmapContainer.AndNot(RunContainer)</c>.
    /// </summary>
    internal void ClearRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < RunCount; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            _ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.AndNot);
        }
    }

    /// <summary>
    /// Toggles all bits covered by this container's runs in <paramref name="bitmap"/>.
    /// Used by <c>BitmapContainer.Xor(RunContainer)</c>.
    /// </summary>
    internal void ToggleRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < RunCount; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            _ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.Xor);
        }
    }

    private enum RangeOp
    {
        Or,
        AndNot,
        Xor,
    }

    /// <summary>
    /// Applies a bitwise operation across the inclusive bit range [<paramref name="startBit"/>,
    /// <paramref name="endBit"/>] in <paramref name="bitmap"/>. Uses ulong-word masks rather than
    /// per-bit loops, so a 65,536-bit run costs at most ~1024 ulong ops.
    /// </summary>
    private static void _ApplyRangeMask(ulong[] bitmap, int startBit, int endBit, RangeOp op)
    {
        // Single-word fast path: compute one mask covering the bit range and apply it.
        int firstWord = startBit >> 6;
        int lastWord = endBit >> 6;
        int firstBit = startBit & 63;
        int lastBit = endBit & 63;

        if (firstWord == lastWord)
        {
            // Construct mask of bits [firstBit..lastBit] inside one ulong without 64-bit shift
            // overflow: when lastBit == 63 and firstBit == 0 the mask must equal ~0UL.
            int width = lastBit - firstBit + 1;
            ulong mask = width == 64 ? ~0UL : ((1UL << width) - 1) << firstBit;
            _ApplyOp(ref bitmap[firstWord], mask, op);
            return;
        }

        // Multi-word path
        ulong firstMask = ~0UL << firstBit;       // high bits of the first word
        ulong lastMask = lastBit == 63 ? ~0UL : (1UL << (lastBit + 1)) - 1; // low bits of the last word

        _ApplyOp(ref bitmap[firstWord], firstMask, op);
        for (int w = firstWord + 1; w < lastWord; w++)
        {
            _ApplyOp(ref bitmap[w], ~0UL, op);
        }
        _ApplyOp(ref bitmap[lastWord], lastMask, op);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ApplyOp(ref ulong word, ulong mask, RangeOp op)
    {
        switch (op)
        {
            case RangeOp.Or:
                word |= mask;
                break;
            case RangeOp.AndNot:
                word &= ~mask;
                break;
            case RangeOp.Xor:
                word ^= mask;
                break;
        }
    }

    #endregion

    #region Run optimization

    /// <summary>Serialized size of a bitmap container in bytes (1024 words; Roaring format).</summary>
    internal const int BitmapContainerBytes = BitmapContainer.BitmapSize * sizeof(ulong);

    /// <summary>Serialized size in bytes of a run container with <paramref name="runCount"/> runs: a count and two
    /// <see cref="ushort"/> values per run (Roaring format).</summary>
    internal static int SerializedSize(int runCount) => 2 + (4 * runCount);

    /// <summary>
    /// Size in bytes used to decide whether the run form is smaller: arrays count a 2-byte header plus 2 bytes per value,
    /// bitmaps a fixed 8192 bytes, run containers <see cref="SerializedSize"/>. The conversion in
    /// <see cref="RoaringBitmap.RunOptimizeKeys"/> happens only when the run size is strictly smaller. This is a sizing
    /// heuristic for in-memory choice; nothing is serialized.
    /// </summary>
    /// <param name="container">The container to size.</param>
    internal static int SerializedSizeOf(IContainer container) => container switch
    {
        ArrayContainer array => 2 + (2 * array.Cardinality),
        RunContainer run => SerializedSize(run.RunCount),
        _ => BitmapContainerBytes,
    };

    /// <summary>
    /// Number of maximal runs of consecutive values in <paramref name="container"/>: one per value that does not
    /// follow its predecessor. O(cardinality) for arrays, O(1024) word operations for bitmaps.
    /// </summary>
    /// <param name="container">The container to inspect.</param>
    /// <exception cref="UnreachableException">The container is none of the three known implementations.</exception>
    internal static int CountRuns(IContainer container)
    {
        // Read-only and allocation-free: this runs for every container of every finished chunk before it is converted.
        switch (container)
        {
            case RunContainer run:
                return run.RunCount;

            case ArrayContainer array:
            {
                int count = array.Cardinality;
                int runs = count > 0 ? 1 : 0;
                for (int i = 1; i < count; i++)
                {
                    if (array.ValueAt(i) != array.ValueAt(i - 1) + 1)
                    {
                        runs++;
                    }
                }

                return runs;
            }

            case BitmapContainer bitmap:
            {
                // A run starts at a set bit whose lower neighbour is clear; the carry brings in bit 63 of the
                // previous word.
                int runs = 0;
                ulong carry = 0;
                foreach (ulong word in bitmap.Bitmap)
                {
                    runs += BitOperations.PopCount(word & ~((word << 1) | carry));
                    carry = word >> 63;
                }

                return runs;
            }

            default:
                throw new UnreachableException("IContainer has exactly three implementations.");
        }
    }

    /// <summary>
    /// The run container holding the same values as <paramref name="container"/> (an array or bitmap container).
    /// Bitmap runs are found word by word: fill the zeros below the first set bit, skip full words, then the first
    /// clear bit ends the run (the CRoaring <c>run_container_from_bitmap</c> scan).
    /// </summary>
    /// <param name="container">The container to convert; it is not modified.</param>
    /// <exception cref="UnreachableException">The container is none of the three known implementations.</exception>
    internal static RunContainer FromContainer(IContainer container)
    {
        // The source is left untouched so the old container stays valid for any reader that still holds it.
        if (container is RunContainer existing)
        {
            return (RunContainer)existing.Clone();
        }

        // Exact capacity: the runs are counted first, so the array never grows.
        (ushort Start, ushort Length)[] runs = new (ushort, ushort)[Math.Max(CountRuns(container), 1)];
        int count = 0;
        switch (container)
        {
            case ArrayContainer array:
            {
                int cardinality = array.Cardinality;
                int i = 0;
                while (i < cardinality)
                {
                    ushort start = array.ValueAt(i);
                    int j = i;
                    while (j + 1 < cardinality && array.ValueAt(j + 1) == array.ValueAt(j) + 1)
                    {
                        j++;
                    }

                    runs[count++] = (start, (ushort)(array.ValueAt(j) - start));
                    i = j + 1;
                }

                break;
            }

            case BitmapContainer bitmap:
            {
                ulong[] words = bitmap.Bitmap;
                int index = 0;
                ulong current = words[0];
                while (true)
                {
                    while (current == 0 && index < words.Length - 1)
                    {
                        current = words[++index];
                    }

                    if (current == 0)
                    {
                        break;
                    }

                    // First set bit: start of a run. Fill the bits below it so the next clear bit ends the run.
                    int start = (index * 64) + BitOperations.TrailingZeroCount(current);
                    ulong filled = current | (current - 1);
                    while (filled == ulong.MaxValue && index < words.Length - 1)
                    {
                        filled = words[++index];
                    }

                    if (filled == ulong.MaxValue)
                    {
                        // The run reaches value 65535.
                        runs[count++] = ((ushort)start, (ushort)(ushort.MaxValue - start));
                        break;
                    }

                    // Exclusive end: the first clear bit; clear the run's bits and continue in the same word.
                    // filled + 1 carries through the trailing ones, so filled & (filled + 1) keeps only the bits above
                    // the run (and none of the artificially filled low bits).
                    int end = (index * 64) + BitOperations.TrailingZeroCount(~filled);
                    runs[count++] = ((ushort)start, (ushort)(end - 1 - start));
                    current = filled & (filled + 1);
                }

                break;
            }

            default:
                throw new UnreachableException("IContainer has exactly three implementations.");
        }

        return new RunContainer(runs, count);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Merges run <paramref name="idx"/> with run <paramref name="idx"/> + 1 when they overlap or touch (the next run
    /// starts at most one value after this run's end). Called after an add widened a run, to restore the invariant that
    /// runs never touch.
    /// </summary>
    private void _TryMergeAt(int idx)
    {
        if (idx + 1 >= RunCount)
        {
            return;
        }
        ushort end = (ushort)(_Runs[idx].Start + _Runs[idx].Length);
        if (end + 1 >= _Runs[idx + 1].Start)
        {
            ushort newEnd = Math.Max(end, (ushort)(_Runs[idx + 1].Start + _Runs[idx + 1].Length));
            _Runs[idx].Length = (ushort)(newEnd - _Runs[idx].Start);
            _RemoveRunAt(idx + 1);
        }
    }

    /// <summary>Inserts a run at position <paramref name="idx"/>, shifting later runs up and growing the backing array when full.</summary>
    private void _InsertRunAt(int idx, ushort start, ushort length)
    {
        // A zero-length run array (Clone of an empty container) grows to the default capacity 4; doubling 0 stays 0.
        if (RunCount == _Runs.Length)
        {
            Array.Resize(ref _Runs, Math.Max(_Runs.Length * 2, 4));
        }
        if (idx < RunCount)
        {
            Array.Copy(_Runs, idx, _Runs, idx + 1, RunCount - idx);
        }
        _Runs[idx] = (start, length);
        RunCount++;
    }

    /// <summary>Removes the run at <paramref name="idx"/> by shifting later runs down. The backing array keeps its capacity.</summary>
    private void _RemoveRunAt(int idx)
    {
        if (idx < RunCount - 1)
        {
            Array.Copy(_Runs, idx + 1, _Runs, idx, RunCount - idx - 1);
        }
        RunCount--;
    }

    #endregion
}
