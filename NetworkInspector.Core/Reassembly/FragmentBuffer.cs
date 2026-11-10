// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// The fragments of one datagram that is being reassembled, sorted by their byte offset, with the rules that decide which
/// bytes are kept. Owned and driven by <see cref="FragmentReassembler{TKey}"/>, which checks every limit that does not depend
/// on the stored fragments (extent, alignment, datagram count, byte budget) before it calls <see cref="Add"/>.
/// <para>
/// <b>Overlap policies.</b> <see cref="FragmentOverlapPolicy.KeepLargestDuplicate"/> keeps the larger of two fragments at the
/// same offset. This deviates from Linux and BSD (first wins) but matches the behavior of several traffic
/// analysers: it tolerates a sender that switches to a smaller MTU and re-emits a longer fragment that supersedes the old one.
/// RFC 791 leaves the strategy open. <see cref="FragmentOverlapPolicy.DiscardDatagram"/> adds the RFC 5722 rule: bytes that
/// intersect a stored fragment at a different offset poison the buffer and the owner discards the datagram.
/// <see cref="FragmentOverlapPolicy.KeepFirst"/> stores only uncovered pieces, so the stored fragments stay pairwise disjoint.
/// </para>
/// <para>
/// <b>Total length.</b> The first fragment without the more-fragments flag fixes the total length. A duplicate at an existing
/// offset sets it only while it is unknown and never replaces a known one. A further last fragment at a <em>new</em> offset
/// whose end differs from the known total contradicts the datagram and poisons the buffer like an overlap does.
/// </para>
/// <para>
/// <b>Allocation.</b> Fragment bytes are copied into pooled arrays (returned and zeroed in <see cref="Release"/>) and the
/// fragment list itself lives in a pooled array, so a warmed pool makes in-order arrival allocation-free. Insertion scans
/// from the tail: fragments almost always arrive in order, so the position is the end and reordered arrival costs the distance
/// from the end. The rejected alternative, a plain <see cref="List{T}"/>, allocates a doubling chain of arrays per datagram
/// (several hundred KiB for a 10 MiB datagram).
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe. The owning reassembler serializes all access; see its thread-safety section.
/// </para>
/// </summary>
/// <typeparam name="TKey">The datagram identification key; kept here so the owner can unlink the buffer without a lookup.</typeparam>
internal sealed class FragmentBuffer<TKey> where TKey : struct, IEquatable<TKey>
{
    #region Configuration and state

    /// <summary>The pools the fragment copies and the fragment list are rented from (the shared pools unless a test injects its own).</summary>
    private readonly FragmentPools _Pools;

    /// <summary>Stored fragments sorted by start offset; the first <see cref="_Count"/> entries are valid. Pooled, see the class remarks.</summary>
    private FragmentSlot[] _Items = [];

    /// <summary>Number of valid entries at the start of <see cref="_Items"/> (pieces under the first-wins policy).</summary>
    private int _Count;

    /// <summary>Total datagram length in bytes; -1 while no last fragment arrived.</summary>
    private int _TotalLength = -1;

    /// <summary>
    /// Sum of the stored fragment lengths. Counts overlapping bytes twice under <see cref="FragmentOverlapPolicy.KeepLargestDuplicate"/>,
    /// which stores overlaps at different offsets as they are; <see cref="FragmentOverlapPolicy.DiscardDatagram"/> refuses
    /// them and <see cref="FragmentOverlapPolicy.KeepFirst"/> stores disjoint pieces.
    /// </summary>
    private long _StoredBytes;

    /// <summary>Set when the buffer was contradicted; every later fragment reports the same discard until the owner removes the buffer.</summary>
    private FragmentDiscardReason _PoisonReason;

    /// <summary>True when one fragment was stored as several pieces, so a contributor can appear more than once in the list.</summary>
    private bool _HasSplitContributors;

