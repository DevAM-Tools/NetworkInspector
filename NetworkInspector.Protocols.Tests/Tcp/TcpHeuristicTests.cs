// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP heuristic protocol detection: HTTP/1.x, TLS, HTTP/2.
/// Verifies that application-layer protocols are correctly identified from payload
/// when port-based dispatch does not match (non-standard ports).
/// Also verifies per-connection heuristic caching.
/// </summary>
internal sealed class TcpHeuristicTests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    // Non-standard port to force heuristic detection (not registered in port table)
    private const ushort ClientPort = 49152;
    private const ushort NonStandardPort = 12345;

    #endregion

    #region Helpers

    /// <summary>Builds a client → server TCP frame on a non-standard port.</summary>
    private static byte[] ClientFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort dstPort = NonStandardPort)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(ClientPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds a server → client TCP frame on a non-standard port.</summary>
    private static byte[] ServerFrame(
        uint seqNum,
        uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default,
        ushort srcPort = NonStandardPort)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(srcPort, ClientPort, seqNum: seqNum, ackNum: ackNum, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds a 3-way handshake.</summary>
    private static void DoHandshake(Stack stack, int startIndex = 0, ushort dstPort = NonStandardPort)
    {
        byte[] syn = ClientFrame(1000, 0, TcpFlags.Syn, dstPort: dstPort);
        ProtocolTestHelper.ParseFrame(stack, syn, startIndex, Timestamp.FromMillis(0));

        byte[] synAck = ServerFrame(2000, 1001, TcpFlags.SynAck, srcPort: dstPort);
        ProtocolTestHelper.ParseFrame(stack, synAck, startIndex + 1, Timestamp.FromMillis(10));

        byte[] ack = ClientFrame(1001, 2001, TcpFlags.Ack, dstPort: dstPort);
        ProtocolTestHelper.ParseFrame(stack, ack, startIndex + 2, Timestamp.FromMillis(15));
    }

    #endregion

    #region HTTP/1.x Heuristic

    [Test]
    public async Task Heuristic_Http_GetRequest_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Send HTTP GET request on non-standard port
        byte[] httpPayload = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, httpPayload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        // HTTP protocol should be detected via heuristic
        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "http").ConfigureAwait(false);
    }

    [Test]
    public async Task Heuristic_Http_PostRequest_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        byte[] httpPayload = "POST /api/data HTTP/1.1\r\nHost: example.com\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, httpPayload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "http").ConfigureAwait(false);
    }

    [Test]
    public async Task Heuristic_Http_Response_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        byte[] httpPayload = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        byte[] data = ServerFrame(2001, 1001, TcpFlags.PshAck, httpPayload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "http").ConfigureAwait(false);
    }

    [Test]
    public async Task Heuristic_Http_NotMatched_ForBinaryData()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Random binary data should not trigger HTTP heuristic
        byte[] binaryPayload = [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0xFD, 0xFC, 0x10, 0x20];
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, binaryPayload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolNotPresent(stack, pData, "http").ConfigureAwait(false);
    }

    #endregion

    #region TLS Heuristic

    [Test]
    public async Task Heuristic_Tls_ClientHello_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // TLS Client Hello: ContentType=22 (Handshake), Version=TLS 1.0, Length=5
        byte[] tlsPayload =
        [
            0x16,       // ContentType: Handshake (22)
            0x03, 0x01, // Version: TLS 1.0
            0x00, 0x05, // Length: 5
            0x01,       // HandshakeType: Client Hello (1)
            0x00, 0x00, 0x01, // HandshakeLength: 1
            0x00        // Minimal content
        ];
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, tlsPayload);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        // TLS should be detected via heuristic on non-standard port
        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "tls").ConfigureAwait(false);
    }

    [Test]
    public async Task Heuristic_Tls_InvalidVersion_NotMatched()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // Invalid TLS version (major != 3)
        byte[] invalidTls =
        [
            0x16,       // ContentType: Handshake
            0x04, 0x00, // Version: Invalid (major=4)
            0x00, 0x05, // Length
            0x01, 0x00, 0x00, 0x01, 0x00
        ];
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, invalidTls);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolNotPresent(stack, pData, "tls").ConfigureAwait(false);
    }

    #endregion

    #region HTTP/2 Heuristic

    [Test]
    public async Task Heuristic_Http2_ConnectionPreface_Detected()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // HTTP/2 connection preface
        byte[] preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();
        byte[] data = ClientFrame(1001, 2001, TcpFlags.PshAck, preface);
        Packet pData = ProtocolTestHelper.ParseFrame(stack, data, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "http2").ConfigureAwait(false);
    }

    #endregion

    #region Port-Based vs Heuristic

    [Test]
    public async Task PortBased_Http_OnPort8080_NoHeuristicNeeded()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection on registered HTTP port 8080
        DoHandshake(stack, dstPort: 8080);

        byte[] httpPayload = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();

        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(ClientPort, 8080, seqNum: 1001, ackNum: 2001, flags: TcpFlags.PshAck);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        byte[] buffer = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpPayload);

        Packet pData = ProtocolTestHelper.ParseFrame(stack, buffer, 3, Timestamp.FromMillis(20));

        // HTTP should be found via port-based dispatch
        await ProtocolTestHelper.AssertProtocolPresent(stack, pData, "http").ConfigureAwait(false);
    }

    #endregion

    #region Heuristic Caching

    [Test]
    public async Task HeuristicCache_SecondPacket_SameConnection_Uses_CachedProtocol()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        DoHandshake(stack);

        // First data packet triggers heuristic → HTTP detected
        byte[] httpReq = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
        byte[] data1 = ClientFrame(1001, 2001, TcpFlags.PshAck, httpReq);
        Packet p1 = ProtocolTestHelper.ParseFrame(stack, data1, 3, Timestamp.FromMillis(20));

        await ProtocolTestHelper.AssertProtocolPresent(stack, p1, "http").ConfigureAwait(false);

        // Server responds — should also use cached HTTP protocol
        byte[] httpResp = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        byte[] data2 = ServerFrame(2001, 1001 + (uint)httpReq.Length, TcpFlags.PshAck, httpResp);
        Packet p2 = ProtocolTestHelper.ParseFrame(stack, data2, 4, Timestamp.FromMillis(25));

        await ProtocolTestHelper.AssertProtocolPresent(stack, p2, "http").ConfigureAwait(false);
    }

    [Test]
    public async Task HeuristicCache_DifferentConnections_IndependentCaches()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Connection 1: HTTP on non-standard port 12345
        DoHandshake(stack, dstPort: 12345);
        byte[] httpPayload = "GET / HTTP/1.1\r\nHost: test\r\n\r\n"u8.ToArray();
        byte[] httpFrame = ClientFrame(1001, 2001, TcpFlags.PshAck, httpPayload, dstPort: 12345);
        Packet pHttp = ProtocolTestHelper.ParseFrame(stack, httpFrame, 3, Timestamp.FromMillis(20));
        await ProtocolTestHelper.AssertProtocolPresent(stack, pHttp, "http").ConfigureAwait(false);

        // Connection 2: TLS on non-standard port 12346
        // Build frames with different port manually
        IPv4Layer ip2C = new(_ClientIp, _ServerIp);
        EthernetLayer eth2C = new(_DstMac, _SrcMac);
        TcpLayer tcp2Syn = new(ClientPort, 12346, seqNum: 5000, ackNum: 0, flags: TcpFlags.Syn);
        byte[] syn2 = FrameStack.Start(eth2C).Then(ip2C).Then(tcp2Syn).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
        ProtocolTestHelper.ParseFrame(stack, syn2, 4, Timestamp.FromMillis(50));

        IPv4Layer ip2S = new(_ServerIp, _ClientIp);
        EthernetLayer eth2S = new(_SrcMac, _DstMac);
        TcpLayer tcp2SynAck = new(12346, ClientPort, seqNum: 6000, ackNum: 5001, flags: TcpFlags.SynAck);
        byte[] synAck2 = FrameStack.Start(eth2S).Then(ip2S).Then(tcp2SynAck).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
        ProtocolTestHelper.ParseFrame(stack, synAck2, 5, Timestamp.FromMillis(55));

        TcpLayer tcp2Ack = new(ClientPort, 12346, seqNum: 5001, ackNum: 6001, flags: TcpFlags.Ack);
        byte[] ack2 = FrameStack.Start(eth2C).Then(ip2C).Then(tcp2Ack).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
        ProtocolTestHelper.ParseFrame(stack, ack2, 6, Timestamp.FromMillis(60));

        // TLS Client Hello on the second connection
        byte[] tlsPayload =
        [
            0x16, 0x03, 0x01, 0x00, 0x05,
            0x01, 0x00, 0x00, 0x01, 0x00
        ];
        TcpLayer tcp2Data = new(ClientPort, 12346, seqNum: 5001, ackNum: 6001, flags: TcpFlags.PshAck);
        byte[] tlsFrame = FrameStack.Start(eth2C).Then(ip2C).Then(tcp2Data).CreateWithFixedValues().EmitFrame(tlsPayload);
        Packet pTls = ProtocolTestHelper.ParseFrame(stack, tlsFrame, 7, Timestamp.FromMillis(65));

        // Connection 2 should detect TLS, not HTTP
        await ProtocolTestHelper.AssertProtocolPresent(stack, pTls, "tls").ConfigureAwait(false);
    }

    #endregion
}
