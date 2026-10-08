// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// State machine for <see cref="SegmentBuffer"/>.
/// </summary>
internal enum SegmentBufferState
{
    /// <summary>No PDU boundary detector assigned yet.</summary>
    Initial,

    /// <summary>Synchronized — actively detecting and extracting PDUs.</summary>
    Synchronized,

    /// <summary>Last detection returned Invalid — attempting resync.</summary>
    Resyncing,

    /// <summary>Unrecoverable error — no more PDUs can be extracted.</summary>
    Error
}

/// <summary>
/// Per-direction segment buffer for TCP stream reassembly.
/// Orders payload by TCP sequence number via <see cref="SequenceStreamBuffer"/> and extracts PDUs
/// with the detector from <see cref="StreamReassemblyConfig"/>.
/// <para>
/// <b>State machine:</b> Initial → Synchronized (on first append) → Resyncing (on invalid data)
/// → Error (on unrecoverable failure or buffer overflow).
/// </para>
/// </summary>
internal sealed class SegmentBuffer
{
    #region Fields

    private readonly SequenceStreamBuffer _Stream;

    /// <summary>Total bytes successfully consumed as complete PDUs.</summary>
    internal int TotalConsumed { get; private set; }

    /// <summary>Total bytes discarded during resynchronization.</summary>
    internal int TotalDiscarded { get; private set; }

    /// <summary>Current buffer state.</summary>
    internal SegmentBufferState State { get; private set; } = SegmentBufferState.Initial;

    private readonly IPduBoundaryDetector? _Detector;
    private readonly IResyncHeuristic? _ResyncHeuristic;
    private readonly int _MaxPduSize;

    #endregion

    #region Construction

    /// <summary>Creates a new segment buffer from a reassembly configuration.</summary>
    internal SegmentBuffer(StreamReassemblyConfig config)
    {
        _Detector = config.BoundaryDetector;
        _ResyncHeuristic = config.ResyncHeuristic;
        _MaxPduSize = config.MaxPduSize;
        _Stream = new SequenceStreamBuffer(config);
    }

    #endregion

    #region Properties

    /// <summary>Bytes currently held in the sequence buffer (contiguous prefix plus holes).</summary>
    internal int TotalLength => _Stream.ContiguousLength;

    #endregion

    #region Append and extract

    /// <summary>
    /// Inserts a TCP payload at <paramref name="sequence"/>.
    /// Retransmitted bytes are dropped by the sequence buffer. A hole is held until the cursor reaches it.
    /// </summary>
    /// <returns><see langword="true"/> if the segment was accepted; <see langword="false"/> if there is no detector or the buffer is full.</returns>
    internal bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload)
    {
        if (State == SegmentBufferState.Error)
        {
            return false;
        }

        if (State == SegmentBufferState.Initial)
        {
            State = _Detector != null ? SegmentBufferState.Synchronized : SegmentBufferState.Error;
            if (State == SegmentBufferState.Error)
            {
                return false;
            }
        }

        if (!_Stream.TryAppend(sequence, payload))
        {
            State = SegmentBufferState.Error;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tries to extract the next complete PDU from the contiguous (in-order) prefix.
    /// </summary>
    /// <param name="context">Stream detection context for context-aware detectors.</param>
    /// <param name="pdu">The extracted PDU data on success. The memory is owned by the caller.</param>
    /// <returns><see langword="true"/> if a complete PDU was extracted.</returns>
    internal bool TryExtractPdu(in StreamDetectionContext context, out ReadOnlyMemory<byte> pdu)
    {
        pdu = default;

        if (State != SegmentBufferState.Synchronized || _Detector == null || _Stream.ContiguousLength == 0)
        {
            return false;
        }

        // One contiguous array: the detector sees a span, not a concatenation of arrival order.
        ReadOnlySpan<byte> view = _Stream.ContiguousSpan;
        PduBoundaryResult result = _Detector is IStreamPduBoundaryDetector streamDetector
            ? streamDetector.Detect(view, in context)
            : _Detector.Detect(view);

        if (result.IsComplete)
        {
            int pduLength = result.Length;
            if (pduLength > _MaxPduSize || pduLength > _Stream.ContiguousLength)
            {
                State = SegmentBufferState.Error;
                return false;
            }

            byte[] owned = new byte[pduLength];
            view[..pduLength].CopyTo(owned);
            _Stream.Consume(pduLength);
            TotalConsumed += pduLength;
            pdu = owned;
            return true;
        }

        if (result.IsInvalid)
        {
            State = SegmentBufferState.Resyncing;
            _TryResync();
        }

        return false;
    }

    /// <summary>Tells a stream-aware detector to drop per-stream state, then clears buffered bytes.</summary>
    internal void ResetDetector(ulong streamId)
    {
        if (_Detector is IStreamPduBoundaryDetector streamDetector)
        {
            streamDetector.ResetStream(streamId);
        }
    }

    /// <summary>Clears buffered segments and resets counters.</summary>
    internal void Clear()
    {
        _Stream.Clear();
        TotalConsumed = 0;
        TotalDiscarded = 0;
        State = _Detector != null ? SegmentBufferState.Initial : SegmentBufferState.Error;
    }

    #endregion

    #region Resync

    private void _TryResync()
    {
        if (_ResyncHeuristic == null || _Stream.ContiguousLength == 0)
        {
            State = SegmentBufferState.Error;
            return;
        }

        ResyncResult result = _ResyncHeuristic.Resync(_Stream.ContiguousSpan);
        if (!result.IsSuccess || result.SkipBytes > _Stream.ContiguousLength)
        {
            State = SegmentBufferState.Error;
            return;
        }

        _Stream.Consume(result.SkipBytes);
        TotalDiscarded += result.SkipBytes;
        State = SegmentBufferState.Synchronized;
    }

    #endregion
}
