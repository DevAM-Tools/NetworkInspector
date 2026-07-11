// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.
namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for the WebSocket protocol parser (RFC 6455).
/// Exercises frame-level fields — FIN, RSV, opcode, mask, payload length, masking key,
/// text/binary payload, close status code and reason, ping, pong, and continuation frames.
///
/// <para><b>Approach:</b> Each test builds a single Ethernet + IPv4 + TCP frame whose TCP
/// payload contains an HTTP/1.1 101 Switching Protocols response immediately followed by the
/// RFC 6455 WebSocket frame bytes.  When NI parses the TCP segment, it dispatches to
/// <c>HttpProtocol</c> (port 80), which recognises the 101 response and dispatches any
/// remaining bytes (the WebSocket frame) to <c>WebSocketProtocol</c> via the
/// <c>http.upgrade</c> table.  This is the canonical trigger for WebSocket parsing in NI
/// without multi-frame TCP reassembly state.</para>
///
/// <para><b>Round-trip coverage:</b> WebSocket frame bytes are produced by
/// <see cref="WebSocketLayer"/> (FrameBuilder) and parsed by <see cref="WebSocketProtocol"/>.
/// This validates that the encoder and decoder are consistent.</para>
///
/// <para><b>Lazy-tree ordering:</b> NI uses targeted lazy materialization:
/// <c>Packet.TryGetFieldValue</c> only triggers lazy containers that belong to the
/// <em>same protocol</em> as the target field.  Because WebSocket fields live inside
/// the HTTP container's lazy subtree (created when HTTP dispatches the upgrade body),
/// every test accesses <c>http.response.code</c> before any WebSocket field to force
/// HTTP's lazy populator to run, which in turn adds the WebSocket lazy container to the
/// flat field array so that subsequent WebSocket field lookups succeed.</para>
///
/// <para>Not thread-safe. All tests run sequentially via TUnit's default runner.</para>
/// </summary>
internal sealed class WebSocketBasicTests
{
    #region Frame helpers

    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    // 10.0.0.1 = client, 10.0.0.2 = server
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private const ushort _ServerPort = 80;
    private const ushort _ClientPort = 49152;

    private const string _Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    /// <summary>
    /// Encodes a WebSocket frame using <see cref="WebSocketLayer"/> and returns the raw bytes.
    /// This exercises the FrameBuilder encoder as part of the round-trip test.
    /// </summary>
    private static byte[] _EncodeWsFrame(byte[] payload, byte opcode, WebSocketFrameOptions options = default)
    {
        WebSocketLayer layer = new(payload, opcode, options);
        ArrayBufferWriter<byte> writer = new(initialCapacity: 256);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Builds a server → client Ethernet + IPv4 + TCP frame whose TCP payload is an HTTP
    /// 101 Switching Protocols response immediately followed by <paramref name="wsBytes"/>.
    /// NI dispatches to HTTP (port 80 matched on low port), HTTP detects the 101 response
    /// and dispatches <paramref name="wsBytes"/> to WebSocket via the http.upgrade table.
    /// </summary>
    private static byte[] _BuildHttpUpgradeWithWebSocketFrame(byte[] wsBytes)
    {
        byte[] http101 = Encoding.ASCII.GetBytes(_Http101Response);
        byte[] combined = new byte[http101.Length + wsBytes.Length];
        http101.CopyTo(combined, 0);
        wsBytes.CopyTo(combined, http101.Length);

        // Server → client: src port = 80 (server), dst port = _ClientPort (client).
        // TCP dispatches to HTTP using the low port min(80, 49152) = 80.
        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(_ServerPort, _ClientPort, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(combined);
    }

    /// <summary>
    /// Encodes a WebSocket close frame payload: 2-byte big-endian status code followed by
    /// optional UTF-8 reason string.
    /// </summary>
    private static byte[] _EncodeClosePayload(ushort statusCode, string reason)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        byte[] payload = new byte[2 + reasonBytes.Length];
        payload[0] = (byte)(statusCode >> 8);
        payload[1] = (byte)statusCode;
        reasonBytes.CopyTo(payload, 2);
        return payload;
    }

    /// <summary>
    /// Compresses <paramref name="data"/> with raw DEFLATE and strips the trailing
    /// <c>0x00 0x00 0xFF 0xFF</c> sync-flush marker so the result is compatible with the
    /// RFC 7692 per-message DEFLATE format expected by <see cref="WebSocketProtocol"/>.
    /// The decompressor appends those 4 bytes itself before inflating.
    /// </summary>
    private static byte[] _DeflateCompressForWebSocket(byte[] data)
    {
        using MemoryStream ms = new();
        using (DeflateStream ds = new(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            ds.Write(data);
        }
        byte[] compressed = ms.ToArray();
        // .NET's raw DeflateStream ends each close with the sync-flush marker 0x00 0x00 0xFF 0xFF.
        // Strip it so the decompressor (which appends it) does not see it twice.
        if (compressed.Length >= 4 &&
            compressed[^4] == 0x00 && compressed[^3] == 0x00 &&
            compressed[^2] == 0xFF && compressed[^1] == 0xFF)
        {
            return compressed[..^4];
        }
        return compressed;
    }

    #endregion

    #region Unmasked text frame (server → client)

    /// <summary>
    /// Verifies that an unmasked FIN text frame (server → client direction) produced
    /// by <see cref="WebSocketLayer"/> is parsed correctly by <see cref="WebSocketProtocol"/>.
    /// Pins: FIN, RSV, opcode, mask flag, payload length, and decoded text payload.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_UnmaskedTextFrame_AllFieldsPresent()
    {
        const string TextPayload = "Hello WebSocket";
        byte[] wsBytes = _EncodeWsFrame(
            Encoding.UTF8.GetBytes(TextPayload),
            WebSocketOpcode.Text,
            new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — WebSocket fields are nested inside the
            // HTTP 101 response body dispatch and only appear in the flat field array
            // after the HTTP container has been materialised.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "websocket.fin", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.rsv", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "websocket.mask", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(
                stack, packet, "websocket.payload_length",
                (ulong)Encoding.UTF8.GetByteCount(TextPayload)).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "websocket.payload.text", TextPayload).ConfigureAwait(false);
        }
    }

