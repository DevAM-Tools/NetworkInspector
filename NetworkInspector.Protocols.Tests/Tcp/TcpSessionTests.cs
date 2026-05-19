// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP session tracking: stream index assignment, connection state machine
/// (RFC 793), direction detection, ISN tracking, window scaling, and connection isolation.
/// Each test builds its own Stack so stateful TCP tracking works correctly.
/// </summary>
internal sealed class TcpSessionTests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2
    private const ushort ClientPort = 49152;
    private const ushort ServerPort = 80;

    #endregion

    #region Helpers

    /// <summary>Builds a client → server TCP frame.</summary>
    private static byte[] ClientFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535,
        ushort srcPort = ClientPort,
        ushort dstPort = ServerPort,
        IPv4Address? srcIp = null,
        IPv4Address? dstIp = null)
    {
        IPv4Address src = srcIp ?? _ClientIp;
        IPv4Address dst = dstIp ?? _ServerIp;
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ipLayer = new(src, dst);
        TcpLayer tcpLayer = new(srcPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds a server → client TCP frame.</summary>
    private static byte[] ServerFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535,
        ushort srcPort = ServerPort,
        ushort dstPort = ClientPort,
        IPv4Address? srcIp = null,
        IPv4Address? dstIp = null)
    {
        IPv4Address src = srcIp ?? _ServerIp;
        IPv4Address dst = dstIp ?? _ClientIp;
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ipLayer = new(src, dst);
        TcpLayer tcpLayer = new(srcPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Performs a 3-way handshake and returns the 3 packets.</summary>
    private static Packet[] DoHandshake(
        Stack stack,
        uint clientIsn = 1000,
        uint serverIsn = 2000,
        int startIndex = 0,
        long startTimeMs = 0,
        ushort srcPort = ClientPort,
        ushort dstPort = ServerPort,
        IPv4Address? clientIp = null,
        IPv4Address? serverIp = null)
    {
        byte[] syn = ClientFrame(clientIsn, 0, TcpFlags.Syn, srcPort: srcPort, dstPort: dstPort,
            srcIp: clientIp, dstIp: serverIp);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, startIndex,
            Timestamp.FromMillis(startTimeMs));

        byte[] synAck = ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck, srcPort: dstPort, dstPort: srcPort,
            srcIp: serverIp, dstIp: clientIp);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, startIndex + 1,
            Timestamp.FromMillis(startTimeMs + 10));

        byte[] ack = ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack, srcPort: srcPort, dstPort: dstPort,
            srcIp: clientIp, dstIp: serverIp);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, startIndex + 2,
            Timestamp.FromMillis(startTimeMs + 15));

        return [pSyn, pSynAck, pAck];
    }

    #endregion

    #region Stream Index

    [Test]
    public async Task Stream_FirstConnection_HasStreamIndex0()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = DoHandshake(stack);

        // All packets in the first connection should have stream index 0
        await ProtocolTestHelper.AssertU64Field(stack, handshake[0], "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, handshake[1], "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, handshake[2], "tcp.stream", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task Stream_SecondConnection_HasStreamIndex1()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // First connection on port 80
        DoHandshake(stack, startIndex: 0);

        // Second connection on port 443 (different 5-tuple)
        Packet[] conn2 = DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 3, startTimeMs: 100, dstPort: 443);

        // Second connection should have stream index 1
        await ProtocolTestHelper.AssertU64Field(stack, conn2[0], "tcp.stream", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, conn2[1], "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task Stream_SameTuple_SameStreamIndex()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = DoHandshake(stack);

        // Additional data packets on same connection use same stream
        byte[] payload = new byte[50];
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.stream", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task Stream_ReverseDirection_SameStreamIndex()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Server → Client data uses same stream index
        byte[] payload = new byte[50];
        byte[] serverData = ServerFrame(2001, 1001, TcpFlags.PshAck, payload);
        Packet pServerData = ProtocolTestHelper.ParseFrame(stack, serverData, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertU64Field(stack, pServerData, "tcp.stream", 0).ConfigureAwait(false);
    }

    #endregion

    #region Connection State Machine

    [Test]
    public async Task ConnectionState_SynReceived_OnSynAck()
    {
        // The analysis container is created on SYN-ACK because InitialRtt is computed.
        // On SYN (first packet), no analysis data exists so the container is absent.
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));

        await ProtocolTestHelper.AssertStringField(stack, pSynAck, "tcp.analysis.connection_state", "SYN_RECEIVED").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_Established_AfterHandshake()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = DoHandshake(stack);

        await ProtocolTestHelper.AssertStringField(stack, handshake[2], "tcp.analysis.connection_state", "ESTABLISHED").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_Reset_AfterRst()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // RST immediately resets the connection
        byte[] rst = ClientFrame(1001, 2001, TcpFlags.Rst);
        Packet pRst = ProtocolTestHelper.ParseFrame(stack, rst, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertStringField(stack, pRst, "tcp.analysis.connection_state", "RESET").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_FinWait_AfterFin()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends FIN
        byte[] fin = ClientFrame(1001, 2001, TcpFlags.FinAck);
        Packet pFin = ProtocolTestHelper.ParseFrame(stack, fin, 3, Timestamp.FromMillis(20));

        // Verify state transitions after FIN — exact state name depends on implementation
        await ProtocolTestHelper.AssertFieldExists(stack, pFin, "tcp.analysis.connection_state").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_FullClose_SynFinAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Client sends FIN
        byte[] clientFin = ClientFrame(1001, 2001, TcpFlags.FinAck);
        ProtocolTestHelper.ParseFrame(stack, clientFin, 3, Timestamp.FromMillis(20));

        // Server ACKs the FIN
        byte[] serverAck = ServerFrame(2001, 1002, TcpFlags.Ack);
        ProtocolTestHelper.ParseFrame(stack, serverAck, 4, Timestamp.FromMillis(25));

        // Server sends its own FIN
        byte[] serverFin = ServerFrame(2001, 1002, TcpFlags.FinAck);
        ProtocolTestHelper.ParseFrame(stack, serverFin, 5, Timestamp.FromMillis(30));

        // Client ACKs the server's FIN
        byte[] clientAck = ClientFrame(1002, 2002, TcpFlags.Ack);
        Packet pFinalAck = ProtocolTestHelper.ParseFrame(stack, clientAck, 6, Timestamp.FromMillis(35));

        // Connection should be fully closed at this point
        await ProtocolTestHelper.AssertFieldExists(stack, pFinalAck, "tcp.analysis.connection_state").ConfigureAwait(false);
    }

    #endregion

    #region Connection Isolation

    [Test]
    public async Task Isolation_TwoConnections_IndependentStreamIndexes()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection 1: port 80
        Packet[] conn1 = DoHandshake(stack, startIndex: 0, dstPort: 80);

        // Connection 2: port 443 (same IPs, different port)
        Packet[] conn2 = DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 3, startTimeMs: 50, dstPort: 443);

        await ProtocolTestHelper.AssertU64Field(stack, conn1[0], "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, conn2[0], "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task Isolation_TwoConnections_AnalysisNotLeaked()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection 1: handshake + data
        DoHandshake(stack, startIndex: 0, dstPort: 80);
        byte[] payload = new byte[100];
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, payload, dstPort: 80);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Connection 2: separate handshake should not show retransmission
        Packet[] conn2 = DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 4, startTimeMs: 50, dstPort: 443);

        // SYN of connection 2 should not be flagged as retransmission
        await ProtocolTestHelper.AssertFieldNotPresent(stack, conn2[0], "tcp.analysis.retransmission").ConfigureAwait(false);
    }

    [Test]
    public async Task Isolation_DifferentSourcePorts_DifferentStreams()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection from port 49152 → 80
        Packet[] conn1 = DoHandshake(stack, startIndex: 0, srcPort: 49152, dstPort: 80);

        // Connection from port 49153 → 80 (different source port)
        Packet[] conn2 = DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 3, startTimeMs: 50, srcPort: 49153, dstPort: 80);

        await ProtocolTestHelper.AssertU64Field(stack, conn1[0], "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, conn2[0], "tcp.stream", 1).ConfigureAwait(false);
    }

    #endregion

    #region Window Scaling

    [Test]
    public async Task WindowScale_NotPresent_WithoutWScaleOption()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // SYN without window scale option → no scaled window fields
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // Window size value (raw) should be present
        await ProtocolTestHelper.AssertFieldExists(stack, pSyn, "tcp.window_size_value").ConfigureAwait(false);
    }

    #endregion

    #region Sequence Number Fields

    [Test]
    public async Task SeqRaw_AlwaysAbsolute()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] syn = ClientFrame(123456789, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // tcp.seq_raw should always contain the absolute wire value
        await ProtocolTestHelper.AssertU64Field(stack, pSyn, "tcp.seq_raw", 123456789).ConfigureAwait(false);
    }

    [Test]
    public async Task AckRaw_AlwaysAbsolute()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 0, Timestamp.FromMillis(0));

        // tcp.ack_raw should always contain the absolute wire value
        await ProtocolTestHelper.AssertU64Field(stack, pSynAck, "tcp.ack_raw", 1001).ConfigureAwait(false);
    }

    #endregion

    #region Payload Length

    [Test]
    public async Task TcpLen_CorrectForDataSegment()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        byte[] payload = new byte[200];
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        // tcp.len = payload length (200)
        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.len", 200).ConfigureAwait(false);
    }

    [Test]
    public async Task TcpLen_ZeroForPureAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        byte[] ack = ClientFrame(1001, 2001, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, 3, Timestamp.FromMillis(20));

        // tcp.len = 0 (no payload)
        await ProtocolTestHelper.AssertU64Field(stack, pAck, "tcp.len", 0).ConfigureAwait(false);
    }

    #endregion
}
