// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

// Per-segment analysis. The part of the tracker that answers "what is unusual about this segment": it runs the checks in
// the order below and updates the flow state exactly once, after all checks.
internal sealed partial class TcpConnectionTracker
{
    #region Analyze

    /// <summary>
    /// Analyzes a TCP segment in the context of its connection.
    /// Updates connection state and returns analysis flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order is critical.</b> The sequence, ACK, window and next-sequence state of a flow is updated only in
    /// <see cref="_UpdateFlowState"/>, so each check compares the segment against the state <i>before</i> it, and some checks
    /// depend on the result of an earlier one:
    /// </para>
    /// <list type="number">
    /// <item>Handshake facts (ISN, window scale, initiator, server port) and the initial round trip.</item>
    /// <item>Forward checks (<see cref="_ClassifyForward"/>): zero window probe first (a hit skips the rest), zero window,
    /// lost segment, keep-alive, window update, window full, keep-alive ACK, probe ACK, duplicate ACK.</item>
    /// <item>ACK of bytes that were never captured.</item>
    /// <item>Retransmission family: spurious, fast, out-of-order, plain retransmission (<see cref="_ClassifyRetransmission"/>).</item>
    /// <item>State update, then this ACK is applied to the peer's unacked list (round trip, acknowledged packet).</item>
    /// <item>Bytes in flight, completeness, connection phase, timing, window values.</item>
    /// </list>
    /// <para>
    /// Static because all state lives in <paramref name="conn"/>. The method must only run once per packet (first parse),
    /// never on a replay, because it mutates the connection.
    /// </para>
    /// </remarks>
    /// <param name="conn">The connection state for this stream.</param>
    /// <param name="isForward">Direction of this segment relative to the normalized key.</param>
    /// <param name="s">The facts of the segment.</param>
    /// <param name="reusedPorts">The segment started a new connection on a known address/port pair (flag it).</param>
    /// <param name="relativeSequenceNumbers">When true, the relative numbers in the result subtract the ISNs.</param>
    /// <returns>The analysis flags and metrics for this segment.</returns>
    internal static TcpAnalysisResult Analyze(
        TcpConnectionState conn, bool isForward, in TcpSegmentView s, bool reusedPorts, bool relativeSequenceNumbers)
    {
        // "flow" is the direction of this segment, "reverse" the other one (the peer whose data this segment may
        // acknowledge).
        TcpFlowState flow = isForward
            ? conn.Forward
            : conn.Reverse;
        TcpFlowState reverse = isForward
            ? conn.Reverse
            : conn.Forward;
        TcpAnalysisFlags flags = reusedPorts
            ? TcpAnalysisFlags.ReusedPorts
            : TcpAnalysisFlags.None;

        // An ACK from this side confirms that the peer's byte counts are meaningful again (a gap on the peer's side marked
        // them as unreliable until the next acknowledgement).
        if (s.HasAck)
        {
            reverse.ValidBytesInFlight = true;
        }

        // Step 1: handshake facts. Everything later (window scale, relative numbers, completeness) reads them.
        _TrackHandshake(conn, flow, reverse, in s);
        double initialRtt = _MeasureInitialRtt(conn, in s);

        // Step 2: checks against the flow state as it was before this segment.
        uint dupAckNum = _ClassifyForward(flow, reverse, in s, ref flags, out int dupAckPacketId);
        _CheckAckedLostPacket(flow, reverse, in s, ref flags);
        _ClassifyRetransmission(conn, flow, reverse, in s, ref flags, out double rto, out int rtoPacketId);

        // Step 3: from here on the flow state is overwritten with this segment's values; every check above has already
        // used the previous values. Do not move checks below this point.
        _UpdateFlowState(flow, in s, flags);
        _AckUnacked(reverse, in s, ref flags, out double ackRtt, out int acksPacketId);

        // Step 4: values derived from the updated state.
        ulong bytesInFlight = _ComputeBytesInFlight(flow, reverse, in s);

        // The completeness shown on a packet is the state of the conversation BEFORE that packet contributes to it: the
        // first packet shows "nothing seen", the third handshake packet shows SYN and SYN-ACK only. This is the convention
        // of the analyzers whose output users compare against (the value describes what the capture held up to the
        // previous packet), so a filter such as "tcp.completeness == 7" selects the packets after the handshake ACK.
        byte completenessBefore = conn.Completeness;
        _UpdateCompleteness(conn, flow, reverse, in s);
        _UpdateConnectionPhase(conn, flow, reverse, in s);
        (double timeRelative, double timeDelta) = _ComputeTiming(conn, in s);

        return _BuildResult(
            conn, flow, reverse, in s, relativeSequenceNumbers, flags, dupAckNum, dupAckPacketId, initialRtt, ackRtt,
            acksPacketId, rto, rtoPacketId, bytesInFlight, timeRelative, timeDelta)
            with
        {
            Completeness = completenessBefore,
        };
    }

