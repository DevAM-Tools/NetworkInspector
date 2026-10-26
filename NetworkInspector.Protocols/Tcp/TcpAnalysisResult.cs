// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Bitmask flags indicating which TCP analysis conditions were detected for a segment.
/// Multiple flags can be set simultaneously.
/// </summary>
/// <remarks>
/// The numeric values are mirrored one-to-one by <c>TcpProtocol.AnalysisEffectFlags</c> (the recorded form used for
/// replay), and <c>TcpProtocol</c> converts between the two with a plain cast. The bit positions of both enums must
/// therefore stay identical. Thread safety: enum value, immutable.
/// </remarks>
[Flags]
internal enum TcpAnalysisFlags : uint
{
    /// <summary>No analysis flags detected.</summary>
    None = 0,

    /// <summary>Segment is a retransmission (end_seq &lt;= next_seq).</summary>
    Retransmission = 1 << 0,

    /// <summary>Fast retransmission (retransmission after 3+ duplicate ACKs from the other direction).</summary>
    FastRetransmission = 1 << 1,

    /// <summary>Out-of-order segment (end_seq &gt; next_seq AND seq != next_seq).</summary>
    OutOfOrder = 1 << 2,

    /// <summary>Duplicate ACK (same ACK number and window as the previous ACK of this direction, no data).</summary>
    DuplicateAck = 1 << 3,

    /// <summary>Lost segment detected (gap in sequence numbers).</summary>
    LostSegment = 1 << 4,

    /// <summary>Keep-alive probe (seq = next-1, len &lt;= 1).</summary>
    KeepAlive = 1 << 5,

    /// <summary>Keep-alive acknowledgment.</summary>
    KeepAliveAck = 1 << 6,

    /// <summary>Zero window advertised (window = 0, no RST/SYN/FIN).</summary>
    ZeroWindow = 1 << 7,

    /// <summary>Zero window probe (data sent to peer with window = 0).</summary>
    ZeroWindowProbe = 1 << 8,

    /// <summary>Zero window probe ACK.</summary>
    ZeroWindowProbeAck = 1 << 9,

    /// <summary>Window update (pure ACK with changed window, no SYN/FIN/RST/PSH).</summary>
    WindowUpdate = 1 << 10,

    /// <summary>Window is full (bytes in flight &gt;= the scaled receiver window).</summary>
    WindowFull = 1 << 11,

    /// <summary>
    /// Connection reuses previously seen port pair. Declared (and mirrored in the recorded effect flags), but nothing
    /// in the connection tracker sets it at present.
    /// </summary>
    ReusedPorts = 1 << 12,

    /// <summary>Spurious retransmission (data was already ACKed by the reverse flow).</summary>
    SpuriousRetransmission = 1 << 13,
}

