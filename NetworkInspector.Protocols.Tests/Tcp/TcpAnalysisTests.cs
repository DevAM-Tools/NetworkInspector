// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP analysis features: retransmission detection, duplicate ACK tracking,
/// out-of-order detection, zero window handling, keep-alive, window analysis, and RTT.
/// Each test builds its own Stack so that TCP connection tracking state accumulates
/// correctly across sequentially parsed packets.
/// </summary>
internal sealed class TcpAnalysisTests
{
    #region Constants

    // Addresses used throughout all tests
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2
    private const ushort _ClientPort = 49152;
    private const ushort _ServerPort = 80;

    #endregion

    #region Helpers

    /// <summary>Builds an Ethernet/IPv4/TCP frame for client → server direction.</summary>
    private static byte[] _ClientFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ipLayer = new(_ClientIp, _ServerIp);
        TcpLayer tcpLayer = new(_ClientPort, _ServerPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds an Ethernet/IPv4/TCP frame for server → client direction.</summary>
    private static byte[] _ServerFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ipLayer = new(_ServerIp, _ClientIp);
        TcpLayer tcpLayer = new(_ServerPort, _ClientPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Builds a standard 3-way handshake (SYN, SYN-ACK, ACK) and returns the packets.
    /// Client ISN = <paramref name="clientIsn"/>, Server ISN = <paramref name="serverIsn"/>.
    /// </summary>
    private static Packet[] _DoHandshake(
        Stack stack,
        uint clientIsn = 1000,
        uint serverIsn = 2000,
        int startIndex = 0,
        long startTimeMs = 0)
    {
        // SYN: Client → Server
        byte[] syn = _ClientFrame(clientIsn, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, startIndex,
            Timestamp.FromMillis(startTimeMs));

        // SYN-ACK: Server → Client
        byte[] synAck = _ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, startIndex + 1,
            Timestamp.FromMillis(startTimeMs + 10));

        // ACK: Client → Server (completes handshake)
        byte[] ack = _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, startIndex + 2,
            Timestamp.FromMillis(startTimeMs + 15));

        return [pSyn, pSynAck, pAck];
    }

    #endregion

    #region Analysis Container Presence

    [Test]
    public async Task Analysis_NotPresent_OnNormalDataAck()
    {
        // Normal data flow with no anomalies should not produce analysis container
        // (or produce it only for benign reasons like bytes_in_flight)
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);