    /// <summary>Creates an empty buffer for <paramref name="key"/>.</summary>
    /// <param name="key">The datagram identification the owner stores the buffer under.</param>
    /// <param name="pools">The pools to rent fragment copies and the fragment list from.</param>
    internal FragmentBuffer(in TKey key, FragmentPools pools)
    {
        Key = key;
        _Pools = pools;
    }

    /// <summary>The datagram identification the owner stores the buffer under.</summary>
    internal TKey Key { get; }

    /// <summary>Neighbour towards the head of the owner's eviction order (the next to be evicted); owned by the reassembler.</summary>
    internal FragmentBuffer<TKey>? Previous { get; set; }

    /// <summary>Neighbour towards the tail of the owner's eviction order; owned by the reassembler.</summary>
    internal FragmentBuffer<TKey>? Next { get; set; }

    /// <summary>True once a partial prefix was reported for this datagram, so it is reported only once.</summary>
    internal bool PartialReported { get; set; }

    /// <summary>Number of stored fragments (pieces under the first-wins policy).</summary>
    internal int FragmentCount => _Count;

    /// <summary>
    /// True when a stored fragment starts at offset 0. A restart (the sender begins the transfer over) is a second beginning, so it
    /// is recognized by the beginning already being there; a fragment at offset 0 that arrives behind later fragments of an
    /// out-of-order transfer is the missing start, not a restart.
    /// </summary>
    internal bool HasStart => _Count > 0 && _Items[0].Start == 0;

    /// <summary>Sum of the stored fragment lengths in bytes (what the owner's byte budget counts).</summary>
    internal long StoredBytes => _StoredBytes;

    /// <summary>The total datagram length, or -1 while no last fragment arrived.</summary>
    internal int TotalLength => _TotalLength;

    #endregion

    #region Fragment storage

    /// <summary>
    /// Stores a fragment according to the overlap policy of <paramref name="options"/>.
    /// </summary>
    /// <param name="start">Byte offset of the fragment; the owner checked that <c>start + data.Length</c> is within the datagram length limit.</param>
    /// <param name="moreFragments">False for the last fragment of the datagram.</param>
    /// <param name="data">The fragment bytes; copied, the caller's span is only valid for this call.</param>
    /// <param name="contributor">The opaque tag returned with the datagram for every stored fragment.</param>
    /// <param name="options">The options of the owner (fragment count limit and overlap policy are read here).</param>
    /// <param name="overlapConflict">True when overlapping bytes differ from the kept bytes (first-wins policy).</param>
    /// <param name="totalFixed">True when this call fixed the total length.</param>
    /// <returns><see cref="FragmentDiscardReason.None"/> when the fragment was accepted, otherwise the reason it was refused.</returns>
    internal FragmentDiscardReason Add(
        int start,
        bool moreFragments,
        ReadOnlySpan<byte> data,
        int contributor,
        in FragmentReassemblyOptions options,
        out bool overlapConflict,
        out bool totalFixed)
    {
        overlapConflict = false;
        totalFixed = false;

        // A poisoned buffer stays poisoned: every later fragment of the same datagram reports the same discard until the
        // owner removes the buffer.
        if (_PoisonReason != FragmentDiscardReason.None)
        {
            return _PoisonReason;
        }

        if (options.OverlapPolicy == FragmentOverlapPolicy.KeepFirst)
        {
            return _AddKeepFirst(start, moreFragments, data, contributor, in options, out overlapConflict, out totalFixed);
        }

        return _AddLegacy(start, moreFragments, data, contributor, in options, out totalFixed);
    }

    #endregion

    #region Completion, assembly and release

