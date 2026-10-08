// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Sequence-ordered byte stream for TCP-style reassembly.
/// In-order bytes sit in one contiguous prefix. A hole holds later segments until the cursor reaches them.
/// Retransmitted bytes at or before the cursor are dropped.
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
    private bool _Initialized;
    private bool _EverConsumed;
    private uint _NextExpected;
    private uint _OriginSeq;

    // Contiguous prefix lives in one array. _Offset is the first unread byte.
    private byte[] _Contiguous = [];
    private int _Offset;
    private int _ContiguousLength;

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

    /// <summary>The contiguous prefix. Valid until the next mutating call.</summary>
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

        // The first segment defines the left edge. Do not assume sequence 0.
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

        _Offset += count;
        _ContiguousLength -= count;
        _EverConsumed = true;

        // Compact when the dead prefix dominates, so the next append does not allocate.
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

    private bool _Rebase(uint sequence, ReadOnlyMemory<byte> payload)
    {
        // Refuse before discarding the current prefix. Overlap can only shrink the total.
        if (_ContiguousLength + _HeldBytes + payload.Length > _MaxBufferSize)
        {
            return false;
        }

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
            if (_ContiguousLength + _HeldBytes + payload.Length > _MaxBufferSize)
            {
                return false;
            }

            _AppendContiguous(payload.Span);
            _NextExpected += (uint)payload.Length;
            _DrainHeld();
            return true;
        }

        // Hole: hold until the cursor reaches this sequence.
        if (_ContiguousLength + _HeldBytes + payload.Length > _MaxBufferSize)
        {
            return false;
        }

        _Hold(sequence, payload);
        return true;
    }

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

            int skip = (int)(_NextExpected - held.Sequence);
            ReadOnlySpan<byte> tail = held.Data.Span[skip..];
            _RemoveHeld(index);
            _AppendContiguous(tail);
            _NextExpected += (uint)tail.Length;
        }
    }

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
            // HeldSegment is a struct. A second property read is a new copy,
            // so the null check above would not flow to Return.
            HeldSegment held = _Held[i];
            if (held.Owned is not null)
            {
                ArrayPool<byte>.Shared.Return(held.Owned);
            }
        }

        _Held.Clear();
        _HeldBytes = 0;
    }

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

        // Grow by doubling, capped at the configured maximum. The caller already refused an append past that cap.
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
