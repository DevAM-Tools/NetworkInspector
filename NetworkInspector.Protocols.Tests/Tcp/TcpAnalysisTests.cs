// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
    private const ushort ClientPort = 49152;
    private const ushort ServerPort = 80;

    #endregion

    #region Helpers

    /// <summary>Builds an Ethernet/IPv4/TCP frame for client → server direction.</summary>
    private static byte[] ClientFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ipLayer = new(_ClientIp, _ServerIp);
        TcpLayer tcpLayer = new(ClientPort, ServerPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds an Ethernet/IPv4/TCP frame for server → client direction.</summary>
    private static byte[] ServerFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ipLayer = new(_ServerIp, _ClientIp);
        TcpLayer tcpLayer = new(ServerPort, ClientPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Builds a standard 3-way handshake (SYN, SYN-ACK, ACK) and returns the packets.
    /// Client ISN = <paramref name="clientIsn"/>, Server ISN = <paramref name="serverIsn"/>.
    /// </summary>
    private static Packet[] DoHandshake(
        Stack stack,
        uint clientIsn = 1000,
        uint serverIsn = 2000,
        int startIndex = 0,
        long startTimeMs = 0)
    {
        // SYN: Client → Server
        byte[] syn = ClientFrame(clientIsn, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, startIndex,
            Timestamp.FromMillis(startTimeMs));

        // SYN-ACK: Server → Client
        byte[] synAck = ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, startIndex + 1,
            Timestamp.FromMillis(startTimeMs + 10));

        // ACK: Client → Server (completes handshake)
        byte[] ack = ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack);
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
        Packet[] handshake = DoHandshake(stack);

        // Client sends data
        byte[] data = new byte[100];
        byte[] dataFrame = ClientFrame(1001, 2001, TcpFlags.PshAck, data);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, dataFrame, 3, Timestamp.FromMillis(20));

        // Server ACKs all data
        byte[] ackFrame = ServerFrame(2001, 1101, TcpFlags.Ack);
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
        DoHandshake(stack);

        // Client sends 100 bytes at seq=1001
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client retransmits the same segment (same seq, same data)
        byte[] data2 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pRetransmit = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(220));

        await ProtocolTestHelper.AssertBoolField(stack, pRetransmit, "tcp.analysis.retransmission", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_FastRetransmission_After3DupAcks()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends 100 bytes at seq=1001
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends another 100 bytes at seq=1101
        byte[] data2 = ClientFrame(1101, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        // Server sends 3 duplicate ACKs (ack=1001, acknowledging nothing new)
        for (int i = 0; i < 3; i++)
        {
            byte[] dupAck = ServerFrame(2001, 1001, TcpFlags.Ack);
            ProtocolTestHelper.ParseFrame(stack, dupAck, 5 + i, Timestamp.FromMillis(30 + i * 5));
        }

        // Client retransmits after 3 dup ACKs → fast retransmission
        byte[] retransmit = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pFastRetx = ProtocolTestHelper.ParseFrame(stack, retransmit, 8, Timestamp.FromMillis(50));

        await ProtocolTestHelper.AssertBoolField(stack, pFastRetx, "tcp.analysis.fast_retransmission", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_SpuriousRetransmission_AlreadyAcked()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends data (seq=1001, 100B → endSeq=1101)
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends more data (seq=1101, 50B → endSeq=1151)
        byte[] payload2 = new byte[50];
        byte[] data2 = ClientFrame(1101, 2001, TcpFlags.PshAck, payload2);
        ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(22));

        // Server ACKs all data (ack=1151, strictly after endSeq=1101 of the first segment)
        byte[] ackFrame = ServerFrame(2001, 1151, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ackFrame, 5, Timestamp.FromMillis(25));

        // Client retransmits the first segment (endSeq=1101) — already fully ACKed
        // since reverseFlow.LastAck(1151) > endSeq(1101) → spurious retransmission
        byte[] spurious = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pSpurious = ProtocolTestHelper.ParseFrame(stack, spurious, 6, Timestamp.FromMillis(225));

        await ProtocolTestHelper.AssertBoolField(stack, pSpurious, "tcp.analysis.spurious_retransmission", true).ConfigureAwait(false);
    }

    #endregion

    #region Out-of-Order Detection

    [Test]
    public async Task Analysis_OutOfOrder_DetectedWhenSegmentArrivesBefore()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends seq=1001, len=100 → NextSeq=1101
        byte[] payload100 = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends seq=1201, len=100 (skips 1101-1200) → gap
        // endSeq=1301 > NextSeq=1101 AND seqNum=1201 != NextSeq=1101 → out-of-order
        byte[] data2 = ClientFrame(1201, 2001, TcpFlags.PshAck, payload100);
        Packet pOoo = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pOoo, "tcp.analysis.out_of_order", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_NoOutOfOrder_WhenInSequence()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Sequential data: 1001, then 1101 — no out-of-order
        byte[] payload100 = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        byte[] data2 = ClientFrame(1101, 2001, TcpFlags.PshAck, payload100);
        Packet pSeq = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, pSeq, "tcp.analysis.out_of_order").ConfigureAwait(false);
    }

    #endregion

    #region Duplicate ACK Detection

    [Test]
    public async Task Analysis_DuplicateAck_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends data
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs partially (ack=1001 — doesn't advance)
        byte[] ack1 = ServerFrame(2001, 1001, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ack1, 4, Timestamp.FromMillis(25));

        // Server sends another ACK with same ack=1001 → dup ACK
        byte[] ack2 = ServerFrame(2001, 1001, TcpFlags.Ack);
        Packet pDupAck = ProtocolTestHelper.ParseFrame(stack, ack2, 5, Timestamp.FromMillis(30));

        await ProtocolTestHelper.AssertBoolField(stack, pDupAck, "tcp.analysis.duplicate_ack", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_DuplicateAckNum_Increments()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends data
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // 3 consecutive dup ACKs — counter should increment
        Packet[] dupAcks = new Packet[3];
        for (int i = 0; i < 3; i++)
        {
            byte[] dup = ServerFrame(2001, 1001, TcpFlags.Ack);
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
        DoHandshake(stack);

        // Client sends seq=1001, len=100 → next expected = 1101
        byte[] payload100 = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload100);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Client sends seq=1301 (gap: 1101-1300 missing) → lost segment
        byte[] data2 = ClientFrame(1301, 2001, TcpFlags.PshAck, payload100);
        Packet pLost = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pLost, "tcp.analysis.lost_segment", true).ConfigureAwait(false);
    }

    #endregion

    #region Keep-Alive Detection

    [Test]
    public async Task Analysis_KeepAlive_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends data so we have a baseline next_seq
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs
        byte[] ackFrame = ServerFrame(2001, 1101, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, ackFrame, 4, Timestamp.FromMillis(25));

        // Client sends keep-alive: seq = next_seq - 1 = 1100, len <= 1, no SYN/FIN/RST
        byte[] keepAlive = ClientFrame(1100, 2001, TcpFlags.Ack, [0x00]);
        Packet pKa = ProtocolTestHelper.ParseFrame(stack, keepAlive, 5, Timestamp.FromMillis(5025));

        await ProtocolTestHelper.AssertBoolField(stack, pKa, "tcp.analysis.keep_alive", true).ConfigureAwait(false);
    }

    #endregion

    #region Zero Window

    [Test]
    public async Task Analysis_ZeroWindow_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Server advertises window=0 in a pure ACK
        byte[] zeroWin = ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 0);
        Packet pZeroWin = ProtocolTestHelper.ParseFrame(stack, zeroWin, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertBoolField(stack, pZeroWin, "tcp.analysis.zero_window", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_ZeroWindowProbe_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Server advertises window=0
        byte[] zeroWin = ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 0);
        ProtocolTestHelper.ParseFrame(stack, zeroWin, 3, Timestamp.FromMillis(20));

        // Client sends data despite window=0 → zero window probe
        byte[] probe = ClientFrame(1001, 2001, TcpFlags.Ack, [0x00]);
        Packet pProbe = ProtocolTestHelper.ParseFrame(stack, probe, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pProbe, "tcp.analysis.zero_window_probe", true).ConfigureAwait(false);
    }

    #endregion

    #region Window Analysis

    [Test]
    public async Task Analysis_WindowUpdate_DetectedOnWindowChange()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Server ACKs with window=1000
        byte[] ack1 = ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 1000);
        ProtocolTestHelper.ParseFrame(stack, ack1, 3, Timestamp.FromMillis(20));

        // Server sends another ACK with same ack but larger window → window update
        byte[] ack2 = ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 32000);
        Packet pWinUpdate = ProtocolTestHelper.ParseFrame(stack, ack2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pWinUpdate, "tcp.analysis.window_update", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_BytesInFlight_Calculated()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends 100 bytes
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Bytes in flight = next_seq(1101) - reverse.last_ack(1001) = 100
        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.analysis.bytes_in_flight", 100).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_WindowFull_DetectedWhenBytesExceedWindow()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Server has small window (100 bytes)
        byte[] smallWin = ServerFrame(2001, 1001, TcpFlags.Ack, windowSize: 100);
        ProtocolTestHelper.ParseFrame(stack, smallWin, 3, Timestamp.FromMillis(20));

        // Client sends exactly 100 bytes → window full
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pFull = ProtocolTestHelper.ParseFrame(stack, data1, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertBoolField(stack, pFull, "tcp.analysis.window_full", true).ConfigureAwait(false);
    }

    #endregion

    #region RTT Measurements

    [Test]
    public async Task Analysis_InitialRtt_MeasuredFromSynToSynAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // SYN at t=0
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // SYN-ACK at t=10ms → iRTT should be ~0.010s
        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pSynAck, "tcp.analysis.initial_rtt", 0.010, 0.001).ConfigureAwait(false);
    }

    [Test]
    public async Task Analysis_AckRtt_MeasuredFromDataToAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends data at t=20ms
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Server ACKs at t=35ms → ACK RTT should be ~0.015s
        byte[] ackFrame = ServerFrame(2001, 1101, TcpFlags.Ack);
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
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(100));

        await ProtocolTestHelper.AssertF64Field(stack, pSyn, "tcp.time_relative", 0.0).ConfigureAwait(false);
    }

    [Test]
    public async Task Timing_TimeRelative_CorrectForSubsequentPackets()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First packet at t=100ms
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(100));

        // Second packet at t=200ms → time_relative = 0.100s
        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(200));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pSynAck, "tcp.time_relative", 0.100, 0.001).ConfigureAwait(false);
    }

    [Test]
    public async Task Timing_TimeDelta_BetweenConsecutivePackets()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First packet at t=0
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // Second at t=10ms
        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck);
        ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));

        // Third at t=50ms → delta from second = 0.040s
        byte[] ack = ClientFrame(1001, 2001, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, 2, Timestamp.FromMillis(50));

        await ProtocolTestHelper.AssertF64FieldApprox(stack, pAck, "tcp.time_delta", 0.040, 0.001).ConfigureAwait(false);
    }

    #endregion
}