    /// <summary>
    /// Whether the stored fragments cover the byte range <c>[0, total)</c> without a hole. Requires a known total length.
    /// </summary>
    /// <returns>True when the datagram can be assembled.</returns>
    internal bool IsComplete()
    {
        if (_TotalLength < 0)
        {
            return false; // Haven't received last fragment yet
        }

        // Quick check: total stored bytes must reach the expected length.
        // This catches the common case without scanning the fragment list.
        if (_StoredBytes < _TotalLength)
        {
            return false;
        }

        // Verify contiguous coverage: walk fragments and check for gaps.
        int expectedOffset = 0;
        for (int i = 0; i < _Count; i++)
        {
            FragmentSlot f = _Items[i];
            if (f.Start > expectedOffset)
            {
                return false; // Gap detected
            }

            if (f.End > expectedOffset)
            {
                expectedOffset = f.End;
            }
        }

        return expectedOffset >= _TotalLength;
    }

    /// <summary>
    /// Assembles all fragments into one contiguous array. Call only when <see cref="IsComplete"/> returned true.
    /// </summary>
    /// <returns>The datagram, or <see langword="null"/> when the total length is not positive or a fragment extends past it.</returns>
    internal byte[]? Assemble()
    {
        if (_TotalLength <= 0)
        {
            return null;
        }

        // A plain allocation, not a pooled rental: the array leaves this type, is bound to the packet as an additional
        // buffer, and lives as long as that packet, so there is no point at which it could be returned to a pool.
        // Holes cannot occur here because IsComplete reported contiguous coverage, so every byte is written.
        byte[] result = new byte[_TotalLength];

        // Copy each fragment into its position; under KeepLargestDuplicate a later fragment overwrites overlapping bytes.
        for (int i = 0; i < _Count; i++)
        {
            FragmentSlot fragment = _Items[i];
            if (fragment.End > _TotalLength)
            {
                return null; // Fragment extends beyond the expected length
            }

            fragment.Data.AsSpan(0, fragment.Length).CopyTo(result.AsSpan(fragment.Start));
        }

        return result;
    }

    /// <summary>
    /// The contributor tags of the stored fragments in offset order. A tag that was stored as several pieces appears once, at
    /// its first piece.
    /// </summary>
    /// <returns>A new array owned by the caller.</returns>
    internal int[] GetContributors()
    {
        if (!_HasSplitContributors)
        {
            // One fragment per tag: no tag can repeat, so no set is needed (the common case allocates only the result).
            int[] plain = new int[_Count];
            for (int i = 0; i < _Count; i++)
            {
                plain[i] = _Items[i].Contributor;
            }

            return plain;
        }

        // A first-wins overlap split one fragment around kept bytes, so its tag can repeat. A set removes the repeats; this
        // path is rare (an overlap with other kept fragments in the middle) and its allocation is bounded by the fragment count.
        HashSet<int> seen = new(_Count);
        List<int> ordered = new(_Count);
        for (int i = 0; i < _Count; i++)
        {
            if (seen.Add(_Items[i].Contributor))
            {
                ordered.Add(_Items[i].Contributor);
            }
        }

        return [.. ordered];
    }

