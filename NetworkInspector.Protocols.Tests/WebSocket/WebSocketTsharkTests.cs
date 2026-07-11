// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the WebSocket dissector (RFC 6455, Plan §3.1.8).
/// Covers opcode, payload length, and close-frame status code fields.
/// </summary>
/// <remarks>
/// <para><b>Frame structure.</b> Each test builds a single Ethernet + IPv4 + TCP frame whose
/// TCP payload is an HTTP/1.1 101 Switching Protocols response immediately followed by the
/// RFC 6455 WebSocket frame bytes.  NI dispatches to HTTP (port 80 matched on low port),
/// HTTP recognises the 101 response and dispatches the remaining bytes to WebSocket via the
/// <c>http.upgrade</c> table.  tshark, when given the same single-frame PCAP, applies its
/// HTTP dissector (port 80), recognises the 101 upgrade to WebSocket, and dissects the body
/// as WebSocket in the same packet — producing <c>websocket.*</c> fields for comparison.</para>
///
/// <para><b>Lazy-tree ordering.</b> NI uses targeted lazy materialization:
/// <see cref="Packet.TryGetFieldValue"/> only triggers lazy containers that belong to the same
/// protocol as the target field.  Because WebSocket fields live inside the HTTP container's
/// lazy subtree, every test accesses <c>http.response.code</c> via
/// <see cref="ProtocolTestHelper.AssertU64Field"/> before any WebSocket field to force
/// HTTP's lazy populator to run.  HTTP's populator calls WebSocket's
/// <see cref="WebSocketProtocol.Parse"/>, which appends the WebSocket lazy container to the
/// flat field array.  Subsequent <c>TryGetFieldValue("websocket.*")</c> calls can then find
/// and trigger the WebSocket lazy populator.</para>
///
/// <para><b>Boolean field exclusion.</b> NI renders boolean fields as "True"/"False" while
/// tshark renders them as "1"/"0".  <see cref="TsharkEquivalence.AreEquivalent"/> does not
/// normalise this difference, so <c>websocket.fin</c> and <c>websocket.mask</c> are tested
/// in <see cref="WebSocketBasicTests"/> only, not here.</para>
///
/// <para>Thread safety: stateless tests over per-test stacks.</para>
/// </remarks>
internal sealed class WebSocketTsharkTests
{
    #region Frame builders

    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    // 10.0.0.1 = client, 10.0.0.2 = server
    private static readonly IPv4Address _ServerIp = new(0x0A000002);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);

    private const ushort _ServerPort = 80;
    private const ushort _ClientPort = 49152;

    private const string _Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    /// <summary>
    /// Encodes a single WebSocket frame using <see cref="WebSocketLayer"/> and returns the
    /// raw RFC 6455 wire bytes.  Uses the FrameBuilder encoder for round-trip fidelity.
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
    /// <para>
    /// The source port is 80 (server), destination port is <see cref="_ClientPort"/> (client).
    /// TCP dispatches to HTTP on the low port <c>min(80, 49152) = 80</c>.  HTTP detects the
    /// 101 response and dispatches <paramref name="wsBytes"/> to WebSocket via the
    /// <c>http.upgrade</c> table.  tshark applies its HTTP dissector on port 80 and
    /// dissects the body as WebSocket in the same packet.
    /// </para>
    /// </summary>
    private static byte[] _BuildHttpUpgradeWithWebSocketFrame(byte[] wsBytes)
    {
        byte[] http101 = Encoding.ASCII.GetBytes(_Http101Response);
        byte[] combined = new byte[http101.Length + wsBytes.Length];
        http101.CopyTo(combined, 0);
        wsBytes.CopyTo(combined, http101.Length);

        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(_ServerPort, _ClientPort, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(combined);
    }

    /// <summary>
    /// Encodes a WebSocket close frame payload: 2-byte big-endian status code followed by
    /// an optional UTF-8 reason string.
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

    #endregion

    #region Text frame

    /// <summary>
    /// Verifies that the WebSocket opcode and payload length for a text frame are reported
    /// identically by NI and tshark.
    /// <para>
    /// Both sides see the same single-frame PCAP: NI's HTTP+WebSocket parse chain and
    /// tshark's HTTP+WebSocket dissector chain must agree on the two integer fields.
    /// </para>
    /// </summary>
    [Test]
    public async Task WebSocket_TextFrame_OpcodeAndPayloadLengthMatchTshark()
    {
        const string TextPayload = "Hello tshark WebSocket";
        byte[] wsBytes = _EncodeWsFrame(
            Encoding.UTF8.GetBytes(TextPayload),
            WebSocketOpcode.Text,
            new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);

            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("websocket.opcode", "websocket.opcode"),
                ("websocket.payload_length", "websocket.payload_length")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Binary frame

    /// <summary>
    /// Verifies that the opcode and payload length for a binary frame match between NI and tshark.
    /// </summary>
    [Test]
    public async Task WebSocket_BinaryFrame_OpcodeAndPayloadLengthMatchTshark()
    {
        byte[] binaryPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03];
        byte[] wsBytes = _EncodeWsFrame(binaryPayload, WebSocketOpcode.Binary, new WebSocketFrameOptions(Fin: true));

        byte[] frame = _BuildHttpUpgradeWithWebSocketFrame(wsBytes);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first — see class-level doc for rationale.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);

            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("websocket.opcode", "websocket.opcode"),
                ("websocket.payload_length", "websocket.payload_length")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Close frame

    /// <summary>
    /// Verifies that the WebSocket close-frame status code and reason string are reported
    /// identically by NI and tshark.
    /// </summary>
    [Test]
    public async Task WebSocket_CloseFrame_StatusCodeAndReasonMatchTshark()
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

            // NI field: websocket.close.code; tshark field: websocket.payload.close.status_code
            // NI field: websocket.close.reason; tshark field: websocket.payload.close.reason
            // (Wireshark's WebSocket dissector nests close sub-fields under
            // websocket.payload.close.*; NI surfaces them directly under websocket.close.*
            // for a flatter, more discoverable field tree.)
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("websocket.close.code", "websocket.payload.close.status_code"),
                ("websocket.close.reason", "websocket.payload.close.reason")).ConfigureAwait(false);
        }
    }

    #endregion
}
