// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// One sent segment (or SYN/FIN) whose acknowledgement has not been seen yet.
/// </summary>
/// <remarks>
/// The list of these entries per direction is what lets the analysis decide "has this byte range been sent before"
/// (retransmission versus reordering), measure the round trip of the exact acknowledgement, name the packet an ACK
/// refers to, and compute the retransmission timeout. Thread safety: plain value type inside the owner's array.
/// </remarks>
internal struct TcpUnackedSegment
{
    /// <summary>First sequence number of the segment; moved forward when an ACK covers only the front of it.</summary>
    internal uint Seq;

    /// <summary>Sequence number after the segment (SYN and FIN count as one).</summary>
    internal uint NextSeq;

    /// <summary>Capture time of the segment in nanoseconds.</summary>
    internal long Nanos;

    /// <summary>Packet that carried the segment (the id the ACK-side fields refer to).</summary>
    internal int PacketId;

    /// <summary>
    /// The same bytes were sent again after this entry was recorded. An ACK for the entry cannot say which transmission it
    /// answers, so it is reported as ambiguous (Karn's algorithm, RFC 6298 section 3). The round trip sample still refers to
    /// the first transmission, which is the entry's own time.
    /// </summary>
    internal bool Retransmitted;
}

/// <summary>
/// Per-direction flow state for TCP connection analysis.
/// </summary>
/// <remarks>
/// Mutable analysis state, written by <c>TcpConnectionTracker.Analyze</c> during the ordered first parse of each packet.
/// <b>Thread safety:</b> not thread-safe; it is never read or written by replays (they use recorded effects). All
/// sequence numbers are raw 32-bit wire values that wrap; comparisons use serial-number arithmetic in the tracker.
/// <para>
/// <b>Memory:</b> an idle flow is one small object. The unacked list is allocated on the first data segment and grows by
/// doubling, so a connection that never carries data (a port scan, a refused connection) costs no list at all.
/// </para>
/// </remarks>
internal sealed class TcpFlowState
{
    /// <summary>
    /// Upper bound for the unacknowledged-segment list of one direction. Limit record: value 10000 entries (about 240 KB per
    /// direction); threat: a receiver that never acknowledges (or a capture that misses all ACKs) would otherwise grow the
    /// list with every segment of a long transfer; constant of the analysis (a memory bound, not configurable); posture:
    /// secure by default, since the data comes from untrusted networks. Reaching the bound is not an error: further
    /// segments are simply not remembered, so they yield no round trip sample and no "already seen" decision, and the
    /// list becomes usable again as ACKs arrive. Rejected alternative: unbounded growth, which cripples the host on a
    /// capture of a long one-way transfer.
    /// </summary>
    internal const int MaxUnacked = 10000;

    #region ISN and handshake facts

    /// <summary>Initial sequence number from the SYN of this direction.</summary>
    internal uint Isn { get; set; }

    /// <summary>Whether the ISN has been captured.</summary>
    internal bool IsnSet { get; set; }

    /// <summary>
    /// Sequence number that relative numbers of this direction count from: the ISN when the SYN was captured, otherwise one
    /// before the first sequence number seen (so the first segment of a connection joined mid-stream reads 1, as the first
    /// data segment after a handshake does). Valid when <see cref="RelativeBaseSet"/>.
    /// </summary>
    internal uint RelativeBase { get; set; }

    /// <summary>Whether <see cref="RelativeBase"/> carries a value.</summary>
    internal bool RelativeBaseSet { get; set; }

    /// <summary>Whether any packet has been seen in this direction.</summary>
    internal bool Seen { get; set; }

    /// <summary>This direction sent the first pure SYN observed on the connection.</summary>
    internal bool IsInitiator { get; set; }

    #endregion

    #region Sequence tracking

    /// <summary>
    /// Highest end sequence seen in this direction. A zero window probe does not advance it: the probe byte is not
    /// delivered, so the next real segment starts at the same number.
    /// </summary>
    internal uint NextSeq { get; set; }

    /// <summary>Capture time (nanoseconds) of the segment that last advanced <see cref="NextSeq"/>.</summary>
    internal long NextSeqNanos { get; set; }

    /// <summary>Highest continuous end sequence the peer is still expected to acknowledge; valid when <see cref="MaxSeqToBeAckedSet"/>.</summary>
    internal uint MaxSeqToBeAcked { get; set; }

    /// <summary>Whether <see cref="MaxSeqToBeAcked"/> carries a value.</summary>
    internal bool MaxSeqToBeAckedSet { get; set; }

    /// <summary>Acknowledgement number of the last segment of this direction.</summary>
    internal uint LastAck { get; set; }

    /// <summary>Capture time (nanoseconds) of the last segment of this direction; the reordering window is measured from it.</summary>
    internal long LastAckNanos { get; set; }

    /// <summary>
    /// Payload length of the last segment that advanced <see cref="NextSeq"/>; 0 means that segment was a pure ACK. A late
    /// segment right after a pure ACK is reordering even though the sequence space is contiguous.
    /// </summary>
    internal uint LastAckLen { get; set; }

