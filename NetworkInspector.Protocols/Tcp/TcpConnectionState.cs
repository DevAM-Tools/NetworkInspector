// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Per-direction flow state for TCP connection analysis.
/// Tracks sequence numbers, acknowledgment numbers, window sizes,
/// and duplicate ACK counters for one direction of a TCP connection.
/// </summary>
/// <remarks>
/// Mutable analysis state, written by <c>TcpConnectionTracker.Analyze</c> during the ordered first parse of each packet.
/// <b>Thread safety:</b> not thread-safe; it is never read or written by replays (they use recorded effects). All
/// sequence numbers are raw 32-bit wire values that wrap; comparisons use serial-number arithmetic in the tracker.
/// </remarks>
internal sealed class TcpFlowState
{
    #region ISN Tracking

    /// <summary>Initial sequence number from the SYN.</summary>
    internal uint Isn
    {
        get; set;
    }

    /// <summary>Whether the ISN has been captured.</summary>
    internal bool IsnSet
    {
        get; set;
    }

    /// <summary>Whether any packet has been seen in this direction.</summary>
    internal bool Seen
    {
        get; set;
    }

    #endregion

    #region Sequence Tracking

    /// <summary>Last sequence number seen.</summary>
    internal uint LastSeq
    {
        get; set;
    }

    /// <summary>Last acknowledgment number seen.</summary>
    internal uint LastAck
    {
        get; set;
    }

    /// <summary>Expected next sequence number (LastSeq + segment length).</summary>
    internal uint NextSeq
    {
        get; set;
    }

    /// <summary>Highest sequence number seen so far.</summary>
    internal uint MaxSeq
    {
        get; set;
    }

    /// <summary>Sequence number of the FIN, if observed.</summary>
    internal uint? FinSeq
    {
        get; set;
    }

    #endregion

    #region Window Tracking

    /// <summary>Last advertised window size.</summary>
    internal ushort LastWindow
    {
        get; set;
    }

    /// <summary>Window scale factor from SYN options (null if not seen).</summary>
    internal byte? WindowScale
    {
        get; set;
    }

    #endregion

    #region Duplicate ACK

    /// <summary>Current consecutive duplicate ACK count.</summary>
    internal uint DupAckCount
    {
        get; set;
    }

    /// <summary>The ACK number that is being duplicated.</summary>
    internal uint LastDupAck
    {
        get; set;
    }

    #endregion

    #region Timestamps

    /// <summary>Timestamp of the SYN packet (for diagnostics). Initial RTT uses the connection's pure-SYN time.</summary>
    internal Timestamp? SynTimestamp
    {
        get; set;
    }

    /// <summary>
    /// Fixed 256-slot ring for ACK RTT. Index is <c>endSeq &amp; 255</c>.
    /// A collision overwrites the older entry. One allocation per flow, not per segment.
    /// </summary>
    /// <remarks>
    /// Why a fixed ring: the RTT of a data segment is measured when the matching ACK (ack number equals the segment's
    /// end sequence) arrives, so the sender's timestamps must be remembered. A dictionary or list would grow with
    /// unacknowledged data; the ring caps memory per flow (about 3 KB across the three parallel arrays) at the price
    /// that a colliding or very old unacknowledged segment simply yields no RTT sample. The three arrays are
    /// parallel (structure of arrays) and are only valid together with <see cref="AckRttSlotUsed"/>.
    /// </remarks>
    internal uint[] AckRttSeq { get; } = new uint[256];

    /// <summary>Timestamp nanoseconds paired with <see cref="AckRttSeq"/>.</summary>
    internal long[] AckRttNanos { get; } = new long[256];

    /// <summary>Whether the ring slot holds a live data-segment timestamp.</summary>
    internal bool[] AckRttSlotUsed { get; } = new bool[256];

    /// <summary>The previous segment in this direction was a keep-alive.</summary>
    internal bool LastSegmentWasKeepAlive
    {
        get; set;
    }

    /// <summary>This direction sent the first pure SYN observed on the connection.</summary>
    internal bool IsInitiator
    {
        get; set;
    }

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
    internal bool InitialRttSet
    {
        get; set;
    }

