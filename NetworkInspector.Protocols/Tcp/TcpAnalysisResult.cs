// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Bitmask flags indicating which TCP analysis conditions were detected for a segment.
/// Multiple flags can be set simultaneously.
/// </summary>
[Flags]
internal enum TcpAnalysisFlags : uint
{
    /// <summary>No analysis flags detected.</summary>
    None = 0,

    /// <summary>Segment is a retransmission (end_seq <= next_seq).</summary>
    Retransmission = 1 << 0,

    /// <summary>Fast retransmission (retransmission after 3+ duplicate ACKs).</summary>
    FastRetransmission = 1 << 1,

    /// <summary>Out-of-order segment (end_seq > next_seq AND seq != next_seq).</summary>
    OutOfOrder = 1 << 2,

    /// <summary>Duplicate ACK (same ACK, no data, connection seen before).</summary>
    DuplicateAck = 1 << 3,

    /// <summary>Lost segment detected (gap in sequence numbers).</summary>
    LostSegment = 1 << 4,

    /// <summary>Keep-alive probe (seq = next-1, len <= 1).</summary>
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

    /// <summary>Window is full (bytes in flight >= receiver window).</summary>
    WindowFull = 1 << 11,

    /// <summary>Connection reuses previously seen port pair.</summary>
    ReusedPorts = 1 << 12,

    /// <summary>Spurious retransmission (data was already ACKed by the reverse flow).</summary>
    SpuriousRetransmission = 1 << 13,
}

/// <summary>
/// Result of TCP segment analysis. Captures all detected conditions and metrics
/// for a single segment, produced during Parse() and consumed during lazy population.
/// </summary>
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
    /// Window scale factor for the sender of this segment, or -1 if unknown.
    /// The sender's window_size_value should be shifted left by the *receiver's* scale factor.
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
    /// Null when no IP layer is found (no connection tracking).
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

    /// <summary>
    /// Source IP address as UInt128 for direction detection in the reassembly engine.
    /// Default when no IP layer is found.
    /// </summary>
    internal UInt128 SrcAddr { get; init; }

    /// <summary>Whether any analysis flag is set.</summary>
    internal bool HasAnyFlag => Flags != TcpAnalysisFlags.None;

    #endregion

    #region Sentinels

    /// <summary>Default result with no flags and NaN RTT values.</summary>
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