    #endregion

    #region Handshake facts

    /// <summary>
    /// Records what the handshake segments tell: the initial sequence number and window scale of each direction, which
    /// direction opened the connection and which port is the server's.
    /// </summary>
    private static void _TrackHandshake(TcpConnectionState conn, TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s)
    {
        // The first SYN of each direction fixes that direction's initial sequence number (used for relative numbers) and its
        // window scale. Later SYNs (a retransmitted handshake) do not overwrite them.
        if (s.Syn && !flow.IsnSet)
        {
            flow.Isn = s.Seq;
            flow.IsnSet = true;
            _NegotiateWindowScale(flow, reverse, in s);
        }

        // Relative numbers count from a base per direction. With the SYN captured the base is its sequence number (the SYN
        // itself reads 0). Without it (a connection joined mid-stream) the base is one before the first number seen, so the
        // first segment reads 1; the base of the peer is derived the same way from the acknowledgement number, which names
        // the next byte the peer is expected to send. Both are fixed at first sight and never move again.
        if (!flow.RelativeBaseSet)
        {
            flow.RelativeBase = s.Syn
                ? s.Seq
                : s.Seq - 1;
            flow.RelativeBaseSet = true;
        }

        if (s.HasAck && !reverse.RelativeBaseSet)
        {
            reverse.RelativeBase = s.AckNumber - 1;
            reverse.RelativeBaseSet = true;
        }

        // The first pure SYN names the initiator and the server port (its destination). A SYN-ACK names the server port
        // from its source when no pure SYN was seen.
        if (s.Syn && !s.HasAck)
        {
            if (!conn.Forward.IsInitiator && !conn.Reverse.IsInitiator)
            {
                flow.IsInitiator = true;
            }

            if (!conn.ServerPortSet)
            {
                conn.ServerPort = s.DstPort;
                conn.ServerPortSet = true;
            }

            conn.PureSynTimestamp = Timestamp.FromNanos(s.Nanos);
        }
        else if (s.Syn && s.HasAck && !conn.ServerPortSet)
        {
            conn.ServerPort = s.SrcPort;
            conn.ServerPortSet = true;
        }
    }

    /// <summary>
    /// Decides the window scale of both directions from the SYN options. Scaling applies only when BOTH SYNs carry the Window
    /// Scale option (RFC 7323 section 2.2), so the SYN-ACK can switch a direction that looked scaled after the SYN to
    /// "no scaling".
    /// </summary>
    /// <remarks>
    /// Values: <c>-2</c> no scaling, <c>0..14</c> the shift count; <c>-1</c> (unknown) is the initial state of a flow whose
    /// SYN was not captured. A pure SYN without the option already means "no scaling" for its sender.
    /// </remarks>
    private static void _NegotiateWindowScale(TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s)
    {
        if (!s.HasAck)
        {
            flow.WindowScale = s.WindowScaleOption >= 0
                ? s.WindowScaleOption
                : -2;
            return;
        }

        // SYN-ACK: scaling is on only if the SYN of the peer offered it and this SYN-ACK accepts it.
        bool scalingAgreed = reverse.WindowScale >= 0 && s.WindowScaleOption >= 0;
        if (scalingAgreed)
        {
            flow.WindowScale = s.WindowScaleOption;
            return;
        }

        flow.WindowScale = -2;
        if (reverse.WindowScale >= 0)
        {
            reverse.WindowScale = -2;
        }
    }

    /// <summary>
    /// Measures the initial round trip once per connection: from the most recent pure SYN to the first data-less ACK (the
    /// third handshake packet). A negative delta (out-of-order timestamps) is not recorded. Returns the connection's value on
    /// every packet after the measurement, so each packet of the conversation can show it.
    /// </summary>
    private static double _MeasureInitialRtt(TcpConnectionState conn, in TcpSegmentView s)
    {
        if (!conn.InitialRttSet && !s.Syn && s.HasAck && s.PayloadLength == 0 && !s.Fin && !s.Rst
            && conn.PureSynTimestamp.HasValue)
        {
            double delta = (s.Nanos - conn.PureSynTimestamp.Value.AsNanos) / 1_000_000_000.0;
            if (delta >= 0)
            {
                conn.InitialRttValue = delta;
                conn.InitialRttSet = true;
            }
        }

        return conn.InitialRttSet
            ? conn.InitialRttValue
            : double.NaN;
    }

