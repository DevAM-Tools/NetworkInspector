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

    /// <summary>
    /// Unrecoverable error — no more PDUs can be extracted. Reserved for a broken framing contract (a detector that reports
    /// an impossible length, or a configuration without a detector) and for a failed resync. Running into the buffer limit
    /// is not an error of this kind (see <see cref="SegmentBuffer.TakeLimitHit"/>).
    /// </summary>
    Error,

    /// <summary>
    /// The sender closed this direction (FIN or RST). Terminal: nothing is appended any more and no PDU can follow, so a
    /// finished stream stops holding buffers and detector state.
    /// </summary>
    Closed,
}

/// <summary>
/// Per-direction segment buffer for TCP stream reassembly.
/// Orders payload by TCP sequence number via <see cref="SequenceStreamBuffer"/> and extracts PDUs
/// with the detector from <see cref="StreamReassemblyConfig"/>.
/// <para>
/// <b>State machine:</b> Initial → Synchronized (on first append; Error instead when the configuration has no detector)
/// → Resyncing (on invalid data) → back to Synchronized when the resync heuristic succeeds, otherwise Error (failed resync
/// or an impossible PDU length). Synchronized → Closed on <see cref="Close"/>. Error and Closed are terminal until
/// <see cref="Clear"/>.
/// </para>
/// <para>
/// <b>Buffer limit:</b> exceeding <see cref="StreamReassemblyConfig.MaxBufferSize"/> is an expected failure and does not end
/// the stream: the message in progress is dropped, the detector forgets the stream and the segment that hit the limit starts
/// a new prefix. Why not Error: one oversized transfer on a keep-alive connection would otherwise switch reassembly off for
/// every later message of the connection, with nothing in the output that says why. The drop is reported through
/// <see cref="TakeLimitHit"/> so the caller can name the limit in the field tree.
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

    /// <summary>
    /// Bytes dropped because a limit was hit or a resync skipped them (diagnostics for the limit message). A long, because
    /// a connection that keeps hitting the limit can discard more than <see cref="int.MaxValue"/> bytes over its lifetime.
    /// </summary>
    internal long TotalDiscarded { get; private set; }

    /// <summary>Current buffer state.</summary>
    internal SegmentBufferState State { get; private set; } = SegmentBufferState.Initial;

    /// <summary>PDU boundary detector from the configuration; null means the stream cannot be reassembled.</summary>
    private readonly IPduBoundaryDetector? _Detector;

    /// <summary>Optional heuristic that finds the next plausible PDU start after an invalid detection.</summary>
    private readonly IResyncHeuristic? _ResyncHeuristic;

    /// <summary>Largest PDU a detector may report (copied from the configuration).</summary>
    private readonly int _MaxPduSize;

    /// <summary>
    /// Set when a limit dropped buffered bytes, until <see cref="TakeLimitHit"/> reads it. Deliberately a pending flag and
    /// not a counter: the caller reports the event once per packet, on the packet that caused it.
    /// </summary>
    private bool _LimitHit;

    /// <summary>Bytes dropped by the pending limit event (valid while <see cref="_LimitHit"/> is set).</summary>
    private int _LimitDiscardedBytes;

    /// <summary>Value of <see cref="SequenceStreamBuffer.RebaseCount"/> the detector state was last reset for.</summary>
    private int _SeenRebaseCount;

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
    /// <param name="packetId">Id of the packet that carries the payload; reported back with each extracted PDU.</param>
    /// <param name="context">Stream identity, used to reset the detector when the buffer limit drops the message in progress.</param>
    /// <returns>
    /// <see langword="true"/> if the segment was accepted; <see langword="false"/> if there is no detector, the direction is
    /// closed or failed, or the segment alone exceeds the buffer limit.
    /// </returns>
    internal bool TryAppend(uint sequence, ReadOnlyMemory<byte> payload, int packetId, in StreamDetectionContext context)
    {
        // Error and Closed are terminal: a stream that lost framing or whose sender is finished stops buffering, so it
        // cannot keep consuming memory.
        if (State is SegmentBufferState.Error or SegmentBufferState.Closed)
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

        if (_Stream.TryAppend(sequence, payload, packetId))
        {
            // A rebase put earlier bytes in FRONT of the prefix a stateful detector already inspected, so its position
            // into the prefix is stale. Dropping the detector state makes it start over on the rebuilt prefix.
            if (_Stream.RebaseCount != _SeenRebaseCount)
            {
                _SeenRebaseCount = _Stream.RebaseCount;
                ResetDetector(context.StreamId);
            }

            return true;
        }

        // The sequence buffer refused the segment because it would exceed the per-direction cap (MaxBufferSize). That is
        // an expected failure for a stalled peer, a hole that is never filled or a message larger than the cap, and it is
        // handled so the connection survives: the message in progress is lost, not the connection. The held bytes are
        // dropped and counted, the detector forgets the stream, and the current segment starts a new prefix that the
        // detector judges on its own. The drop is reported through TakeLimitHit instead of being truncated silently.
        int discarded = _Stream.BufferedLength;
        TotalDiscarded += discarded;
        _Stream.Clear();
        ResetDetector(context.StreamId);
        _LimitHit = true;
        _LimitDiscardedBytes = discarded;

        // A segment that is larger than the whole cap on its own is refused again here; the buffer stays usable.
        return _Stream.TryAppend(sequence, payload, packetId);
    }

    /// <summary>
    /// Reports and clears the pending limit event: <see langword="true"/> once after bytes were dropped because a limit was
    /// hit. The caller shows it on the packet that caused it, which is why it is read right after the append or the extract.
    /// </summary>
    /// <param name="discardedBytes">Number of buffered bytes the event dropped; 0 when there was no event.</param>
    /// <returns>Whether a limit event was pending.</returns>
    internal bool TakeLimitHit(out int discardedBytes)
    {
        discardedBytes = _LimitHit ? _LimitDiscardedBytes : 0;
        bool hit = _LimitHit;
        _LimitHit = false;
        _LimitDiscardedBytes = 0;
        return hit;
    }

    /// <summary>
    /// Tries to extract the next complete PDU from the contiguous (in-order) prefix.
    /// </summary>
    /// <param name="context">Stream detection context for context-aware detectors.</param>
    /// <param name="pdu">The extracted PDU data on success. The memory is owned by the caller.</param>
    /// <param name="packetIds">Ids of the packets that carried the bytes of the PDU, in stream order; empty on failure.</param>
    /// <returns>
    /// <see langword="true"/> if a complete PDU was extracted. A complete result of length 0, longer than
    /// the PDU cap, or longer than the contiguous prefix moves the buffer to <see cref="SegmentBufferState.Error"/>
    /// and returns <see langword="false"/>.
    /// </returns>
    internal bool TryExtractPdu(in StreamDetectionContext context, out ReadOnlyMemory<byte> pdu, out int[] packetIds)
    {
        pdu = default;
        packetIds = [];

        // Only a synchronized buffer with in-order bytes can yield a PDU; bytes behind a hole are not inspected.
        if (State != SegmentBufferState.Synchronized || _Detector == null || _Stream.ContiguousLength == 0)
        {
            return false;
        }

        // One contiguous array: the detector sees a span, not a concatenation of arrival order.
        // Detectors that implement IStreamPduBoundaryDetector additionally receive the stream context (stream id,
        // direction, handshake observed, closed) so they can keep per-stream state; plain detectors only see the bytes. The
        // span is valid only until the next mutating call on the sequence buffer, which is why it is not stored.
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
            // and the packet id array are the only per-PDU allocations.
            byte[] owned = new byte[pduLength];
            view[..pduLength].CopyTo(owned);
            packetIds = _Stream.GetContributingPacketIds(pduLength);
            _Stream.Consume(pduLength);
            pdu = owned;
            return true;
        }

        // Invalid framing: try to find the next plausible PDU start. A successful resync still returns false from
        // this call (no PDU was extracted); the next call examines the bytes after the skipped region. Incomplete
        // (need more bytes) falls through to false as well, and the buffer waits for more segments.
        if (result.IsInvalid)
        {
            State = SegmentBufferState.Resyncing;
            _TryResync(context.StreamId);
        }

        return false;
    }

    /// <summary>
    /// Closes this direction because its sender sent FIN or RST. On FIN the bytes that are still buffered in order are
    /// handed out as one last PDU (a message that ends with the connection, or one the capture cut short); on RST they are
    /// dropped, because the peer aborted and the bytes were never a complete message. Either way the direction refuses all
    /// further appends afterwards.
    /// </summary>
    /// <remarks>
    /// Run the normal extraction loop with <see cref="StreamDetectionContext.StreamClosed"/> set before calling this, so a
    /// message that is complete only because the stream ended is framed by its detector and not delivered here as a blob.
    /// What remains is, by definition, incomplete. Bytes held behind a hole are dropped: their predecessors never arrived.
    /// </remarks>
    /// <param name="context">Stream identity, used to reset the detector.</param>
    /// <param name="discard"><see langword="true"/> on RST: drop everything, deliver nothing.</param>
    /// <param name="tail">The remaining in-order bytes (caller-owned) when the method returns <see langword="true"/>.</param>
    /// <param name="packetIds">Ids of the packets that carried the tail, in stream order.</param>
    /// <returns><see langword="true"/> when <paramref name="tail"/> holds a PDU to dispatch.</returns>
    internal bool Close(in StreamDetectionContext context, bool discard, out ReadOnlyMemory<byte> tail, out int[] packetIds)
    {
        tail = default;
        packetIds = [];
        bool deliver = false;

        if (State == SegmentBufferState.Synchronized && _Stream.ContiguousLength > 0)
        {
            int length = _Stream.ContiguousLength;
            if (!discard && length <= _MaxPduSize)
            {
                byte[] owned = new byte[length];
                _Stream.ContiguousSpan.CopyTo(owned);
                packetIds = _Stream.GetContributingPacketIds(length);
                tail = owned;
                deliver = true;
            }
            else if (!discard)
            {
                // More bytes than one PDU may hold: the tail cannot be delivered. This is the PDU limit, reported like the
                // buffer limit, never a silent truncation.
                TotalDiscarded += length;
                _LimitHit = true;
                _LimitDiscardedBytes = length;
            }
            else
            {
                TotalDiscarded += length;
            }
        }

        // The direction is finished: the detector drops its per-stream state, the buffers are released and the state is
        // terminal. Held segments behind a hole are dropped with the clear.
        ResetDetector(context.StreamId);
        _Stream.Clear();
        State = SegmentBufferState.Closed;
        return deliver;
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
        TotalDiscarded = 0;
        _LimitHit = false;
        _LimitDiscardedBytes = 0;
        State = _Detector != null ? SegmentBufferState.Initial : SegmentBufferState.Error;
    }

    #endregion

    #region Resync

    /// <summary>
    /// Resolves the transient Resyncing state: asks the resync heuristic how many leading bytes to skip and drops them.
    /// Ends in Synchronized on success and in Error when there is no heuristic, no data, or the heuristic fails or
    /// reports a skip count beyond the held bytes (which would be a heuristic bug or a hostile result).
    /// </summary>
    /// <param name="streamId">Stream identity, used to reset the detector after bytes were skipped.</param>
    private void _TryResync(ulong streamId)
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

        // The skipped bytes were consumed without a completed PDU, so a detector that remembers a position into the prefix
        // would now point at the wrong byte: it starts over at the new front.
        ResetDetector(streamId);
        State = SegmentBufferState.Synchronized;
    }

    #endregion
}
