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

    /// <summary>
    /// Last detection returned Invalid — attempting resync. Transient: <see cref="SegmentBuffer"/> resolves it inside the
    /// same <c>TryExtractPdu</c> call (back to <see cref="Synchronized"/> or on to <see cref="Error"/>), so callers never
    /// observe this state between calls.
    /// </summary>
    Resyncing,

    /// <summary>Unrecoverable error — no more PDUs can be extracted.</summary>
    Error
}

/// <summary>
/// Per-direction segment buffer for TCP stream reassembly.
/// Orders payload by TCP sequence number via <see cref="SequenceStreamBuffer"/> and extracts PDUs
/// with the detector from <see cref="StreamReassemblyConfig"/>.
/// <para>
/// <b>State machine:</b> Initial → Synchronized (on first append; Error instead when the configuration has no detector)
/// → Resyncing (on invalid data) → back to Synchronized when the resync heuristic succeeds, otherwise Error
/// (unrecoverable failure, impossible PDU length, or buffer overflow). Error is terminal until <see cref="Clear"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. A buffer belongs to one TCP direction of one stream and is touched only by the
/// ordered first parse of each packet (replays use the recorded PDUs and never reach it).
/// </para>
/// <para>
/// <b>Trust boundary / limits:</b> payload bytes and the lengths a boundary detector reports come from the network.
/// The total buffered bytes are capped by <see cref="StreamReassemblyConfig.MaxBufferSize"/> (enforced by the sequence
/// buffer) and every reported PDU length is checked against <see cref="StreamReassemblyConfig.MaxPduSize"/> and the
/// bytes actually held before anything is copied, so a hostile length cannot force a large allocation.
/// </para>
/// </summary>
internal sealed class SegmentBuffer
{
    #region Fields

    /// <summary>Sequence-ordered byte store: one contiguous prefix plus segments held behind a hole.</summary>
    private readonly SequenceStreamBuffer _Stream;

    /// <summary>Total bytes successfully consumed as complete PDUs.</summary>
    internal int TotalConsumed { get; private set; }

    /// <summary>Total bytes discarded during resynchronization.</summary>
    internal int TotalDiscarded { get; private set; }

    /// <summary>Current buffer state.</summary>
    internal SegmentBufferState State { get; private set; } = SegmentBufferState.Initial;

    /// <summary>PDU boundary detector from the configuration; null means the stream cannot be reassembled.</summary>
    private readonly IPduBoundaryDetector? _Detector;

    /// <summary>Optional heuristic that finds the next plausible PDU start after an invalid detection.</summary>
    private readonly IResyncHeuristic? _ResyncHeuristic;

    /// <summary>Largest PDU a detector may report (copied from the configuration).</summary>
    private readonly int _MaxPduSize;

    #endregion

    #region Construction

