// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Sequence-ordered byte stream for transports that number their bytes in a 32-bit serial-number space (TCP is the first user).
/// In-order bytes sit in one contiguous prefix. A hole holds later segments until the cursor reaches them.
/// Retransmitted bytes at or before the cursor are dropped.
/// <para>
/// <b>Sequence arithmetic:</b> the sequence numbers are 32-bit and wrap (as TCP's do). Every ordering test therefore uses the signed
/// difference <c>(int)(a - b)</c> (serial number arithmetic): negative means <c>a</c> is before <c>b</c>. This is
/// correct while the two compared numbers are less than 2^31 apart; a segment further away is classified by the sign of
/// the difference alone (before the cursor, so dropped as a retransmission, or after it, so held behind a hole).
/// </para>
/// <para>
/// <b>Memory limit:</b> the sum of the contiguous prefix and all held segments never exceeds
/// <see cref="StreamReassemblyConfig.MaxBufferSize"/> (threat: a peer that never completes a PDU or leaves holes).
/// An append that would exceed it returns <see langword="false"/> and the owner moves the stream to its error state;
/// nothing is truncated silently.
/// </para>
/// <para>
/// <b>Thread-safety:</b> one owner thread at a time, the same rule as
/// <see cref="IStreamPduBoundaryDetector"/>. Not safe to share across threads without external synchronization.
/// </para>
/// </summary>
public sealed class SequenceStreamBuffer
{
    #region Fields

    private readonly int _MaxBufferSize;
    private readonly bool _CopySegments;

    // Right edge: sequence number of the next byte that extends the contiguous prefix.
    // _OriginSeq is the sequence number of the first byte of the prefix while nothing has been consumed; it lets an
    // earlier-arriving segment move the left edge backward (see _Rebase). After the first Consume it is no longer used.
    private bool _Initialized;
    private bool _EverConsumed;
    private uint _NextExpected;
    private uint _OriginSeq;

    // Contiguous prefix lives in one array, so a boundary detector sees one span and not a chain of segments.
    // _Offset is the first unread byte; the dead bytes before it are reclaimed lazily by _AppendContiguous.
    private byte[] _Contiguous = [];
    private int _Offset;
    private int _ContiguousLength;

    // Segments behind a hole, sorted by sequence with the HIGHEST sequence first (serial-number order, so the order survives
    // a 32-bit wrap). Motivation: the segment to drain next is always the lowest one, and with the lowest at the END of the
    // list its removal is O(1) instead of a List.RemoveAt shift. An insert is a binary search (O(log k)) plus a shift of
    // the entries below the insert position; a reorder burst inserts near the end, so that shift is short. The previous
    // arrival-order list scanned all k held segments once per drained segment (O(k^2)); a hole at the start of a large
    // download holds thousands of segments, and only the byte cap bounds k.
    // _HeldBytes mirrors the sum of their lengths so the cap check is O(1).
    private readonly List<HeldSegment> _Held = [];
    private int _HeldBytes;

    // Which packets delivered the bytes of the contiguous prefix, in stream order. Motivation: a reassembled PDU is shown
    // together with the packets that carried its bytes, and only this buffer knows which packet contributed which byte
    // (held segments reach the prefix later than they arrived). One entry per append to the prefix, merged when the same
    // packet appends twice in a row; End is the running total of bytes appended when the entry was closed, so an entry
    // covers [previous End, End). Entries that lie completely before the consumed offset are trimmed in Consume.
    // Memory: one 16-byte entry per contributing segment of the unconsumed prefix, bounded by MaxBufferSize / 1 byte in
    // the worst case of one-byte segments (the byte cap also bounds the number of entries).
    private readonly List<Contributor> _Contributors = [];
    private int _ContributorHead;
    private long _AppendedTotal;
    private long _ConsumedTotal;

    #endregion

    #region Construction

    /// <summary>Creates a buffer bounded by <see cref="StreamReassemblyConfig.MaxBufferSize"/>.</summary>
    /// <param name="config">Reassembly limits and the copy-on-append flag. The detector is not used here.</param>
    public SequenceStreamBuffer(StreamReassemblyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _MaxBufferSize = config.MaxBufferSize;
        _CopySegments = config.CopySegments;
    }

