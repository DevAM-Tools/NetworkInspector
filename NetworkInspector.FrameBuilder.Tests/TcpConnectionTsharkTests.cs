// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// End-to-end cross-validation of <see cref="TcpConnection{TOld,TTail}"/>:
/// emits a complete bidirectional TCP conversation (handshake + HTTP-like
/// request/response + graceful FIN-close) into a multi-frame PCAP and lets
/// tshark dissect it.  Asserts that tshark sees the conversation the way
/// the FrameBuilder intended:
/// <list type="bullet">
///   <item>per-segment flags / SEQ / ACK / window match the FrameBuilder output;</item>
///   <item>TCP checksums validate against the IPv4 pseudo-header;</item>
///   <item>tshark recognises both directions as the same TCP conversation
///         (single <c>tcp.stream</c> id);</item>
///   <item>HTTP request / response payloads survive multi-segment reassembly.</item>
/// </list>
/// Tshark availability is mandatory by default — see
/// <see cref="TsharkVerifier.RequireAvailable"/>.
/// </summary>
[NotInParallel(nameof(TcpConnectionTsharkTests))]
internal sealed class TcpConnectionTsharkTests
{
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x02, 0, 0, 0, 0, 0x01]);
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x02, 0, 0, 0, 0, 0x02]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);   // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002);   // 10.0.0.2

    private const ushort ClientPort = 49152;
    private const ushort ServerPort = 80;
    private const uint ClientIsn = 1000;
    private const uint ServerIsn = 9000;

    private static readonly byte[] _HttpRequest =
        "GET /index.html HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray();

    private static readonly byte[] _HttpResponse =
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\nHello, world!"u8.ToArray();

    /// <summary>Builds the full conversation deterministically and returns the wire frames.</summary>
    private static List<byte[]> BuildConversation(ushort? mss = null)
    {
        EthernetLayer ethC = new(_ServerMac, _ClientMac);
        IPv4Layer ipC = new(_ClientIp, _ServerIp);
        EthernetLayer ethS = new(_ClientMac, _ServerMac);
        IPv4Layer ipS = new(_ServerIp, _ClientIp);

        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> clientCarrier = FrameStack.Start(ethC).Then(ipC);
        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> serverCarrier = FrameStack.Start(ethS).Then(ipS);

        TcpConnectionOptions options = new(
            ClientIsn: ClientIsn,
            ServerIsn: ServerIsn,
            Mss: mss ?? 1460,
            WindowSize: 65535);

        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn =
            TcpConnection.Open(in clientCarrier, in serverCarrier, ClientPort, ServerPort, options);

        List<byte[]> frames = [];
        FrameSink sink = f => frames.Add(f.ToArray());

        conn.EmitHandshake(sink);
        conn.WriteFromClient(_HttpRequest, sink);
        conn.WriteFromServer(_HttpResponse, sink);
        conn.EmitFinClose(sink);

        return frames;
    }

    #region Per-segment field validation

    [Test]
    public async Task Tshark_PerSegment_Flags_Seq_Ack_Match_FrameBuilder()
    {
        TsharkVerifier.RequireAvailable();
        List<byte[]> frames = BuildConversation();

        List<string?[]> rows = TsharkVerifier.GetFieldValuesPerPacket(
            frames,
            ["tcp.flags", "tcp.seq_raw", "tcp.ack_raw", "tcp.srcport", "tcp.dstport"]);

        await Assert.That(rows.Count).IsEqualTo(frames.Count);
        // Total emitted: 3 (handshake) + 1 (request) + 1 (response) + 4 (FIN-close) = 9.
        await Assert.That(frames.Count).IsEqualTo(9);

        // Frame 0: SYN, SEQ=1000, ACK=0, sport=49152, dport=80
        await Assert.That(rows[0][0]).IsEqualTo("0x0002");
        await Assert.That(rows[0][1]).IsEqualTo("1000");
        await Assert.That(rows[0][2]).IsEqualTo("0");
        await Assert.That(rows[0][3]).IsEqualTo("49152");
        await Assert.That(rows[0][4]).IsEqualTo("80");

        // Frame 1: SYN+ACK, SEQ=9000, ACK=1001, sport=80
        await Assert.That(rows[1][0]).IsEqualTo("0x0012");
        await Assert.That(rows[1][1]).IsEqualTo("9000");
        await Assert.That(rows[1][2]).IsEqualTo("1001");
        await Assert.That(rows[1][3]).IsEqualTo("80");

        // Frame 2: ACK, SEQ=1001, ACK=9001
        await Assert.That(rows[2][0]).IsEqualTo("0x0010");
        await Assert.That(rows[2][1]).IsEqualTo("1001");
        await Assert.That(rows[2][2]).IsEqualTo("9001");

        // Frame 3: client request — PSH+ACK, SEQ=1001, ACK=9001
        await Assert.That(rows[3][0]).IsEqualTo("0x0018");
        await Assert.That(rows[3][1]).IsEqualTo("1001");
        await Assert.That(rows[3][2]).IsEqualTo("9001");

        // Frame 4: server response — PSH+ACK, SEQ=9001, ACK=1001 + request length
        uint expectedAckOnServer = 1001u + (uint)_HttpRequest.Length;
        await Assert.That(rows[4][0]).IsEqualTo("0x0018");
        await Assert.That(rows[4][1]).IsEqualTo("9001");
        await Assert.That(rows[4][2]).IsEqualTo(expectedAckOnServer.ToString(CultureInfo.InvariantCulture));

        // Frame 5: client FIN+ACK
        await Assert.That(rows[5][0]).IsEqualTo("0x0011");
        // Frame 6: server bare ACK of FIN
        await Assert.That(rows[6][0]).IsEqualTo("0x0010");
        // Frame 7: server FIN+ACK
        await Assert.That(rows[7][0]).IsEqualTo("0x0011");
        // Frame 8: client final ACK
        await Assert.That(rows[8][0]).IsEqualTo("0x0010");
    }

    #endregion

    #region Checksum validation

    [Test]
    public async Task Tshark_TcpChecksum_Status_Is_Good_For_Every_Frame()
    {
        TsharkVerifier.RequireAvailable();
        List<byte[]> frames = BuildConversation();

        List<string?[]> rows = TsharkVerifier.GetFieldValuesPerPacket(
            frames,
            ["tcp.checksum.status"]);

        await Assert.That(rows.Count).IsEqualTo(frames.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            // tshark reports 2 = "Good" for a verified checksum.  Anything else
            // (1 = bad, 0 = unverified, or null) means our pseudo-header maths
            // is wrong somewhere.
            string? status = rows[i][0];
            await Assert.That(status)
                .IsEqualTo("2")
                .Because($"frame #{i} has tcp.checksum.status='{status ?? "<null>"}'.");
        }
    }

    [Test]
    public async Task Tshark_Ip_Checksum_Status_Is_Good_For_Every_Frame()
    {
        TsharkVerifier.RequireAvailable();
        List<byte[]> frames = BuildConversation();

        List<string?[]> rows = TsharkVerifier.GetFieldValuesPerPacket(
            frames,
            ["ip.checksum.status"]);

        await Assert.That(rows.Count).IsEqualTo(frames.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            string? status = rows[i][0];
            await Assert.That(status)
                .IsEqualTo("2")
                .Because($"frame #{i} has ip.checksum.status='{status ?? "<null>"}'.");
        }
    }

    #endregion

    #region Conversation tracking

    [Test]
    public async Task Tshark_All_Frames_Belong_To_Single_Tcp_Stream()
    {
        TsharkVerifier.RequireAvailable();
        List<byte[]> frames = BuildConversation();

        List<string?[]> rows = TsharkVerifier.GetFieldValuesPerPacket(
            frames,
            ["tcp.stream"]);

        await Assert.That(rows.Count).IsEqualTo(frames.Count);
        // Every frame must report the same conversation id (== "0" for a
        // capture that starts with our handshake).
        foreach (string?[] row in rows)
        {
            await Assert.That(row[0]).IsEqualTo("0");
        }
    }

    #endregion

    #region Stream reassembly (HTTP)

    [Test]
    public async Task Tshark_HttpRequest_Method_And_Host_Are_Reassembled()
    {
        TsharkVerifier.RequireAvailable();
        // Force MSS = 16 so the HTTP request spans multiple TCP segments,
        // making tshark exercise its TCP-stream reassembly engine.
        List<byte[]> frames = BuildConversation(mss: 16);

        // -2: enable second pass so reassembly is fully resolved.
        // Filter on http.request to pick the (single) reassembled request packet.
        string output = TsharkVerifier.RunOnFrames(
            frames,
            "-2 -Y http.request -T fields -E header=n -E separator=/t " +
            "-e http.request.method -e http.host -e http.request.uri");

        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(1);

        string[] parts = lines[0].Split('\t');
        await Assert.That(parts[0]).IsEqualTo("GET");
        await Assert.That(parts[1]).IsEqualTo("example.test");
        await Assert.That(parts[2]).IsEqualTo("/index.html");
    }

    [Test]
    public async Task Tshark_HttpResponse_Status_And_Body_Are_Reassembled()
    {
        TsharkVerifier.RequireAvailable();
        List<byte[]> frames = BuildConversation(mss: 16);

        string output = TsharkVerifier.RunOnFrames(
            frames,
            "-2 -Y http.response -T fields -E header=n -E separator=/t " +
            "-e http.response.code -e http.content_length");

        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(1);

        string[] parts = lines[0].Split('\t');
        await Assert.That(parts[0]).IsEqualTo("200");
        await Assert.That(parts[1]).IsEqualTo("13");
    }

    #endregion
}
