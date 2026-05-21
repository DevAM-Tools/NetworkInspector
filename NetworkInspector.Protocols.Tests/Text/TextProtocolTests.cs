// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="TextProtocol"/> — line-based text display protocol
/// (Wireshark equivalent: "data-text-lines" dissector).
/// <para>
/// Tests reach the protocol via a WebSocket text-opcode (1) frame. When WebSocket parses a
/// text payload with no matching sub-protocol in the ws.port table, it forwards the payload
/// to <c>TextProtocol</c> via <c>DispatchTextPayload</c>.
/// </para>
/// <para>
/// <b>Line splitting rules:</b>
/// Lines are delimited by LF (0x0A). An optional CR (0x0D) immediately before the LF is
/// stripped. Trailing LF produces no extra empty line. A payload with no LF is treated as a
/// single line.
/// </para>
/// <para>
/// <b>Lazy-tree ordering:</b> same as <see cref="DataProtocolTests"/> — HTTP's lazy populator
/// must fire first (<c>http.response.code</c>), then WebSocket's (<c>websocket.opcode</c>),
/// then TextProtocol's (<c>text.lines</c>).
/// </para>
/// </summary>
internal sealed class TextProtocolTests
{
    #region Frame helpers

    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);

    private const string Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    private static byte[] EncodeWsTextFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        WebSocketLayer layer = new(payload, WebSocketOpcode.Text, new WebSocketFrameOptions(Fin: true));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 512);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildFrame(byte[] wsBytes)
    {
        byte[] http101 = Encoding.ASCII.GetBytes(Http101Response);
        byte[] combined = new byte[http101.Length + wsBytes.Length];
        http101.CopyTo(combined, 0);
        wsBytes.CopyTo(combined, http101.Length);

        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(80, 49152, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(combined);
    }

    #endregion

    // === Single line ===

    [Test]
    public async Task Parse_SingleLineWithoutLF_ProducesOneLineField()
    {
        // Arrange — payload with no LF: treated as one line
        byte[] frame = BuildFrame(EncodeWsTextFrame("Hello"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — one line, with the correct text
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 1UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "text.line", "Hello").ConfigureAwait(false);
        }
    }

    // === Multiple lines ===

    [Test]
    public async Task Parse_TwoLinesWithLF_ProducesTwoLineFields()
    {
        // Arrange — two lines separated by LF
        byte[] frame = BuildFrame(EncodeWsTextFrame("First\nSecond"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // Assert — two lines; TryGetFieldValue returns the first match
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 2UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "text.line", "First").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ThreeLinesWithLF_ProducesThreeLineFields()
    {
        byte[] frame = BuildFrame(EncodeWsTextFrame("A\nB\nC"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 3UL).ConfigureAwait(false);
        }
    }

    // === CRLF handling ===

    [Test]
    public async Task Parse_CrlfLineSeparators_CrIsStrippedFromLineText()
    {
        // CRLF is the HTTP convention; CR before LF must be stripped
        byte[] frame = BuildFrame(EncodeWsTextFrame("HTTP/1.0 200 OK\r\nContent-Type: text/plain"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 2UL).ConfigureAwait(false);

            // First line must NOT contain the trailing CR
            await ProtocolTestHelper.AssertStringField(stack, packet, "text.line", "HTTP/1.0 200 OK").ConfigureAwait(false);
        }
    }

    // === Trailing LF ===

    [Test]
    public async Task Parse_SingleLineWithTrailingLF_ProducesOneLineNotTwo()
    {
        // "Line\n" ends with LF — should produce exactly one line "Line", not a second empty line
        byte[] frame = BuildFrame(EncodeWsTextFrame("Line\n"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // CountLines: 1 LF → count=1, span[^1]==LF so no extra line → 1
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 1UL).ConfigureAwait(false);
        }
    }

    // === Text container field is present ===

    [Test]
    public async Task Parse_TextFrame_TextContainerFieldPresent()
    {
        byte[] frame = BuildFrame(EncodeWsTextFrame("payload"));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // The "text" container field must be present when TextProtocol fires
            await ProtocolTestHelper.AssertU64Field(stack, packet, "text.lines", 1UL).ConfigureAwait(false);
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
        byte[] frame = BuildFrame(writer.WrittenSpan.ToArray());

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);

            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "text.lines").ConfigureAwait(false);
        }
    }
}
