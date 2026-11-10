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

    #region Configuration and state

    /// <summary>
    /// The runs, sorted by <c>Start</c>; only the first <see cref="RunCount"/> entries are valid. <c>Length</c> is the run
    /// length minus one (the end is inclusive), so a full 65 536-value run fits a <see cref="ushort"/>.
    /// </summary>
    private (ushort Start, ushort Length)[] _Runs;

    /// <summary>Number of valid runs in the backing array.</summary>
    internal int RunCount { get; private set; }

    /// <summary>Creates an empty run-length encoded container.</summary>
    internal RunContainer()
    {
        _Runs = new (ushort, ushort)[4];
        RunCount = 0;
    }

    /// <summary>Creates a run container that takes over existing runs (used by <see cref="Clone"/> and <see cref="FromContainer"/>).</summary>
    /// <param name="runs">The backing array; sorted, non-overlapping, non-touching runs; ownership passes to the container.</param>
    /// <param name="count">The number of valid entries at the start of <paramref name="runs"/>.</param>
    private RunContainer((ushort Start, ushort Length)[] runs, int count)
    {
        _Runs = runs;
        RunCount = count;
    }

    #endregion

    #region Contents and copy

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

    #endregion

    #region Adding values

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

        // Extend the previous run upwards when the value is directly above its end.
        if (idx > 0)
        {
            ref (ushort Start, ushort Length) prev = ref _Runs[idx - 1];
            ushort prevEnd = (ushort)(prev.Start + prev.Length);
            if (prevEnd + 1 == value)
            {
                prev.Length++;

                // The widened run may now touch the next one: merge to keep the "no touching runs" invariant.
                _TryMergeAt(idx - 1);
                return this;
            }
        }

        // Extend the next run downwards when the value is directly below its start.
        if (idx < RunCount && _Runs[idx].Start == value + 1)
        {
            _Runs[idx].Start = value;
            _Runs[idx].Length++;

            // The widened run may now touch the previous one: merge.
            if (idx > 0)
            {
                _TryMergeAt(idx - 1);
            }

            return this;
        }

        // The value touches no run: insert a new single-value run.
        _InsertRunAt(idx, value, 0);
        return this;
    }

    /// <summary>
    /// Merges run <paramref name="idx"/> with run <paramref name="idx"/> + 1 when they overlap or touch (the next run
    /// starts at most one value after this run's end). Called after an add widened a run, to restore the invariant that
    /// runs never touch.
    /// </summary>
    /// <param name="idx">The index of the run to merge with its successor; a value at or above <c>RunCount - 1</c> does nothing.</param>
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
    /// <param name="idx">The insertion index, 0 to <c>RunCount</c>; <c>RunCount</c> appends without shifting.</param>
    /// <param name="start">The first value of the run; the caller keeps the runs sorted and non-touching.</param>
    /// <param name="length">The run length minus one (the end is inclusive).</param>
    private void _InsertRunAt(int idx, ushort start, ushort length)
    {
        // A zero-length run array (Clone of an empty container) grows to the default capacity 4; doubling 0 stays 0.
        if (RunCount == _Runs.Length)
        {
            Array.Resize(ref _Runs, Math.Max(_Runs.Length * 2, 4));
        }

        // Open a gap only for a real insert; an append shifts nothing.
        if (idx < RunCount)
        {
            Array.Copy(_Runs, idx, _Runs, idx + 1, RunCount - idx);
        }

        _Runs[idx] = (start, length);
        RunCount++;
    }

    /// <summary>Removes the run at <paramref name="idx"/> by shifting later runs down. The backing array keeps its capacity.</summary>
    /// <param name="idx">The index of the run to remove, 0 to <c>RunCount - 1</c>.</param>
    private void _RemoveRunAt(int idx)
    {
        if (idx < RunCount - 1)
        {
            Array.Copy(_Runs, idx + 1, _Runs, idx, RunCount - idx - 1);
        }

        RunCount--;
    }

    #endregion

    #region Set operations

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
    /// <param name="other">The run container to intersect with; not modified.</param>
    /// <returns>A new run container that holds the values present in both; empty when they share none.</returns>
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
    /// <param name="arr">The array container to intersect with; not modified.</param>
    /// <returns>A new array container that holds the values of <paramref name="arr"/> that lie inside a run.</returns>
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
            // Perf: set whole runs with word masks (at most 1 024 word stores per chunk) instead of one Add per value (up to
            // 65 536). Run with run stays in the word domain; FromPopcount then counts once and picks the result kind.
            BitmapContainer bitmap = new();
            SetRangesIn(bitmap.Bitmap);
            if (other is RunContainer otherRun)
            {
                otherRun.SetRangesIn(bitmap.Bitmap);
                return BitmapContainer.FromPopcount(bitmap);
            }

            // FromPopcount first: SetRangesIn does not maintain the cached cardinality that the receiver's Or relies on.
            return BitmapContainer.FromPopcount(bitmap).Or(other);
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
        // Symmetric difference in the word domain: set this side's runs with word masks, then toggle (run operand) or let the
        // counted container do the XOR (array or bitmap operand). Perf: word masks instead of one Add per value.
        BitmapContainer bitmap = new();
        SetRangesIn(bitmap.Bitmap);
        if (other is RunContainer otherRun)
        {
            otherRun.ToggleRangesIn(bitmap.Bitmap);
            return BitmapContainer.FromPopcount(bitmap);
        }

        return BitmapContainer.FromPopcount(bitmap).Xor(other);
    }

    #endregion

    #region Bitmap word-range interop

    /// <summary>
    /// Returns the i-th run as <c>(start, end)</c> where <c>end</c> is inclusive.
    /// </summary>
    /// <param name="index">The run index, 0 to <see cref="RunCount"/> - 1; not range-checked.</param>
    /// <returns>The first and the last value of the run.</returns>
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
    /// <param name="bitmap">The words to change; exactly <see cref="BitmapContainer.BitmapSize"/> entries; not range-checked.</param>
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
    /// <param name="bitmap">The words to change; exactly <see cref="BitmapContainer.BitmapSize"/> entries; not range-checked.</param>
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
    /// <param name="bitmap">The words to change; exactly <see cref="BitmapContainer.BitmapSize"/> entries; not range-checked.</param>
    internal void ToggleRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < RunCount; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            _ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.Xor);
        }
    }

    /// <summary>The word operation that <see cref="_ApplyRangeMask"/> applies to every covered word.</summary>
    private enum RangeOp
    {
        /// <summary>Sets the covered bits.</summary>
        Or,

        /// <summary>Clears the covered bits.</summary>
        AndNot,

        /// <summary>Toggles the covered bits.</summary>
        Xor,
    }

    /// <summary>
    /// Applies a bitwise operation across the inclusive bit range [<paramref name="startBit"/>,
    /// <paramref name="endBit"/>] in <paramref name="bitmap"/>. Uses ulong-word masks rather than
    /// per-bit loops, so a 65,536-bit run costs at most ~1024 ulong ops.
    /// </summary>
    /// <param name="bitmap">The words to change; exactly <see cref="BitmapContainer.BitmapSize"/> entries.</param>
    /// <param name="startBit">The first bit of the range, 0 to 65535.</param>
    /// <param name="endBit">The last bit of the range (inclusive), <paramref name="startBit"/> to 65535.</param>
    /// <param name="op">The operation applied to each covered word with the range mask.</param>
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
            ulong mask = width == 64
                ? ~0UL
                : ((1UL << width) - 1) << firstBit;
            _ApplyOp(ref bitmap[firstWord], mask, op);
            return;
        }

        // Multi-word path: a partial first word, whole words in between, a partial last word.
        // The first mask holds the high bits of the first word, the last mask the low bits of the last word.
        ulong firstMask = ~0UL << firstBit;
        ulong lastMask = lastBit == 63
            ? ~0UL
            : (1UL << (lastBit + 1)) - 1;

        _ApplyOp(ref bitmap[firstWord], firstMask, op);
        for (int w = firstWord + 1; w < lastWord; w++)
        {
            _ApplyOp(ref bitmap[w], ~0UL, op);
        }

        _ApplyOp(ref bitmap[lastWord], lastMask, op);
    }

    /// <summary>Applies one word operation with a mask to a word of the bitmap.</summary>
    /// <param name="word">The word to change in place.</param>
    /// <param name="mask">The bits to set, clear, or toggle.</param>
    /// <param name="op">The operation to apply.</param>
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

    #region Rank and select

    /// <summary>Counts the values of this container that are at most <paramref name="threshold"/>. O(runs), no allocation.</summary>
    /// <param name="threshold">The largest value to count.</param>
    /// <returns>The number of contained values in <c>0..threshold</c>.</returns>
    internal int Rank(ushort threshold)
    {
        int count = 0;
        for (int i = 0; i < RunCount; i++)
        {
            int start = _Runs[i].Start;
            if (start > threshold)
            {
                // Runs are sorted by start: no later run can contribute.
                break;
            }

            // Length is the run length minus one, so the inclusive end is Start + Length.
            int end = start + _Runs[i].Length;
            count += Math.Min(end, threshold) - start + 1;
        }

        return count;
    }

    /// <summary>Returns the value at position <paramref name="n"/> (0-based, ascending). O(runs), no allocation.</summary>
    /// <param name="n">The position; the caller guarantees <c>0 &lt;= n &lt; Cardinality</c>.</param>
    /// <returns>The n-th smallest contained value; 0 when <paramref name="n"/> is out of range (the caller's check prevents it).</returns>
    internal ushort Select(int n)
    {
        int remaining = n;
        for (int i = 0; i < RunCount; i++)
        {
            int size = _Runs[i].Length + 1;
            if (remaining < size)
            {
                return (ushort)(_Runs[i].Start + remaining);
            }

            remaining -= size;
        }

        return 0;
    }

    #endregion

    #region Run optimization

    /// <summary>Serialized size of a bitmap container in bytes (1024 words; Roaring format).</summary>
    internal const int BitmapContainerBytes = BitmapContainer.BitmapSize * sizeof(ulong);

    /// <summary>
    /// Serialized size in bytes of a run container with <paramref name="runCount"/> runs: a count and two
    /// <see cref="ushort"/> values per run (Roaring format).
    /// </summary>
    /// <param name="runCount">The number of runs, 0 to 32 768.</param>
    /// <returns>The size in bytes: <c>2 + 4 * runCount</c>.</returns>
    internal static int SerializedSize(int runCount) => 2 + (4 * runCount);

    /// <summary>
    /// Size in bytes used to decide whether the run form is smaller: arrays count a 2-byte header plus 2 bytes per value,
    /// bitmaps a fixed 8192 bytes, run containers <see cref="SerializedSize"/>. The conversion in
    /// <see cref="RoaringBitmap.RunOptimizeKeys"/> happens only when the run size is strictly smaller. This is a sizing
    /// heuristic for in-memory choice; nothing is serialized.
    /// </summary>
    /// <param name="container">The container to size.</param>
    /// <returns>The size in bytes of <paramref name="container"/> in its current kind.</returns>
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
    /// <returns>The number of runs; 0 for an empty container.</returns>
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
                int runs = count > 0
                    ? 1
                    : 0;
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
    /// <returns>A new run container; for a run container, a clone of it.</returns>
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

}
