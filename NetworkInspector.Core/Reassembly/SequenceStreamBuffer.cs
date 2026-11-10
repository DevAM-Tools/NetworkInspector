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
/// nothing is truncated silently. A segment that starts before the first byte seen rebuilds the buffer; the number of rebuilds is
/// bounded by <see cref="StreamReassemblyConfig.MaxRebases"/> (threat: quadratic work from descending segments), and
/// <see cref="TryAppend(uint, ReadOnlyMemory{byte}, int, out StreamAppendRefusal)"/> names the limit that refused a segment.
/// </para>
/// <para>
/// <b>Thread-safety:</b> one owner thread at a time, the same rule as
/// <see cref="IStreamPduBoundaryDetector"/>. Not safe to share across threads without external synchronization.
/// </para>
/// </summary>
public sealed class SequenceStreamBuffer
{
    #region Configuration and state

    /// <summary>
    /// Cap on <see cref="BufferedLength"/> in bytes, taken from <see cref="StreamReassemblyConfig.MaxBufferSize"/> (positive
    /// by that property's validation). Threat, posture, and range are documented there.
    /// </summary>
    private readonly int _MaxBufferSize;

    /// <summary>The most rebuilds of the buffer for a segment that starts before the origin, from <see cref="StreamReassemblyConfig.MaxRebases"/>.</summary>
    private readonly int _MaxRebases;

    /// <summary>Whether held segments are copied into pooled arrays (<see cref="StreamReassemblyConfig.CopySegments"/>).</summary>
    private readonly bool _CopySegments;

    /// <summary>
    /// Whether the first segment has defined the left edge of the stream (<see cref="_OriginSeq"/> and
    /// <see cref="_NextExpected"/> are valid).
    /// </summary>
    private bool _Initialized;

    /// <summary>
    /// Whether <see cref="Consume"/> was called since the last <see cref="Clear"/>; from then on the left edge is fixed and
    /// no rebase happens.
    /// </summary>
    private bool _EverConsumed;

    /// <summary>Right edge: sequence number of the next byte that extends the contiguous prefix.</summary>
    private uint _NextExpected;

    /// <summary>
    /// Sequence number of the first byte of the prefix while nothing has been consumed; it lets an earlier-arriving segment
    /// move the left edge backward (see <see cref="_Rebase"/>). After the first <see cref="Consume"/> it is no longer used.
    /// </summary>
    private uint _OriginSeq;

    /// <summary>
    /// Backing array of the contiguous prefix. One array, so a boundary detector sees one span and not a chain of segments.
    /// The live bytes are <c>[_Offset, _Offset + _ContiguousLength)</c>.
    /// </summary>
    private byte[] _Contiguous = [];

    /// <summary>
    /// Index of the first unread byte in <see cref="_Contiguous"/>; the dead bytes before it are reclaimed lazily by
    /// <see cref="_AppendContiguous"/>.
    /// </summary>
    private int _Offset;

    /// <summary>Number of unread bytes of the contiguous prefix.</summary>
    private int _ContiguousLength;

    /// <summary>
    /// Segments behind a hole, as a binary min-heap keyed by sequence in serial-number order (so the order survives a 32-bit
    /// wrap). Why a heap: only the lowest held segment is ever taken (it is the next one the cursor can reach), and a heap
    /// inserts and removes in O(log k) for every arrival order. A sorted list is O(k) per insert at one of its ends, and the
    /// sender of the bytes chooses the order (threat: algorithmic complexity). Only the byte cap bounds k. Array-backed, so a
    /// held segment costs no allocation of its own.
    /// </summary>
    private readonly PriorityQueue<HeldSegment, uint> _Held = new(SerialNumberComparer.Instance);

    /// <summary>Sum of the lengths of the held segments, so the cap check is O(1).</summary>
    private int _HeldBytes;

