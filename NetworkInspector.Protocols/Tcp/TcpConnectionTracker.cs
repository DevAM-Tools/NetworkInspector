// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Manages TCP connection states and performs per-segment analysis.
/// Implements Wireshark-compatible TCP analysis (retransmission, dup-ack, keep-alive, etc.).
/// <para>
/// <b>Analysis order is critical</b> — checks must run in a specific sequence:
/// 1. ISN/SYN tracking
/// 2. Initial RTT (SYN → SYN-ACK)
/// 3. Retransmission / Out-of-Order
/// 4. Keep-Alive
/// 5. Zero Window
/// 6. Window Update (MUST be before Dup-ACK)
/// 7. Duplicate ACK
/// 8. ACK RTT
/// 9. Bytes in Flight
/// 10. State update
/// </para>
/// <para>
/// A 4-entry inline LRU cache sits in front of the dictionary to exploit temporal
/// locality in TCP traffic (bursts from the same connection). Cache hits avoid the
/// full dictionary lookup. Promote-to-front uses a swap with position 0 (O(1),
/// avoids shifting the 40-byte key structs). Insert shifts entries only on cache miss.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Will be instantiated by TcpProtocol in Phase 5 TCP-analysis integration")]
internal sealed class TcpConnectionTracker
{
    /// <summary>LRU cache capacity — 4 entries provide a good balance between
    /// hit rate and linear scan cost for typical network traffic patterns.</summary>
    private const int CacheSize = 4;

    /// <summary>All tracked connections keyed by normalized connection key.</summary>
    private readonly Dictionary<TcpConnectionKey, TcpConnectionState> _Connections = [];

    /// <summary>Monotonically increasing stream index counter.</summary>
    private uint _NextStreamIndex;

    /// <summary>Inline LRU cache — most recently used entry is at index 0.</summary>
    private readonly TcpConnectionKey[] _CacheKeys = new TcpConnectionKey[CacheSize];

    /// <summary>Cached connection states corresponding to <see cref="_CacheKeys"/>.</summary>
    private readonly TcpConnectionState?[] _CacheValues = new TcpConnectionState?[CacheSize];

    /// <summary>Number of valid entries in the LRU cache (0..CacheSize).</summary>
    private int _CacheCount;

    /// <summary>Gets or creates a connection state for the given key.</summary>
    /// <param name="key">Normalized TCP connection key.</param>
    /// <param name="isNew">Set to <see langword="true"/> if this is a new connection.</param>
    internal TcpConnectionState GetOrCreate(in TcpConnectionKey key, out bool isNew)
    {
        // Linear probe — sequential access from MRU ([0]) toward LRU.
        for (int i = 0; i < _CacheCount; i++)
        {
            if (_CacheKeys[i].Equals(key))
            {
                isNew = false;

                // Swap with MRU position [0] — O(1), avoids shifting all entries.
                if (i > 0)
                {
                    TcpConnectionState? hitValue = _CacheValues[i];
                    _CacheKeys[i] = _CacheKeys[0];
                    _CacheValues[i] = _CacheValues[0];
                    _CacheKeys[0] = key;
                    _CacheValues[0] = hitValue;
                }

                return _CacheValues[0]!;
            }
        }

        // Cache miss — fall through to dictionary
        TcpConnectionState state;
        if (_Connections.TryGetValue(key, out TcpConnectionState? existing))
        {
            state = existing;
            isNew = false;
        }
        else
        {
            state = new TcpConnectionState { StreamIndex = _NextStreamIndex++ };
            _Connections[key] = state;
            isNew = true;
        }

        // Insert at MRU position [0] — shift existing entries toward LRU.
        int shiftCount = Math.Min(_CacheCount, CacheSize - 1);
        for (int j = shiftCount; j > 0; j--)
        {
            _CacheKeys[j] = _CacheKeys[j - 1];
            _CacheValues[j] = _CacheValues[j - 1];
        }

        _CacheKeys[0] = key;
        _CacheValues[0] = state;

        if (_CacheCount < CacheSize)
        {
            _CacheCount++;
        }

        return state;
    }



