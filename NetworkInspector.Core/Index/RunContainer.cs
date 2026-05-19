// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Run-length encoded container. Efficient for dense sequential ranges.
/// Stores sorted (start, length) pairs where each run covers [start, start+length].
/// </summary>
internal sealed class RunContainer : IContainer
{
    #region Fields

    private (ushort Start, ushort Length)[] _Runs;
    private int _Count;

    #endregion

    #region Constructors

    /// <summary>Creates an empty run-length encoded container.</summary>
    internal RunContainer()
    {
        _Runs = new (ushort, ushort)[4];
        _Count = 0;
    }

    /// <summary>Creates a run container from existing runs (used by Clone).</summary>
    private RunContainer((ushort Start, ushort Length)[] runs, int count)
    {
        _Runs = runs;
        _Count = count;
    }

    #endregion

    #region Properties

    public int Cardinality
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _Count; i++)
            {
                total += _Runs[i].Length + 1;
            }
            return total;
        }
    }

    public ushort Min => _Count > 0 ? _Runs[0].Start : ushort.MinValue;

    public ushort Max
    {
        get
        {
            if (_Count == 0)
            {
                return ushort.MinValue;
            }
            (ushort Start, ushort Length) = _Runs[_Count - 1];
            return (ushort)(Start + Length);
        }
    }

    #endregion

    #region Public API

    public bool Contains(ushort value)
    {
        // Binary search for the run containing value
        int lo = 0, hi = _Count - 1;
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

    public IContainer Clone()
    {
        (ushort Start, ushort Length)[] runsCopy = new (ushort, ushort)[_Count];
        Array.Copy(_Runs, runsCopy, _Count);
        return new RunContainer(runsCopy, _Count);
    }

    public IContainer Add(ushort value)
    {
        if (Contains(value))
        {
            return this;
        }

        // Find insertion point
        int idx = 0;
        while (idx < _Count && _Runs[idx].Start < value)
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
                TryMergeAt(idx - 1);
                return this;
            }
        }

        // Check if can extend next run
        if (idx < _Count && _Runs[idx].Start == value + 1)
        {
            _Runs[idx].Start = value;
            _Runs[idx].Length++;
            // Check merge with prev
            if (idx > 0)
            {
                TryMergeAt(idx - 1);
            }
            return this;
        }

        // Insert new run
        InsertRunAt(idx, value, 0);
        return this;
    }

    public IContainer And(IContainer other)
    {
        // Convert to array for simplicity
        ArrayContainer result = new();
        IContainer r = result;
        for (int i = 0; i < _Count; i++)
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

    public IContainer Or(IContainer other)
    {
        // Convert to bitmap for large unions, add both sides
        if (Cardinality + other.Cardinality > ArrayContainer.MaxCapacity)
        {
            BitmapContainer bitmap = new();
            IContainer b = bitmap;
            for (int i = 0; i < _Count; i++)
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
            for (int i = 0; i < run._Count; i++)
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

    public IContainer AndNot(IContainer other)
    {
        // Materialize as array, removing elements present in other
        ArrayContainer result = new();
        IContainer r = result;
        for (int i = 0; i < _Count; i++)
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

    public IContainer Xor(IContainer other)
    {
        // Materialize to bitmap for XOR — symmetric difference is complex with runs
        BitmapContainer bitmap = new();
        for (int i = 0; i < _Count; i++)
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
    /// Number of runs currently stored. Internal accessor used by sibling containers
    /// to choose dispatch strategy (e.g. tight loop vs. word-level set operations).
    /// </summary>
    internal int RunCount => _Count;

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
        for (int i = 0; i < _Count; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.Or);
        }
    }

    /// <summary>
    /// Clears all bits covered by this container's runs in <paramref name="bitmap"/>.
    /// Used by <c>BitmapContainer.AndNot(RunContainer)</c>.
    /// </summary>
    internal void ClearRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < _Count; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.AndNot);
        }
    }

    /// <summary>
    /// Toggles all bits covered by this container's runs in <paramref name="bitmap"/>.
    /// Used by <c>BitmapContainer.Xor(RunContainer)</c>.
    /// </summary>
    internal void ToggleRangesIn(ulong[] bitmap)
    {
        for (int i = 0; i < _Count; i++)
        {
            (ushort Start, ushort Length) = _Runs[i];
            ApplyRangeMask(bitmap, Start, (ushort)(Start + Length), RangeOp.Xor);
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
    private static void ApplyRangeMask(ulong[] bitmap, int startBit, int endBit, RangeOp op)
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
            ApplyOp(ref bitmap[firstWord], mask, op);
            return;
        }

        // Multi-word path
        ulong firstMask = ~0UL << firstBit;       // high bits of the first word
        ulong lastMask = lastBit == 63 ? ~0UL : (1UL << (lastBit + 1)) - 1; // low bits of the last word

        ApplyOp(ref bitmap[firstWord], firstMask, op);
        for (int w = firstWord + 1; w < lastWord; w++)
        {
            ApplyOp(ref bitmap[w], ~0UL, op);
        }
        ApplyOp(ref bitmap[lastWord], lastMask, op);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyOp(ref ulong word, ulong mask, RangeOp op)
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

    #region Private Helpers

    private void TryMergeAt(int idx)
    {
        if (idx + 1 >= _Count)
        {
            return;
        }
        ushort end = (ushort)(_Runs[idx].Start + _Runs[idx].Length);
        if (end + 1 >= _Runs[idx + 1].Start)
        {
            ushort newEnd = Math.Max(end, (ushort)(_Runs[idx + 1].Start + _Runs[idx + 1].Length));
            _Runs[idx].Length = (ushort)(newEnd - _Runs[idx].Start);
            RemoveRunAt(idx + 1);
        }
    }

    private void InsertRunAt(int idx, ushort start, ushort length)
    {
        if (_Count == _Runs.Length)
        {
            Array.Resize(ref _Runs, _Runs.Length * 2);
        }
        if (idx < _Count)
        {
            Array.Copy(_Runs, idx, _Runs, idx + 1, _Count - idx);
        }
        _Runs[idx] = (start, length);
        _Count++;
    }

    private void RemoveRunAt(int idx)
    {
        if (idx < _Count - 1)
        {
            Array.Copy(_Runs, idx + 1, _Runs, idx, _Count - idx - 1);
        }
        _Count--;
    }

    #endregion
}