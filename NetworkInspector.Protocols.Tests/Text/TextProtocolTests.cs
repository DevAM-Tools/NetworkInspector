// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="TextProtocol"/> — plain text block display protocol
/// (Wireshark equivalent: "data-text-lines" dissector).
/// <para>
/// Tests reach the protocol via a WebSocket text-opcode (1) frame. When WebSocket parses a
/// text payload with no matching sub-protocol in the ws.port table, it forwards the payload
/// to <c>TextProtocol</c> via <c>DispatchTextPayload</c>.
/// </para>
/// <para>
/// The protocol appends the entire UTF-8 decoded payload verbatim as a single <c>text</c>
/// string field. No line splitting is performed.
/// </para>
/// <para>
/// <b>Lazy-tree ordering:</b> same as <see cref="DataProtocolTests"/> — HTTP's lazy populator
/// must fire first (<c>http.response.code</c>), then WebSocket's (<c>websocket.opcode</c>),
/// then TextProtocol's (<c>text</c>).
/// </para>
/// </summary>
internal sealed class TextProtocolTests
{
    #region Frame helpers

    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);

    private const string _Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    private static byte[] _EncodeWsTextFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        WebSocketLayer layer = new(payload, WebSocketOpcode.Text, new WebSocketFrameOptions(Fin: true));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 512);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] _BuildFrame(byte[] wsBytes)
    {
        byte[] http101 = Encoding.ASCII.GetBytes(_Http101Response);
        byte[] combined = new byte[http101.Length + wsBytes.Length];
        http101.CopyTo(combined, 0);
        wsBytes.CopyTo(combined, http101.Length);

        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(80, 49152, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(combined);
    }

    #endregion

    // === Text field contains full payload verbatim ===

    [Test]
    public async Task Parse_TextFrame_TextFieldContainsPayload()
    {
        // Arrange — simple payload with no newlines
        byte[] frame = _BuildFrame(_EncodeWsTextFrame("Hello"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — entire payload stored verbatim as single text field
            await ProtocolTestHelper.AssertStringField(stack, packet, "text", "Hello").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TextFrame_MultilinePayloadStoredVerbatim()
    {
        // Arrange — payload containing newlines; no splitting should occur
        byte[] frame = _BuildFrame(_EncodeWsTextFrame("First\nSecond"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — newlines preserved; whole payload as one field
            await ProtocolTestHelper.AssertStringField(stack, packet, "text", "First\nSecond").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TextFrame_CrlfPayloadStoredVerbatim()
    {
        // Arrange — CRLF-separated lines stored without modification
        byte[] frame = _BuildFrame(_EncodeWsTextFrame("HTTP/1.0 200 OK\r\nContent-Type: text/plain"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — CRLF preserved verbatim
            await ProtocolTestHelper.AssertStringField(stack, packet, "text", "HTTP/1.0 200 OK\r\nContent-Type: text/plain").ConfigureAwait(false);
        }
    }

    // === Text container field is present ===

    [Test]
    public async Task Parse_TextFrame_TextFieldPresent()
    {
        byte[] frame = _BuildFrame(_EncodeWsTextFrame("payload"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — text field is present when TextProtocol fires
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "text").ConfigureAwait(false);
        }
    }

    // === Binary frame must NOT produce text.* fields ===

    [Test]
    public async Task Parse_BinaryFrame_NoTextFieldsPresent()
    {
        // Binary (opcode=2) dispatches to DataProtocol, NOT TextProtocol
        byte[] payload = [0x01, 0x02, 0x03];
        WebSocketLayer layer = new(payload, WebSocketOpcode.Binary, new WebSocketFrameOptions(Fin: true));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 64);
        layer.WriteStream(writer);
        byte[] frame = _BuildFrame(writer.WrittenSpan.ToArray());

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);

            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "text").ConfigureAwait(false);
        }
    }
}
