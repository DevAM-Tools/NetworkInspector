// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Manages TCP connection states and performs per-segment analysis.
/// Implements Wireshark-compatible TCP analysis (retransmission, dup-ack, keep-alive, etc.).
/// <para>
/// <b>Analysis order is critical</b> — the sequence, ACK, window and next-sequence state of a flow is updated only in the final
/// "state update" step of <see cref="Analyze"/>, so each check compares the segment against the state <i>before</i> it,
/// and some checks depend on the result of an earlier one:
/// 1. ISN/SYN tracking, server port, initiator
/// 2. Initial RTT (first pure SYN → first pure ACK)
/// 3. Keep-Alive (BEFORE retransmission: a keep-alive is not a retransmission and skips that check)
/// 4. Retransmission / Out-of-Order / Lost segment
/// 5. Zero Window, Zero Window Probe, Zero Window Probe ACK
/// 6. Window Update (MUST be before Dup-ACK: a window change is not a duplicate ACK)
/// 7. Duplicate ACK, then Keep-Alive ACK (both need the other direction's keep-alive flag)
/// 8. ACK RTT
/// 9. Bytes in flight, then Window Full (needs the bytes in flight)
/// 10. State update (sequence/ack/window, ACK RTT timestamp, keep-alive memory), connection phase, timing, scaled window
/// The <c>#region</c> markers in the method body number the same steps in finer detail (steps 5, 7 and 9 here are split into several).
/// </para>
/// <para>
/// A 4-entry inline LRU cache sits in front of the dictionary to exploit temporal
/// locality in TCP traffic (bursts from the same connection). Cache hits avoid the
/// full dictionary lookup. Promote-to-front uses a swap with position 0 (O(1),
/// avoids shifting the large key structs: two 128-bit addresses plus ports and discriminators).
/// Insert shifts entries only on cache miss. Because a hit only swaps with slot 0, slots 1..3 are
/// not kept in strict recency order: the cache is an approximation of LRU, which is enough for its purpose.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The tracker mutates connection state and is used only by the ordered,
/// single-threaded first parse of each packet; replays never touch it (they use the effects recorded at first parse).
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by TcpProtocol (field _ConnectionTracker)")]
internal sealed class TcpConnectionTracker
{
    /// <summary>LRU cache capacity — 4 entries provide a good balance between
    /// hit rate and linear scan cost for typical network traffic patterns.</summary>
    private const int _CacheSize = 4;

    /// <summary>
    /// All tracked connections keyed by normalized connection key. The key's hash is seeded per process, so crafted
    /// address/port combinations cannot be chosen to collide. The number of entries is capped by the caller through
    /// the <c>maxStreams</c> argument of <see cref="TryGetOrCreate"/>.
    /// </summary>
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
    /// <param name="key">Normalized connection key of the segment.</param>
    /// <param name="maxStreams">
    /// Upper bound for the number of tracked connections (trust-boundary limit against memory exhaustion by a flood of
    /// distinct connections; set by the caller from the <c>tcp.max_tracked_streams</c> setting). Reaching it is an
    /// expected failure that the caller reports, not an exception; existing connections keep working.
    /// </param>
    /// <param name="state">The connection state on success; <see langword="null"/> when the limit is reached.</param>
    /// <returns><see langword="true"/> when a state is available.</returns>
    internal bool TryGetOrCreate(in TcpConnectionKey key, int maxStreams, [NotNullWhen(true)] out TcpConnectionState? state)
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