    /// <summary>
    /// Builds the decodable part of a datagram that has a hole: the contiguous prefix followed by the covered ranges behind
    /// the first hole. Requires a known total length. The allocation is bounded by the stored bytes, never by the datagram
    /// extent, so a hostile total length cannot request a huge array.
    /// </summary>
    /// <param name="payload">The prefix followed by the covered ranges, concatenated.</param>
    /// <param name="prefixLength">Number of bytes at the start of <paramref name="payload"/> that start at datagram offset 0.</param>
    /// <param name="gaps">The holes inside the datagram.</param>
    /// <param name="coveredAfterGap">The covered ranges behind the first hole.</param>
    internal void BuildPartial(
        out byte[] payload,
        out int prefixLength,
        out FragmentRange[] gaps,
        out FragmentRange[] coveredAfterGap)
    {
        // Merge the sorted fragments into maximal covered runs. A zero-length marker (an empty last fragment) covers nothing.
        List<FragmentRange> runs = [];
        int runStart = 0;
        int runEnd = -1;
        for (int i = 0; i < _Count; i++)
        {
            FragmentSlot f = _Items[i];
            if (f.Length == 0)
            {
                continue;
            }

            if (runEnd < 0)
            {
                runStart = f.Start;
                runEnd = f.End;
            }
            else if (f.Start <= runEnd)
            {
                runEnd = Math.Max(runEnd, f.End);
            }
            else
            {
                runs.Add(new FragmentRange(runStart, runEnd - runStart));
                runStart = f.Start;
                runEnd = f.End;
            }
        }

        if (runEnd >= 0)
        {
            runs.Add(new FragmentRange(runStart, runEnd - runStart));
        }

        // Holes are the spaces between runs, before the first run, and behind the last run up to the total length.
        List<FragmentRange> holes = [];
        int cursor = 0;
        foreach (FragmentRange run in runs)
        {
            if (run.Start > cursor)
            {
                holes.Add(new FragmentRange(cursor, run.Start - cursor));
            }

            cursor = run.Start + run.Length;
        }

        if (cursor < _TotalLength)
        {
            holes.Add(new FragmentRange(cursor, _TotalLength - cursor));
        }

        // The prefix is the first run when it starts at offset 0; every other run is "covered behind a hole".
        bool hasPrefix = runs.Count > 0 && runs[0].Start == 0;
        prefixLength = hasPrefix
            ? runs[0].Length
            : 0;
        int firstRunAfterGap = hasPrefix
            ? 1
            : 0;
        coveredAfterGap = CollectionsMarshal.AsSpan(runs)[firstRunAfterGap..].ToArray();
        gaps = [.. holes];

        // The payload is all runs concatenated in offset order; copy every fragment to its run's position.
        int total = 0;
        foreach (FragmentRange run in runs)
        {
            total += run.Length;
        }

        payload = new byte[total];
        int runIndex = 0;
        int runOutput = 0;
        for (int i = 0; i < _Count; i++)
        {
            FragmentSlot f = _Items[i];
            if (f.Length == 0)
            {
                continue;
            }

            while (f.Start >= runs[runIndex].Start + runs[runIndex].Length)
            {
                runOutput += runs[runIndex].Length;
                runIndex++;
            }

            f.Data.AsSpan(0, f.Length).CopyTo(payload.AsSpan(runOutput + (f.Start - runs[runIndex].Start)));
        }
    }

    /// <summary>
    /// Returns the rented fragment copies and the pooled list to their pools (zeroed first) and clears the state. Call when the
    /// buffer is evicted, completed, discarded or restarted.
    /// </summary>
    internal void Release()
    {
        for (int i = 0; i < _Count; i++)
        {
            _ReturnData(_Items[i].Data);
        }

        // The list holds references to the fragment arrays, so it is cleared on return; stale references would keep the
        // zeroed arrays alive and let a later renter of this list see them.
        if (_Items.Length > 0)
        {
            _Pools.Slots.Return(_Items, clearArray: true);
        }

        _Items = [];
        _Count = 0;
        _TotalLength = -1;
        _StoredBytes = 0;
        _PoisonReason = FragmentDiscardReason.None;
        _HasSplitContributors = false;
        PartialReported = false;
    }

    #endregion

    #region Overlap policies

