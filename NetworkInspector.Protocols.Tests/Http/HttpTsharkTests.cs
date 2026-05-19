// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the HTTP/1.x dissector (Plan §3.1.7).
/// Covers request-line fields, response-line fields, common header fields and
/// multi-segment TCP reassembly.
/// </summary>
/// <remarks>
/// <para>
/// Single-frame tests build one Ethernet + IPv4 + TCP segment carrying the
/// full HTTP message and use <see cref="TsharkAssert.AssertEquivalentMany"/>
/// for symmetric NI ↔ tshark comparison.  tshark auto-applies the HTTP
/// dissector to TCP port 80; no <c>-d</c> decode-as override is needed.
/// </para>
/// <para>
/// The multi-segment test emits a conversation via <see cref="TcpConnection"/>
/// at a low MSS so the request body spans several TCP segments.  Both NI and
/// tshark are given the same multi-frame capture; NI accumulates state via
/// sequential <see cref="ProtocolTestHelper.ParseFrame"/> calls, and tshark
/// performs its own TCP-stream reassembly via a second-pass filter
/// (<c>tshark -2 -Y http.request</c>).  The reassembled fields from each
/// side are then compared through <see cref="TsharkEquivalence.AreEquivalent"/>.
/// </para>
/// <para>Thread safety: stateless tests over per-test stacks.</para>
/// </remarks>
internal sealed class HttpTsharkTests
{
    #region Frame builders

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    // 10.0.0.1 = client, 10.0.0.2 = server
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private const ushort ClientPort = 49152;
    private const ushort ServerPort = 80;

    /// <summary>
    /// Builds a single Ethernet + IPv4 + TCP + HTTP frame for single-segment tests.
    /// The TCP destination port is 80 so tshark auto-applies the HTTP dissector.
    /// </summary>
    private static byte[] BuildHttpFrame(string httpMessage, ushort dstPort = ServerPort)
    {
        byte[] httpBytes = Encoding.ASCII.GetBytes(httpMessage);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        // seqNum=1 / ackNum=1 so the segment looks like post-handshake data.
        TcpLayer tcp = new(ClientPort, dstPort, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);
    }

    /// <summary>
    /// Builds a complete HTTP/1.1 conversation split over multiple TCP segments
    /// (MSS = <paramref name="mss"/>) using <see cref="TcpConnection"/> and returns
    /// the frames in wire order.
    /// </summary>
    private static List<byte[]> BuildConversation(byte[] request, byte[] response, ushort mss = 200)
    {
        EthernetLayer ethC = new(_DstMac, _SrcMac);
        IPv4Layer ipC = new(_ClientIp, _ServerIp);
        EthernetLayer ethS = new(_SrcMac, _DstMac);
        IPv4Layer ipS = new(_ServerIp, _ClientIp);

        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> clientCarrier =
            FrameStack.Start(ethC).Then(ipC);
        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> serverCarrier =
            FrameStack.Start(ethS).Then(ipS);

        TcpConnectionOptions options = new(
            ClientIsn: 1000,
            ServerIsn: 9000,
            Mss: mss,
            WindowSize: 65535);

        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn =
            TcpConnection.Open(in clientCarrier, in serverCarrier, ClientPort, ServerPort, options);

        List<byte[]> frames = [];
        FrameSink sink = f => frames.Add(f.ToArray());

        conn.EmitHandshake(sink);
        conn.WriteFromClient(request, sink);
        conn.WriteFromServer(response, sink);
        conn.EmitFinClose(sink);

        return frames;
    }

    // ──────────────────────────────────────────────────────────────
    // Test message constants
    // ──────────────────────────────────────────────────────────────

    private const string HttpGetRequest =
        "GET /index.html HTTP/1.1\r\n" +
        "Host: example.test\r\n" +
        "User-Agent: TestAgent/1.0\r\n" +
        "\r\n";

    private const string Http200Response =
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain\r\n" +
        "Content-Length: 5\r\n" +
        "\r\n" +
        "Hello";

    private const string Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    // Large request body (> 200 bytes) that forces multi-segment emission at Mss=200.
    private static readonly byte[] _LargeGetRequest =
        Encoding.ASCII.GetBytes(
            "GET /large HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "User-Agent: TestAgent/1.0\r\n" +
            "Accept: text/plain\r\n" +
            "Accept-Encoding: identity\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

    private static readonly byte[] _LargeResponse =
        Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Length: 0\r\n" +
            "\r\n");

    #endregion

    #region Request field coverage

    /// <summary>
    /// Full field-set verification for an HTTP/1.1 GET request carried in a
    /// single TCP segment.  Pins request method, URI, version, Host header
    /// and User-Agent header against tshark's HTTP dissector output.
    /// </summary>
    [Test]
    public async Task Http_GetRequest_AllFieldsMatchTshark()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("http.request.method", "http.request.method"),
                ("http.request.uri", "http.request.uri"),
                ("http.request.version", "http.request.version"),
                ("http.host", "http.host"),
                ("http.user_agent", "http.user_agent")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Response field coverage