    #endregion

    #region Window tracking

    /// <summary>Last advertised (unscaled) window of this direction.</summary>
    internal ushort LastWindow { get; set; }

    /// <summary>
    /// Window scale state of this direction: <c>-1</c> unknown (no SYN captured), <c>-2</c> no scaling (the handshake did
    /// not negotiate it on both sides), <c>0..14</c> the shift count.
    /// </summary>
    internal int WindowScale { get; set; } = -1;

    /// <summary>
    /// No ACK has been seen from this direction since its SYN. Before the first ACK the window of a connection is not scaled
    /// yet (RFC 7323 section 2.2: the window in a SYN is never scaled), which matters for the window-full test.
    /// </summary>
    internal bool IsFirstAck { get; set; } = true;

    #endregion

    #region Duplicate ACK

    /// <summary>Current number of consecutive duplicate ACKs this direction sent for <see cref="LastAck"/>.</summary>
    internal uint DupAckCount { get; set; }

    /// <summary>Packet id of the last segment of this direction that moved the acknowledgement number (the duplicates refer to it).</summary>
    internal int LastNonDupAckPacketId { get; set; } = -1;

    #endregion

    #region Analysis memory

    /// <summary>Whether the bytes-in-flight value of this direction can be trusted (a gap or a missing ACK invalidates it).</summary>
    internal bool ValidBytesInFlight { get; set; } = true;

    /// <summary>Analysis flags of the previous segment of this direction (keep-alive and probe replies look at the peer's).</summary>
    internal TcpAnalysisFlags LastSegmentFlags { get; set; }

    #endregion

    #region Close facts

    /// <summary>This direction sent a FIN.</summary>
    internal bool FinSent { get; set; }

    /// <summary>Sequence number after the FIN; an ACK at or past it acknowledges the FIN.</summary>
    internal uint FinEndSeq { get; set; }

    /// <summary>The peer acknowledged the FIN.</summary>
    internal bool FinAcked { get; set; }

    /// <summary>0 when this direction sent the first FIN of the connection, 1 for the second, -1 while none was sent.</summary>
    internal int FinOrder { get; set; } = -1;

    #endregion

    #region Unacknowledged segments

    /// <summary>
    /// Unacked segments, oldest first; null until the first data segment, then doubling up to <see cref="MaxUnacked"/>.
    /// Only the first <see cref="UnackedCount"/> entries are live.
    /// </summary>
    internal TcpUnackedSegment[]? Unacked { get; set; }

    /// <summary>Number of live entries in <see cref="Unacked"/>.</summary>
    internal int UnackedCount { get; set; }

    /// <summary>
    /// Sum of the lengths of the live unacked entries: the bytes this direction has in flight. Maintained on every add,
    /// acknowledgement and trim instead of summed per segment, because summing would walk up to
    /// <see cref="MaxUnacked"/> entries for every data segment of a long transfer. It counts bytes that were sent and are not
    /// acknowledged; a hole in the sequence space (a segment that was never captured) is not in flight from the capture's
    /// point of view and is not counted.
    /// </summary>
    internal uint UnackedBytes { get; set; }

    #endregion
}

/// <summary>
/// Per-TCP-connection state containing both direction flow states
/// and connection-level metadata (stream index, completeness, etc.).
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> not thread-safe; mutated by the tracker during the ordered first parse and never touched by
/// replays. Directions are named relative to the normalized connection key (lower endpoint first), not by who opened
/// the connection; <see cref="TcpFlowState.IsInitiator"/> marks the sender of the first pure SYN.
/// </remarks>
internal sealed class TcpConnectionState
{
    /// <summary>Unique index for this TCP stream (0-based, monotonically increasing).</summary>
    internal uint StreamIndex
    {
        get; init;
    }

    /// <summary>Lower (address, port) endpoint. Not the SYN sender.</summary>
    internal TcpFlowState Forward { get; } = new();

    /// <summary>The other endpoint. Not the SYN-ACK sender.</summary>
    internal TcpFlowState Reverse { get; } = new();

    /// <summary>Whether the initial RTT has been computed for this connection.</summary>
    internal bool InitialRttSet { get; set; }

    /// <summary>Measured initial RTT in seconds (NaN if not yet computed); shown on every later packet of the connection.</summary>
    internal double InitialRttValue { get; set; } = double.NaN;

    /// <summary>Timestamp of the most recent pure SYN (no ACK). Used once, on the first pure ACK, as iRTT.</summary>
    internal Timestamp? PureSynTimestamp { get; set; }

    /// <summary>Server port learned from the first SYN (destination) or SYN-ACK (source).</summary>
    internal ushort ServerPort { get; set; }

    /// <summary>Whether <see cref="ServerPort"/> was learned from a SYN.</summary>
    internal bool ServerPortSet { get; set; }

    #region Stream timing

    /// <summary>Timestamp of the first packet seen in this stream (for tcp.time_relative).</summary>
    internal Timestamp? FirstTimestamp { get; set; }