    /// <summary>
    /// Stores a fragment under <see cref="FragmentOverlapPolicy.KeepLargestDuplicate"/> (largest duplicate wins, overlaps
    /// at different offsets are stored as they are) or <see cref="FragmentOverlapPolicy.DiscardDatagram"/> (an overlap at a
    /// different offset poisons the buffer, RFC 5722). The two differ only in the overlap check.
    /// </summary>
    /// <param name="start">Byte offset of the fragment.</param>
    /// <param name="moreFragments">False for the last fragment of the datagram.</param>
    /// <param name="data">The fragment bytes; copied.</param>
    /// <param name="contributor">The opaque tag stored with the fragment.</param>
    /// <param name="options">The options of the owner (fragment count limit and overlap policy are read here).</param>
    /// <param name="totalFixed">True when this call fixed the total length.</param>
    /// <returns><see cref="FragmentDiscardReason.None"/> when accepted, otherwise the reason it was refused.</returns>
    private FragmentDiscardReason _AddLegacy(
        int start,
        bool moreFragments,
        ReadOnlySpan<byte> data,
        int contributor,
        in FragmentReassemblyOptions options,
        out bool totalFixed)
    {
        totalFixed = false;
        int length = data.Length;

        // Find the sorted position, or the duplicate on the way. Offsets are unique in this list (a duplicate replaces).
        int index = _FindPosition(start, out bool duplicate);
        if (duplicate)
        {
            // Duplicate fragment at same offset — replace if larger (retransmission scenario).
            // Duplicate detection is not overlap detection; RFC 5722 only applies to
            // fragments at different offsets whose byte ranges intersect.
            // A smaller or equal duplicate is ignored.
            FragmentSlot existing = _Items[index];
            if (length > existing.Length)
            {
                byte[] rented = _Pools.Bytes.Rent(length);
                data.CopyTo(rented.AsSpan(0, length));
                _ReturnData(existing.Data);
                _StoredBytes += length - existing.Length;
                _Items[index] = new FragmentSlot(start, rented, length, contributor);
            }

            // The duplicate's MF flag is consulted only to learn a still unknown total length. Without this a retransmitted
            // last fragment whose first copy was stored as a non-last fragment could never tell where the datagram ends.
            // A known total length is never replaced here, and a duplicate never discards the datagram; the end is taken from
            // the fragment that is kept, so the kept bytes always lie inside the total.
            if (!moreFragments && _TotalLength < 0)
            {
                _TotalLength = start + _Items[index].Length;
                totalFixed = true;
            }

            return FragmentDiscardReason.None;
        }

        // Fragment-count limit. Checked only for a genuinely new offset, after the duplicate branch: a duplicate adds no entry.
        if (_Count >= options.MaxFragments)
        {
            return FragmentDiscardReason.FragmentLimit;
        }

        // A last fragment (MF=0) states where the datagram ends. All fragments of one datagram agree on that end, so a second
        // last fragment with another end belongs to a different datagram that reuses the identification (or is forged).
        // Replacing the known total length would silently turn the buffer into a mix of both; the datagram is discarded
        // instead, like an overlap, and the buffer is poisoned. Checked before anything is stored, so a rejected fragment
        // leaves no state behind. The sum cannot overflow: the owner checked start + length against the datagram limit.
        int terminalTotal = -1;
        if (!moreFragments)
        {
            terminalTotal = start + length;
            if (_TotalLength >= 0 && _TotalLength != terminalTotal)
            {
                _PoisonReason = FragmentDiscardReason.ConflictingEnd;
                return FragmentDiscardReason.ConflictingEnd;
            }
        }

        // RFC 5722 overlap check: the new fragment's byte range [start, start+length) must not intersect the immediately
        // adjacent fragments. Because the list is sorted, only the neighbours need checking: any non-adjacent overlap would
        // require an adjacent overlap too. This relies on the stored fragments being pairwise disjoint, which holds when every
        // earlier call of this buffer used the same policy (the owner passes one fixed value for all calls).
        if (options.OverlapPolicy == FragmentOverlapPolicy.DiscardDatagram)
        {
            if (index > 0 && _Items[index - 1].End > start)
            {
                _PoisonReason = FragmentDiscardReason.Overlap;
                return FragmentDiscardReason.Overlap;
            }

            if (index < _Count && start + length > _Items[index].Start)
            {
                _PoisonReason = FragmentDiscardReason.Overlap;
                return FragmentDiscardReason.Overlap;
            }
        }

        _Insert(index, start, data, contributor);

        // A conflicting second last fragment was rejected above, so this only sets the length or repeats the known value.
        if (terminalTotal >= 0)
        {
            totalFixed = _TotalLength < 0;
            _TotalLength = terminalTotal;
        }

        return FragmentDiscardReason.None;
    }

