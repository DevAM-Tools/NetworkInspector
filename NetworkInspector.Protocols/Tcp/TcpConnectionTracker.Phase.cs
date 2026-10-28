// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

// Conversation completeness and the connection state machine: both are derived from facts recorded once per segment
// (who sent a SYN, a FIN, which FIN was acknowledged), never stepped by flags alone, so a retransmitted segment cannot
// move them.
internal sealed partial class TcpConnectionTracker
{
    #region Completeness

    /// <summary>
    /// Records which parts of a conversation the capture contains: SYN, SYN-ACK, the ACK that completes the handshake, data,
    /// FIN and RST. The bits only ever get set, so the value at a packet describes the conversation up to that packet.
    /// </summary>
    private static void _UpdateCompleteness(
        TcpConnectionState conn, TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s)
    {
        byte bits = conn.Completeness;

        if (s.Syn && !s.HasAck)
        {
            bits |= TcpConnectionState.SynSeen;
        }
        else if (s.Syn && s.HasAck)
        {
            bits |= TcpConnectionState.SynAckSeen;
        }

        // The ACK bit marks the data-less ACK that completes the handshake. Which numbers identify it depends on which
        // SYNs the capture contains: with both, the segment must continue both initial sequence numbers; with only one SYN,
        // the number of that side must match; a direction whose ISN is unknown cannot be checked and does not disqualify, so
        // without any SYN (a capture joined mid-stream) every data-less ACK counts.
        if (!s.Syn && s.HasAck && !s.Fin && !s.Rst && s.PayloadLength == 0
            && (!flow.IsnSet || s.Seq == flow.Isn + 1) && (!reverse.IsnSet || s.AckNumber == reverse.Isn + 1))
        {
            bits |= TcpConnectionState.AckSeen;
        }

        // Data: payload on a segment that is neither a SYN nor an RST (a FIN with payload still carries data).
        if ((s.Flags & 0x16) == 0x10 && s.PayloadLength > 0)
        {
            bits |= TcpConnectionState.DataSeen;
        }
        else if (s.Syn && !s.HasAck && s.PayloadLength > 0)
        {
            // Data in the SYN (fast open) is data of the conversation.
            bits |= TcpConnectionState.DataSeen;
        }

        if (s.Fin)
        {
            bits |= TcpConnectionState.FinSeen;
        }

        if (s.Rst)
        {
            bits |= TcpConnectionState.RstSeen;
        }

        conn.Completeness = bits;
    }

    #endregion

    #region Connection state machine

    /// <summary>
    /// Records the close facts of this segment and derives the connection phase from them. The phase is the state of the side
    /// that closed first (the active closer, RFC 9293 figure 5); a connection whose handshake was not captured is reported
    /// as established as soon as it carries data or acknowledgements, never as closed.
    /// </summary>
    /// <remarks>
    /// Facts, not flags: a FIN is recorded once per direction (a retransmitted FIN changes nothing), and a FIN counts as
    /// acknowledged when an ACK number at or past its end arrives from the peer. Stepping the phase by flags alone reports
    /// CLOSING on a retransmitted FIN and FIN_WAIT_2 on any ACK, including the FIN sender's own next segment. An RST ends the
    /// connection in RESET for good; only a new connection on the same address/port pair (a new state) leaves it.
    /// </remarks>
    private static void _UpdateConnectionPhase(
        TcpConnectionState conn, TcpFlowState flow, TcpFlowState reverse, in TcpSegmentView s)
    {
        if (s.Rst)
        {
            conn.ResetSeen = true;
            conn.Phase = TcpConnectionPhase.Reset;
            return;
        }

        if (conn.ResetSeen)
        {
            return;
        }

        // Record the facts of this segment.
        if (s.Fin && !flow.FinSent)
        {
            flow.FinSent = true;
            flow.FinEndSeq = s.NextSeq;
            flow.FinOrder = conn.FinCount++;
        }

        if (s.HasAck && reverse.FinSent && !reverse.FinAcked && !_IsSequenceBefore(s.AckNumber, reverse.FinEndSeq))
        {
            reverse.FinAcked = true;
        }

        bool synSeen = (conn.Completeness & TcpConnectionState.SynSeen) != 0;
        bool synAckSeen = (conn.Completeness & TcpConnectionState.SynAckSeen) != 0;

        // Any ACK segment without SYN after the handshake started ends the handshake: it acknowledges the SYN-ACK, or data
        // and acknowledgements flow although a handshake packet is missing from the capture.
        if (!s.Syn && s.HasAck && (synSeen || synAckSeen))
        {
            conn.HandshakeDone = true;
        }

        // The side that sent the first FIN is the active closer; its state is the one reported.
        TcpFlowState? first = null;
        if (flow.FinSent && flow.FinOrder == 0)
        {
            first = flow;
        }
        else if (reverse.FinSent && reverse.FinOrder == 0)
        {
            first = reverse;
        }

        if (first is null)
        {
            conn.Phase = _PhaseBeforeClose(conn, synSeen, synAckSeen);
            return;
        }

        TcpFlowState second = ReferenceEquals(first, flow)
            ? reverse
            : flow;
        if (!second.FinSent)
        {
            conn.Phase = first.FinAcked
                ? TcpConnectionPhase.FinWait2
                : TcpConnectionPhase.FinWait1;
            return;
        }

        // Both sides sent a FIN: the acknowledgements decide. Both FINs acknowledged is TIME_WAIT. Only the first FIN
        // acknowledged is the normal close, waiting for the last ACK. Anything else (neither acknowledged, or only the
        // second FIN acknowledged) is the simultaneous close: the active closer's own FIN is still unacknowledged, which
        // keeps it in CLOSING until the peer's ACK arrives (RFC 9293 figure 5).
        if (first.FinAcked && second.FinAcked)
        {
            conn.Phase = TcpConnectionPhase.TimeWait;
        }
        else if (first.FinAcked)
        {
            conn.Phase = TcpConnectionPhase.LastAck;
        }
        else
        {
            conn.Phase = TcpConnectionPhase.Closing;
        }
    }

