// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Holds fragments for one datagram being reassembled.
/// Fragments are stored sorted by their byte offset.
/// When all fragments are received (contiguous range from 0 to total length),
/// the complete payload can be extracted.
/// <para>
/// <b>Duplicate fragment policy (largest-wins):</b> When a second fragment with an offset
/// already present is received, the buffer keeps the version with the larger payload size.
/// This deviates from Linux (first-wins) and BSD (first-wins) but matches the historical
/// behavior of several traffic analysers — it tolerates retransmissions where the sender
/// switches to a smaller MTU and re-emits a longer fragment that supersedes the old one.
/// RFC 791 leaves this strategy implementation-defined.
/// </para>
/// <para>
/// <b>Overlap detection:</b> When <c>dropOnOverlap</c> is true in
/// <see cref="AddFragment"/>, any new fragment whose byte range overlaps that of an
/// existing fragment at a <em>different</em> offset causes the method to return
/// <see cref="FragmentAddResult.OverlapDiscarded"/>. The caller must then discard the
/// entire datagram. This implements the RFC 5722 requirement for IPv6.
/// Duplicate fragments (same offset) are still handled by the largest-wins policy and
/// do not trigger overlap discarding.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. The owning <see cref="DatagramDefragmenter{TKey}"/>
/// must serialize all access. Designed for single-threaded use during packet parsing.
/// </para>
/// </summary>
internal sealed class DatagramFragmentBuffer
{
    #region Constants

    /// <summary>Maximum allowed total datagram payload length (IPv4 max = 65535).</summary>
    private const int MaxTotalLength = 65535;

    #endregion

    #region Fields

    /// <summary>Represents a single fragment in the reassembly buffer.</summary>
    private readonly record struct Fragment(int Offset, byte[] Data);

    /// <summary>Sorted list of fragments by offset (typically 1–3 fragments per datagram).</summary>
    private readonly List<Fragment> _Fragments = [];

    /// <summary>Total expected payload length in bytes. Set when the last fragment (MF=0) is received.</summary>
    private int _TotalLength = -1;

    /// <summary>Sum of all received fragment data bytes. Used for quick completeness check.</summary>
    private int _ReceivedBytes;

    #endregion

    #region Properties

    /// <summary>
    /// Number of fragments received so far.
    /// </summary>
    internal int FragmentCount => _Fragments.Count;

    #endregion

    #region Internal API