    /// <summary>
    /// First-wins storage: only the parts of the fragment that no stored fragment covers are stored, so the stored fragments
    /// stay pairwise disjoint and the result never depends on a later arrival.
    /// </summary>
    /// <param name="start">Byte offset of the fragment.</param>
    /// <param name="moreFragments">False for the last fragment of the datagram.</param>
    /// <param name="data">The fragment bytes; the uncovered parts are copied.</param>
    /// <param name="contributor">The opaque tag stored with every piece.</param>
    /// <param name="options">The options of the owner (the fragment count limit is read here).</param>
    /// <param name="overlapConflict">True when covered bytes differ from the kept bytes.</param>
    /// <param name="totalFixed">True when this call fixed the total length.</param>
    /// <returns><see cref="FragmentDiscardReason.None"/> when accepted, otherwise the reason it was refused.</returns>
    private FragmentDiscardReason _AddKeepFirst(
        int start,
        bool moreFragments,
        ReadOnlySpan<byte> data,
        int contributor,
        in FragmentReassemblyOptions options,
        out bool overlapConflict,
        out bool totalFixed)
    {
        overlapConflict = false;
        totalFixed = false;
        int length = data.Length;
        int end = start + length;

        // The end rules. A last fragment must agree with a known total; a non-last fragment must lie inside it. Without a
        // known total, a first last fragment must not end before bytes that are already stored.
        if (_TotalLength >= 0)
        {
            if (!moreFragments)
            {
                if (end != _TotalLength)
                {
                    _PoisonReason = FragmentDiscardReason.ConflictingEnd;
                    return FragmentDiscardReason.ConflictingEnd;
                }
            }
            else if (start >= _TotalLength || end > _TotalLength)
            {
                return FragmentDiscardReason.BeyondEnd;
            }
        }
        else if (!moreFragments && _Count > 0 && _Items[_Count - 1].End > end)
        {
            _PoisonReason = FragmentDiscardReason.ConflictingEnd;
            return FragmentDiscardReason.ConflictingEnd;
        }

        if (length == 0)
        {
            // An empty last fragment fixes the end and still counts as a contributor, so it is stored as a zero-length marker.
            // An empty non-last fragment carries nothing and is not stored.
            if (!moreFragments)
            {
                if (_Count >= options.MaxFragments)
                {
                    return FragmentDiscardReason.FragmentLimit;
                }

                int markerIndex = _FindPosition(start, out _);
                _Insert(markerIndex, start, data, contributor);
            }
        }
        else
        {
            FragmentDiscardReason stored = _StoreUncovered(start, end, data, contributor, in options, out overlapConflict);
            if (stored != FragmentDiscardReason.None)
            {
                return stored;
            }
        }

        if (!moreFragments && _TotalLength < 0)
        {
            _TotalLength = end;
            totalFixed = true;
        }

        return FragmentDiscardReason.None;
    }