    #endregion

    #region Public API

    /// <summary>Bytes currently readable as one contiguous prefix (no hole in front).</summary>
    public int ContiguousLength => _ContiguousLength;

    /// <summary>
    /// All bytes the buffer holds: the contiguous prefix plus the segments held behind a hole. This is the sum that
    /// <see cref="StreamReassemblyConfig.MaxBufferSize"/> bounds, and what is lost when the owner drops the buffer.
    /// </summary>
    public int BufferedLength => _ContiguousLength + _HeldBytes;

    /// <summary>
    /// How many times an earlier segment moved the left edge backward and rebuilt the prefix (see <see cref="TryAppend(uint, ReadOnlyMemory{byte}, int)"/>).
    /// A rebase changes what lies in FRONT of already inspected bytes, so an owner that keeps a position into the prefix
    /// (a stateful boundary detector) compares this counter after each append and drops its position when it changed.
    /// </summary>
    public int RebaseCount { get; private set; }

    /// <summary>
    /// The contiguous prefix. Valid until the next mutating call (<see cref="TryAppend(uint, ReadOnlyMemory{byte}, int)"/>, <see cref="Consume"/>,
    /// <see cref="Clear"/>): an append may move the bytes into a new array or slide them to the array start.
    /// </summary>
    public ReadOnlySpan<byte> ContiguousSpan => _Contiguous.AsSpan(_Offset, _ContiguousLength);

    /// <summary>
    /// Inserts payload that starts at <paramref name="sequence"/>.
    /// Bytes at or before the contiguous cursor are dropped (retransmission overlap).
    /// A hole holds the segment until the cursor reaches it.
    /// Returns <see langword="false"/> only when the buffer cap would be exceeded; the cursor does not move in that case.
    /// </summary>
    /// <param name="sequence">Sequence number of <paramref name="payload"/>[0] in the stream's 32-bit serial-number space.</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload) => TryAppend(sequence, payload, NoPacket);

    /// <summary>
    /// Inserts payload like <see cref="TryAppend(uint, ReadOnlyMemory{byte})"/> and remembers which packet delivered it, so
    /// <see cref="GetContributingPacketIds"/> can later name the packets behind a prefix.
    /// </summary>
    /// <param name="sequence">Sequence number of <paramref name="payload"/>[0] in the stream's 32-bit serial-number space.</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    /// <param name="packetId">Opaque id chosen by the caller for the packet (or other unit) that carried the payload; the buffer only stores and returns it. <see cref="NoPacket"/> when it is not tracked.</param>
    /// <returns><see langword="false"/> only when the buffer cap would be exceeded.</returns>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload, int packetId)
    {
        if (payload.Length == 0)
        {
            return true;
        }

        // The first segment defines the left edge. Do not assume sequence 0 (the capture may start mid-connection, and
        // the initial sequence number is random). Refuse an oversize first segment before any state changes.
        if (!_Initialized)
        {
            if (payload.Length > _MaxBufferSize)
            {
                return false;
            }

            _Initialized = true;
            _OriginSeq = sequence;
            _NextExpected = sequence;
        }

        // An earlier segment may arrive before the left edge is known. Until bytes have been
        // consumed, move the origin backward and rebuild so the hole is in front, not behind.
        if (_ContiguousLength > 0 && !_EverConsumed && (int)(sequence - _OriginSeq) < 0)
        {
            return _Rebase(sequence, payload, packetId);
        }

        return _Insert(sequence, payload, packetId);
    }

    /// <summary>Packet id value that means "this append is not attributed to a packet".</summary>
    public const int NoPacket = -1;