                state = _CacheValues[0]!; // Invariant: slots 0.._CacheCount-1 hold a state, and a hit is inside that range.
                return true;
            }
        }

        // Cache miss: known connection, full dictionary lookup, then promote it into the cache.
        if (_Connections.TryGetValue(key, out TcpConnectionState? existing))
        {
            state = existing;
            _InsertCache(key, state);
            return true;
        }

        // New connection: refuse beyond the limit. The check sits after the lookups so connections that are already
        // tracked keep being analyzed when the table is full. No stream index is consumed on refusal.
        if (_Connections.Count >= maxStreams)
        {
            state = null;
            return false;
        }

        // Stream indices are handed out in order of first appearance and are what the user sees as tcp.stream.
        state = new TcpConnectionState { StreamIndex = _NextStreamIndex++ };
        _Connections[key] = state;
        _InsertCache(key, state);
        return true;
    }

    /// <summary>
    /// Inserts a connection at the front of the cache, shifting the others one slot back. The last slot falls out when
    /// the cache is full. Only runs on a cache miss, which is why the shift cost is acceptable.
    /// </summary>
    /// <param name="key">Connection key.</param>
    /// <param name="state">Connection state for <paramref name="key"/>.</param>
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
    /// <returns>
    /// The analysis flags and metrics for this segment. The connection state passed in is updated as a side effect, which
    /// is why this method must only run once per packet (first parse), never on a replay. Static because all state lives
    /// in <paramref name="conn"/>.
    /// </returns>
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
        // "flow" is the direction of this segment, "reverseFlow" the other one (the peer whose data this segment may
        // acknowledge). RTT results start as NaN = "not measured".
        TcpFlowState flow = isForward ? conn.Forward : conn.Reverse;
        TcpFlowState reverseFlow = isForward ? conn.Reverse : conn.Forward;
        TcpAnalysisFlags analysisFlags = TcpAnalysisFlags.None;
        uint dupAckNum = 0;
        double initialRtt = double.NaN;
        double ackRtt = double.NaN;

        // Flag bits of the TCP flags byte: FIN 0x01, SYN 0x02, RST 0x04, PSH 0x08, ACK 0x10.
        bool isSyn = (flags & 0x02) != 0;
        bool isAck = (flags & 0x10) != 0;
        bool isFin = (flags & 0x01) != 0;
        bool isRst = (flags & 0x04) != 0;
        bool isPsh = (flags & 0x08) != 0;

        // Calculate end sequence number (seq + payload_len, +1 for SYN/FIN, because SYN and FIN each occupy one
        // sequence number). Unsigned arithmetic on purpose: sequence numbers wrap at 2^32, and every later ordering
        // test uses serial-number comparison (_IsSequenceAfter/_IsSequenceBefore), never plain < or >.
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
        // The first SYN of each direction fixes that direction's initial sequence number (used for relative numbers)
        // and its window scale option. Later SYNs (retransmitted handshake) do not overwrite them.
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
        // Measured once per connection from the most recent pure SYN to the first data-less ACK (the third handshake
        // packet). A negative delta (out-of-order timestamps) is not recorded.
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
        // Only segments that occupy sequence space (data/FIN) in an already seen direction are classified; SYNs and
        // keep-alives are excluded. All comparisons are wrap-safe serial-number comparisons against NextSeq, the
        // end of the data already seen in this direction (state before this segment).
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

            // Lost segment detection: gap between expected and received (a segment starting after NextSeq means
            // earlier bytes were never captured). Such a gap segment also satisfies the out-of-order branch above,
            // so both flags are set for it.
            if (_IsSequenceAfter(seqNum, flow.NextSeq) && !isSyn)
            {
                analysisFlags |= TcpAnalysisFlags.LostSegment;
            }
        }

        #endregion

        #region 5. Zero Window
        // The advertised window of this segment is zero (not on SYN/FIN/RST, where the window is not meaningful).
        if (window == 0 && !isRst && !isSyn && !isFin)
        {
            analysisFlags |= TcpAnalysisFlags.ZeroWindow;
        }

        #endregion

        #region 6. Zero Window Probe (exactly one new byte into a zero window)
        // Uses the other direction's last advertised window (state before this segment).
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
        // A pure ACK that repeats the previous ACK number but changes the window. It must be decided first because the
        // duplicate-ACK test below excludes it (an ACK with a new window is not a duplicate); the PSH exclusion mirrors
        // the pure-ACK definition of the analysis.
        bool isWindowUpdate = false;
        if (isAck && payloadLen == 0 && !isSyn && !isFin && !isRst && !isPsh
            && flow.Seen && window != flow.LastWindow && ackNum == flow.LastAck)
        {
            analysisFlags |= TcpAnalysisFlags.WindowUpdate;
            isWindowUpdate = true;
        }

        #endregion

        #region 9. Duplicate ACK
        // Same ACK number and window as the previous segment of this direction, no data, non-zero window, and not the
        // reply to a keep-alive. The counter feeds the fast-retransmission test (3+ duplicates) for the other direction.
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
        // An ACK completes the RTT sample of the other direction's data segment whose end sequence equals the ACK
        // number. The slot is cleared on use so a repeated ACK does not yield a second sample for the same segment.
        // Index mask 255 matches the 256-slot ring in TcpFlowState.
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
        // Compares the unacknowledged bytes with the receiver's scaled window. The shift count is at most 14 (the
        // option scan caps it per RFC 7323), so a 16-bit window shifted in a uint cannot overflow.
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
        // From here on the flow state is overwritten with this segment's values; every check above has already used
        // the previous values. Do not move checks below this point.
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
        // IsInitiator is true only for the sender of the first pure SYN; a FIN from the other side leads to CLOSE_WAIT
        // (see _UpdateConnectionPhase). Runs after the flow update so the phase reflects this segment.
        _UpdateConnectionPhase(conn, flow, isSyn, isAck, isFin, isRst);

        #endregion

        #region 14. Stream timing
        // tcp.time_relative: since the first packet of the stream; tcp.time_delta: since the previous packet of the
        // stream (either direction). NaN means "no previous packet".
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

        // Relative numbers: wire value minus the ISN of the sending direction (sequence) or of the peer (ack). The
        // uint subtraction wraps correctly modulo 2^32. They stay raw (and flagged as such) while the needed ISN is
        // unknown, e.g. for a capture that joins mid-connection.
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

        // The cache holds references to the discarded states, so it must be emptied together with the dictionary
        // (a stale hit would resurrect a connection that no longer exists). Clearing the values also releases them
        // for garbage collection; the key slots are overwritten before they are read again.
        _CacheCount = 0;
        Array.Clear(_CacheValues);
    }

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

    /// <summary>
    /// Updates the TCP connection phase based on the current segment's flags.
    /// Follows the simplified RFC 793 state diagram with tracking for both directions.
    /// A reset in either direction ends in Reset regardless of the current phase. Other segments only advance the phase
    /// along the transitions below; anything else leaves it unchanged (including every segment of a connection whose
    /// handshake was not captured).
    /// </summary>
    /// <param name="conn">Connection whose phase is updated.</param>
    /// <param name="flow">Direction state of the segment's sender (its IsInitiator flag decides FIN handling).</param>
    /// <param name="isSyn">SYN flag.</param>
    /// <param name="isAck">ACK flag.</param>
    /// <param name="isFin">FIN flag.</param>
    /// <param name="isRst">RST flag.</param>
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
                    // A FIN from the sender of the first pure SYN is reported as FIN_WAIT_1 (active close by the
                    // initiator). A FIN from the other side (IsInitiator false) is reported as CLOSE_WAIT. This is a
                    // connection-level simplification, not the state of one host. The Established phase is only
                    // reachable after a pure SYN was seen, so one side is always flagged as initiator here.
                    conn.Phase = flow.IsInitiator
                        ? TcpConnectionPhase.FinWait1
                        : TcpConnectionPhase.CloseWait;
                }
                break;

            case TcpConnectionPhase.FinWait1:
                // A FIN with or without ACK means the other side closed too (simultaneous close): Closing.
                if (isFin)
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
    /// Returns a display-friendly text for a TCP connection phase (RFC 793 state names).
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
        TcpConnectionPhase.CloseWait => "CLOSE_WAIT",
        TcpConnectionPhase.Closing => "CLOSING",
        TcpConnectionPhase.TimeWait => "TIME_WAIT",
        TcpConnectionPhase.LastAck => "LAST_ACK",
        TcpConnectionPhase.Reset => "RESET",
        _ => "UNKNOWN",
    };
        #endregion
}
