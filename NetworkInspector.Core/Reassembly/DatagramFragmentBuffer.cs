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
/// <see cref="FragmentAddResult.OverlapDiscarded"/> and poisons the buffer. The caller must
/// then discard the entire datagram. This implements the RFC 5722 requirement for IPv6.
/// Duplicate fragments (same offset) are still handled by the largest-wins policy and
/// do not trigger overlap discarding.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. The owning <see cref="DatagramDefragmenter{TKey}"/>
/// must serialize all access. Designed for single-threaded use during packet parsing.
/// </para>
/// </summary>
public sealed class DatagramFragmentBuffer
{
    #region Constants

    /// <summary>Maximum allowed total datagram payload length (IPv4 max = 65535).</summary>
    private const int _MaxTotalLength = 65535;

    /// <summary>Maximum number of fragments accepted per datagram.</summary>
    private const int _MaxFragmentCount = 8192;

    #endregion

    #region Fields

    /// <summary>Represents a single fragment in the reassembly buffer.</summary>
    private readonly record struct Fragment(int Offset, byte[] Data, int Length);

    /// <summary>Sorted list of fragments by offset (typically 1–3 fragments per datagram).</summary>
    private readonly List<Fragment> _Fragments = [];

    /// <summary>Total expected payload length in bytes. Set when the last fragment (MF=0) is received.</summary>
    private int _TotalLength = -1;

    /// <summary>Sum of all received fragment data bytes. Used for quick completeness check.</summary>
    private int _ReceivedBytes;

    /// <summary>When true, further fragments cannot complete reassembly.</summary>
    private bool _Poisoned;

    #endregion

    #region Properties

    /// <summary>
    /// Number of fragments received so far.
    /// </summary>
    public int FragmentCount => _Fragments.Count;

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
    public FragmentAddResult AddFragment(int offset, bool moreFragments, ReadOnlySpan<byte> data, bool dropOnOverlap = false)
    {
        if (_Poisoned)
        {
            return FragmentAddResult.OverlapDiscarded;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        int payloadLength = data.Length;
        if (payloadLength > _MaxTotalLength || offset >= _MaxTotalLength - payloadLength + 1)
        {
            return FragmentAddResult.OversizeDiscarded;
        }

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
                if (payloadLength > _Fragments[i].Length)
                {
                    _ReceivedBytes = checked(_ReceivedBytes + payloadLength - _Fragments[i].Length);
                    byte[] rented = ArrayPool<byte>.Shared.Rent(payloadLength);
                    data.CopyTo(rented.AsSpan(0, payloadLength));
                    _ReturnFragment(_Fragments[i].Data);
                    _Fragments[i] = new Fragment(offset, rented, payloadLength);
                }
                if (_IsComplete())
                {
                    return FragmentAddResult.Complete;
                }
                return FragmentAddResult.Incomplete;
            }
            if (_Fragments[i].Offset > offset)
            {
                insertIdx = i;
                break;
            }
        }

        if (_Fragments.Count >= _MaxFragmentCount)
        {
            return FragmentAddResult.OversizeDiscarded;
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
                if (prev.Offset + prev.Length > offset)
                {
                    _Poisoned = true;
                    return FragmentAddResult.OverlapDiscarded;
                }
            }

            // Check overlap with the fragment immediately after: newEnd > next.offset
            if (insertIdx < _Fragments.Count)
            {
                Fragment next = _Fragments[insertIdx];
                if (offset + payloadLength > next.Offset)
                {
                    _Poisoned = true;
                    return FragmentAddResult.OverlapDiscarded;
                }
            }
        }

        byte[] newRented = ArrayPool<byte>.Shared.Rent(payloadLength);
        data.CopyTo(newRented.AsSpan(0, payloadLength));
        _Fragments.Insert(insertIdx, new Fragment(offset, newRented, payloadLength));
        _ReceivedBytes = checked(_ReceivedBytes + payloadLength);

        // When the last fragment arrives (MF=0), we know the total datagram payload length.
        if (!moreFragments)
        {
            int total = checked(offset + payloadLength);
            _TotalLength = total;
        }

        if (_IsComplete())
        {
            return FragmentAddResult.Complete;
        }
        return FragmentAddResult.Incomplete;
    }

    /// <summary>
    /// Reassembles all fragments into a single contiguous byte array.
    /// Must only be called when <see cref="AddFragment"/> returned
    /// <see cref="FragmentAddResult.Complete"/>.
    /// </summary>
    /// <returns>
    /// The reassembled payload, or <see langword="null"/> when the total length is unknown
    /// or a fragment extends past the terminal length.
    /// </returns>
    public byte[]? Reassemble()
    {
        if (_TotalLength <= 0)
        {
            return null;
        }

        byte[] result = new byte[_TotalLength];

        // Copy each fragment into its position within the reassembled buffer.
        foreach (Fragment fragment in _Fragments)
        {
            int end = fragment.Offset + fragment.Length;
            if (end > _TotalLength)
            {
                return null; // Fragment extends beyond expected length
            }
            fragment.Data.AsSpan(0, fragment.Length).CopyTo(result.AsSpan(fragment.Offset));
        }

        return result;
    }

    /// <summary>
    /// Returns rented fragment buffers to <see cref="ArrayPool{T}.Shared"/> and clears state.
    /// Call when the buffer is evicted or removed from the defragmenter.
    /// </summary>
    internal void Release()
    {
        for (int i = 0; i < _Fragments.Count; i++)
        {
            _ReturnFragment(_Fragments[i].Data);
        }
        _Fragments.Clear();
        _TotalLength = -1;
        _ReceivedBytes = 0;
        _Poisoned = false;
    }

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ReturnFragment(byte[] buffer) =>
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

    /// <summary>
    /// Checks whether all fragments have been received to form a complete datagram.
    /// Requires: (1) last fragment received (MF=0 → _TotalLength known),
    ///           (2) no gaps in the byte range [0, _TotalLength).
    /// </summary>
    private bool _IsComplete()
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
            int end = f.Offset + f.Length;
            if (end > expectedOffset)
            {
                expectedOffset = end;
            }
        }

        return expectedOffset >= _TotalLength;
    }

    #endregion
}