    #endregion

    #region Forward checks

    /// <summary>
    /// Runs the checks that look only at this segment and the flow state before it: zero window probe, zero window, lost
    /// segment, keep-alive, window update, window full, keep-alive ACK, zero window probe ACK and duplicate ACK.
    /// </summary>
    /// <remarks>
    /// A zero window probe, a keep-alive ACK and a zero window probe ACK end the checks early: what they are is already
    /// decided, and treating them as a duplicate ACK or a window update as well would double-report one event. The duplicate
    /// ACK counter is maintained either way (see the end of the method).
    /// </remarks>
    private static uint _ClassifyForward(
        TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s, ref TcpAnalysisFlags flags, out int dupAckPacketId)
    {
        uint dupAckNum = 0;
        dupAckPacketId = -1;
        bool skipForward = false;

        // Zero window probe: exactly one byte at the next sequence number into a window the peer advertised as zero. It is
        // checked first because the probe is neither new data nor a retransmission.
        if (s.PayloadLength == 1 && s.IsPlain && flow.Seen && s.Seq == flow.NextSeq && reverse.Seen && reverse.LastWindow == 0)
        {
            flags |= TcpAnalysisFlags.ZeroWindowProbe;
            skipForward = true;
        }

        if (!skipForward)
        {
            // Zero window: the advertised window of this segment is zero (not on SYN/FIN/RST, where the window carries no
            // flow control meaning).
            if (s.Window == 0 && s.IsPlain)
            {
                flags |= TcpAnalysisFlags.ZeroWindow;
            }

            // Lost segment: any non-RST segment that starts beyond the end of the data seen so far; the bytes in between
            // were not captured. Bytes in flight cannot be trusted until the peer acknowledges again.
            if (flow.Seen && _IsSequenceAfter(s.Seq, flow.NextSeq) && !s.Rst)
            {
                flags |= TcpAnalysisFlags.LostSegment;
                flow.ValidBytesInFlight = false;
            }

            // Keep-alive: zero or one byte, one sequence number before the next one. It is not a retransmission; the
            // retransmission check skips it.
            if (flow.Seen && s.PayloadLength <= 1 && s.Seq == flow.NextSeq - 1 && s.IsPlain)
            {
                flags |= TcpAnalysisFlags.KeepAlive;
            }

            // Window update: a pure ACK that repeats the previous ACK number and only changes a non-zero window.
            if (s.PayloadLength == 0 && s.Window != 0 && flow.Seen && s.Window != flow.LastWindow
                && s.Seq == flow.NextSeq && s.AckNumber == flow.LastAck && s.IsPlain)
            {
                flags |= TcpAnalysisFlags.WindowUpdate;
            }

            // Window full: the data reaches the right edge of the window the peer advertised. Needs a known scale, because
            // the edge is computed with the peer's shift; before the peer's first ACK or without scaling the window is
            // unscaled. The shift is at most 14, so a 16-bit window shifted in a uint cannot overflow.
            if (s.PayloadLength > 0 && reverse.Seen && reverse.WindowScale != -1 && s.IsPlain)
            {
                int shift = reverse.IsFirstAck || reverse.WindowScale == -2
                    ? 0
                    : reverse.WindowScale;
                if (s.Seq + s.PayloadLength == reverse.LastAck + ((uint)reverse.LastWindow << shift))
                {
                    flags |= TcpAnalysisFlags.WindowFull;
                }
            }

            // Keep-alive ACK: repeats the previous ACK after a keep-alive from the peer.
            if (s.PayloadLength == 0 && s.Window != 0 && s.Window == flow.LastWindow && flow.Seen
                && s.Seq == flow.NextSeq && s.AckNumber == flow.LastAck
                && (reverse.LastSegmentFlags & TcpAnalysisFlags.KeepAlive) != 0 && s.IsPlain)
            {
                flags |= TcpAnalysisFlags.KeepAliveAck;
                skipForward = true;
            }

            // Zero window probe ACK: the peer answers a probe and keeps the window at zero. It repeats the ACK number or
            // advances it by one when the peer consumed the probe byte; the peer's next sequence number then moves with it.
            if (!skipForward && s.PayloadLength == 0 && s.Window == 0 && s.Window == flow.LastWindow && flow.Seen
                && s.Seq == flow.NextSeq && (s.AckNumber == flow.LastAck || s.AckNumber == flow.LastAck + 1)
                && (reverse.LastSegmentFlags & TcpAnalysisFlags.ZeroWindowProbe) != 0 && s.IsPlain)
            {
                flags |= TcpAnalysisFlags.ZeroWindowProbeAck;
                if (s.AckNumber == flow.LastAck + 1)
                {
                    reverse.NextSeq = s.AckNumber;
                    reverse.MaxSeqToBeAcked = s.AckNumber;
                }

                skipForward = true;
            }

            // Duplicate ACK: same sequence, ACK number and window as the previous segment of this direction, no data. The
            // sequence test keeps a data segment with the same ACK number from counting as a duplicate (data resets nothing).
            if (!skipForward && s.PayloadLength == 0 && s.Window != 0 && s.Window == flow.LastWindow && flow.Seen
                && s.Seq == flow.NextSeq && s.AckNumber == flow.LastAck && s.IsPlain)
            {
                flow.DupAckCount++;
                flags |= TcpAnalysisFlags.DuplicateAck;
                dupAckNum = flow.DupAckCount;
                dupAckPacketId = flow.LastNonDupAckPacketId;
            }
        }

        // The duplicate counter resets only when the acknowledgement number moves. A data segment with the old ACK number in
        // between two duplicates must not restart the count, because the receiver keeps repeating the same hole.
        if (s.HasAck && (!flow.Seen || s.AckNumber != flow.LastAck))
        {
            flow.DupAckCount = 0;
            flow.LastNonDupAckPacketId = s.PacketId;
        }

        return dupAckNum;
    }

