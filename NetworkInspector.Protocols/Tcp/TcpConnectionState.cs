// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Per-direction flow state for TCP connection analysis.
/// Tracks sequence numbers, acknowledgment numbers, window sizes,
/// and duplicate ACK counters for one direction of a TCP connection.
/// </summary>
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

    /// <summary>Timestamp of the SYN packet (for Initial RTT calculation).</summary>
    internal Timestamp? SynTimestamp
    {
        get; set;
    }

    /// <summary>
    /// Maps sequence numbers to timestamps for ACK RTT calculation.
    /// Limited to 256 entries to prevent unbounded growth.
    /// </summary>
    internal Dictionary<uint, Timestamp> DataSegmentTimestamps { get; } = new(capacity: 64);
}

/// <summary>
/// Per-TCP-connection state containing both direction flow states
/// and connection-level metadata (stream index, completeness, etc.).
/// </summary>
internal sealed class TcpConnectionState
{
    /// <summary>Unique index for this TCP stream (0-based, monotonically increasing).</summary>
    internal uint StreamIndex
    {
        get; init;
    }

    /// <summary>Forward direction flow state (initial SYN sender → receiver).</summary>
    internal TcpFlowState Forward { get; } = new();

    /// <summary>Reverse direction flow state (SYN-ACK sender → initiator).</summary>
    internal TcpFlowState Reverse { get; } = new();

    /// <summary>Whether the initial RTT has been computed for this connection.</summary>
    internal bool InitialRttSet
    {
        get; set;
    }

    /// <summary>Measured initial RTT in seconds (NaN if not yet computed).</summary>
    internal double InitialRttValue { get; set; } = double.NaN;

    #endregion

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

    // Completeness flag constants
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
internal enum TcpConnectionPhase : byte
{
    /// <summary>No SYN seen yet.</summary>
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

    /// <summary>Our FIN ACKed after peer's FIN — connection fully closed.</summary>
    LastAck,

    /// <summary>RST received — connection aborted.</summary>
    Reset,
    #endregion
}