    /// <summary>The phase of a connection that no side has started to close.</summary>
    private static TcpConnectionPhase _PhaseBeforeClose(TcpConnectionState conn, bool synSeen, bool synAckSeen)
    {
        // No SYN or SYN-ACK in the capture: the connection was joined mid-stream and carries traffic (this very segment).
        if (!synSeen && !synAckSeen)
        {
            return TcpConnectionPhase.EstablishedMidStream;
        }

        if (conn.HandshakeDone)
        {
            return TcpConnectionPhase.Established;
        }

        return synAckSeen
            ? TcpConnectionPhase.SynReceived
            : TcpConnectionPhase.SynSent;
    }

    #endregion

    #region Display text

    /// <summary>
    /// Returns a display-friendly text for a TCP connection phase (RFC 9293 state names).
    /// </summary>
    /// <param name="phase">Phase to describe.</param>
    /// <returns>The state name, or <c>UNKNOWN</c> for an undefined value.</returns>
    internal static string GetPhaseDisplayText(TcpConnectionPhase phase) => phase switch
    {
        TcpConnectionPhase.Closed => "CLOSED",
        TcpConnectionPhase.SynSent => "SYN_SENT",
        TcpConnectionPhase.SynReceived => "SYN_RECEIVED",
        TcpConnectionPhase.Established => "ESTABLISHED",
        TcpConnectionPhase.FinWait1 => "FIN_WAIT_1",
        TcpConnectionPhase.FinWait2 => "FIN_WAIT_2",
        TcpConnectionPhase.Closing => "CLOSING",
        TcpConnectionPhase.TimeWait => "TIME_WAIT",
        TcpConnectionPhase.LastAck => "LAST_ACK",
        TcpConnectionPhase.Reset => "RESET",
        TcpConnectionPhase.EstablishedMidStream => "ESTABLISHED (handshake not captured)",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// Formats the completeness bitmask as six letters, most significant bit first: <c>R</c>eset, <c>F</c>in, <c>D</c>ata,
    /// <c>A</c>ck, <c>S</c>yn-ack, <c>S</c>yn, with <c>·</c> for a part that is missing. Zero reads <c>[ Null ]</c>.
    /// </summary>
    /// <param name="completeness">Bitmask from <see cref="TcpConnectionState.Completeness"/>.</param>
    /// <returns>The text, for example <c>·FDASS</c> for a complete conversation.</returns>
    internal static string FormatCompleteness(byte completeness)
    {
        if (completeness == 0)
        {
            return "[ Null ]";
        }

        // Fixed six letters, one per bit; the string is short and only built on first access of the field text.
        return string.Create(6, completeness, static (span, bits) =>
        {
            span[0] = (bits & TcpConnectionState.RstSeen) != 0 ? 'R' : '·';
            span[1] = (bits & TcpConnectionState.FinSeen) != 0 ? 'F' : '·';
            span[2] = (bits & TcpConnectionState.DataSeen) != 0 ? 'D' : '·';
            span[3] = (bits & TcpConnectionState.AckSeen) != 0 ? 'A' : '·';
            span[4] = (bits & TcpConnectionState.SynAckSeen) != 0 ? 'S' : '·';
            span[5] = (bits & TcpConnectionState.SynSeen) != 0 ? 'S' : '·';
        });
    }

    #endregion
}