    /// <summary>Timestamp of the previous packet seen in this stream (for tcp.time_delta).</summary>
    internal Timestamp? LastTimestamp { get; set; }

    #endregion

    #region Completeness

    /// <summary>
    /// Connection completeness bitmask: which parts of a conversation the capture contains so far.
    /// Bit 0 = SYN, bit 1 = SYN-ACK, bit 2 = ACK that completes the handshake, bit 3 = DATA, bit 4 = FIN, bit 5 = RST.
    /// The value is the state at the current packet (single pass); the final value of a conversation is not known while
    /// packets are still arriving, so it is not shown retroactively on earlier packets.
    /// </summary>
    internal byte Completeness { get; set; }

    /// <summary>Completeness bit: a pure SYN was seen.</summary>
    internal const byte SynSeen = 1 << 0;

    /// <summary>Completeness bit: a SYN-ACK was seen.</summary>
    internal const byte SynAckSeen = 1 << 1;

    /// <summary>Completeness bit: the ACK that completes the handshake was seen.</summary>
    internal const byte AckSeen = 1 << 2;

    /// <summary>Completeness bit: a segment with payload was seen.</summary>
    internal const byte DataSeen = 1 << 3;

    /// <summary>Completeness bit: a FIN was seen.</summary>
    internal const byte FinSeen = 1 << 4;

    /// <summary>Completeness bit: an RST was seen.</summary>
    internal const byte RstSeen = 1 << 5;

    #endregion

    #region Heuristic Protocol Detection

    /// <summary>
    /// Cached protocol ID from heuristic detection for this connection.
    /// Once a protocol is detected via heuristic matching on the first data packet,
    /// subsequent packets on the same connection reuse this cached ID without
    /// re-running heuristic tests.
    /// </summary>
    internal ProtocolId? HeuristicProtocolId { get; set; }

    #endregion

    #region TCP Connection State Machine

    /// <summary>
    /// Current TCP connection state (e.g., SYN_SENT, ESTABLISHED, FIN_WAIT_1).
    /// Updated by the analyzer after processing each segment.
    /// </summary>
    internal TcpConnectionPhase Phase { get; set; }

    /// <summary>Number of different directions that have sent a FIN (0..2); orders the two FINs for the close states.</summary>
    internal int FinCount { get; set; }

    /// <summary>An RST was seen on this connection. A SYN after it starts a new connection.</summary>
    internal bool ResetSeen { get; set; }

    /// <summary>
    /// A segment with the ACK flag and without SYN was seen after the handshake started: the handshake is over (or was never
    /// captured but data and acknowledgements flow). Decides between SYN_RECEIVED and ESTABLISHED.
    /// </summary>
    internal bool HandshakeDone { get; set; }

    #endregion
}

/// <summary>
/// TCP connection state machine phases, tracking the lifecycle of a connection.
/// Based on the RFC 9293 state diagram (section 3.3.2).
/// </summary>
/// <remarks>
/// A passive capture sees both endpoints, so this is one connection-level phase derived from the facts both directions
/// produced (who sent a SYN, who sent a FIN, which FIN was acknowledged), not the real state of either host. The phase
/// reported is the one of the side that closed first (the active closer), so a normal close reads FIN_WAIT_1, FIN_WAIT_2,
/// TIME_WAIT. A retransmitted FIN or ACK never changes it, because the facts are recorded once. The names are for
/// display; see <c>TcpConnectionTracker</c> for the derivation.
/// </remarks>
internal enum TcpConnectionPhase : byte
{
    /// <summary>
    /// Initial value of a connection that has not carried a segment yet. Never reported for a segment, because the phase is
    /// derived after the segment was recorded; a connection that was joined mid-stream reads <see cref="EstablishedMidStream"/>.
    /// </summary>
    Closed,

    /// <summary>SYN sent by initiator, waiting for SYN-ACK.</summary>
    SynSent,

    /// <summary>SYN-ACK sent by responder, waiting for final ACK.</summary>
    SynReceived,

    /// <summary>Three-way handshake complete, data can flow.</summary>
    Established,

    /// <summary>First FIN sent, waiting for ACK.</summary>
    FinWait1,

    /// <summary>First FIN ACKed, waiting for peer's FIN.</summary>
    FinWait2,

    /// <summary>Both sides sent FIN, neither FIN is acknowledged yet (simultaneous close).</summary>
    Closing,

    /// <summary>Both FINs are acknowledged — TIME_WAIT timer running.</summary>
    TimeWait,

    /// <summary>
    /// The second FIN was sent after the first one was acknowledged; the acknowledgement of the second FIN is awaited.
    /// </summary>
    LastAck,

    /// <summary>RST seen — connection aborted. Only a new connection on the same address/port pair leaves this phase.</summary>
    Reset,

    /// <summary>
    /// The capture contains no SYN of this connection (it was joined mid-stream) but it carries data or acknowledgements, so
    /// it is established as far as anyone can tell.
    /// </summary>
    EstablishedMidStream,
}