        // Client sends data
        byte[] data = new byte[100];
        byte[] dataFrame = _ClientFrame(1001, 2001, TcpFlags.PshAck, data);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, dataFrame, 3, Timestamp.FromMillis(20));

        // Server ACKs all data
        byte[] ackFrame = _ServerFrame(2001, 1101, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ackFrame, 4, Timestamp.FromMillis(25));

        // The ACK packet should not have retransmission/out-of-order/dup-ack flags
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pAck, "tcp.analysis.retransmission").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pAck, "tcp.analysis.out_of_order").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pAck, "tcp.analysis.duplicate_ack").ConfigureAwait(false);
    }

    #endregion

    #region Retransmission Detection

    [Test]
    public async Task Analysis_Retransmission_DetectedOnDuplicateSegment()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends 100 bytes at seq=1001
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client retransmits the same segment (same seq, same data)
        byte[] data2 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pRetransmit = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(220));

        await ProtocolTestHelper.AssertBoolField(stack, pRetransmit, "tcp.analysis.retransmission", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_FastRetransmission_After3DupAcks()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends 100 bytes at seq=1001
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends another 100 bytes at seq=1101
        byte[] data2 = _ClientFrame(1101, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        // Server sends 3 duplicate ACKs (ack=1001, acknowledging nothing new)
        for (int i = 0; i < 3; i++)
        {
            byte[] dupAck = _ServerFrame(2001, 1001, TcpFlags.Ack);
            ProtocolTestHelper.ParseFrame(stack, dupAck, 5 + i, Timestamp.FromMillis(30 + i * 5));
        }

        // Client retransmits after 3 dup ACKs → fast retransmission
        byte[] retransmit = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pFastRetx = ProtocolTestHelper.ParseFrame(stack, retransmit, 8, Timestamp.FromMillis(50));

        await ProtocolTestHelper.AssertBoolField(stack, pFastRetx, "tcp.analysis.fast_retransmission", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_SpuriousRetransmission_AlreadyAcked()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends data (seq=1001, 100B → endSeq=1101)
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends more data (seq=1101, 50B → endSeq=1151)
        byte[] payload2 = new byte[50];
        byte[] data2 = _ClientFrame(1101, 2001, TcpFlags.PshAck, payload2);
        ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(22));

        // Server ACKs all data (ack=1151, strictly after endSeq=1101 of the first segment)
        byte[] ackFrame = _ServerFrame(2001, 1151, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ackFrame, 5, Timestamp.FromMillis(25));

        // Client retransmits the first segment (endSeq=1101) — already fully ACKed
        // since reverseFlow.LastAck(1151) > endSeq(1101) → spurious retransmission
        byte[] spurious = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pSpurious = ProtocolTestHelper.ParseFrame(stack, spurious, 6, Timestamp.FromMillis(225));

        await ProtocolTestHelper.AssertBoolField(stack, pSpurious, "tcp.analysis.spurious_retransmission", true).ConfigureAwait(false);
    }

    #endregion

    #region Out-of-Order Detection

    /// <summary>
    /// A segment that starts beyond the end of the data seen so far leaves a hole. That is a lost segment (the bytes in
    /// between were not captured), not reordering: nothing has arrived late yet.
    /// </summary>
    [Test]
    public async Task GapSegmentIsOnlyLostSegment()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends seq=1001, len=100 → NextSeq=1101
        byte[] payload100 = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends seq=1201, len=100 (skips 1101-1200) → gap
        byte[] data2 = _ClientFrame(1201, 2001, TcpFlags.PshAck, payload100);
        Packet pGap = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pGap, "tcp.analysis.lost_segment", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pGap, "tcp.analysis.out_of_order").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pGap, "tcp.analysis.retransmission").ConfigureAwait(false);
    }

    /// <summary>
    /// The segment that fills the hole later is reordering: it starts before the end of the data seen so far, was not sent
    /// before, and arrives right after the peer's last ACK. It is not a retransmission.
    /// </summary>
    [Test]
    public async Task LateSegmentIsOutOfOrderNotRetransmission()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        byte[] payload100 = new byte[100];
        ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, payload100), 3, Timestamp.FromMillis(20));
        ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1201, 2001, TcpFlags.PshAck, payload100), 4, Timestamp.FromMillis(21));
        Packet pLate = ProtocolTestHelper.ParseFrame(
            stack, _ClientFrame(1101, 2001, TcpFlags.PshAck, payload100), 5, Timestamp.FromMillis(22));

        await ProtocolTestHelper.AssertBoolField(stack, pLate, "tcp.analysis.out_of_order", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pLate, "tcp.analysis.retransmission").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pLate, "tcp.analysis.lost_segment").ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_NoOutOfOrder_WhenInSequence()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Sequential data: 1001, then 1101 — no out-of-order
        byte[] payload100 = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        byte[] data2 = _ClientFrame(1101, 2001, TcpFlags.PshAck, payload100);
        Packet pSeq = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, pSeq, "tcp.analysis.out_of_order").ConfigureAwait(false);
    }

    #endregion

    #region Duplicate ACK Detection

    [Test]
    public async Task Analysis_DuplicateAck_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends data
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs partially (ack=1001 — doesn't advance)
        byte[] ack1 = _ServerFrame(2001, 1001, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ack1, 4, Timestamp.FromMillis(25));

        // Server sends another ACK with same ack=1001 → dup ACK
        byte[] ack2 = _ServerFrame(2001, 1001, TcpFlags.Ack);
        Packet pDupAck = ProtocolTestHelper.ParseFrame(stack, ack2, 5, Timestamp.FromMillis(30));

        await ProtocolTestHelper.AssertBoolField(stack, pDupAck, "tcp.analysis.duplicate_ack", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_DuplicateAckNum_Increments()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends data
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // 3 consecutive dup ACKs — counter should increment
        Packet[] dupAcks = new Packet[3];
        for (int i = 0; i < 3; i++)
        {
            byte[] dup = _ServerFrame(2001, 1001, TcpFlags.Ack);
            dupAcks[i] = ProtocolTestHelper.ParseFrame(stack, dup, 4 + i, Timestamp.FromMillis(25 + i * 5));
        }

        // Verify dup ack numbers increment
        await ProtocolTestHelper.AssertU64Field(stack, dupAcks[0], "tcp.analysis.duplicate_ack_num", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, dupAcks[1], "tcp.analysis.duplicate_ack_num", 2).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, dupAcks[2], "tcp.analysis.duplicate_ack_num", 3).ConfigureAwait(false);
    }

    #endregion

    #region Lost Segment Detection

    [Test]
    public async Task Analysis_LostSegment_DetectedOnGap()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends seq=1001, len=100 → next expected = 1101
        byte[] payload100 = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends seq=1301 (gap: 1101-1300 missing) → lost segment
        byte[] data2 = _ClientFrame(1301, 2001, TcpFlags.PshAck, payload100);
        Packet pLost = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pLost, "tcp.analysis.lost_segment", true).ConfigureAwait(false);
    }

    #endregion

    #region Keep-Alive Detection

    [Test]
    public async Task Analysis_KeepAlive_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends data so we have a baseline next_seq
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs
        byte[] ackFrame = _ServerFrame(2001, 1101, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ackFrame, 4, Timestamp.FromMillis(25));

        // Client sends keep-alive: seq = next_seq - 1 = 1100, len <= 1, no SYN/FIN/RST
        byte[] keepAlive = _ClientFrame(1100, 2001, TcpFlags.Ack, [0x00]);
        Packet pKa = ProtocolTestHelper.ParseFrame(stack, keepAlive, 5, Timestamp.FromMillis(5025));

        await ProtocolTestHelper.AssertBoolField(stack, pKa, "tcp.analysis.keep_alive", true).ConfigureAwait(false);
    }

    #endregion

    #region Zero Window

    [Test]
    public async Task Analysis_ZeroWindow_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Server advertises window=0 in a pure ACK
        byte[] zeroWin = _ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 0);
        Packet pZeroWin = ProtocolTestHelper.ParseFrame(stack, zeroWin, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertBoolField(stack, pZeroWin, "tcp.analysis.zero_window", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_ZeroWindowProbe_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Server advertises window=0
        byte[] zeroWin = _ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 0);
        ProtocolTestHelper.ParseFrame(stack, zeroWin, 3, Timestamp.FromMillis(20));

        // Client sends data despite window=0 → zero window probe
        byte[] probe = _ClientFrame(1001, 2001, TcpFlags.Ack, [0x00]);
        Packet pProbe = ProtocolTestHelper.ParseFrame(stack, probe, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pProbe, "tcp.analysis.zero_window_probe", true).ConfigureAwait(false);
    }

    #endregion

    #region Window Analysis

    [Test]
    public async Task Analysis_WindowUpdate_DetectedOnWindowChange()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Server ACKs with window=1000
        byte[] ack1 = _ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 1000);
        ProtocolTestHelper.ParseFrame(stack, ack1, 3, Timestamp.FromMillis(20));

        // Server sends another ACK with same ack but larger window → window update
        byte[] ack2 = _ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 32000);
        Packet pWinUpdate = ProtocolTestHelper.ParseFrame(stack, ack2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pWinUpdate, "tcp.analysis.window_update", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_BytesInFlight_Calculated()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends 100 bytes
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Bytes in flight = next_seq(1101) - reverse.last_ack(1001) = 100
        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.analysis.bytes_in_flight", 100).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_WindowFull_DetectedWhenBytesExceedWindow()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Server has small window (100 bytes)
        byte[] smallWin = _ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 100);
        ProtocolTestHelper.ParseFrame(stack, smallWin, 3, Timestamp.FromMillis(20));

        // Client sends exactly 100 bytes → window full
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pFull = ProtocolTestHelper.ParseFrame(stack, data1, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pFull, "tcp.analysis.window_full", true).ConfigureAwait(false);
    }

    #endregion

    #region RTT Measurements

    [Test]
    public async Task Analysis_InitialRtt_MeasuredFromSynToFinalAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        byte[] synAck = _ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pSynAck, "tcp.analysis.initial_rtt").ConfigureAwait(false);

        byte[] ack = _ClientFrame(1001, 2001, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, 2, Timestamp.FromMillis(25));
        await ProtocolTestHelper.AssertF64FieldApprox(stack, pAck, "tcp.analysis.initial_rtt", 0.025, 0.001).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_AckRtt_MeasuredFromDataToAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Client sends data at t=20ms
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs at t=35ms → ACK RTT should be ~0.015s
        byte[] ackFrame = _ServerFrame(2001, 1101, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ackFrame, 4, Timestamp.FromMillis(35));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pAck, "tcp.analysis.ack_rtt", 0.015, 0.001).ConfigureAwait(false);
    }

    #endregion

    #region Timing Fields

    [Test]
    public async Task Timing_TimeRelative_ZeroForFirstPacket()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First packet in stream has time_relative = 0.0
        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(100));

        await ProtocolTestHelper.AssertF64Field(stack, pSyn, "tcp.time_relative", 0.0).ConfigureAwait(false);
    }

    [Test]
    public async Task Timing_TimeRelative_CorrectForSubsequentPackets()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First packet at t=100ms
        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(100));

        // Second packet at t=200ms → time_relative = 0.100s
        byte[] synAck = _ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(200));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pSynAck, "tcp.time_relative", 0.100, 0.001).ConfigureAwait(false);
    }

    [Test]
    public async Task Timing_TimeDelta_BetweenConsecutivePackets()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First packet at t=0
        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // Second at t=10ms
        byte[] synAck = _ServerFrame(2000, 1001, TcpFlags.SynAck);
        ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));

        // Third at t=50ms → delta from second = 0.040s
        byte[] ack = _ClientFrame(1001, 2001, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, 2, Timestamp.FromMillis(50));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pAck, "tcp.time_delta", 0.040, 0.001).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_AsymmetricWindowScale_UsesSenderShiftAndSkipsSyn()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] clientSyn = _FrameWithScale(isClient: true, shift: 7, seq: 1000, ack: 0, flags: TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, clientSyn, 0, Timestamp.FromMillis(0));

        // A SYN window is never scaled: the raw value is shown and there is no scaling factor yet.
        await ProtocolTestHelper.AssertU64Field(stack, pSyn, "tcp.window_size", 100).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pSyn, "tcp.window_size_scalefactor").ConfigureAwait(false);

        byte[] serverSyn = _FrameWithScale(isClient: false, shift: 2, seq: 2000, ack: 1001, flags: TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, serverSyn, 1, Timestamp.FromMillis(1));
        await ProtocolTestHelper.AssertU64Field(stack, pSynAck, "tcp.window_size", 100).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, pSynAck, "tcp.window_size_scalefactor").ConfigureAwait(false);

        // Each direction uses its own shift; the factor is the multiplier (1 << shift), not the shift.
        byte[] clientData = _ClientFrame(1001, 2001, TcpFlags.PshAck, "x"u8, windowSize: 100);
        Packet pClient = ProtocolTestHelper.ParseFrame(stack, clientData, 2, Timestamp.FromMillis(2));
        await ProtocolTestHelper.AssertU64Field(stack, pClient, "tcp.window_size", 12800).ConfigureAwait(false);
        await ProtocolTestHelper.AssertI64Field(stack, pClient, "tcp.window_size_scalefactor", 128).ConfigureAwait(false);

        byte[] serverData = _ServerFrame(2001, 1002, TcpFlags.PshAck, "y"u8, windowSize: 100);
        Packet pServer = ProtocolTestHelper.ParseFrame(stack, serverData, 3, Timestamp.FromMillis(3));
        await ProtocolTestHelper.AssertU64Field(stack, pServer, "tcp.window_size", 400).ConfigureAwait(false);
        await ProtocolTestHelper.AssertI64Field(stack, pServer, "tcp.window_size_scalefactor", 4).ConfigureAwait(false);
    }

    /// <summary>
    /// Without scaling in the handshake the factor is -2 and the window is shown unscaled; a connection whose SYN was not
    /// captured has the factor -1 ("unknown") and an unscaled window.
    /// </summary>
    [Test]
    public async Task WindowScaleFactorIsMinusTwoWithoutNegotiationAndMinusOneWithoutSyn()
    {
        using Stack negotiated = ProtocolTestHelper.BuildStack();
        _DoHandshake(negotiated);
        Packet pData = ProtocolTestHelper.ParseFrame(
            negotiated, _ClientFrame(1001, 2001, TcpFlags.PshAck, "x"u8, windowSize: 100), 3, Timestamp.FromMillis(20));
        await ProtocolTestHelper.AssertU64Field(negotiated, pData, "tcp.window_size", 100).ConfigureAwait(false);
        await ProtocolTestHelper.AssertI64Field(negotiated, pData, "tcp.window_size_scalefactor", -2).ConfigureAwait(false);

        using Stack midStream = ProtocolTestHelper.BuildStack();
        Packet pMid = ProtocolTestHelper.ParseFrame(
            midStream, _ClientFrame(5001, 7001, TcpFlags.PshAck, "x"u8, windowSize: 100), 0, Timestamp.FromMillis(0));
        await ProtocolTestHelper.AssertU64Field(midStream, pMid, "tcp.window_size", 100).ConfigureAwait(false);
        await ProtocolTestHelper.AssertI64Field(midStream, pMid, "tcp.window_size_scalefactor", -1).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_KeepAlive_IsNotRetransmission()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] keep = _ClientFrame(1000, 2001, TcpFlags.Ack, [0x00]);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, keep, 3, Timestamp.FromMillis(30));
        await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.analysis.keep_alive", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.analysis.retransmission").ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_ServerFin_IsFinWait1()
    {
        // The phase is the state of the side that closed FIRST (the active closer), whichever side that is. A server that
        // closes first is in FIN_WAIT_1; the peer's CLOSE_WAIT is not a state the connection reports.
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] fin = _ServerFrame(2001, 1001, TcpFlags.FinAck);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, fin, 3, Timestamp.FromMillis(30));
        await ProtocolTestHelper.AssertStringField(stack, packet, "tcp.analysis.connection_state", "FIN_WAIT_1").ConfigureAwait(false);
    }

    private static byte[] _FrameWithScale(bool isClient, byte shift, uint seq, uint ack, byte flags)
    {
        byte[] options = [0x03, 0x03, shift, 0x01];
        EthernetLayer eth = isClient ? new(_DstMac, _SrcMac) : new(_SrcMac, _DstMac);
        IPv4Layer ip = isClient ? new(_ClientIp, _ServerIp) : new(_ServerIp, _ClientIp);
        ushort src = isClient ? _ClientPort : _ServerPort;
        ushort dst = isClient ? _ServerPort : _ClientPort;
        TcpLayerWithOptions tcp = new(src, dst, options, seqNum: seq, ackNum: ack, flags: flags, windowSize: 100);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion

    #region Duplicate ACK counter

    [Test]
    public async Task DupAckCountSurvivesADataSegment()
    {
        // The receiver repeats the same hole. A data segment of its own with the unchanged ACK number in between must not
        // restart the count: only a moving acknowledgement number does.
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 3, Timestamp.FromMillis(20));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 4, Timestamp.FromMillis(21));
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1201, 2001, TcpFlags.PshAck, new byte[100]), 5, Timestamp.FromMillis(22));
        Packet first = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 6, Timestamp.FromMillis(23));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.PshAck, new byte[10]), 7, Timestamp.FromMillis(24));
        Packet second = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2011, 1101, TcpFlags.Ack), 8, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertU64Field(stack, first, "tcp.analysis.duplicate_ack_num", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, second, "tcp.analysis.duplicate_ack_num", 2).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, second, "tcp.analysis.duplicate_ack_frame", 5).ConfigureAwait(false);
    }

    [Test]
    public async Task DupAckCountRestartsWhenTheAckNumberMoves()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 3, Timestamp.FromMillis(20));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 4, Timestamp.FromMillis(21));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 5, Timestamp.FromMillis(22));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 6, Timestamp.FromMillis(23));
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1101, 2001, TcpFlags.PshAck, new byte[100]), 7, Timestamp.FromMillis(24));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1201, TcpFlags.Ack), 8, Timestamp.FromMillis(25));

        Packet afterMove = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1201, TcpFlags.Ack), 9, Timestamp.FromMillis(26));

        await ProtocolTestHelper.AssertU64Field(stack, afterMove, "tcp.analysis.duplicate_ack_num", 1).ConfigureAwait(false);
    }

    #endregion

    #region Retransmission families

    [Test]
    public async Task SpuriousRetransmissionIsAlsoARetransmission()
    {
        // Everything it carries was acknowledged, so it is spurious - and it still repeats data, so it is a retransmission.
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] segment = _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]);
        _ = ProtocolTestHelper.ParseFrame(stack, segment, 3, Timestamp.FromMillis(20));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 4, Timestamp.FromMillis(21));

        Packet repeat = ProtocolTestHelper.ParseFrame(stack, segment, 5, Timestamp.FromMillis(400));

        await ProtocolTestHelper.AssertFieldExists(stack, repeat, "tcp.analysis.spurious_retransmission").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, repeat, "tcp.analysis.retransmission").ConfigureAwait(false);
    }

    [Test]
    public async Task OneByteMoreThanAcknowledgedIsNotSpurious()
    {
        // The boundary of "spurious": a repeat that ends exactly at the acknowledgement is spurious, one that carries a single
        // new byte beyond it is not.
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 3, Timestamp.FromMillis(20));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 4, Timestamp.FromMillis(21));

        Packet exact = ProtocolTestHelper.ParseFrame(
            stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 5, Timestamp.FromMillis(400));
        Packet oneMore = ProtocolTestHelper.ParseFrame(
            stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[101]), 6, Timestamp.FromMillis(800));

        await ProtocolTestHelper.AssertFieldExists(stack, exact, "tcp.analysis.spurious_retransmission").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, oneMore, "tcp.analysis.spurious_retransmission").ConfigureAwait(false);
    }

    [Test]
    public async Task RetransmissionReportsTheTimeoutFromTheFirstTransmission()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] segment = _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]);
        _ = ProtocolTestHelper.ParseFrame(stack, segment, 3, Timestamp.FromMillis(100));

        Packet repeat = ProtocolTestHelper.ParseFrame(stack, segment, 4, Timestamp.FromMillis(300));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, repeat, "tcp.analysis.rto", 0.2, 1e-6).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, repeat, "tcp.analysis.rto_frame", 4).ConfigureAwait(false);
    }

    [Test]
    public async Task AckOfARetransmittedSegmentIsAmbiguousAndMeasuresFromTheFirstTransmission()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] segment = _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]);
        _ = ProtocolTestHelper.ParseFrame(stack, segment, 3, Timestamp.FromMillis(100));
        _ = ProtocolTestHelper.ParseFrame(stack, segment, 4, Timestamp.FromMillis(300));

        Packet ack = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1101, TcpFlags.Ack), 5, Timestamp.FromMillis(310));

        await ProtocolTestHelper.AssertU64Field(stack, ack, "tcp.analysis.acks_frame", 4).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, ack, "tcp.analysis.ambiguous_ack").ConfigureAwait(false);
        await ProtocolTestHelper.AssertF64FieldApprox(stack, ack, "tcp.analysis.ack_rtt", 0.21, 1e-6).ConfigureAwait(false);
    }

    #endregion

    #region Connection facts

    [Test]
    public async Task NxtSeqIsTheSequenceAfterTheSegment()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);
        Packet data = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 3, Timestamp.FromMillis(20));
        Packet fin = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1101, 2001, TcpFlags.FinAck), 4, Timestamp.FromMillis(30));

        // Relative numbers: SYN occupies one, so the SYN's next is 1; the data segment 1..101; the FIN adds one.
        await ProtocolTestHelper.AssertU64Field(stack, handshake[0], "tcp.nxtseq", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, data, "tcp.nxtseq", 101).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, fin, "tcp.nxtseq", 102).ConfigureAwait(false);
    }

    [Test]
    public async Task CompletenessShowsTheStateBeforeThePacket()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);
        Packet data = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[10]), 3, Timestamp.FromMillis(20));
        Packet next = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1011, TcpFlags.Ack), 4, Timestamp.FromMillis(21));

        // Each packet shows what the capture held BEFORE it: nothing, SYN, SYN+SYN-ACK, +ACK, +data.
        await ProtocolTestHelper.AssertU64Field(stack, handshake[0], "tcp.completeness", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, handshake[0], "tcp.completeness.str", "[ Null ]").ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, handshake[1], "tcp.completeness", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, handshake[2], "tcp.completeness", 3).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, data, "tcp.completeness", 7).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, next, "tcp.completeness", 15).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, next, "tcp.completeness.str", "··DASS").ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, next, "tcp.completeness.data", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, next, "tcp.completeness.fin", false).ConfigureAwait(false);
    }

    [Test]
    public async Task PacketInfoShowsPortsFlagsNumbersWindowAndLength()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        Packet data = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.PshAck, new byte[100]), 3, Timestamp.FromMillis(20));

        await Assert.That(data.Info).IsEqualTo("49152 → 80 [PSH, ACK] Seq=1 Ack=1 Win=65535 Len=100");
    }

    [Test]
    public async Task PacketInfoOmitsTheAckNumberWithoutTheAckFlag()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        Packet syn = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));

        await Assert.That(syn.Info).IsEqualTo("49152 → 80 [SYN] Seq=0 Win=65535 Len=0");
    }

    [Test]
    public async Task AnIdleConnectionAllocatesNoUnackedList()
    {
        // A connection that never carries data (a port scan) must stay small: the unacked list is created on the first data
        // segment, not with the connection. Measured as the allocation of the tracker state object itself.
        // The first construction pays for type loading and JIT, so it runs before the window. Up to five rounds are measured
        // and the smallest is kept: a one-off allocation on this thread inflates only its own round, while a real per-object
        // cost appears in every round and keeps the proof.
        TcpConnectionState state = new();
        long allocated = long.MaxValue;
        for (int round = 0; round < 5 && allocated >= 400; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            state = new();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        await Assert.That(state.Forward.Unacked).IsNull();
        await Assert.That(state.Reverse.Unacked).IsNull();
        await Assert.That(allocated).IsLessThan(400);
    }

    [Test]
    public async Task UnackedListIsBoundedAndTheConnectionKeepsWorking()
    {
        // A receiver that never acknowledges: the list stops growing at the documented bound; analysis goes on.
        TcpConnectionState connection = new();
        for (int index = 0; index < TcpFlowState.MaxUnacked + 50; index++)
        {
            TcpSegmentView segment = new(
                Seq: (uint)(1000 + (index * 10)), AckNumber: 1, PayloadLength: 10, Flags: TcpFlags.PshAck, Window: 65535,
                Nanos: index * 1_000_000L, PacketId: index, SrcPort: 1, DstPort: 2, WindowScaleOption: -1);
            _ = TcpConnectionTracker.Analyze(connection, isForward: true, in segment, reusedPorts: false, relativeSequenceNumbers: true);
        }

        await Assert.That(connection.Forward.UnackedCount).IsEqualTo(TcpFlowState.MaxUnacked);
        await Assert.That(connection.Forward.UnackedBytes).IsEqualTo((uint)(TcpFlowState.MaxUnacked * 10));
    }

    #endregion

}