    #endregion

    #region Masked text frame (client → server)

    /// <summary>
    /// Verifies that a masked FIN text frame produced by <see cref="WebSocketLayer"/> is
    /// correctly unmasked and parsed by <see cref="WebSocketProtocol"/>.
    /// The masking key is included in the wire frame; NI must XOR-unmask before decoding.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_MaskedTextFrame_UnmasksPayloadCorrectly()
    {
        const string TextPayload = "NI Unmask Me";
        const uint MaskingKey = 0xDEADBEEF;
        byte[] wsBytes = _EncodeWsFrame(
            Encoding.UTF8.GetBytes(TextPayload),
            WebSocketOpcode.Text,
            new WebSocketFrameOptions(Fin: true, MaskingKey: MaskingKey));

        // Even though this is logically client→server, we still build the frame with
        // server src-port 80 so that TCP dispatches to HTTP.  The masking key in the
        // wire frame is what matters for the round-trip test; NI must unmask regardless
        // of the perceived direction at the Ethernet/IP level.
        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "websocket.mask", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.masking_key", MaskingKey).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(
                stack, packet, "websocket.payload_length",
                (ulong)Encoding.UTF8.GetByteCount(TextPayload)).ConfigureAwait(false);
            // NI must XOR-unmask the payload before decoding; if the text field is present
            // with the correct value, unmasking is proven correct.
            await ProtocolTestHelper.AssertStringField(stack, packet, "websocket.payload.text", TextPayload).ConfigureAwait(false);
        }
    }

    #endregion

    #region Binary frame

    /// <summary>
    /// Verifies that an unmasked FIN binary frame is recognised and its opcode and payload
    /// length are reported correctly.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_BinaryFrame_OpcodeAndLengthPresent()
    {
        byte[] binaryPayload = [0x01, 0x02, 0x03, 0xFF, 0x00];
        byte[] wsBytes = _EncodeWsFrame(binaryPayload, WebSocketOpcode.Binary, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "websocket.mask", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.payload_length", (ulong)binaryPayload.Length).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload").ConfigureAwait(false);
        }
    }

    #endregion

    #region Ping frame

    /// <summary>
    /// Verifies that a Ping control frame produces the expected opcode and ping payload field.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_PingFrame_PingPayloadPresent()
    {
        byte[] pingPayload = Encoding.ASCII.GetBytes("ping-data");
        byte[] wsBytes = _EncodeWsFrame(pingPayload, WebSocketOpcode.Ping, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Ping).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.ping").ConfigureAwait(false);
        }
    }

    #endregion

    #region Pong frame

    /// <summary>
    /// Verifies that a Pong control frame produces the expected opcode and pong payload field.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_PongFrame_PongPayloadPresent()
    {
        byte[] pongPayload = Encoding.ASCII.GetBytes("pong-data");
        byte[] wsBytes = _EncodeWsFrame(pongPayload, WebSocketOpcode.Pong, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Pong).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.pong").ConfigureAwait(false);
        }
    }

    #endregion

    #region Close frame

    /// <summary>
    /// Verifies that a Close frame with a 2-byte status code and UTF-8 reason produces the
    /// correct <c>websocket.close.code</c> and <c>websocket.close.reason</c> fields.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_CloseFrame_StatusCodeAndReasonCorrect()
    {
        const ushort StatusCode = 1000; // Normal Closure
        const string Reason = "Normal Closure";
        byte[] closePayload = _EncodeClosePayload(StatusCode, Reason);
        byte[] wsBytes = _EncodeWsFrame(closePayload, WebSocketOpcode.Close, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Close).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.close.code", StatusCode).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "websocket.close.reason", Reason).ConfigureAwait(false);
        }
    }

    #endregion

    #region Fragmented message — continuation frame

    /// <summary>
    /// Verifies that a fragmented WebSocket message is parsed correctly when both fragments
    /// appear in the same TCP segment body.  The first fragment (FIN=false, opcode=Text) is
    /// followed immediately by the continuation fragment (FIN=true, opcode=Continuation).
    /// NI processes both frames from the same buffer via its inner <c>while</c> loop; the
    /// continuation field must be present on the second frame.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_FragmentedMessage_ContinuationFieldPresent()
    {
        // First fragment: FIN=false, opcode=Text (starts the fragmented message)
        byte[] fragment1Bytes = _EncodeWsFrame(
            Encoding.UTF8.GetBytes("Hello "),
            WebSocketOpcode.Text,
            new WebSocketFrameOptions(Fin: false));

        // Continuation: FIN=true, opcode=Continuation (ends the fragmented message)
        byte[] fragment2Bytes = _EncodeWsFrame(
            Encoding.UTF8.GetBytes("World"),
            WebSocketOpcode.Continuation,
            new WebSocketFrameOptions(Fin: true));

        // Concatenate both WebSocket frames into one HTTP 101 body.
        byte[] wsBytes = new byte[fragment1Bytes.Length + fragment2Bytes.Length];
        fragment1Bytes.CopyTo(wsBytes, 0);
        fragment2Bytes.CopyTo(wsBytes, fragment1Bytes.Length);

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            // websocket.fin from the first frame is false; both frames share the same
            // container so TryGetFieldValue returns the first occurrence.
            await ProtocolTestHelper.AssertBoolField(stack, packet, "websocket.fin", false).ConfigureAwait(false);
            // websocket.continuation is appended only for opcode-0 frames; its presence
            // confirms the continuation fragment was parsed.
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.continuation").ConfigureAwait(false);
        }
    }

    #endregion

    #region Extended payload length (16-bit)

    /// <summary>
    /// Verifies that a WebSocket frame with a 16-bit extended payload length
    /// (payload > 125 bytes, requiring the two-byte extended length field per RFC 6455 §5.2)
    /// is parsed and its length reported correctly.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_ExtendedPayloadLength_LengthFieldCorrect()
    {
        // 200-byte payload forces 16-bit extended length encoding (>125 requires Len16Sentinel)
        byte[] largePayload = new byte[200];
        for (int i = 0; i < largePayload.Length; i++)
        {
            largePayload[i] = (byte)(i & 0xFF);
        }

        byte[] wsBytes = _EncodeWsFrame(largePayload, WebSocketOpcode.Binary, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.payload_length", (ulong)largePayload.Length).ConfigureAwait(false);
        }
    }

    #endregion

    #region Per-message DEFLATE (RSV1, RFC 7692)

    /// <summary>
    /// Verifies that a compressed WebSocket binary frame (RSV1=1, RFC 7692) with a valid
    /// per-message DEFLATE payload is parsed correctly and the decompressed field appears.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_ValidCompressedFrame_DecompressedFieldPresent()
    {
        // Compress the payload using raw DEFLATE so the decompressor can recover it
        // after the 0x00 0x00 0xFF 0xFF trailer is appended.
        byte[] original = Encoding.UTF8.GetBytes("Per-message DEFLATE payload");
        byte[] compressedPayload = _DeflateCompressForWebSocket(original);

        byte[] wsBytes = _EncodeWsFrame(
            compressedPayload,
            WebSocketOpcode.Binary,
            new WebSocketFrameOptions(Fin: true, Rsv1: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            // On success the decompressed bytes field must be present;
            // the error field must not be present.
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.decompressed").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a compressed WebSocket frame (RSV1=1) with a corrupted DEFLATE payload
    /// surfaces the decompression error via the <c>websocket.payload.decompressed.error</c>
    /// field rather than silently omitting the field.
    /// </summary>
    [Test]
    public async Task Parse_WebSocket_CorruptedCompressedFrame_ErrorFieldPresent()
    {
        // 0xFF → DEFLATE bits 0-2 LSB-first = BFINAL=1, BTYPE=11 (reserved) → guaranteed InvalidDataException.
        // Any raw DEFLATE decompressor must reject BTYPE=11 per RFC 1951 §3.2.3.
        byte[] corruptedPayload = [0xFF, 0xFF, 0xFF, 0xFF];

        byte[] wsBytes = _EncodeWsFrame(
            corruptedPayload,
            WebSocketOpcode.Binary,
            new WebSocketFrameOptions(Fin: true, Rsv1: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            // On failure the error field must be present; the decompressed bytes field must not.
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.decompressed.error").ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "websocket.payload.decompressed.error", "Decompression failed").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed").ConfigureAwait(false);
        }
    }

    #endregion
}