/// <summary>
/// Result of TCP segment analysis. Captures all detected conditions and metrics
/// for a single segment, produced during Parse() and consumed during lazy population.
/// </summary>
/// <remarks>
/// Produced by the connection tracker on the first parse of a packet only. On a replay the result is rebuilt from the
/// recorded effect instead, which carries every value below except the live references
/// (<see cref="ConnectionState"/>, <see cref="ConnectionKey"/>, <see cref="SrcAddr"/>); those are therefore only
/// meaningful on the first-parse path (heuristic caching and reassembly). Thread safety: immutable value type, but
/// <see cref="ConnectionState"/> points at mutable tracker state that must not be touched from replay threads.
/// </remarks>
internal readonly record struct TcpAnalysisResult
{
    #region Properties

    /// <summary>Bitmask of detected analysis conditions.</summary>
    internal TcpAnalysisFlags Flags { get; init; }

    /// <summary>Stream index for this connection (0-based).</summary>
    internal uint StreamIndex { get; init; }

    /// <summary>Duplicate ACK count (only valid when <see cref="TcpAnalysisFlags.DuplicateAck"/> is set).</summary>
    internal uint DupAckNum { get; init; }

    /// <summary>Bytes currently in flight (unacknowledged). 0 if not applicable.</summary>
    internal ulong BytesInFlight { get; init; }

    /// <summary>Initial RTT in seconds (SYN → SYN-ACK delta). NaN if not measured.</summary>
    internal double InitialRtt { get; init; }

    /// <summary>ACK RTT in seconds (data → ACK delta). NaN if not measured.</summary>
    internal double AckRtt { get; init; }

    /// <summary>Time since first packet in this stream, in seconds. NaN if not measured.</summary>
    internal double TimeRelative { get; init; }

    /// <summary>Time since previous packet in this stream, in seconds. NaN if not measured.</summary>
    internal double TimeDelta { get; init; }

    /// <summary>
    /// Calculated (scaled) window size. Zero means not computed.
    /// <c>window_size_value &lt;&lt; window_scale</c> when the scale factor is known.
    /// </summary>
    internal ulong ScaledWindowSize { get; init; }

    /// <summary>
    /// Window scale factor advertised by the sender of this segment, or -1 when this segment is a SYN
    /// or the sender's SYN did not carry a scale. The sender's window is shifted by this value.
    /// </summary>
    internal int WindowScaleFactor { get; init; }

    /// <summary>
    /// <see langword="true"/> when no enclosing IPv4/IPv6 layer was found, preventing
    /// stream tracking and analysis. The caller should append a diagnostic error field.
    /// </summary>
    internal bool NoIpLayer { get; init; }

    /// <summary>
    /// Reference to the connection state for this segment's TCP stream.
    /// Used for heuristic protocol caching — once a protocol is detected by payload inspection,
    /// subsequent segments on the same connection reuse the cached protocol ID.
    /// Null when no IP layer is found (no connection tracking), when the stream table is full, and on replay.
    /// </summary>
    internal TcpConnectionState? ConnectionState { get; init; }

    /// <summary>
    /// Current TCP connection phase (state machine state) after processing this segment.
    /// </summary>
    internal TcpConnectionPhase Phase { get; init; }

    /// <summary>
    /// The normalized connection key for this segment's TCP stream.
    /// Used by the reassembly engine to look up per-connection stream state.
    /// Default when no IP layer is found.
    /// </summary>
    internal TcpConnectionKey ConnectionKey { get; init; }

    /// <summary>Source IP address as UInt128 for direction detection in the reassembly engine.</summary>
    internal UInt128 SrcAddr { get; init; }

    /// <summary>Wire sequence minus this direction's ISN when relative sequence numbers are on and the ISN is known.</summary>
    internal uint RelativeSeq { get; init; }

    /// <summary>Wire acknowledgement minus the peer ISN when relative sequence numbers are on and that ISN is known.</summary>
    internal uint RelativeAck { get; init; }

    /// <summary>Whether <see cref="RelativeSeq"/> is the ISN-adjusted value.</summary>
    internal bool SequenceIsRelative { get; init; }

    /// <summary>Whether <see cref="RelativeAck"/> is the ISN-adjusted value.</summary>
    internal bool AckIsRelative { get; init; }

    /// <summary>The stream table refused this key. Header fields are still parsed; <c>tcp.stream</c> is not appended.</summary>
    internal bool StreamLimitReached { get; init; }

    /// <summary>Whether any analysis flag is set.</summary>
    internal bool HasAnyFlag => Flags != TcpAnalysisFlags.None;

    #endregion

    #region Sentinels

    /// <summary>
    /// Default result with no flags and NaN RTT values (NaN means "not measured", unlike 0 which is a valid measurement)
    /// and a window scale of -1 ("none").
    /// </summary>
    internal static TcpAnalysisResult Empty => new()
    {
        Flags = TcpAnalysisFlags.None,
        InitialRtt = double.NaN,
        AckRtt = double.NaN,
        TimeRelative = double.NaN,
        TimeDelta = double.NaN,
        WindowScaleFactor = -1,
    };

    #endregion
}