    /// <summary>
    /// An ACK beyond the highest end the peer is known to have sent acknowledges bytes that were never captured (common at
    /// capture start or after a gap). When it instead repeats the probe byte consumed after a zero window probe it is only a
    /// window update.
    /// </summary>
    private static void _CheckAckedLostPacket(
        TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s, ref TcpAnalysisFlags flags)
    {
        if (!reverse.MaxSeqToBeAckedSet || !s.HasAck || !_IsSequenceAfter(s.AckNumber, reverse.MaxSeqToBeAcked))
        {
            return;
        }

        if (s.AckNumber == flow.LastAck + 1 && s.Seq == flow.NextSeq
            && (reverse.LastSegmentFlags & TcpAnalysisFlags.ZeroWindowProbe) != 0)
        {
            // The window re-opens by acknowledging the probe byte: the peer's probe consumed one sequence number that its
            // next-sequence bookkeeping deliberately did not advance.
            reverse.NextSeq = s.AckNumber;
            reverse.MaxSeqToBeAcked = s.AckNumber;
            flags |= TcpAnalysisFlags.WindowUpdate;
            return;
        }

        reverse.MaxSeqToBeAcked = s.AckNumber;
        flags |= TcpAnalysisFlags.AckLostSegment;
    }

    #endregion

    #region Retransmission family

    /// <summary>Segments that arrive within this time after the peer's last ACK count as a duplicate-ACK reaction (fast retransmission).</summary>
    private const long _FastRetransmissionWindowNanos = 20_000_000;

    /// <summary>
    /// Reordering window used when no initial round trip is known: a late segment that arrives within 3 ms of the peer's last
    /// ACK is reordered rather than retransmitted. The value comes from the established analysis rules; with a known initial
    /// round trip that value is used instead, because reordering cannot be later than one round trip.
    /// </summary>
    private const long _DefaultReorderingWindowNanos = 3_000_000;