    /// <summary>
    /// Stores the uncovered pieces of <c>[start, end)</c>, compares the covered bytes with the kept ones, and enforces the
    /// fragment count limit before it changes anything.
    /// </summary>
    /// <param name="start">Offset of the first byte of the fragment.</param>
    /// <param name="end">Offset behind the last byte of the fragment; greater than <paramref name="start"/>.</param>
    /// <param name="data">The fragment bytes; the uncovered parts are copied.</param>
    /// <param name="contributor">The opaque tag stored with every piece.</param>
    /// <param name="options">The options of the owner (the fragment count limit is read here).</param>
    /// <param name="overlapConflict">True when covered bytes differ from the kept bytes.</param>
    /// <returns><see cref="FragmentDiscardReason.None"/> when stored (or fully covered), otherwise the reason it was refused.</returns>
    private FragmentDiscardReason _StoreUncovered(
        int start,
        int end,
        ReadOnlySpan<byte> data,
        int contributor,
        in FragmentReassemblyOptions options,
        out bool overlapConflict)
    {
        overlapConflict = false;

        // The stored fragments that touch [start, end) are the index range [low, high). Found from the tail because the new
        // fragment usually lies behind everything stored.
        int high = _Count;
        while (high > 0 && _Items[high - 1].Start >= end)
        {
            high--;
        }

        int low = high;
        while (low > 0 && _Items[low - 1].End > start)
        {
            low--;
        }

        // First pass: count the uncovered pieces and compare the covered bytes. Nothing is changed yet, so a refusal below
        // leaves the buffer untouched.
        int pieces = 0;
        int cursor = start;
        for (int j = low; j < high; j++)
        {
            FragmentSlot kept = _Items[j];
            int overlapStart = Math.Max(start, kept.Start);
            int overlapEnd = Math.Min(end, kept.End);
            if (!overlapConflict && overlapEnd > overlapStart)
            {
                ReadOnlySpan<byte> incoming = data.Slice(overlapStart - start, overlapEnd - overlapStart);
                ReadOnlySpan<byte> keptBytes = kept.Data.AsSpan(overlapStart - kept.Start, overlapEnd - overlapStart);
                overlapConflict = !incoming.SequenceEqual(keptBytes);
            }

            if (kept.Start > cursor)
            {
                pieces++;
            }

            cursor = Math.Max(cursor, kept.End);
        }

        if (cursor < end)
        {
            pieces++;
        }

        if (pieces == 0)
        {
            return FragmentDiscardReason.None;
        }

        // The list bound protects every later insert; the check counts the pieces this fragment would add.
        if (_Count + pieces > options.MaxFragments)
        {
            return FragmentDiscardReason.FragmentLimit;
        }

        // Second pass: insert from the back so the indices below the insert position stay valid.
        int tailStart = high > low
            ? Math.Max(start, _Items[high - 1].End)
            : start;
        if (tailStart < end)
        {
            _InsertPiece(high, tailStart, end, start, data, contributor);
        }

        for (int j = high - 1; j >= low; j--)
        {
            int previousEnd = j > low
                ? Math.Max(start, _Items[j - 1].End)
                : start;
            int keptStart = _Items[j].Start;
            if (keptStart > previousEnd)
            {
                _InsertPiece(j, previousEnd, keptStart, start, data, contributor);
            }
        }

        // One fragment became several pieces: its tag can now appear more than once in offset order.
        if (pieces > 1)
        {
            _HasSplitContributors = true;
        }

        return FragmentDiscardReason.None;
    }

    #endregion

    #region Sorted list maintenance

    /// <summary>Inserts the part <c>[pieceStart, pieceEnd)</c> of a fragment as its own entry.</summary>
    /// <param name="index">Position in the sorted list where the entry is inserted.</param>
    /// <param name="pieceStart">Datagram offset of the first byte of the piece.</param>
    /// <param name="pieceEnd">Datagram offset behind the last byte of the piece.</param>
    /// <param name="dataStart">Datagram offset of the first byte of <paramref name="data"/>.</param>
    /// <param name="data">The whole fragment; the piece is a slice of it and is copied.</param>
    /// <param name="contributor">The opaque tag stored with the piece.</param>
    private void _InsertPiece(int index, int pieceStart, int pieceEnd, int dataStart, ReadOnlySpan<byte> data, int contributor) =>
        _Insert(index, pieceStart, data.Slice(pieceStart - dataStart, pieceEnd - pieceStart), contributor);