    /// <summary>
    /// Analyzes a TCP segment in the context of its connection.
    /// Updates connection state and returns analysis flags.
    /// </summary>
    /// <param name="conn">The connection state for this stream.</param>
    /// <param name="isForward">Direction of this segment (true = initiator → responder).</param>
    /// <param name="seqNum">Sequence number from the TCP header.</param>
    /// <param name="ackNum">Acknowledgment number from the TCP header.</param>
    /// <param name="flags">TCP flags byte.</param>
    /// <param name="window">Advertised window size.</param>
    /// <param name="payloadLen">TCP payload length in bytes.</param>
    /// <param name="timestamp">Packet timestamp for RTT calculations.</param>
    /// <param name="windowScale">Window Scale shift count from SYN options, or null if not a SYN or no WScale option.</param>
    internal static TcpAnalysisResult Analyze(
        TcpConnectionState conn,
        bool isForward,
        uint seqNum,
        uint ackNum,
        byte flags,
        ushort window,
        int payloadLen,
        Timestamp timestamp,
        byte? windowScale = null)
    {
        TcpFlowState flow = isForward ? conn.Forward : conn.Reverse;
        TcpFlowState reverseFlow = isForward ? conn.Reverse : conn.Forward;
        TcpAnalysisFlags analysisFlags = TcpAnalysisFlags.None;
        uint dupAckNum = 0;
        double initialRtt = double.NaN;
        double ackRtt = double.NaN;

        bool isSyn = (flags & 0x02) != 0;
        bool isAck = (flags & 0x10) != 0;
        bool isFin = (flags & 0x01) != 0;
        bool isRst = (flags & 0x04) != 0;
        bool isPsh = (flags & 0x08) != 0;

        // Calculate end sequence number (seq + payload_len, +1 for SYN/FIN)
        uint segLen = (uint)payloadLen;
        if (isSyn)
        {
            segLen++;
        }
        if (isFin)
        {
            segLen++;
        }
        uint endSeq = seqNum + segLen;

        #region 1. ISN / SYN tracking
        if (isSyn && !flow.IsnSet)
        {
            flow.Isn = seqNum;
            flow.IsnSet = true;
            flow.SynTimestamp = timestamp;

            // Store window scale from SYN/SYN-ACK options for later use in scaling
            if (windowScale.HasValue)
            {
                flow.WindowScale = windowScale.Value;
            }

            if (isAck)
            {
                conn.Completeness |= TcpConnectionState.SynAckSeen;
            }
            else
            {
                conn.Completeness |= TcpConnectionState.SynSeen;
            }
        }

        #endregion

        #region 2. Initial RTT: SYN-ACK → delta from SYN
        if (isSyn && isAck && !conn.InitialRttSet && reverseFlow.SynTimestamp.HasValue)
        {
            // SYN-ACK seen; compute delta from the original SYN
            double delta = ComputeTimeDelta(reverseFlow.SynTimestamp.Value, timestamp);
            if (delta >= 0)
            {
                conn.InitialRttValue = delta;
                conn.InitialRttSet = true;
                initialRtt = delta;
            }
        }

        #endregion

        #region 3. Retransmission / Out-of-Order
        if (flow.Seen && segLen > 0 && !isSyn)
        {
            // Check if this segment's data has already been seen
            if (IsSequenceBefore(endSeq, flow.NextSeq) || endSeq == flow.NextSeq)
            {
                // end_seq <= next_seq → retransmission
                if (endSeq != flow.NextSeq || seqNum != flow.NextSeq)
                {
                    // Check for fast retransmission (after 3+ dup ACKs)
                    if (reverseFlow.DupAckCount >= 3)
                    {
                        analysisFlags |= TcpAnalysisFlags.FastRetransmission;
                    }
                    else
                    {
                        analysisFlags |= TcpAnalysisFlags.Retransmission;
                    }
                }
            }
            else if (seqNum != flow.NextSeq)
            {
                // end_seq > next_seq AND seq != next_seq → out of order
                analysisFlags |= TcpAnalysisFlags.OutOfOrder;
            }

            // Spurious retransmission: segment was retransmitted but reverse flow
            // already ACKed past its end — the retransmission was unnecessary.
            if ((analysisFlags & (TcpAnalysisFlags.Retransmission | TcpAnalysisFlags.FastRetransmission)) != 0
                && reverseFlow.Seen
                && IsSequenceAfter(reverseFlow.LastAck, endSeq))
            {
                analysisFlags |= TcpAnalysisFlags.SpuriousRetransmission;
            }

            // Lost segment detection: gap between expected and received
            if (IsSequenceAfter(seqNum, flow.NextSeq) && !isSyn)
            {
                analysisFlags |= TcpAnalysisFlags.LostSegment;
            }
        }

        #endregion

        #region 4. Keep-Alive
        if (flow.Seen && payloadLen <= 1 && seqNum == flow.NextSeq - 1 && !isSyn && !isFin && !isRst)
        {
            analysisFlags |= TcpAnalysisFlags.KeepAlive;
        }

        #endregion

        #region 5. Zero Window
        if (window == 0 && !isRst && !isSyn && !isFin)
        {
            analysisFlags |= TcpAnalysisFlags.ZeroWindow;
        }

        #endregion

        #region 6. Zero Window Probe (data sent to peer with zero window)
        if (payloadLen > 0 && reverseFlow.Seen && reverseFlow.LastWindow == 0)
        {
            analysisFlags |= TcpAnalysisFlags.ZeroWindowProbe;
        }

        #endregion

        #region 7. Zero Window Probe ACK
        if (isAck && payloadLen == 0 && flow.Seen && flow.LastWindow == 0 && window > 0)
        {
            analysisFlags |= TcpAnalysisFlags.ZeroWindowProbeAck;
        }

        #endregion

        #region 8. Window Update (MUST be before Dup-ACK check)
        bool isWindowUpdate = false;
        if (isAck && payloadLen == 0 && !isSyn && !isFin && !isRst && !isPsh
            && flow.Seen && window != flow.LastWindow && ackNum == flow.LastAck)
        {
            analysisFlags |= TcpAnalysisFlags.WindowUpdate;
            isWindowUpdate = true;
        }

        #endregion

        #region 9. Duplicate ACK
        if (isAck && payloadLen == 0 && flow.Seen && !isSyn && !isFin && !isRst
            && ackNum == flow.LastAck && window == flow.LastWindow
            && !isWindowUpdate)
        {
            flow.DupAckCount++;
            flow.LastDupAck = ackNum;
            analysisFlags |= TcpAnalysisFlags.DuplicateAck;
            dupAckNum = flow.DupAckCount;
        }
        else if (isAck)
        {
            // Reset dup ACK counter on any non-duplicate ACK
            flow.DupAckCount = 0;
        }

        #endregion

        #region 10. ACK RTT
        if (isAck && reverseFlow.DataSegmentTimestamps.Count > 0)
        {
            if (reverseFlow.DataSegmentTimestamps.Remove(ackNum, out Timestamp segTs))
            {
                double delta = ComputeTimeDelta(segTs, timestamp);
                if (delta >= 0)
                {
                    ackRtt = delta;
                }
            }
        }

        #endregion

        #region 11. Bytes in Flight
        ulong bytesInFlight = 0;
        if (flow.Seen && reverseFlow.Seen && segLen > 0)
        {
            // bytes_in_flight = next_seq - reverse.last_ack (using updated next_seq)
            uint nextSeqUpdated = IsSequenceAfter(endSeq, flow.NextSeq) ? endSeq : flow.NextSeq;
            if (IsSequenceAfter(nextSeqUpdated, reverseFlow.LastAck))
            {
                bytesInFlight = nextSeqUpdated - reverseFlow.LastAck;
            }
        }

        #endregion

        #region 12. Window Full
        if (bytesInFlight > 0 && reverseFlow.Seen)
        {
            uint scaledWindow = (uint)reverseFlow.LastWindow;
            if (reverseFlow.WindowScale.HasValue)
            {
                scaledWindow <<= reverseFlow.WindowScale.Value;
            }
            if (bytesInFlight >= scaledWindow && scaledWindow > 0)
            {
                analysisFlags |= TcpAnalysisFlags.WindowFull;
            }
        }

        #endregion

        #region 13. State update
        if (payloadLen > 0)
        {
            conn.Completeness |= TcpConnectionState.DataSeen;

            // Record timestamp for ACK RTT (limit to 256 entries)
            if (flow.DataSegmentTimestamps.Count < 256)
            {
                flow.DataSegmentTimestamps.TryAdd(endSeq, timestamp);
            }
        }

        if (isFin)
        {
            flow.FinSeq = seqNum;
            conn.Completeness |= TcpConnectionState.FinSeen;
        }

        if (isRst)
        {
            conn.Completeness |= TcpConnectionState.RstSeen;
        }

        flow.LastSeq = seqNum;
        if (isAck)
        {
            flow.LastAck = ackNum;
        }
        flow.LastWindow = window;

        // Update NextSeq and MaxSeq
        if (!flow.Seen || IsSequenceAfter(endSeq, flow.NextSeq))
        {
            flow.NextSeq = endSeq;
        }
        if (!flow.Seen || IsSequenceAfter(endSeq, flow.MaxSeq))
        {
            flow.MaxSeq = endSeq;
        }

        flow.Seen = true;

        #endregion

        #region Connection state machine transitions (RFC 793)
        UpdateConnectionPhase(conn, isSyn, isAck, isFin, isRst, isForward);

        #endregion

        #region 14. Stream timing
        double timeRelative = double.NaN;
        double timeDelta = double.NaN;

        if (!conn.FirstTimestamp.HasValue)
        {
            // First packet in this stream
            conn.FirstTimestamp = timestamp;
            timeRelative = 0.0;
        }
        else
        {
            timeRelative = ComputeTimeDelta(conn.FirstTimestamp.Value, timestamp);
        }

        if (conn.LastTimestamp.HasValue)
        {
            timeDelta = ComputeTimeDelta(conn.LastTimestamp.Value, timestamp);
        }
        conn.LastTimestamp = timestamp;

        #endregion

        #region 15. Scaled window size
        // The *receiver's* scale factor applies to the window value advertised by this sender.
        // E.g., if the reverse flow negotiated window scale = 7, then this segment's window
        // should be shifted by 7 to get the actual receive window.
        ulong scaledWindowSize = 0;
        int windowScaleFactor = -1;
        if (reverseFlow.WindowScale.HasValue)
        {
            windowScaleFactor = reverseFlow.WindowScale.Value;
            scaledWindowSize = (ulong)window << reverseFlow.WindowScale.Value;
        }

        // Store initial RTT from connection state if this is the first time we have it
        if (conn.InitialRttSet && double.IsNaN(initialRtt))
        {
            initialRtt = conn.InitialRttValue;
        }

        return new TcpAnalysisResult
        {
            Flags = analysisFlags,
            StreamIndex = conn.StreamIndex,
            DupAckNum = dupAckNum,
            BytesInFlight = bytesInFlight,
            InitialRtt = initialRtt,
            AckRtt = ackRtt,
            TimeRelative = timeRelative,
            TimeDelta = timeDelta,
            ScaledWindowSize = scaledWindowSize,
            WindowScaleFactor = windowScaleFactor,
            ConnectionState = conn,
            Phase = conn.Phase,
        };
    }