    /// <summary>
    /// Decides whether a segment that starts before the end of the data already sent is a spurious retransmission, a fast
    /// retransmission, reordering or a plain retransmission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The families are not exclusive, which is what the independent dissector reports on real conversations and what the
    /// cross-validation tests pin: a <i>spurious</i> retransmission (everything it carries was already acknowledged) is also
    /// a retransmission, and a <i>fast</i> retransmission (the answer to two duplicate ACKs) is also a retransmission.
    /// <i>Out-of-order</i> is the one exclusive outcome: a segment that is reordering is by definition not a retransmission.
    /// </para>
    /// <para>
    /// Precedence: the fast-retransmission test runs before the out-of-order test, so a segment that answers two duplicate
    /// ACKs within 20 ms is never reported as reordering even though it was not sent before (the lost segment it repeats was
    /// not captured).
    /// </para>
    /// </remarks>
    /// <param name="conn">Connection (its initial round trip sets the reordering window).</param>
    /// <param name="flow">Direction of the segment.</param>
    /// <param name="reverse">The peer.</param>
    /// <param name="s">The segment.</param>
    /// <param name="flags">Analysis flags; the family flags are added.</param>
    /// <param name="rto">Time since the retransmitted data was first sent, in seconds; NaN unless a retransmission was flagged.</param>
    /// <param name="rtoPacketId">The packet the timeout is measured from, or -1.</param>
    private static void _ClassifyRetransmission(
        TcpConnectionState conn,
        TcpFlowState flow,
        TcpFlowState reverse,
        in TcpSegmentView s,
        ref TcpAnalysisFlags flags,
        out double rto,
        out int rtoPacketId)
    {
        rto = double.NaN;
        rtoPacketId = -1;

        // Segments without sequence space (pure ACKs) and keep-alives are never retransmissions.
        bool occupiesSequenceSpace = s.PayloadLength > 0 || s.SynOrFin;
        if (!occupiesSequenceSpace || (flags & TcpAnalysisFlags.KeepAlive) != 0)
        {
            return;
        }

        // Does the segment start before the end of the data already sent? A segment of more than one byte that starts one
        // before the next number is a keep-alive that carries new data, so it advances.
        bool seqNotAdvanced = flow.Seen && _IsSequenceBefore(s.Seq, flow.NextSeq);
        if (s.PayloadLength > 1 && flow.NextSeq - 1 == s.Seq)
        {
            seqNotAdvanced = false;
        }

        if (!seqNotAdvanced)
        {
            return;
        }

        // Spurious: the peer already acknowledged everything this segment carries, so sending it again was unnecessary.
        // Additive: the segment is still a retransmission of acknowledged data.
        if (s.PayloadLength > 0 && reverse.Seen && !_IsSequenceAfter(s.NextSeq, reverse.LastAck))
        {
            flags |= TcpAnalysisFlags.SpuriousRetransmission;
        }

        // Time since the peer last acknowledged. Without a peer segment there is no reference, so the time is unbounded and
        // neither reordering nor fast retransmission can apply.
        long sincePeerAck = reverse.Seen && s.Nanos >= reverse.LastAckNanos
            ? s.Nanos - reverse.LastAckNanos
            : long.MaxValue;

        // Fast retransmission: the peer repeated the ACK for exactly this sequence number twice and the segment follows
        // within 20 ms, so the sender reacted to the duplicate ACKs instead of waiting for its timer. It is also reported as
        // a retransmission (below) and never as reordering.
        if (sincePeerAck < _FastRetransmissionWindowNanos && reverse.DupAckCount >= 2 && reverse.LastAck == s.Seq)
        {
            flags |= TcpAnalysisFlags.FastRetransmission | TcpAnalysisFlags.Retransmission;
            _MeasureRto(flow, in s, ref rto, ref rtoPacketId);
            _MarkRetransmitted(flow, in s);
            return;
        }

        // Out-of-order: the bytes were not sent before, and the segment arrives within the reordering window. A segment
        // that ends exactly at the next number repeats the last segment unless the last advancing segment was a pure ACK.
        long reorderingWindow = conn.InitialRttSet
            ? (long)(conn.InitialRttValue * 1_000_000_000.0)
            : _DefaultReorderingWindowNanos;
        if (sincePeerAck < reorderingWindow && !_UnackedContains(flow, s.Seq, s.NextSeq)
            && (flow.NextSeq != s.NextSeq || flow.LastAckLen == 0))
        {
            flags |= TcpAnalysisFlags.OutOfOrder;
            return;
        }

        flags |= TcpAnalysisFlags.Retransmission;
        _MeasureRto(flow, in s, ref rto, ref rtoPacketId);
        _MarkRetransmitted(flow, in s);
    }

    /// <summary>
    /// Computes the retransmission timeout of a retransmitted segment: the time since the oldest still unacknowledged
    /// segment that covers its first byte was sent, and that segment's packet. Nothing is reported when no remembered
    /// segment covers the byte (the first transmission was never captured, or it was acknowledged already).
    /// </summary>
    private static void _MeasureRto(TcpFlowState flow, in TcpSegmentView s, ref double rto, ref int rtoPacketId)
    {
        TcpUnackedSegment[]? list = flow.Unacked;
        for (int i = 0; i < flow.UnackedCount; i++)
        {
            ref TcpUnackedSegment entry = ref list![i];
            if (!_IsSequenceBefore(s.Seq, entry.Seq) && _IsSequenceAfter(entry.NextSeq, s.Seq))
            {
                rto = (s.Nanos - entry.Nanos) / 1_000_000_000.0;
                rtoPacketId = entry.PacketId;
                return;
            }
        }
    }