    /// <summary>
    /// The insert position for <paramref name="start"/> scanning from the tail, or the index of the fragment that already
    /// starts there.
    /// </summary>
    /// <param name="start">The datagram offset to find.</param>
    /// <param name="duplicate">True when a stored fragment starts at <paramref name="start"/>; the result is then its index.</param>
    /// <returns>The index of the duplicate, or the index where a fragment at <paramref name="start"/> is inserted.</returns>
    private int _FindPosition(int start, out bool duplicate)
    {
        int i = _Count;
        while (i > 0 && _Items[i - 1].Start > start)
        {
            i--;
        }

        duplicate = i > 0 && _Items[i - 1].Start == start;
        return duplicate
            ? i - 1
            : i;
    }

    /// <summary>
    /// Copies <paramref name="data"/> into a pooled array and inserts it at <paramref name="index"/>. The array may be longer
    /// than the data, so the real length is kept in the fragment; it is returned (and zeroed) in <see cref="Release"/>.
    /// </summary>
    /// <param name="index">Position in the sorted list; later entries move up by one.</param>
    /// <param name="start">Datagram offset of the first byte of <paramref name="data"/>.</param>
    /// <param name="data">The bytes to store; copied.</param>
    /// <param name="contributor">The opaque tag stored with the entry.</param>
    private void _Insert(int index, int start, ReadOnlySpan<byte> data, int contributor)
    {
        if (_Count == _Items.Length)
        {
            // Grow through the pool: the list of a large datagram would otherwise allocate a doubling chain of arrays.
            FragmentSlot[] grown = _Pools.Slots.Rent(Math.Max(4, _Count * 2));
            Array.Copy(_Items, grown, _Count);
            if (_Items.Length > 0)
            {
                _Pools.Slots.Return(_Items, clearArray: true);
            }

            _Items = grown;
        }

        byte[] rented = _Pools.Bytes.Rent(data.Length);
        data.CopyTo(rented.AsSpan(0, data.Length));
        if (index < _Count)
        {
            Array.Copy(_Items, index, _Items, index + 1, _Count - index);
        }

        _Items[index] = new FragmentSlot(start, rented, data.Length, contributor);
        _Count++;
        _StoredBytes += data.Length;
    }

    /// <summary>
    /// Returns a fragment copy to its pool, zeroed first (<c>clearArray: true</c>) so captured payload bytes do
    /// not linger in pooled arrays that other code may rent later.
    /// </summary>
    /// <param name="buffer">A fragment copy rented from <see cref="FragmentPools.Bytes"/>; not used after this call.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ReturnData(byte[] buffer) =>
        _Pools.Bytes.Return(buffer, clearArray: true);

    #endregion
}

/// <summary>One stored fragment. <c>Data</c> is a pooled array that can be longer than <c>Length</c>.</summary>
/// <param name="Start">Offset of the first byte in the datagram.</param>
/// <param name="Data">The pooled copy of the fragment bytes.</param>
/// <param name="Length">Number of valid bytes in <paramref name="Data"/>.</param>
/// <param name="Contributor">The opaque tag the owner supplied with the fragment.</param>
internal readonly record struct FragmentSlot(int Start, byte[] Data, int Length, int Contributor)
{
    /// <summary>The first byte behind the fragment.</summary>
    public int End => Start + Length;
}

/// <summary>
/// The pools a reassembler rents from. Production code uses <see cref="Shared"/>. A test injects pools that retain every
/// array, because the shared pools keep only a few arrays per size and per core, so a datagram of thousands of fragments
/// would miss the pool on every rental and a measurement of "allocation per fragment" would depend on the machine.
/// <para><b>Thread-safety:</b> immutable; the pools are thread-safe.</para>
/// </summary>
/// <param name="Bytes">Pool for the fragment copies.</param>
/// <param name="Slots">Pool for the fragment lists.</param>
internal sealed record FragmentPools(ArrayPool<byte> Bytes, ArrayPool<FragmentSlot> Slots)
{
    /// <summary>The process-wide shared pools.</summary>
    internal static FragmentPools Shared { get; } = new(ArrayPool<byte>.Shared, ArrayPool<FragmentSlot>.Shared);
}
