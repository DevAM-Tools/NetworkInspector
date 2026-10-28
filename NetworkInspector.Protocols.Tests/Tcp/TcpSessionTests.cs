// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
    private const ushort _ClientPort = 49152;
    private const ushort _ServerPort = 80;

    #endregion

    #region Helpers

    /// <summary>Builds a client → server TCP frame.</summary>
    private static byte[] _ClientFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535,
        ushort srcPort = _ClientPort,
        ushort dstPort = _ServerPort,
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
    private static byte[] _ServerFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535,
        ushort srcPort = _ServerPort,
        ushort dstPort = _ClientPort,
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
    private static Packet[] _DoHandshake(
        Stack stack,
        uint clientIsn = 1000,
        uint serverIsn = 2000,
        int startIndex = 0,
        long startTimeMs = 0,
        ushort srcPort = _ClientPort,
        ushort dstPort = _ServerPort,
        IPv4Address? clientIp = null,
        IPv4Address? serverIp = null)
    {
        byte[] syn = _ClientFrame(clientIsn, 0, TcpFlags.Syn, srcPort: srcPort, dstPort: dstPort,
            srcIp: clientIp, dstIp: serverIp);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, startIndex,
            Timestamp.FromMillis(startTimeMs));

        byte[] synAck = _ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck, srcPort: dstPort, dstPort: srcPort,
            srcIp: serverIp, dstIp: clientIp);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, startIndex + 1,
            Timestamp.FromMillis(startTimeMs + 10));

        byte[] ack = _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack, srcPort: srcPort, dstPort: dstPort,
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
        Packet[] handshake = _DoHandshake(stack);

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
        _DoHandshake(stack, startIndex: 0);

        // Second connection on port 443 (different 5-tuple)
        Packet[] conn2 = _DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 3, startTimeMs: 100, dstPort: 443);

        // Second connection should have stream index 1
        await ProtocolTestHelper.AssertU64Field(stack, conn2[0], "tcp.stream", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, conn2[1], "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task Stream_SameTuple_SameStreamIndex()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);

        // Additional data packets on same connection use same stream
        byte[] payload = new byte[50];
        byte[] data = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.stream", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task Stream_ReverseDirection_SameStreamIndex()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // Server → Client data uses same stream index
        byte[] payload = new byte[50];
        byte[] serverData = _ServerFrame(2001, 1001, TcpFlags.PshAck, payload);
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

        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
        ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        byte[] synAck = _ServerFrame(2000, 1001, TcpFlags.SynAck);
        Packet pSynAck = ProtocolTestHelper.ParseFrame(stack, synAck, 1, Timestamp.FromMillis(10));

        await ProtocolTestHelper.AssertStringField(stack, pSynAck, "tcp.analysis.connection_state", "SYN_RECEIVED").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_Established_AfterHandshake()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);

        await ProtocolTestHelper.AssertStringField(stack, handshake[2], "tcp.analysis.connection_state", "ESTABLISHED").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionState_Reset_AfterRst()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        // RST immediately resets the connection
        byte[] rst = _ClientFrame(1001, 2001, TcpFlags.Rst);
        Packet pRst = ProtocolTestHelper.ParseFrame(stack, rst, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertStringField(stack, pRst, "tcp.analysis.connection_state", "RESET").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionStateFullCloseSequence()
    {
        // The state is the one of the side that closed FIRST; the FIN is recorded once and acknowledged by the peer's ACK.
        // SYN, SYN-ACK, ACK, client FIN, client FIN again, server ACK, server FIN, client ACK.
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet[] handshake = _DoHandshake(stack);
        Packet clientFin = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.FinAck), 3, Timestamp.FromMillis(20));
        Packet clientFinAgain = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.FinAck), 4, Timestamp.FromMillis(220));
        Packet serverAck = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1002, TcpFlags.Ack), 5, Timestamp.FromMillis(225));
        Packet serverFin = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1002, TcpFlags.FinAck), 6, Timestamp.FromMillis(230));
        Packet clientAck = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1002, 2002, TcpFlags.Ack), 7, Timestamp.FromMillis(235));

        const string field = "tcp.analysis.connection_state";
        await ProtocolTestHelper.AssertStringField(stack, handshake[0], field, "SYN_SENT").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, handshake[1], field, "SYN_RECEIVED").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, handshake[2], field, "ESTABLISHED").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, clientFin, field, "FIN_WAIT_1").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, clientFinAgain, field, "FIN_WAIT_1").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, serverAck, field, "FIN_WAIT_2").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, serverFin, field, "LAST_ACK").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, clientAck, field, "TIME_WAIT").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionStateRetransmittedFinStaysFinWait1()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.FinAck), 3, Timestamp.FromMillis(20));

        Packet again = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.FinAck), 4, Timestamp.FromMillis(220));

        // A retransmitted FIN is not the peer's FIN (CLOSING) and not an acknowledgement.
        await ProtocolTestHelper.AssertStringField(stack, again, "tcp.analysis.connection_state", "FIN_WAIT_1").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionStateSimultaneousCloseIsClosingUntilBothFinsAreAcknowledged()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        Packet clientFin = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.FinAck), 3, Timestamp.FromMillis(20));
        Packet serverFin = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1001, TcpFlags.FinAck), 4, Timestamp.FromMillis(21));
        Packet clientAck = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1002, 2002, TcpFlags.Ack), 5, Timestamp.FromMillis(22));
        Packet serverAck = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2002, 1002, TcpFlags.Ack), 6, Timestamp.FromMillis(23));

        const string field = "tcp.analysis.connection_state";
        await ProtocolTestHelper.AssertStringField(stack, clientFin, field, "FIN_WAIT_1").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, serverFin, field, "CLOSING").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, clientAck, field, "CLOSING").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, serverAck, field, "TIME_WAIT").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionStateMidStreamIsEstablished()
    {
        // A capture that joins a connection has no SYN: the connection carries data, so it is not CLOSED.
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet data = ProtocolTestHelper.ParseFrame(
            stack, _ClientFrame(5001, 7001, TcpFlags.PshAck, "x"u8), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(
            stack, data, "tcp.analysis.connection_state", "ESTABLISHED (handshake not captured)").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionStateResetStaysResetUntilANewConnection()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1001, 2001, TcpFlags.Rst), 3, Timestamp.FromMillis(20));

        Packet after = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(2001, 1001, TcpFlags.Ack), 4, Timestamp.FromMillis(21));

        await ProtocolTestHelper.AssertStringField(stack, after, "tcp.analysis.connection_state", "RESET").ConfigureAwait(false);
    }

    #endregion

    #region Connection Isolation

    [Test]
    public async Task Isolation_TwoConnections_IndependentStreamIndexes()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection 1: port 80
        Packet[] conn1 = _DoHandshake(stack, startIndex: 0, dstPort: 80);

        // Connection 2: port 443 (same IPs, different port)
        Packet[] conn2 = _DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 3, startTimeMs: 50, dstPort: 443);

        await ProtocolTestHelper.AssertU64Field(stack, conn1[0], "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, conn2[0], "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task Isolation_TwoConnections_AnalysisNotLeaked()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection 1: handshake + data
        _DoHandshake(stack, startIndex: 0, dstPort: 80);
        byte[] payload = new byte[100];
        byte[] data1 = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload, dstPort: 80);
        ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        // Connection 2: separate handshake should not show retransmission
        Packet[] conn2 = _DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
            startIndex: 4, startTimeMs: 50, dstPort: 443);

        // SYN of connection 2 should not be flagged as retransmission
        await ProtocolTestHelper.AssertFieldNotPresent(stack, conn2[0], "tcp.analysis.retransmission").ConfigureAwait(false);
    }

    [Test]
    public async Task Isolation_DifferentSourcePorts_DifferentStreams()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection from port 49152 → 80
        Packet[] conn1 = _DoHandshake(stack, startIndex: 0, srcPort: 49152, dstPort: 80);

        // Connection from port 49153 → 80 (different source port)
        Packet[] conn2 = _DoHandshake(stack, clientIsn: 5000, serverIsn: 6000,
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
        byte[] syn = _ClientFrame(1000, 0, TcpFlags.Syn);
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

        byte[] syn = _ClientFrame(123456789, 0, TcpFlags.Syn);
        Packet pSyn = ProtocolTestHelper.ParseFrame(stack, syn, 0, Timestamp.FromMillis(0));

        // tcp.seq_raw should always contain the absolute wire value
        await ProtocolTestHelper.AssertU64Field(stack, pSyn, "tcp.seq_raw", 123456789).ConfigureAwait(false);
    }

    [Test]
    public async Task AckRaw_AlwaysAbsolute()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] synAck = _ServerFrame(2000, 1001, TcpFlags.SynAck);
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
        _DoHandshake(stack);

        byte[] payload = new byte[200];
        byte[] data = _ClientFrame(1001, 2001, TcpFlags.PshAck, payload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        // tcp.len = payload length (200)
        await ProtocolTestHelper.AssertU64Field(stack, pData, "tcp.len", 200).ConfigureAwait(false);
    }

    [Test]
    public async Task TcpLen_ZeroForPureAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);

        byte[] ack = _ClientFrame(1001, 2001, TcpFlags.Ack);
        Packet pAck = ProtocolTestHelper.ParseFrame(stack, ack, 3, Timestamp.FromMillis(20));

        // tcp.len = 0 (no payload)
        await ProtocolTestHelper.AssertU64Field(stack, pAck, "tcp.len", 0).ConfigureAwait(false);
    }

    #endregion

    #region Stream key

    [Test]
    public async Task StreamKey_VlanOff_SameFiveTuple_OneStream()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet first = ProtocolTestHelper.ParseFrame(stack, _VlanFrame(10), 0, Timestamp.FromMillis(0));
        Packet second = ProtocolTestHelper.ParseFrame(stack, _VlanFrame(20), 1, Timestamp.FromMillis(1));
        await ProtocolTestHelper.AssertU64Field(stack, first, "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, second, "tcp.stream", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task StreamKey_LastVlanOn_SplitsInnerVlan()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.stream_key_last_vlan", SettingValue.Bool(true)));
        Packet first = ProtocolTestHelper.ParseFrame(stack, _VlanFrame(10), 0, Timestamp.FromMillis(0));
        Packet second = ProtocolTestHelper.ParseFrame(stack, _VlanFrame(20), 1, Timestamp.FromMillis(1));
        await ProtocolTestHelper.AssertU64Field(stack, first, "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, second, "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task StreamKey_VlanZero_DoesNotSplitFromUntagged()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.stream_key_last_vlan", SettingValue.Bool(true)));
        Packet tagged = ProtocolTestHelper.ParseFrame(stack, _VlanFrame(0), 0, Timestamp.FromMillis(0));
        Packet plain = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 1, Timestamp.FromMillis(1));
        await ProtocolTestHelper.AssertU64Field(stack, tagged, "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, plain, "tcp.stream", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task StreamKey_FrameInterfaceOff_OneStream()
    {
        (Stack stack, FrameInterfaceId first, FrameInterfaceId second) = _StackWithTwoInterfaces(includeFrame: false);
        using (stack)
        {
            Packet a = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0), interfaceId: first);
            Packet b = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 1, Timestamp.FromMillis(1), interfaceId: second);
            await ProtocolTestHelper.AssertU64Field(stack, a, "tcp.stream", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, b, "tcp.stream", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task StreamKey_FrameInterfaceOn_SplitsInterfaces()
    {
        (Stack stack, FrameInterfaceId first, FrameInterfaceId second) = _StackWithTwoInterfaces(includeFrame: true);
        using (stack)
        {
            Packet a = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0), interfaceId: first);
            Packet b = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 1, Timestamp.FromMillis(1), interfaceId: second);
            await ProtocolTestHelper.AssertU64Field(stack, a, "tcp.stream", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, b, "tcp.stream", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task StreamLimit_SecondFiveTuple_ReportsFull()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.max_tracked_streams", SettingValue.U64(1)));
        Packet first = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(1000, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
        byte[] other = _ClientFrame(1000, 0, TcpFlags.Syn, srcPort: 49153);
        Packet second = ProtocolTestHelper.ParseFrame(stack, other, 1, Timestamp.FromMillis(1));
        await ProtocolTestHelper.AssertU64Field(stack, first, "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, second, "tcp.stream").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, second, "tcp.error.stream_limit").ConfigureAwait(false);
    }

    [Test]
    public async Task RelativeSeq_DataAfterSyn_IsOffsetFromIsn()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        _DoHandshake(stack);
        byte[] data = _ClientFrame(1001, 2001, TcpFlags.PshAck, "x"u8);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));
        await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.seq", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.seq_raw", 1001).ConfigureAwait(false);
    }

    private static byte[] _VlanFrame(ushort vlanId)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        VlanLayer vlan = new(vlanId);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(_ClientPort, _ServerPort, seqNum: 1000, flags: TcpFlags.Syn);
        return FrameStack.Start(eth).Then(vlan).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static (Stack Stack, FrameInterfaceId First, FrameInterfaceId Second) _StackWithTwoInterfaces(bool includeFrame)
    {
        Stack stack = includeFrame
            ? ProtocolTestHelper.BuildStackWithSettings(("tcp.stream_key_frame", SettingValue.Bool(true)))
            : ProtocolTestHelper.BuildStack();
        FrameSourceId source = stack.FrameInterfaceRegistry.RegisterSource(new _TcpTestSource());
        FrameInterfaceId first = stack.FrameInterfaceRegistry.Register(source, "eth0");
        FrameInterfaceId second = stack.FrameInterfaceRegistry.Register(source, "eth1");
        return (stack, first, second);
    }

    private sealed class _TcpTestSource : IFrameSource
    {
        public string UiName => "test";
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Stop() => _ = UiName;
        public void Dispose() { }
    }

    #endregion
}