    /// <summary>
    /// Ids of the packets that delivered the first <paramref name="length"/> bytes of the contiguous prefix, in stream order.
    /// A packet whose payload straddles the end of the range is included, so the same packet can be named for two
    /// consecutive prefixes. Appends without a packet id are skipped.
    /// </summary>
    /// <remarks>
    /// Allocates the result array; it is called once per extracted PDU, which already allocates the PDU copy. Two passes
    /// (count, fill) give an exact-size array without a temporary list. Thread safety: owner thread only.
    /// </remarks>
    /// <param name="length">Number of leading prefix bytes; clamped to <see cref="ContiguousLength"/>.</param>
    /// <returns>The packet ids; empty when no tracked packet contributed.</returns>
    public int[] GetContributingPacketIds(int length)
    {
        long from = _ConsumedTotal;
        long to = from + Math.Clamp(length, 0, _ContiguousLength);

        // Pass 1: count the entries that overlap [from, to). The first live entry starts at the consumed offset because
        // the bytes in front of it were already delivered.
        int count = 0;
        for (int i = _ContributorHead; i < _Contributors.Count; i++)
        {
            long start = i == _ContributorHead ? from : _Contributors[i - 1].End;
            if (start >= to)
            {
                break;
            }

            if (_Contributors[i].PacketId != NoPacket)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        // Pass 2: the same walk, writing the ids.
        int[] ids = new int[count];
        int written = 0;
        for (int i = _ContributorHead; i < _Contributors.Count && written < count; i++)
        {
            long start = i == _ContributorHead ? from : _Contributors[i - 1].End;
            if (start >= to)
            {
                break;
            }

            if (_Contributors[i].PacketId != NoPacket)
            {
                ids[written++] = _Contributors[i].PacketId;
            }
        }

        return ids;
    }

    /// <summary>Copies up to <paramref name="destination"/>'s length from the contiguous prefix.</summary>
    /// <param name="destination">Caller buffer.</param>
    /// <param name="written">Number of bytes copied.</param>
    /// <returns><see langword="true"/> when the prefix was non-empty.</returns>
    public bool TryCopyContiguous(Span<byte> destination, out int written)
    {
        if (_ContiguousLength == 0 || destination.IsEmpty)
        {
            written = 0;
            return false;
        }

        written = Math.Min(destination.Length, _ContiguousLength);
        ContiguousSpan[..written].CopyTo(destination);
        return true;
    }

    /// <summary>Drops <paramref name="count"/> bytes from the front of the contiguous prefix.</summary>
    /// <param name="count">Bytes to drop. Must be between 0 and <see cref="ContiguousLength"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is outside that range.</exception>
    public void Consume(int count)
    {
        if ((uint)count > (uint)_ContiguousLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Consume cannot pass the contiguous prefix.");
        }

        // Consuming only advances the read offset (no copy). From now on the left edge is fixed, so a later segment
        // that lies before the origin is a retransmission and is dropped instead of rebasing.
        _Offset += count;
        _ContiguousLength -= count;
        _EverConsumed = true;
        _ConsumedTotal += count;
        _TrimContributors();

        // When everything was consumed, rewind the offset so the next append reuses the array from index 0 without a
        // copy and without allocating. A partly consumed prefix is slid down lazily by _AppendContiguous.
        if (_Offset > 0 && _ContiguousLength == 0)
        {
            _Offset = 0;
        }
    }

    /// <summary>Drops the prefix, held segments, and the sequence cursor.</summary>
    public void Clear()
    {
        _ReleaseHeld();
        _ResetContributors();
        _Initialized = false;
        _EverConsumed = false;
        _NextExpected = 0;
        _OriginSeq = 0;
        _Offset = 0;
        _ContiguousLength = 0;
        _Contiguous = [];
    }

    #endregion

    #region Insert

    /// <summary>
    /// Rebuilds the buffer around an earlier left edge: a segment arrived whose sequence lies before the origin while
    /// nothing has been consumed yet (capture started mid-stream, or the first seen segment was a reordered later one).
    /// The old prefix and held segments are copied out, the origin moves to <paramref name="sequence"/>, and everything is
    /// inserted again, so the old prefix becomes a held segment behind a hole, or joins the prefix when contiguous.
    /// Cold path (copies the whole buffer), so allocation is acceptable here. Returns <see langword="false"/> on the cap.
    /// </summary>
    private bool _Rebase(uint sequence, ReadOnlyMemory<byte> payload, int packetId)
    {
        // Refuse before discarding the current prefix. Overlap can only shrink the total.
        if (_WouldExceedMax(payload.Length))
        {
            return false;
        }

        // Snapshot the old content as owned arrays first: the buffers below are about to be reset and reused. The prefix is
        // snapshotted piece by piece, one piece per contributing packet, so the packet ids survive the rebuild (a single
        // copy would attribute the whole old prefix to one packet). Nothing was consumed yet (that is the rebase
        // condition), so the contributor offsets are offsets into the prefix.
        List<(uint Sequence, byte[] Data, int PacketId)> previous = [];
        if (_ContiguousLength > 0)
        {
            ReadOnlySpan<byte> prefix = ContiguousSpan;
            long covered = 0;
            for (int i = _ContributorHead; i < _Contributors.Count && covered < prefix.Length; i++)
            {
                long end = Math.Min(_Contributors[i].End, prefix.Length);
                if (end > covered)
                {
                    previous.Add((_OriginSeq + (uint)covered, prefix[(int)covered..(int)end].ToArray(), _Contributors[i].PacketId));
                    covered = end;
                }
            }

            if (covered < prefix.Length)
            {
                previous.Add((_OriginSeq + (uint)covered, prefix[(int)covered..].ToArray(), NoPacket));
            }
        }

        foreach (HeldSegment held in _Held)
        {
            previous.Add((held.Sequence, held.Data.ToArray(), held.PacketId));
        }

        _ReleaseHeld();
        _ResetContributors();
        RebaseCount++;
        _Offset = 0;
        _ContiguousLength = 0;
        _OriginSeq = sequence;
        _NextExpected = sequence;

        // Insert the new earliest segment first so it defines the cursor, then replay the old content behind it.
        // The cap was checked up front, so a false result here is not expected; if it happens the buffer is left
        // partially rebuilt, which is acceptable only because the owner treats any false as a fatal stream error.
        if (!_Insert(sequence, payload, packetId))
        {
            return false;
        }

        foreach ((uint seq, byte[] data, int oldPacketId) in previous)
        {
            if (!_Insert(seq, data, oldPacketId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Places a segment relative to the cursor: drops already-delivered bytes, trims a partial overlap to the new tail,
    /// appends an in-order segment to the contiguous prefix (and drains held segments it unblocks), or holds a segment
    /// that lies behind a hole. Returns <see langword="false"/> only when the buffer cap would be exceeded.
    /// </summary>
    private bool _Insert(uint sequence, ReadOnlyMemory<byte> payload, int packetId)
    {
        uint end = sequence + (uint)payload.Length;

        // Fully at or before the cursor: retransmission of bytes we already have.
        if ((int)(end - _NextExpected) <= 0)
        {
            return true;
        }

        // Overlap: keep only the tail that starts at the cursor.
        if ((int)(sequence - _NextExpected) < 0)
        {
            int skip = (int)(_NextExpected - sequence);
            payload = payload[skip..];
            sequence = _NextExpected;
        }

        if (sequence == _NextExpected)
        {
            if (_WouldExceedMax(payload.Length))
            {
                return false;
            }

            _AppendContiguous(payload.Span, packetId);
            _NextExpected += (uint)payload.Length;
            _DrainHeld();
            return true;
        }

        // Hole: hold until the cursor reaches this sequence.
        if (_WouldExceedMax(payload.Length))
        {
            return false;
        }

        _Hold(sequence, payload, packetId);
        return true;
    }

    /// <summary>
    /// Whether adding <paramref name="payloadLength"/> bytes would exceed the buffer cap. Summed in <see cref="long"/>:
    /// with a cap near <see cref="int.MaxValue"/> the <see cref="int"/> sum would wrap and let the payload in.
    /// </summary>
    private bool _WouldExceedMax(int payloadLength) =>
        (long)_ContiguousLength + _HeldBytes + payloadLength > _MaxBufferSize;

    /// <summary>
    /// After the cursor advanced, moves every held segment that now starts at (or overlaps) the cursor into the
    /// contiguous prefix, lowest sequence first, and discards held segments that are fully behind the cursor.
    /// Cost: O(1) per moved segment, because the lowest held segment is the last list entry (see the field comment).
    /// </summary>
    private void _DrainHeld()
    {
        while (_Held.Count > 0)
        {
            // The lowest sequence is at the end of the sorted list. Everything above it is further away from the cursor.
            HeldSegment held = _Held[^1];
            uint end = held.Sequence + (uint)held.Data.Length;
            if ((int)(end - _NextExpected) <= 0)
            {
                // Fully behind the cursor (a retransmission filled the same bytes): its bytes are already in the prefix.
                _RemoveLastHeld();
                continue;
            }

            if ((int)(held.Sequence - _NextExpected) > 0)
            {
                // The lowest held segment still sits behind a hole, so every other held segment does too.
                return;
            }

            // Copy the tail before removing the segment: removal returns its array to the shared pool, where another
            // renter could overwrite it while it is still being read.
            int skip = (int)(_NextExpected - held.Sequence);
            ReadOnlySpan<byte> tail = held.Data.Span[skip..];
            _AppendContiguous(tail, held.PacketId);
            _NextExpected += (uint)tail.Length;
            _RemoveLastHeld();
        }
    }

    /// <summary>
    /// Stores a segment that lies behind a hole, keeping the list sorted highest sequence first. With <c>CopySegments</c>
    /// the bytes are copied into a pooled array, because the caller's memory (a live-capture packet buffer) may be
    /// recycled before the hole is filled; without it the segment keeps a reference to the caller's memory (zero copy,
    /// valid for file-based sources). The pooled array is returned in <see cref="_RemoveLastHeld"/> or
    /// <see cref="_ReleaseHeld"/>.
    /// </summary>
    private void _Hold(uint sequence, ReadOnlyMemory<byte> payload, int packetId)
    {
        ReadOnlyMemory<byte> stored = payload;
        byte[]? owned = null;
        if (_CopySegments)
        {
            owned = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.Span.CopyTo(owned);
            stored = owned.AsMemory(0, payload.Length);
        }

        _Held.Insert(_FindInsertIndex(sequence), new HeldSegment(sequence, stored, owned, packetId));
        _HeldBytes += payload.Length;
    }

    /// <summary>
    /// Index at which a segment with <paramref name="sequence"/> keeps <see cref="_Held"/> sorted highest first.
    /// Binary search with the signed 32-bit difference, so the order is correct across a sequence wrap (all held
    /// sequences lie less than 2^31 after the cursor, the same precondition as the rest of this class).
    /// </summary>
    private int _FindInsertIndex(uint sequence)
    {
        int low = 0;
        int high = _Held.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if ((int)(_Held[middle].Sequence - sequence) > 0)
            {
                // The entry at "middle" is higher than the new segment, so the new one goes behind it.
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Removes the lowest held segment (the last entry) and returns its pooled array.</summary>
    private void _RemoveLastHeld()
    {
        int index = _Held.Count - 1;
        HeldSegment held = _Held[index];
        _HeldBytes -= held.Data.Length;
        if (held.Owned is not null)
        {
            ArrayPool<byte>.Shared.Return(held.Owned);
        }

        _Held.RemoveAt(index);
    }

    private void _ReleaseHeld()
    {
        for (int i = 0; i < _Held.Count; i++)
        {
            // HeldSegment is a struct stored in the list: copy it once into a local, so the null check and the Return
            // below use the same Owned reference.
            HeldSegment held = _Held[i];
            if (held.Owned is not null)
            {
                ArrayPool<byte>.Shared.Return(held.Owned);
            }
        }

        _Held.Clear();
        _HeldBytes = 0;
    }

    /// <summary>
    /// Appends bytes to the contiguous prefix, in order of cheapness: (1) copy into spare capacity behind the live
    /// tail, (2) slide the live bytes to the array start when that frees enough room (no allocation), (3) allocate a
    /// larger array. The caller has already checked the buffer cap, so growth never exceeds it.
    /// </summary>
    private void _AppendContiguous(ReadOnlySpan<byte> data, int packetId)
    {
        if (data.IsEmpty)
        {
            return;
        }

        // Attribution first: the bytes below are about to be copied, and the entry only needs the length.
        _RecordContributor(data.Length, packetId);

        // Fast path: the live tail still fits after the current prefix.
        int liveEnd = _Offset + _ContiguousLength;
        if (liveEnd + data.Length <= _Contiguous.Length)
        {
            data.CopyTo(_Contiguous.AsSpan(liveEnd));
            _ContiguousLength += data.Length;
            return;
        }

        int newLength = _ContiguousLength + data.Length;

        // Compact the dead prefix when the array is large enough after a slide.
        if (newLength <= _Contiguous.Length)
        {
            if (_ContiguousLength > 0 && _Offset > 0)
            {
                _Contiguous.AsSpan(_Offset, _ContiguousLength).CopyTo(_Contiguous);
            }

            _Offset = 0;
            data.CopyTo(_Contiguous.AsSpan(_ContiguousLength));
            _ContiguousLength = newLength;
            return;
        }

        // Grow by doubling (amortized O(1) copies per byte), capped at the configured maximum. The caller already
        // refused an append past that cap. The new array is a plain allocation, not a pooled rental: it stays alive as
        // the stream's prefix and ContiguousSpan hands out views of it, so it has no point at which it could be returned.
        // The halving test avoids overflow of cap * 2 for caps near int.MaxValue.
        int cap = _Contiguous.Length == 0 ? newLength : _Contiguous.Length;
        while (cap < newLength)
        {
            if (cap > _MaxBufferSize / 2)
            {
                cap = _MaxBufferSize;
                break;
            }

            cap *= 2;
        }

        if (cap < newLength)
        {
            cap = newLength;
        }

        byte[] grown = new byte[cap];
        if (_ContiguousLength > 0)
        {
            _Contiguous.AsSpan(_Offset, _ContiguousLength).CopyTo(grown);
        }

        data.CopyTo(grown.AsSpan(_ContiguousLength));
        _Contiguous = grown;
        _Offset = 0;
        _ContiguousLength = newLength;
    }

    #endregion

    #region Contributors

    /// <summary>
    /// Notes that <paramref name="length"/> bytes were appended to the prefix on behalf of <paramref name="packetId"/>. Two
    /// appends by the same packet in a row share one entry, so a packet is listed once per contiguous run.
    /// </summary>
    private void _RecordContributor(int length, int packetId)
    {
        _AppendedTotal += length;

        int last = _Contributors.Count - 1;
        if (last >= _ContributorHead && _Contributors[last].PacketId == packetId)
        {
            _Contributors[last] = new Contributor(_AppendedTotal, packetId);
            return;
        }

        _Contributors.Add(new Contributor(_AppendedTotal, packetId));
    }

    /// <summary>
    /// Drops the entries that lie completely before the consumed offset. Entries are removed from the front in bulk: the head
    /// index moves on every call (O(1)), and the list is compacted only when more than half of it is dead, so the shift cost
    /// is amortized over the entries that were skipped.
    /// </summary>
    private void _TrimContributors()
    {
        while (_ContributorHead < _Contributors.Count && _Contributors[_ContributorHead].End <= _ConsumedTotal)
        {
            _ContributorHead++;
        }

        if (_ContributorHead > 32 && _ContributorHead * 2 > _Contributors.Count)
        {
            _Contributors.RemoveRange(0, _ContributorHead);
            _ContributorHead = 0;
        }
    }

    /// <summary>Forgets all attribution; used when the whole stream state is dropped or rebuilt.</summary>
    private void _ResetContributors()
    {
        _Contributors.Clear();
        _ContributorHead = 0;
        _AppendedTotal = 0;
        _ConsumedTotal = 0;
    }

    /// <summary>One attributed run of the prefix: it ends at stream offset <paramref name="end"/> (running total).</summary>
    private readonly struct Contributor(long end, int packetId)
    {
        internal long End { get; } = end;

        internal int PacketId { get; } = packetId;
    }

    #endregion

    #region Held segment

    private readonly struct HeldSegment(uint sequence, ReadOnlyMemory<byte> data, byte[]? owned, int packetId)
    {
        internal uint Sequence { get; } = sequence;

        internal ReadOnlyMemory<byte> Data { get; } = data;

        internal byte[]? Owned { get; } = owned;

        /// <summary>Packet that delivered the bytes, kept so the attribution survives the wait behind the hole.</summary>
        internal int PacketId { get; } = packetId;
    }

    #endregion
}