    /// <summary>Clears all connection states and resets the stream counter.</summary>
    internal void Clear()
    {
        _Connections.Clear();
        _NextStreamIndex = 0;
    }

    /// <summary>
    /// Computes time delta in seconds between two timestamps.
    /// Uses nanosecond-precision subtraction for accuracy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeTimeDelta(Timestamp t1, Timestamp t2) =>
        // Subtract raw nanosecond values and convert to seconds
        (t2.AsNanos - t1.AsNanos) / 1_000_000_000.0;

    /// <summary>
    /// Checks if sequence number <paramref name="a"/> is strictly after <paramref name="b"/>
    /// using 32-bit wrapping arithmetic (handles sequence number wraparound).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSequenceAfter(uint a, uint b) =>
        // Signed comparison of (a - b) handles 32-bit wraparound
        (int)(a - b) > 0;

    /// <summary>
    /// Checks if sequence number <paramref name="a"/> is strictly before <paramref name="b"/>
    /// using 32-bit wrapping arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSequenceBefore(uint a, uint b) =>
        (int)(a - b) < 0;

    /// <summary>
    /// Updates the TCP connection phase based on the current segment's flags.
    /// Follows the simplified RFC 793 state diagram with tracking for both directions.
    /// </summary>
    private static void UpdateConnectionPhase(
        TcpConnectionState conn, bool isSyn, bool isAck, bool isFin, bool isRst, bool isForward)
    {
        if (isRst)
        {
            conn.Phase = TcpConnectionPhase.Reset;
            return;
        }

        TcpConnectionPhase phase = conn.Phase;

        switch (phase)
        {
            case TcpConnectionPhase.Closed:
                if (isSyn && !isAck)
                {
                    conn.Phase = TcpConnectionPhase.SynSent;
                }
                break;

            case TcpConnectionPhase.SynSent:
                if (isSyn && isAck)
                {
                    conn.Phase = TcpConnectionPhase.SynReceived;
                }
                break;

            case TcpConnectionPhase.SynReceived:
                if (isAck && !isSyn)
                {
                    conn.Phase = TcpConnectionPhase.Established;
                }
                break;

            case TcpConnectionPhase.Established:
                if (isFin)
                {
                    conn.Phase = TcpConnectionPhase.FinWait1;
                }
                break;

            case TcpConnectionPhase.FinWait1:
                if (isFin && isAck)
                {
                    // Simultaneous close: both sides FIN at the same time
                    conn.Phase = TcpConnectionPhase.Closing;
                }
                else if (isFin)
                {
                    conn.Phase = TcpConnectionPhase.Closing;
                }
                else if (isAck)
                {
                    conn.Phase = TcpConnectionPhase.FinWait2;
                }
                break;

            case TcpConnectionPhase.FinWait2:
                if (isFin)
                {
                    conn.Phase = TcpConnectionPhase.TimeWait;
                }
                break;

            case TcpConnectionPhase.Closing:
                if (isAck)
                {
                    conn.Phase = TcpConnectionPhase.TimeWait;
                }
                break;

            case TcpConnectionPhase.CloseWait:
                if (isFin)
                {
                    conn.Phase = TcpConnectionPhase.LastAck;
                }
                break;

            case TcpConnectionPhase.LastAck:
                if (isAck)
                {
                    conn.Phase = TcpConnectionPhase.Closed;
                }
                break;
        }
    }

    /// <summary>
    /// Returns a display-friendly text for a TCP connection phase.
    /// </summary>
    internal static string GetPhaseDisplayText(TcpConnectionPhase phase) => phase switch
    {
        TcpConnectionPhase.Closed => "CLOSED",
        TcpConnectionPhase.SynSent => "SYN_SENT",
        TcpConnectionPhase.SynReceived => "SYN_RECEIVED",
        TcpConnectionPhase.Established => "ESTABLISHED",
        TcpConnectionPhase.FinWait1 => "FIN_WAIT_1",
        TcpConnectionPhase.FinWait2 => "FIN_WAIT_2",
        TcpConnectionPhase.CloseWait => "CLOSE_WAIT",
        TcpConnectionPhase.Closing => "CLOSING",
        TcpConnectionPhase.TimeWait => "TIME_WAIT",
        TcpConnectionPhase.LastAck => "LAST_ACK",
        TcpConnectionPhase.Reset => "RESET",
        _ => "UNKNOWN",
    };
        #endregion
}