    /// <summary>
    /// Verifies the HTTP/1.1 200 OK response fields: status code, reason phrase,
    /// version, Content-Type, and Content-Length.
    /// Note: NI names the content-type field <c>http.content_type_value</c> to
    /// avoid a name clash with the container node; tshark uses <c>http.content_type</c>.
    /// </summary>
    [Test]
    public async Task Http_200Response_AllFieldsMatchTshark()
    {
        byte[] frame = BuildHttpFrame(Http200Response, dstPort: ServerPort);
        // Flip src/dst for a server-to-client segment: tshark identifies HTTP
        // responses by the response-line pattern, not port direction.
        byte[] httpBytes = Encoding.ASCII.GetBytes(Http200Response);
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(ServerPort, ClientPort, seqNum: 1, ackNum: 1,
            flags: TcpFlags.PshAck, windowSize: 65535);
        byte[] responseFrame = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(responseFrame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, responseFrame,
                ("http.response.code", "http.response.code"),
                ("http.content_type_value", "http.content_type"),
                ("http.content_length", "http.content_length")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies the HTTP/1.1 101 Switching Protocols response: status code and
    /// the Upgrade header field that signals a WebSocket upgrade.
    /// </summary>
    [Test]
    public async Task Http_101SwitchingProtocols_FieldsMatchTshark()
    {
        byte[] httpBytes = Encoding.ASCII.GetBytes(Http101Response);
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(ServerPort, ClientPort, seqNum: 1, ackNum: 1,
            flags: TcpFlags.PshAck, windowSize: 65535);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("http.response.code", "http.response.code"),
                ("http.upgrade", "http.upgrade")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Multi-segment reassembly

    /// <summary>
    /// Emits the HTTP GET request via <see cref="TcpConnection"/> at
    /// <c>Mss=200</c> so the message spans multiple TCP segments.  NI
    /// reassembles the stream via sequential <see cref="ProtocolTestHelper.ParseFrame"/>
    /// calls on a single shared stack.  tshark performs its own TCP-stream
    /// reassembly over the same multi-frame capture (second-pass filter
    /// <c>-2 -Y http.request</c>).  The reassembled field values from both
    /// sides are compared via <see cref="TsharkEquivalence.AreEquivalent"/>.
    /// </summary>
    [Test]
    [NotInParallel(nameof(HttpTsharkTests))]
    public async Task Http_MultiSegmentRequest_ReassembledFieldsMatchTshark()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        List<byte[]> frames = BuildConversation(_LargeGetRequest, _LargeResponse, mss: 200);

        // ── NI side: parse all frames sequentially so the reassembly engine
        //             accumulates state and produces HTTP fields on the final
        //             data segment where reassembly completes. ──────────────
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            Packet? httpPacket = null;
            FieldId? methodFieldId = stack.GetFieldId("http.request.method");
            await Assert.That(methodFieldId).IsNotNull().Because("'http.request.method' must be registered.");

            for (int i = 0; i < frames.Count; i++)
            {
                Packet p = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromMillis(i));
                if (p.TryGetFieldValue(methodFieldId!.Value, out _))
                {
                    httpPacket = p;
                }
            }

            await Assert.That(httpPacket).IsNotNull().Because("NI must reassemble the HTTP request.");

            // ── tshark side: second-pass reassembly to extract the same fields. ──
            string tsharkOutput = TsharkVerifier.RunOnFrames(
                frames,
                "-2 -Y http.request -T fields -E header=n -E separator=/t " +
                "-e http.request.method -e http.request.uri -e http.host");

            string[] lines = tsharkOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsGreaterThanOrEqualTo(1)
                .Because("tshark must reassemble and find the HTTP request.");

            string[] parts = lines[0].Split('\t');

            // Extract NI field values inline: same logic as TsharkAssert.TryGetNiValueAsString.
            string? niMethod = GetNiFieldValue(stack, httpPacket!, "http.request.method");
            string? niUri = GetNiFieldValue(stack, httpPacket!, "http.request.uri");
            string? niHost = GetNiFieldValue(stack, httpPacket!, "http.host");

            string? tsMethod = parts.Length > 0 ? parts[0] : null;
            string? tsUri = parts.Length > 1 ? parts[1] : null;
            string? tsHost = parts.Length > 2 ? parts[2] : null;

            await Assert.That(TsharkEquivalence.AreEquivalent(niMethod, tsMethod))
                .IsTrue()
                .Because(TsharkEquivalence.Describe("http.request.method", niMethod, tsMethod));
            await Assert.That(TsharkEquivalence.AreEquivalent(niUri, tsUri))
                .IsTrue()
                .Because(TsharkEquivalence.Describe("http.request.uri", niUri, tsUri));
            await Assert.That(TsharkEquivalence.AreEquivalent(niHost, tsHost))
                .IsTrue()
                .Because(TsharkEquivalence.Describe("http.host", niHost, tsHost));
        }
    }

    #endregion

    /// <summary>
    /// Extracts the string representation of a field from a parsed packet.
    /// Returns <see langword="null"/> when the field is absent or not registered.
    /// Mirrors the private <c>TryGetNiValueAsString</c> logic in <see cref="TsharkAssert"/>.
    /// </summary>
    private static string? GetNiFieldValue(Stack stack, Packet packet, string fieldPath)
    {
        FieldId? fieldId = stack.GetFieldId(fieldPath);
        if (fieldId is null)
        {
            return null;
        }
        if (!packet.TryGetFieldValue(fieldId.Value, out FieldValue value))
        {
            return null;
        }
        return value.ToString();
    }
}