    /// <summary>
    /// Adds a fragment to the reassembly buffer.
    /// </summary>
    /// <param name="offset">Byte offset of this fragment within the original datagram payload (fragment offset × 8).</param>
    /// <param name="moreFragments">True if the More Fragments (MF) flag is set.</param>
    /// <param name="data">The fragment payload data.</param>
    /// <param name="dropOnOverlap">
    /// When true, any new fragment that overlaps with an existing fragment at a different
    /// offset causes an immediate <see cref="FragmentAddResult.OverlapDiscarded"/> return
    /// and the buffer state is poisoned. This implements RFC 5722 for IPv6.
    /// </param>
    /// <returns>A <see cref="FragmentAddResult"/> indicating whether the datagram is
    /// complete, still incomplete, or must be discarded due to an overlap.</returns>
    internal FragmentAddResult AddFragment(int offset, bool moreFragments, ReadOnlySpan<byte> data, bool dropOnOverlap = false)
    {
        byte[] copy = data.ToArray();

        // Insert in sorted order by offset.
        // Typical fragment counts are small (1–3), so linear search is efficient.
        int insertIdx = _Fragments.Count;
        for (int i = 0; i < _Fragments.Count; i++)
        {
            if (_Fragments[i].Offset == offset)
            {
                // Duplicate fragment at same offset — replace if larger (retransmission scenario).
                // Duplicate detection is not overlap detection; RFC 5722 only applies to
                // fragments at different offsets whose byte ranges intersect.
                if (copy.Length > _Fragments[i].Data.Length)
                {
                    _ReceivedBytes += copy.Length - _Fragments[i].Data.Length;
                    _Fragments[i] = new Fragment(offset, copy);
                }
                return IsComplete() ? FragmentAddResult.Complete : FragmentAddResult.Incomplete;
            }
            if (_Fragments[i].Offset > offset)
            {
                insertIdx = i;
                break;
            }
        }

        // RFC 5722 overlap check: verify that the new fragment's byte range [offset, offset+len)
        // does not intersect the range of the immediately adjacent fragments.
        // Because the list is sorted by offset, only the neighbours need to be checked —
        // any non-adjacent overlap would require an adjacent overlap to exist too.
        if (dropOnOverlap)
        {
            // Check overlap with the fragment immediately before: prev.end > newOffset
            if (insertIdx > 0)
            {
                Fragment prev = _Fragments[insertIdx - 1];
                if (prev.Offset + prev.Data.Length > offset)
                {
                    return FragmentAddResult.OverlapDiscarded;
                }
            }

            // Check overlap with the fragment immediately after: newEnd > next.offset
            if (insertIdx < _Fragments.Count)
            {
                Fragment next = _Fragments[insertIdx];
                if (offset + copy.Length > next.Offset)
                {
                    return FragmentAddResult.OverlapDiscarded;
                }
            }
        }

        _Fragments.Insert(insertIdx, new Fragment(offset, copy));
        _ReceivedBytes += copy.Length;

        // When the last fragment arrives (MF=0), we know the total datagram payload length.
        if (!moreFragments)
        {
            int total = offset + copy.Length;
            // Reject datagrams exceeding the maximum allowed size to prevent memory exhaustion.
            // Return OversizeDiscarded so the caller can remove the buffer immediately instead
            // of letting it linger until eviction (which would waste memory under malformed traffic).
            if (total > MaxTotalLength)
            {
                return FragmentAddResult.OversizeDiscarded;
            }
            _TotalLength = total;
        }

        return IsComplete() ? FragmentAddResult.Complete : FragmentAddResult.Incomplete;
    }

    /// <summary>
    /// Reassembles all fragments into a single contiguous byte array.
    /// Must only be called when <see cref="AddFragment"/> has returned <c>true</c>.
    /// </summary>
    /// <returns>The reassembled payload, or null if fragments don't form a contiguous range.</returns>
    internal byte[]? Reassemble()
    {
        if (_TotalLength <= 0)
        {
            return null;
        }

        byte[] result = new byte[_TotalLength];

        // Copy each fragment into its position within the reassembled buffer.
        foreach (Fragment fragment in _Fragments)
        {
            int end = fragment.Offset + fragment.Data.Length;
            if (end > _TotalLength)
            {
                return null; // Fragment extends beyond expected length
            }
            fragment.Data.CopyTo(result, fragment.Offset);
        }

        return result;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Checks whether all fragments have been received to form a complete datagram.
    /// Requires: (1) last fragment received (MF=0 → _TotalLength known),
    ///           (2) no gaps in the byte range [0, _TotalLength).
    /// </summary>
    private bool IsComplete()
    {
        if (_TotalLength < 0)
        {
            return false; // Haven't received last fragment yet
        }

        // Quick check: total received bytes must equal expected length.
        // This catches the common case without scanning the fragment list.
        if (_ReceivedBytes < _TotalLength)
        {
            return false;
        }

        // Verify contiguous coverage: walk fragments and check for gaps.
        int expectedOffset = 0;
        for (int i = 0; i < _Fragments.Count; i++)
        {
            Fragment f = _Fragments[i];
            if (f.Offset > expectedOffset)
            {
                return false; // Gap detected
            }
            int end = f.Offset + f.Data.Length;
            if (end > expectedOffset)
            {
                expectedOffset = end;
            }
        }

        return expectedOffset >= _TotalLength;
    }

    #endregion
}