    /// <summary>
    /// Which packets delivered the bytes of the contiguous prefix, in stream order. Motivation: a reassembled PDU is shown
    /// together with the packets that carried its bytes, and only this buffer knows which packet contributed which byte
    /// (held segments reach the prefix later than they arrived). One entry per append to the prefix, merged when the same
    /// packet appends twice in a row; <c>End</c> is the running total of bytes appended when the entry was closed, so an
    /// entry covers <c>[previous End, End)</c>. Entries that lie completely before the consumed offset are trimmed in
    /// <see cref="Consume"/>. Memory: one 16-byte entry per contributing segment of the unconsumed prefix, bounded by
    /// <see cref="_MaxBufferSize"/> entries in the worst case of one-byte segments (the byte cap also bounds the number of entries).
    /// </summary>
    private readonly List<Contributor> _Contributors = [];

    /// <summary>Index of the first live entry of <see cref="_Contributors"/>; the entries before it are dead and compacted in bulk.</summary>
    private int _ContributorHead;

    /// <summary>Running total of bytes ever appended to the prefix since the last reset; the <c>End</c> of the newest entry.</summary>
    private long _AppendedTotal;

    /// <summary>Running total of bytes consumed from the prefix since the last reset; the stream offset of the first unread byte.</summary>
    private long _ConsumedTotal;

