// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Sequence-ordered byte stream for TCP-style reassembly.
/// In-order bytes sit in one contiguous prefix. A hole holds later segments until the cursor reaches them.
/// Retransmitted bytes at or before the cursor are dropped.
/// <para>
/// <b>Sequence arithmetic:</b> TCP sequence numbers are 32-bit and wrap. Every ordering test therefore uses the signed
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

    // Segments behind a hole, in arrival order (kept unsorted; _IndexOfNextHeld scans for the lowest sequence, so the
    // cost grows with the number of held segments, which only the byte cap bounds).
    // _HeldBytes mirrors the sum of their lengths so the cap check is O(1).
    private readonly List<HeldSegment> _Held = [];
    private int _HeldBytes;

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
    /// The contiguous prefix. Valid until the next mutating call (<see cref="TryAppend"/>, <see cref="Consume"/>,
    /// <see cref="Clear"/>): an append may move the bytes into a new array or slide them to the array start.
    /// </summary>
    public ReadOnlySpan<byte> ContiguousSpan => _Contiguous.AsSpan(_Offset, _ContiguousLength);

    /// <summary>
    /// Inserts payload that starts at <paramref name="sequence"/>.
    /// Bytes at or before the contiguous cursor are dropped (retransmission overlap).
    /// A hole holds the segment until the cursor reaches it.
    /// Returns <see langword="false"/> only when the buffer cap would be exceeded; the cursor does not move in that case.
    /// </summary>
    /// <param name="sequence">TCP sequence number of <paramref name="payload"/>[0].</param>
    /// <param name="payload">Segment payload. Empty is a no-op success.</param>
    public bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload)
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
            return _Rebase(sequence, payload);
        }

        return _Insert(sequence, payload);
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
    private bool _Rebase(uint sequence, ReadOnlyMemory<byte> payload)
    {
        // Refuse before discarding the current prefix. Overlap can only shrink the total.
        if (_WouldExceedMax(payload.Length))
        {
            return false;
        }

        // Snapshot the old content as owned arrays first: the buffers below are about to be reset and reused.
        List<(uint Sequence, byte[] Data)> previous = [];
        if (_ContiguousLength > 0)
        {
            previous.Add((_OriginSeq, ContiguousSpan.ToArray()));
        }

        foreach (HeldSegment held in _Held)
        {
            previous.Add((held.Sequence, held.Data.ToArray()));
        }

        _ReleaseHeld();
        _Offset = 0;
        _ContiguousLength = 0;
        _OriginSeq = sequence;
        _NextExpected = sequence;

        // Insert the new earliest segment first so it defines the cursor, then replay the old content behind it.
        // The cap was checked up front, so a false result here is not expected; if it happens the buffer is left
        // partially rebuilt, which is acceptable only because the owner treats any false as a fatal stream error.
        if (!_Insert(sequence, payload))
        {
            return false;
        }

        foreach ((uint seq, byte[] data) in previous)
        {
            if (!_Insert(seq, data))
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
    private bool _Insert(uint sequence, ReadOnlyMemory<byte> payload)
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

            _AppendContiguous(payload.Span);
            _NextExpected += (uint)payload.Length;
            _DrainHeld();
            return true;
        }

        // Hole: hold until the cursor reaches this sequence.
        if (_WouldExceedMax(payload.Length))
        {
            return false;
        }

        _Hold(sequence, payload);
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
    /// Cost: a linear scan of the held list per moved segment, so it grows with the number of held segments.
    /// </summary>
    private void _DrainHeld()
    {
        while (_Held.Count > 0)
        {
            int index = _IndexOfNextHeld();
            if (index < 0)
            {
                return;
            }

            HeldSegment held = _Held[index];
            uint end = held.Sequence + (uint)held.Data.Length;
            if ((int)(end - _NextExpected) <= 0)
            {
                _RemoveHeld(index);
                continue;
            }

            if ((int)(held.Sequence - _NextExpected) > 0)
            {
                return;
            }

            // Copy the tail before removing the segment: removal returns its array to the shared pool, where another
            // renter could overwrite it while it is still being read.
            int skip = (int)(_NextExpected - held.Sequence);
            ReadOnlySpan<byte> tail = held.Data.Span[skip..];
            _AppendContiguous(tail);
            _NextExpected += (uint)tail.Length;
            _RemoveHeld(index);
        }
    }

    /// <summary>Index of the held segment with the lowest sequence (wrap-safe comparison), or -1 when none is held.</summary>
    private int _IndexOfNextHeld()
    {
        int best = -1;
        uint bestSeq = 0;
        for (int i = 0; i < _Held.Count; i++)
        {
            uint seq = _Held[i].Sequence;
            if (best < 0 || (int)(seq - bestSeq) < 0)
            {
                best = i;
                bestSeq = seq;
            }
        }

        return best;
    }

    /// <summary>
    /// Stores a segment that lies behind a hole. With <c>CopySegments</c> the bytes are copied into a pooled array,
    /// because the caller's memory (a live-capture packet buffer) may be recycled before the hole is filled; without it
    /// the segment keeps a reference to the caller's memory (zero copy, valid for file-based sources). The pooled array
    /// is returned in <see cref="_RemoveHeld"/> or <see cref="_ReleaseHeld"/>.
    /// </summary>
    private void _Hold(uint sequence, ReadOnlyMemory<byte> payload)
    {
        ReadOnlyMemory<byte> stored = payload;
        byte[]? owned = null;
        if (_CopySegments)
        {
            owned = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.Span.CopyTo(owned);
            stored = owned.AsMemory(0, payload.Length);
        }

        _Held.Add(new HeldSegment(sequence, stored, owned));
        _HeldBytes += payload.Length;
    }

    private void _RemoveHeld(int index)
    {
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
    private void _AppendContiguous(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

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

    #region Held segment

    private readonly struct HeldSegment(uint sequence, ReadOnlyMemory<byte> data, byte[]? owned)
    {
        internal uint Sequence { get; } = sequence;

        internal ReadOnlyMemory<byte> Data { get; } = data;

        internal byte[]? Owned { get; } = owned;
    }

    #endregion
}