    /// <summary>Measured initial RTT in seconds (NaN if not yet computed).</summary>
    internal double InitialRttValue { get; set; } = double.NaN;

    /// <summary>Timestamp of the most recent pure SYN (no ACK). Used once, on the first pure ACK, as iRTT.</summary>
    internal Timestamp? PureSynTimestamp
    {
        get; set;
    }

    /// <summary>Server port learned from the first SYN (destination) or SYN-ACK (source).</summary>
    internal ushort ServerPort
    {
        get; set;
    }

    /// <summary>Whether <see cref="ServerPort"/> was learned from a SYN.</summary>
    internal bool ServerPortSet
    {
        get; set;
    }

    #region Stream timing

    /// <summary>Timestamp of the first packet seen in this stream (for tcp.time_relative).</summary>
    internal Timestamp? FirstTimestamp
    {
        get; set;
    }

    /// <summary>Timestamp of the previous packet seen in this stream (for tcp.time_delta).</summary>
    internal Timestamp? LastTimestamp
    {
        get; set;
    }

    /// <summary>
    /// Connection completeness bitmask tracking which phases have been observed.
    /// Bit 0 = SYN, Bit 1 = SYN-ACK, Bit 2 = ACK, Bit 3 = DATA, Bit 4 = FIN, Bit 5 = RST.
    /// </summary>
    internal byte Completeness
    {
        get; set;
    }

    // Completeness flag constants (bit positions of Completeness). Bit 2 (ACK) is documented above but has no
    // constant because nothing sets or reads it.
    internal const byte SynSeen = 1 << 0;
    internal const byte SynAckSeen = 1 << 1;
    internal const byte DataSeen = 1 << 3;
    internal const byte FinSeen = 1 << 4;
    internal const byte RstSeen = 1 << 5;

    #endregion

    #region Heuristic Protocol Detection

    /// <summary>
    /// Cached protocol ID from heuristic detection for this connection.
    /// Once a protocol is detected via heuristic matching on the first data packet,
    /// subsequent packets on the same connection reuse this cached ID without
    /// re-running heuristic tests.
    /// </summary>
    internal ProtocolId? HeuristicProtocolId
    {
        get; set;
    }

    #endregion

    #region TCP Connection State Machine

    /// <summary>
    /// Current TCP connection state (e.g., SYN_SENT, ESTABLISHED, FIN_WAIT_1).
    /// Updated by the analyzer after processing each segment.
    /// </summary>
    internal TcpConnectionPhase Phase
    {
        get; set;
    }
}

/// <summary>
/// TCP connection state machine phases, tracking the lifecycle of a connection.
/// Based on RFC 793 TCP state diagram.
/// </summary>
/// <remarks>
/// A passive capture sees both endpoints, so this is one simplified connection-level phase (advanced by the segments of
/// either direction), not the real state of either host. It only leaves <see cref="Closed"/> when a pure SYN is seen, so
/// a connection joined mid-stream stays in <see cref="Closed"/> and its FIN/ACK segments do not change the phase
/// (only an RST does). The names are for display; see <c>TcpConnectionTracker</c> for the exact transitions.
/// </remarks>
internal enum TcpConnectionPhase : byte
{
    /// <summary>
    /// No pure SYN seen yet (initial phase), or the connection finished the last-ACK exchange and is closed again.
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

    /// <summary>Peer sent FIN, waiting for application close.</summary>
    CloseWait,

    /// <summary>Both sides sent FIN, waiting for final ACKs.</summary>
    Closing,

    /// <summary>Received FIN after our FIN was ACKed — TIME_WAIT timer running.</summary>
    TimeWait,

    /// <summary>
    /// Second FIN seen after the first side was in <see cref="CloseWait"/>; the final ACK is awaited. The next ACK moves
    /// the phase to <see cref="Closed"/>.
    /// </summary>
    LastAck,

    /// <summary>RST received — connection aborted.</summary>
    Reset,
    #endregion
}