    /// <summary>
    /// Remembers that the bytes of this segment were sent again: the entries that cover them are marked, so the ACK that
    /// finally acknowledges them is reported as ambiguous.
    /// </summary>
    private static void _MarkRetransmitted(TcpFlowState flow, in TcpSegmentView s)
    {
        TcpUnackedSegment[]? list = flow.Unacked;
        for (int i = 0; i < flow.UnackedCount; i++)
        {
            ref TcpUnackedSegment entry = ref list![i];
            if (_IsSequenceBefore(s.Seq, entry.NextSeq) && _IsSequenceAfter(s.NextSeq, entry.Seq))
            {
                entry.Retransmitted = true;
            }
        }
    }

    /// <summary>
    /// True when one unacked entry covers the whole range <c>[seq, end)</c>: the same bytes were sent before, so the segment
    /// is not new data.
    /// </summary>
    private static bool _UnackedContains(TcpFlowState flow, uint seq, uint end)
    {
        TcpUnackedSegment[]? list = flow.Unacked;
        for (int i = 0; i < flow.UnackedCount; i++)
        {
            ref TcpUnackedSegment entry = ref list![i];
            if (!_IsSequenceBefore(seq, entry.Seq) && !_IsSequenceAfter(end, entry.NextSeq))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region State update

    /// <summary>
    /// Overwrites the flow state with this segment's values and remembers the segment in the unacked list. After this call
    /// the checks of the next segment see this segment as "before".
    /// </summary>
    private static void _UpdateFlowState(TcpFlowState flow, in TcpSegmentView s, TcpAnalysisFlags flags)
    {
        bool isProbe = (flags & TcpAnalysisFlags.ZeroWindowProbe) != 0;

        // Remember the segment until it is acknowledged, unless the same bytes are already remembered: a repeat of a
        // remembered segment (a retransmission, a repeated probe) keeps the FIRST transmission as the entry, so the round trip
        // and the "acknowledges frame" reference name the original, and the bytes in flight do not count the repeat twice.
        // The list is bounded (TcpFlowState.MaxUnacked): beyond the bound the segment is simply not remembered, which costs a
        // round trip sample and an "already seen" decision, never an error.
        if ((s.PayloadLength > 0 || s.SynOrFin) && flow.UnackedCount < TcpFlowState.MaxUnacked
            && !_UnackedContains(flow, s.Seq, s.NextSeq))
        {
            _AddUnacked(flow, in s);
        }

        // The next sequence number advances only for data that moves past the end seen so far. A zero window probe does not
        // advance it: the probe byte is not delivered, so the next real segment starts at the same number.
        bool advances = !flow.Seen || _IsSequenceAfter(s.NextSeq, flow.NextSeq);
        if (advances && !isProbe)
        {
            flow.NextSeq = s.NextSeq;
            flow.NextSeqNanos = s.Nanos;
            flow.LastAckLen = s.PayloadLength;
        }

        // The highest end the peer must still acknowledge follows the continuous stream: it moves with the segment that
        // starts at it (or is the first one seen), never with a probe.
        if ((!flow.MaxSeqToBeAckedSet || s.Seq == flow.MaxSeqToBeAcked) && !isProbe)
        {
            flow.MaxSeqToBeAcked = flow.NextSeq;
            flow.MaxSeqToBeAckedSet = true;
        }

        flow.LastWindow = s.Window;
        if (s.HasAck)
        {
            // The ACK number is only meaningful with the ACK flag; a segment without it carries a zero or arbitrary field.
            flow.LastAck = s.AckNumber;
            flow.LastAckNanos = s.Nanos;
        }

        flow.LastSegmentFlags = flags;
        flow.Seen = true;
        if (!s.Syn && s.HasAck)
        {
            flow.IsFirstAck = false;
        }
    }

    /// <summary>
    /// Appends one entry to the unacked list. The array is created on the first data segment with 8 entries and doubles up to
    /// <see cref="TcpFlowState.MaxUnacked"/>, so an idle connection owns no list.
    /// </summary>
    private static void _AddUnacked(TcpFlowState flow, in TcpSegmentView s)
    {
        TcpUnackedSegment[] list = flow.Unacked ??= new TcpUnackedSegment[8];
        if (flow.UnackedCount == list.Length)
        {
            // Doubling is amortized O(1) per entry; the cap keeps the final size within the documented bound. The caller
            // checked UnackedCount < MaxUnacked, so there is always room for one more entry.
            int newLength = Math.Min(list.Length * 2, TcpFlowState.MaxUnacked);
            Array.Resize(ref list, newLength);
            flow.Unacked = list;
        }

        list[flow.UnackedCount++] = new TcpUnackedSegment
        {
            Seq = s.Seq,
            NextSeq = s.NextSeq,
            Nanos = s.Nanos,
            PacketId = s.PacketId,
        };
        flow.UnackedBytes += s.NextSeq - s.Seq;
    }

    /// <summary>
    /// Applies the ACK of this segment to the PEER's unacked list: every entry the acknowledgement number covers is removed.
    /// An acknowledgement that equals the end of an entry yields the round trip sample and the packet it acknowledges; one
    /// that ends inside an entry trims the front of that entry and counts as a partial acknowledgement.
    /// </summary>
    /// <remarks>
    /// The sample and the acknowledged packet come from the OLDEST entry the acknowledgement completes, because that is the
    /// first transmission. An acknowledgement of an entry that was sent again is reported as ambiguous: it cannot say which
    /// transmission it answers. The walk is linear in the live entries of the peer, which are at most the unacknowledged
    /// window divided by the segment size.
    /// </remarks>
    private static void _AckUnacked(
        TcpFlowState reverse,
        in TcpSegmentView s,
        ref TcpAnalysisFlags flags,
        out double ackRtt,
        out int acksPacketId)
    {
        ackRtt = double.NaN;
        acksPacketId = -1;

        TcpUnackedSegment[]? list = reverse.Unacked;
        if (!s.HasAck || list is null || reverse.UnackedCount == 0)
        {
            return;
        }

        // Compact in place: entries that survive are written back to the front, so the pass allocates nothing.
        int write = 0;
        bool sampled = false;
        for (int i = 0; i < reverse.UnackedCount; i++)
        {
            TcpUnackedSegment entry = list[i];
            if (s.AckNumber == entry.NextSeq)
            {
                // Exact acknowledgement: the entry is done. The first (oldest) one provides the sample and the packet.
                reverse.UnackedBytes -= entry.NextSeq - entry.Seq;
                if (!sampled)
                {
                    sampled = true;
                    acksPacketId = entry.PacketId;
                    ackRtt = (s.Nanos - entry.Nanos) / 1_000_000_000.0;
                    if (entry.Retransmitted)
                    {
                        flags |= TcpAnalysisFlags.AmbiguousAck;
                    }
                }

                continue;
            }

            if (_IsSequenceAfter(s.AckNumber, entry.Seq) && _IsSequenceBefore(s.AckNumber, entry.NextSeq))
            {
                // The acknowledgement ends inside the entry: the front is acknowledged, the rest stays outstanding.
                reverse.UnackedBytes -= s.AckNumber - entry.Seq;
                entry.Seq = s.AckNumber;
                if (!sampled)
                {
                    acksPacketId = entry.PacketId;
                }

                flags |= TcpAnalysisFlags.PartialAck;
                list[write++] = entry;
                continue;
            }

            if (_IsSequenceAfter(entry.NextSeq, s.AckNumber))
            {
                // Not covered yet.
                list[write++] = entry;
                continue;
            }

            // Lies completely before the acknowledgement: dropped without a sample (a cumulative acknowledgement of several
            // segments reports the oldest exact one).
            reverse.UnackedBytes -= entry.NextSeq - entry.Seq;
        }

        reverse.UnackedCount = write;
    }

    #endregion

    #region Derived values

    /// <summary>
    /// Bytes the sender has sent that the peer has not acknowledged: the sum of the unacked entries of this direction,
    /// including this segment when it is new. Only reported for data segments, and only while the figure can be trusted (no
    /// unacknowledged gap): a gap makes the peer's acknowledgements refer to bytes the capture does not contain.
    /// </summary>
    private static ulong _ComputeBytesInFlight(TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s)
    {
        if (s.PayloadLength == 0 || !reverse.Seen || !flow.ValidBytesInFlight)
        {
            return 0;
        }

        return flow.UnackedBytes;
    }

    /// <summary>
    /// Time since the first packet of the stream and since the previous one (either direction), in seconds. NaN means "no
    /// previous packet".
    /// </summary>
    private static (double TimeRelative, double TimeDelta) _ComputeTiming(TcpConnectionState conn, in TcpSegmentView s)
    {
        Timestamp now = Timestamp.FromNanos(s.Nanos);
        double timeRelative;
        double timeDelta = double.NaN;

        if (!conn.FirstTimestamp.HasValue)
        {
            // First packet in this stream
            conn.FirstTimestamp = now;
            timeRelative = 0.0;
        }
        else
        {
            timeRelative = _ComputeTimeDelta(conn.FirstTimestamp.Value, now);
        }

        if (conn.LastTimestamp.HasValue)
        {
            timeDelta = _ComputeTimeDelta(conn.LastTimestamp.Value, now);
        }

        conn.LastTimestamp = now;
        return (timeRelative, timeDelta);
    }

    /// <summary>
    /// Assembles the result: the window values of the sender and the relative sequence numbers, plus everything computed by
    /// the steps above.
    /// </summary>
    private static TcpAnalysisResult _BuildResult(
        TcpConnectionState conn,
        TcpFlowState flow,
        TcpFlowState reverse,
        in TcpSegmentView s,
        bool relativeSequenceNumbers,
        TcpAnalysisFlags flags,
        uint dupAckNum,
        int dupAckPacketId,
        double initialRtt,
        double ackRtt,
        int acksPacketId,
        double rto,
        int rtoPacketId,
        ulong bytesInFlight,
        double timeRelative,
        double timeDelta)
    {
        // Window values of the sender. The SYN and SYN-ACK windows are never scaled (RFC 7323 section 2.2). The factor is
        // the multiplier when the shift is known, -1 when no SYN was captured and -2 when the handshake negotiated no scaling,
        // so a filter on the factor distinguishes "unknown" from "unscaled".
        int windowScaleFactor = flow.WindowScale < 0
            ? flow.WindowScale
            : 1 << flow.WindowScale;
        ulong scaledWindowSize = !s.Syn && flow.WindowScale >= 0
            ? (ulong)s.Window << flow.WindowScale
            : s.Window;

        // Relative numbers: wire value minus the base of the sending direction (sequence) or of the peer (ack); the bases are
        // fixed in _TrackHandshake. The uint subtraction wraps correctly modulo 2^32. The peer's base is unknown only when
        // neither this nor an earlier segment carried an ACK (a lone SYN), and the acknowledgement number is shown raw then.
        uint relativeSeq = s.Seq;
        bool sequenceIsRelative = false;
        if (relativeSequenceNumbers && flow.RelativeBaseSet)
        {
            relativeSeq = s.Seq - flow.RelativeBase;
            sequenceIsRelative = true;
        }

        uint relativeAck = s.AckNumber;
        bool ackIsRelative = false;
        if (relativeSequenceNumbers && reverse.RelativeBaseSet)
        {
            relativeAck = s.AckNumber - reverse.RelativeBase;
            ackIsRelative = true;
        }

        return new TcpAnalysisResult
        {
            Flags = flags,
            StreamIndex = conn.StreamIndex,
            DupAckNum = dupAckNum,
            DupAckPacketId = dupAckPacketId,
            BytesInFlight = bytesInFlight,
            InitialRtt = initialRtt,
            AckRtt = ackRtt,
            AcksPacketId = acksPacketId,
            Rto = rto,
            RtoPacketId = rtoPacketId,
            TimeRelative = timeRelative,
            TimeDelta = timeDelta,
            ScaledWindowSize = scaledWindowSize,
            WindowScaleFactor = windowScaleFactor,
            ConnectionState = conn,
            Phase = conn.Phase,
            RelativeSeq = relativeSeq,
            RelativeAck = relativeAck,
            SequenceIsRelative = sequenceIsRelative,
            AckIsRelative = ackIsRelative,
        };
    }

    #endregion

    #region Arithmetic helpers

    /// <summary>
    /// Computes time delta in seconds between two timestamps.
    /// Uses nanosecond-precision subtraction for accuracy. The result is negative when the timestamps are out of order
    /// (callers that need a duration discard negative values).
    /// </summary>
    /// <param name="t1">Earlier timestamp.</param>
    /// <param name="t2">Later timestamp.</param>
    /// <returns>Seconds from <paramref name="t1"/> to <paramref name="t2"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double _ComputeTimeDelta(Timestamp t1, Timestamp t2) =>
        // Subtract raw nanosecond values and convert to seconds
        (t2.AsNanos - t1.AsNanos) / 1_000_000_000.0;

    /// <summary>
    /// Checks if sequence number <paramref name="a"/> is strictly after <paramref name="b"/>
    /// using 32-bit wrapping arithmetic (handles sequence number wraparound).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsSequenceAfter(uint a, uint b) =>
        // Signed comparison of (a - b) handles 32-bit wraparound
        (int)(a - b) > 0;

    /// <summary>
    /// Checks if sequence number <paramref name="a"/> is strictly before <paramref name="b"/>
    /// using 32-bit wrapping arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsSequenceBefore(uint a, uint b) =>
        (int)(a - b) < 0;

    #endregion
}