    /// <summary>Creates a new segment buffer from a reassembly configuration.</summary>
    /// <param name="config">Detector, resync heuristic, and the size limits; must not be null.</param>
    internal SegmentBuffer(StreamReassemblyConfig config)
    {
        _Detector = config.BoundaryDetector;
        _ResyncHeuristic = config.ResyncHeuristic;
        _MaxPduSize = config.MaxPduSize;
        _Stream = new SequenceStreamBuffer(config);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Bytes currently readable as one in-order prefix. Segments held behind a hole are not counted until the hole is
    /// filled.
    /// </summary>
    internal int TotalLength => _Stream.ContiguousLength;

    #endregion

    #region Append and extract

    /// <summary>
    /// Inserts a TCP payload at <paramref name="sequence"/>.
    /// Retransmitted bytes are dropped by the sequence buffer. A hole is held until the cursor reaches it.
    /// </summary>
    /// <param name="sequence">TCP sequence number of the first payload byte.</param>
    /// <param name="payload">TCP payload; empty is accepted and ignored.</param>
    /// <returns><see langword="true"/> if the segment was accepted; <see langword="false"/> if there is no detector or the buffer is full.</returns>
    internal bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload)
    {
        // Error is terminal: a stream that overflowed or lost framing stops buffering, so a bad stream cannot keep
        // consuming memory.
        if (State == SegmentBufferState.Error)
        {
            return false;
        }

        // Lazy start: without a boundary detector nothing can ever be extracted, so such a stream goes straight to
        // Error instead of buffering bytes that would never be consumed.
        if (State == SegmentBufferState.Initial)
        {
            State = _Detector != null ? SegmentBufferState.Synchronized : SegmentBufferState.Error;
            if (State == SegmentBufferState.Error)
            {
                return false;
            }
        }

        // The sequence buffer refuses data that would exceed the per-direction cap (MaxBufferSize). That is an
        // expected failure for a hostile or stalled peer, and the stream is abandoned (Error) rather than truncated.
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
    /// <returns>
    /// <see langword="true"/> if a complete PDU was extracted. A complete result of length 0, longer than
    /// the PDU cap, or longer than the contiguous prefix moves the buffer to <see cref="SegmentBufferState.Error"/>
    /// and returns <see langword="false"/>.
    /// </returns>
    internal bool TryExtractPdu(in StreamDetectionContext context, out ReadOnlyMemory<byte> pdu)
    {
        pdu = default;

        // Only a synchronized buffer with in-order bytes can yield a PDU; bytes behind a hole are not inspected.
        if (State != SegmentBufferState.Synchronized || _Detector == null || _Stream.ContiguousLength == 0)
        {
            return false;
        }

        // One contiguous array: the detector sees a span, not a concatenation of arrival order.
        // Detectors that implement IStreamPduBoundaryDetector additionally receive the stream context (stream id,
        // handshake observed) so they can keep per-stream state; plain detectors only see the bytes. The span is
        // valid only until the next mutating call on the sequence buffer, which is why it is not stored.
        ReadOnlySpan<byte> view = _Stream.ContiguousSpan;
        PduBoundaryResult result = _Detector is IStreamPduBoundaryDetector streamDetector
            ? streamDetector.Detect(view, in context)
            : _Detector.Detect(view);

        if (result.IsComplete)
        {
            // A complete PDU must consume at least one byte. A detector that reports length 0 would hand the
            // caller's extraction loop the same empty PDU forever, so it is treated like an impossible length.
            // The length comes from a detector that parses untrusted bytes, so it is validated before it sizes an
            // allocation: not above the configured PDU cap and not beyond the bytes actually held. Violations end the
            // stream (Error) instead of clamping, because a wrong length means the framing is lost.
            int pduLength = result.Length;
            if (pduLength <= 0 || pduLength > _MaxPduSize || pduLength > _Stream.ContiguousLength)
            {
                State = SegmentBufferState.Error;
                return false;
            }

            // The PDU is copied into its own array: the sequence buffer reuses and slides its storage after Consume, and
            // the PDU is later attached to the packet as an additional buffer that must outlive the stream state. This
            // is the only per-PDU allocation.
            byte[] owned = new byte[pduLength];
            view[..pduLength].CopyTo(owned);
            _Stream.Consume(pduLength);
            TotalConsumed += pduLength;
            pdu = owned;
            return true;
        }

        // Invalid framing: try to find the next plausible PDU start. A successful resync still returns false from
        // this call (no PDU was extracted); the next call examines the bytes after the skipped region. Incomplete
        // (need more bytes) falls through to false as well, and the buffer waits for more segments.
        if (result.IsInvalid)
        {
            State = SegmentBufferState.Resyncing;
            _TryResync();
        }

        return false;
    }

    /// <summary>
    /// Tells a stream-aware detector to drop the state it keeps for <paramref name="streamId"/>. Does not touch the
    /// buffered bytes (use <see cref="Clear"/> for that); callers reset the detector first, then clear.
    /// </summary>
    /// <param name="streamId">Stream identifier the detector knows this stream by.</param>
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

    /// <summary>
    /// Resolves the transient Resyncing state: asks the resync heuristic how many leading bytes to skip and drops them.
    /// Ends in Synchronized on success and in Error when there is no heuristic, no data, or the heuristic fails or
    /// reports a skip count beyond the held bytes (which would be a heuristic bug or a hostile result).
    /// </summary>
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

        // Skipped bytes are discarded for good (counted in TotalDiscarded), not delivered as a partial PDU.
        _Stream.Consume(result.SkipBytes);
        TotalDiscarded += result.SkipBytes;
        State = SegmentBufferState.Synchronized;
    }

    #endregion
}