    /// <summary>Creates a buffer bounded by <see cref="StreamReassemblyConfig.MaxBufferSize"/>.</summary>
    /// <param name="config">Reassembly limits and the copy-on-append flag. The detector is not used here.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public SequenceStreamBuffer(StreamReassemblyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _MaxBufferSize = config.MaxBufferSize;
        _MaxRebases = config.MaxRebases;
        _CopySegments = config.CopySegments;
    }

    /// <summary>Packet id value that means "this append is not attributed to a packet".</summary>
    public const int NoPacket = -1;

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

    #endregion

    #region Reading and consuming the prefix

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
            long start = i == _ContributorHead
                ? from
                : _Contributors[i - 1].End;
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
            long start = i == _ContributorHead
                ? from
                : _Contributors[i - 1].End;
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

    #region Segment placement

    /// <summary>
    /// Inserts payload that starts at <paramref name="sequence"/>.
    /// Bytes at or before the contiguous cursor are dropped (retransmission overlap).
    /// A hole holds the segment until the cursor reaches it.
    /// Returns <see langword="false"/> only when the buffer cap would be exceeded; the cursor does not move in that case.
    /// </summary>
    /// <param name="sequence">Sequence number of <paramref name="payload"/>[0] in the stream's 32-bit serial-number space.</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    /// <returns><see langword="false"/> only when the buffer cap would be exceeded.</returns>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload) => TryAppend(sequence, payload, NoPacket);

    /// <summary>
    /// Inserts payload like <see cref="TryAppend(uint, ReadOnlyMemory{byte})"/> and remembers which packet delivered it, so
    /// <see cref="GetContributingPacketIds"/> can later name the packets behind a prefix.
    /// </summary>
    /// <param name="sequence">Sequence number of <paramref name="payload"/>[0] in the stream's 32-bit serial-number space.</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    /// <param name="packetId">
    /// Opaque id chosen by the caller for the packet (or other unit) that carried the payload; the buffer only stores and returns it.
    /// <see cref="NoPacket"/> when it is not tracked.
    /// </param>
    /// <returns><see langword="false"/> only when the buffer cap would be exceeded.</returns>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload, int packetId) =>
        TryAppend(sequence, payload, packetId, out _);

    /// <summary>
    /// Inserts payload like <see cref="TryAppend(uint, ReadOnlyMemory{byte}, int)"/> and says which limit refused it when it
    /// stored nothing.
    /// </summary>
    /// <param name="sequence">Sequence number of <paramref name="payload"/>[0] in the stream's 32-bit serial-number space.</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    /// <param name="packetId">The packet that carried the payload, or <see cref="NoPacket"/>.</param>
    /// <param name="refusal">The limit that refused the segment; <see cref="StreamAppendRefusal.None"/> on success.</param>
    /// <returns><see langword="false"/> when the buffer cap or the rebase limit refused the segment.</returns>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload, int packetId, out StreamAppendRefusal refusal)
    {
        refusal = StreamAppendRefusal.None;
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
                refusal = StreamAppendRefusal.BufferSize;
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
            // Security: each rebase copies the whole buffer and the sender chooses how often a segment arrives before the
            // origin, so the number of rebases is a limit (reassembly.maxStreamRebases). Threat: quadratic work from descending
            // segments. Mitigation: refuse the segment and name the limit. Rejected: skipping the rebase silently (the bytes
            // would be lost without a trace). Residual: up to the limit many full copies of the buffer per stream direction.
            if (RebaseCount >= _MaxRebases)
            {
                refusal = StreamAppendRefusal.RebaseLimit;
                return false;
            }

            if (!_Rebase(sequence, payload, packetId))
            {
                refusal = StreamAppendRefusal.BufferSize;
                return false;
            }

            return true;
        }

        if (!_Insert(sequence, payload, packetId))
        {
            refusal = StreamAppendRefusal.BufferSize;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the buffer around an earlier left edge: a segment arrived whose sequence lies before the origin while
    /// nothing has been consumed yet (capture started mid-stream, or the first seen segment was a reordered later one).
    /// The old prefix and held segments are copied out, the origin moves to <paramref name="sequence"/>, and everything is
    /// inserted again, so the old prefix becomes a held segment behind a hole, or joins the prefix when contiguous.
    /// Cold path (copies the whole buffer), so allocation is acceptable here. Returns <see langword="false"/> on the cap.
    /// </summary>
    /// <param name="sequence">Sequence number of the first byte of <paramref name="payload"/>; lies before the current origin.</param>
    /// <param name="payload">The earlier segment that moves the left edge.</param>
    /// <param name="packetId">The packet that carried <paramref name="payload"/>, or <see cref="NoPacket"/>.</param>
    /// <returns><see langword="false"/> when the buffer cap would be exceeded.</returns>
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

        // The pieces are inserted again below, so the heap order of this enumeration is irrelevant.
        foreach ((HeldSegment held, uint _) in _Held.UnorderedItems)
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
    /// <param name="sequence">Sequence number of the first byte of <paramref name="payload"/>.</param>
    /// <param name="payload">The segment; bytes at or before the cursor are dropped.</param>
    /// <param name="packetId">The packet that carried <paramref name="payload"/>, or <see cref="NoPacket"/>.</param>
    /// <returns><see langword="false"/> only when the buffer cap would be exceeded.</returns>
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
    /// <param name="payloadLength">Length in bytes of the segment that would be added.</param>
    /// <returns><see langword="true"/> when the prefix, the held segments, and the payload together exceed the cap.</returns>
    private bool _WouldExceedMax(int payloadLength) =>
        (long)_ContiguousLength + _HeldBytes + payloadLength > _MaxBufferSize;

    /// <summary>
    /// After the cursor advanced, moves every held segment that now starts at (or overlaps) the cursor into the
    /// contiguous prefix, lowest sequence first, and discards held segments that are fully behind the cursor.
    /// Cost: O(log k) per moved segment, because the lowest held segment is the root of the heap (see the field doc).
    /// </summary>
    private void _DrainHeld()
    {
        while (_Held.TryPeek(out HeldSegment held, out _))
        {
            // The lowest sequence is the heap root. Everything else is further away from the cursor.
            uint end = held.Sequence + (uint)held.Data.Length;
            if ((int)(end - _NextExpected) <= 0)
            {
                // Fully behind the cursor (a retransmission filled the same bytes): its bytes are already in the prefix.
                _RemoveLowestHeld();
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
            _RemoveLowestHeld();
        }
    }

    /// <summary>
    /// Stores a segment that lies behind a hole in the heap of held segments. With <c>CopySegments</c>
    /// the bytes are copied into a pooled array, because the caller's memory (a live-capture packet buffer) may be
    /// recycled before the hole is filled; without it the segment keeps a reference to the caller's memory (zero copy,
    /// valid for file-based sources). The pooled array is returned in <see cref="_RemoveLowestHeld"/> or
    /// <see cref="_ReleaseHeld"/>.
    /// </summary>
    /// <param name="sequence">Sequence number of the first byte of <paramref name="payload"/>; behind the cursor.</param>
    /// <param name="payload">The segment; the caller already checked the buffer cap.</param>
    /// <param name="packetId">The packet that carried <paramref name="payload"/>, or <see cref="NoPacket"/>.</param>
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

        _Held.Enqueue(new HeldSegment(sequence, stored, owned, packetId), sequence);
        _HeldBytes += payload.Length;
    }

    /// <summary>Removes the lowest held segment (the heap root) and returns its pooled array.</summary>
    private void _RemoveLowestHeld()
    {
        HeldSegment held = _Held.Dequeue();
        _HeldBytes -= held.Data.Length;
        if (held.Owned is not null)
        {
            ArrayPool<byte>.Shared.Return(held.Owned);
        }
    }

    /// <summary>
    /// Returns every pooled array of the held segments and empties the heap. Order does not matter here, so the unordered
    /// enumeration is used. Each pooled array is returned exactly once because the heap is cleared right after.
    /// </summary>
    private void _ReleaseHeld()
    {
        foreach ((HeldSegment held, uint _) in _Held.UnorderedItems)
        {
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
    /// <param name="data">The bytes that continue the prefix; empty is a no-op.</param>
    /// <param name="packetId">The packet that carried <paramref name="data"/>, or <see cref="NoPacket"/>; recorded for attribution.</param>
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
        int cap = _Contiguous.Length == 0
            ? newLength
            : _Contiguous.Length;
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

    #region Packet attribution

    /// <summary>
    /// Notes that <paramref name="length"/> bytes were appended to the prefix on behalf of <paramref name="packetId"/>. Two
    /// appends by the same packet in a row share one entry, so a packet is listed once per contiguous run.
    /// </summary>
    /// <param name="length">Number of bytes appended; positive.</param>
    /// <param name="packetId">The packet that carried the bytes, or <see cref="NoPacket"/>.</param>
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
    /// <param name="end">Running total of bytes appended to the prefix when the run was closed.</param>
    /// <param name="packetId">The packet that delivered the run, or <see cref="NoPacket"/> when it is not tracked.</param>
    private readonly struct Contributor(long end, int packetId)
    {
        /// <summary>Running total of appended bytes at the end of the run; the run covers <c>[previous End, End)</c>.</summary>
        internal long End { get; } = end;

        /// <summary>The packet that delivered the run, or <see cref="NoPacket"/>.</summary>
        internal int PacketId { get; } = packetId;
    }

    #endregion

    #region Held segment ordering and storage

    /// <summary>
    /// Orders 32-bit sequence numbers in serial-number order. Valid while all compared values lie within 2^31 of each other,
    /// which is the precondition of this class (all held sequences lie less than 2^31 after the cursor). Stateless; thread-safe.
    /// </summary>
    private sealed class SerialNumberComparer : IComparer<uint>
    {
        /// <summary>The one instance; the comparer has no state.</summary>
        public static readonly SerialNumberComparer Instance = new();

        /// <summary>Negative when <paramref name="x"/> comes before <paramref name="y"/> in the stream, also across a wrap.</summary>
        /// <param name="x">The first sequence number.</param>
        /// <param name="y">The second sequence number.</param>
        /// <returns>The signed 32-bit difference <c>x - y</c>.</returns>
        public int Compare(uint x, uint y) => (int)(x - y);
    }

    /// <summary>One segment held behind a hole: its bytes, the pooled array to return (null when the caller's memory is used), and the packet.</summary>
    /// <param name="sequence">Sequence number of the first byte of the segment.</param>
    /// <param name="data">The segment bytes: a view of <paramref name="owned"/> or of the caller's memory.</param>
    /// <param name="owned">
    /// The pooled array to return to <see cref="ArrayPool{T}.Shared"/>, or <see langword="null"/> when the caller's memory is used.
    /// </param>
    /// <param name="packetId">The packet that carried the segment, or <see cref="NoPacket"/>.</param>
    private readonly struct HeldSegment(uint sequence, ReadOnlyMemory<byte> data, byte[]? owned, int packetId)
    {
        /// <summary>Sequence number of the first byte of the segment, in the stream's serial-number space.</summary>
        internal uint Sequence { get; } = sequence;

        /// <summary>The segment bytes; valid until the segment is removed from the heap.</summary>
        internal ReadOnlyMemory<byte> Data { get; } = data;

        /// <summary>The pooled array behind <see cref="Data"/> that must be returned on removal, or <see langword="null"/>.</summary>
        internal byte[]? Owned { get; } = owned;

        /// <summary>Packet that delivered the bytes, kept so the attribution survives the wait behind the hole.</summary>
        internal int PacketId { get; } = packetId;
    }

    #endregion
}
