// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
    private const int _CacheSize = 4;

    /// <summary>All tracked connections keyed by normalized connection key.</summary>
    private readonly Dictionary<TcpConnectionKey, TcpConnectionState> _Connections = [];

    /// <summary>Monotonically increasing stream index counter.</summary>
    private uint _NextStreamIndex;

    /// <summary>Inline LRU cache — most recently used entry is at index 0.</summary>
    private readonly TcpConnectionKey[] _CacheKeys = new TcpConnectionKey[_CacheSize];

    /// <summary>Cached connection states corresponding to <see cref="_CacheKeys"/>.</summary>
    private readonly TcpConnectionState?[] _CacheValues = new TcpConnectionState?[_CacheSize];

    /// <summary>Number of valid entries in the LRU cache (0.._CacheSize).</summary>
    private int _CacheCount;

    /// <summary>
    /// Gets or creates a connection state for the given key.
    /// Returns <see langword="false"/> when the key is new and <paramref name="maxStreams"/> is already reached.
    /// </summary>
    internal bool TryGetOrCreate(in TcpConnectionKey key, int maxStreams, out TcpConnectionState state)
    {
        // Linear probe — sequential access from MRU ([0]) toward LRU.
        for (int i = 0; i < _CacheCount; i++)
        {
            if (_CacheKeys[i].Equals(key))
            {
                // Swap with MRU position [0] — O(1), avoids shifting all entries.
                if (i > 0)
                {
                    TcpConnectionState? hitValue = _CacheValues[i];
                    _CacheKeys[i] = _CacheKeys[0];
                    _CacheValues[i] = _CacheValues[0];
                    _CacheKeys[0] = key;
                    _CacheValues[0] = hitValue;
                }

                state = _CacheValues[0]!;
                return true;
            }
        }

        if (_Connections.TryGetValue(key, out TcpConnectionState? existing))
        {
            state = existing;
            _InsertCache(key, state);
            return true;
        }

        if (_Connections.Count >= maxStreams)
        {
            state = null!;
            return false;
        }

        state = new TcpConnectionState { StreamIndex = _NextStreamIndex++ };
        _Connections[key] = state;
        _InsertCache(key, state);
        return true;
    }

    private void _InsertCache(in TcpConnectionKey key, TcpConnectionState state)
    {
        int shiftCount = Math.Min(_CacheCount, _CacheSize - 1);
        for (int j = shiftCount; j > 0; j--)
        {
            _CacheKeys[j] = _CacheKeys[j - 1];
            _CacheValues[j] = _CacheValues[j - 1];
        }

        _CacheKeys[0] = key;
        _CacheValues[0] = state;

        if (_CacheCount < _CacheSize)
        {
            _CacheCount++;
        }
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
    /// <param name="srcPort">Source TCP port. Used to learn the server port from a SYN-ACK.</param>
    /// <param name="dstPort">Destination TCP port. Used to learn the server port from a pure SYN.</param>
    /// <param name="windowScale">Window Scale shift count from SYN options, or null if not a SYN or no WScale option.</param>
    /// <param name="relativeSequenceNumbers">When true, <see cref="TcpAnalysisResult.RelativeSeq"/> subtracts the sender ISN.</param>
    internal static TcpAnalysisResult Analyze(
        TcpConnectionState conn,
        bool isForward,
        uint seqNum,
        uint ackNum,
        byte flags,
        ushort window,
        int payloadLen,
        Timestamp timestamp,
        ushort srcPort,
        ushort dstPort,
        byte? windowScale = null,
        bool relativeSequenceNumbers = true)
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

        #region Server port and initiator
        // The first pure SYN names the initiator and the server port (its destination).
        // A SYN-ACK names the server port from its source when no pure SYN was seen.
        if (isSyn && !isAck)
        {
            if (!conn.Forward.IsInitiator && !conn.Reverse.IsInitiator)
            {
                flow.IsInitiator = true;
            }

            if (!conn.ServerPortSet)
            {
                conn.ServerPort = dstPort;
                conn.ServerPortSet = true;
            }

            conn.PureSynTimestamp = timestamp;
        }
        else if (isSyn && isAck && !conn.ServerPortSet)
        {
            conn.ServerPort = srcPort;
            conn.ServerPortSet = true;
        }

        #endregion

        #region 2. Initial RTT: pure SYN to the first pure ACK
        if (!isSyn && isAck && payloadLen == 0 && !isFin && !isRst
            && !conn.InitialRttSet && conn.PureSynTimestamp.HasValue)
        {
            double delta = _ComputeTimeDelta(conn.PureSynTimestamp.Value, timestamp);
            if (delta >= 0)
            {
                conn.InitialRttValue = delta;
                conn.InitialRttSet = true;
                initialRtt = delta;
            }
        }

        #endregion

        #region 3. Keep-alive (before retransmission)
        // A keep-alive sits one sequence before next and carries 0 or 1 bytes.
        // It is not a retransmission. Wireshark skips the retransmission check once this flag is set.
        bool isKeepAlive = flow.Seen && payloadLen <= 1 && seqNum == flow.NextSeq - 1 && !isSyn && !isFin && !isRst;
        if (isKeepAlive)
        {
            analysisFlags |= TcpAnalysisFlags.KeepAlive;
        }

        #endregion

        #region 4. Retransmission / Out-of-Order
        if (!isKeepAlive && flow.Seen && segLen > 0 && !isSyn)
        {
            // Check if this segment's data has already been seen
            if (_IsSequenceBefore(endSeq, flow.NextSeq) || endSeq == flow.NextSeq)
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
                && _IsSequenceAfter(reverseFlow.LastAck, endSeq))
            {
                analysisFlags |= TcpAnalysisFlags.SpuriousRetransmission;
            }

            // Lost segment detection: gap between expected and received
            if (_IsSequenceAfter(seqNum, flow.NextSeq) && !isSyn)
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

        #region 5. Zero Window Probe (exactly one new byte into a zero window)
        if (payloadLen == 1 && flow.Seen && seqNum == flow.NextSeq
            && reverseFlow.Seen && reverseFlow.LastWindow == 0
            && !isSyn && !isFin && !isRst)
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
        if (isAck && payloadLen == 0 && window != 0 && flow.Seen && !isSyn && !isFin && !isRst
            && ackNum == flow.LastAck && window == flow.LastWindow
            && !isWindowUpdate
            && !reverseFlow.LastSegmentWasKeepAlive)
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

        #region 9b. Keep-alive ACK
        if (isAck && payloadLen == 0 && window != 0 && flow.Seen
            && !isSyn && !isFin && !isRst
            && seqNum == flow.NextSeq && ackNum == flow.LastAck && window == flow.LastWindow
            && reverseFlow.LastSegmentWasKeepAlive
            && (analysisFlags & TcpAnalysisFlags.KeepAlive) == 0)
        {
            analysisFlags |= TcpAnalysisFlags.KeepAliveAck;
        }

        #endregion

        #region 10. ACK RTT
        if (isAck)
        {
            int slot = (int)(ackNum & 255);
            if (reverseFlow.AckRttSlotUsed[slot] && reverseFlow.AckRttSeq[slot] == ackNum)
            {
                reverseFlow.AckRttSlotUsed[slot] = false;
                Timestamp segTs = Timestamp.FromNanos(reverseFlow.AckRttNanos[slot]);
                double delta = _ComputeTimeDelta(segTs, timestamp);
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
            uint nextSeqUpdated = _IsSequenceAfter(endSeq, flow.NextSeq) ? endSeq : flow.NextSeq;
            if (_IsSequenceAfter(nextSeqUpdated, reverseFlow.LastAck))
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

            // Record timestamp for ACK RTT. The ring overwrites a colliding slot instead of growing.
            int rttSlot = (int)(endSeq & 255);
            flow.AckRttSeq[rttSlot] = endSeq;
            flow.AckRttNanos[rttSlot] = timestamp.AsNanos;
            flow.AckRttSlotUsed[rttSlot] = true;
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
        if (!flow.Seen || _IsSequenceAfter(endSeq, flow.NextSeq))
        {
            flow.NextSeq = endSeq;
        }
        if (!flow.Seen || _IsSequenceAfter(endSeq, flow.MaxSeq))
        {
            flow.MaxSeq = endSeq;
        }

        flow.Seen = true;
        flow.LastSegmentWasKeepAlive = (analysisFlags & TcpAnalysisFlags.KeepAlive) != 0;

        #endregion

        #region Connection state machine transitions (RFC 793)
        // If no pure SYN was seen, IsInitiator stays false and a FIN is reported as FIN_WAIT_1.
        _UpdateConnectionPhase(conn, flow, isSyn, isAck, isFin, isRst);

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
            timeRelative = _ComputeTimeDelta(conn.FirstTimestamp.Value, timestamp);
        }

        if (conn.LastTimestamp.HasValue)
        {
            timeDelta = _ComputeTimeDelta(conn.LastTimestamp.Value, timestamp);
        }
        conn.LastTimestamp = timestamp;

        #endregion

        #region 15. Scaled window size
        // SYN and SYN-ACK windows are not scaled. The shift is the one this sender put in its own SYN.
        ulong scaledWindowSize = 0;
        int windowScaleFactor = -1;
        if (!isSyn && flow.WindowScale.HasValue)
        {
            windowScaleFactor = flow.WindowScale.Value;
            scaledWindowSize = (ulong)window << flow.WindowScale.Value;
        }

        uint relativeSeq = seqNum;
        bool sequenceIsRelative = false;
        if (relativeSequenceNumbers && flow.IsnSet)
        {
            relativeSeq = seqNum - flow.Isn;
            sequenceIsRelative = true;
        }

        uint relativeAck = ackNum;
        bool ackIsRelative = false;
        if (relativeSequenceNumbers && reverseFlow.IsnSet)
        {
            relativeAck = ackNum - reverseFlow.Isn;
            ackIsRelative = true;
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
            RelativeSeq = relativeSeq,
            RelativeAck = relativeAck,
            SequenceIsRelative = sequenceIsRelative,
            AckIsRelative = ackIsRelative,
        };
    }

    /// <summary>Clears all connection states and resets the stream counter.</summary>
    internal void Clear()
    {
        _Connections.Clear();
        _NextStreamIndex = 0;
        _CacheCount = 0;
        Array.Clear(_CacheValues);
    }

    /// <summary>
    /// Computes time delta in seconds between two timestamps.
    /// Uses nanosecond-precision subtraction for accuracy.
    /// </summary>
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

    /// <summary>
    /// Updates the TCP connection phase based on the current segment's flags.
    /// Follows the simplified RFC 793 state diagram with tracking for both directions.
    /// </summary>
    private static void _UpdateConnectionPhase(
        TcpConnectionState conn, TcpFlowState flow, bool isSyn, bool isAck, bool isFin, bool isRst)
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
                    // The side that sent the first pure SYN is the active closer. The other side enters CLOSE_WAIT.
                    // With no pure SYN in the capture, IsInitiator is false and the FIN is reported as FIN_WAIT_1.
                    conn.Phase = flow.IsInitiator
                        ? TcpConnectionPhase.FinWait1
                        : TcpConnectionPhase.CloseWait;
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
