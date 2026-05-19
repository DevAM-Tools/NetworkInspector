// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="DataProtocol"/> — the universal fallback dissector for unrecognized
/// binary payloads (Wireshark equivalent: "data" protocol).
/// <para>
/// Tests reach the protocol via a WebSocket binary-opcode (2) frame. When WebSocket parses a
/// binary payload with no matching sub-protocol in the ws.port table, it forwards the payload
/// to <c>DataProtocol</c> via <c>DispatchBinaryPayload</c>.
/// </para>
/// <para>
/// <b>Lazy-tree ordering:</b> HTTP's lazy populator must be triggered first (by accessing
/// <c>http.response.code</c>) to add the WebSocket container to the flat field array.
/// Then the WebSocket lazy populator must be triggered (by accessing any WebSocket field)
/// to run <c>DispatchBinaryPayload</c>, which populates the <c>data.*</c> fields.
/// </para>
/// </summary>
internal sealed class DataProtocolTests
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

    /// <summary>
    /// Encodes a WebSocket frame and returns raw wire bytes.
    /// </summary>
    private static byte[] EncodeWsFrame(byte[] payload, byte opcode)
    {
        WebSocketLayer layer = new(payload, opcode, new WebSocketFrameOptions(Fin: true));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 256);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP frame whose TCP payload is an HTTP 101 response
    /// followed by <paramref name="wsBytes"/>. Port 80 triggers HTTP dispatch.
    /// </summary>
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

    // === Field presence and value ===

    [Test]
    public async Task Parse_BinaryFrame_DataLenEqualsPayloadSize()
    {
        // Arrange — binary frame with 7-byte payload
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];
        byte[] frame = BuildFrame(EncodeWsFrame(payload, WebSocketOpcode.Binary));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Trigger HTTP lazy populator first, then WS lazy populator
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);

            // Assert — data.len must equal the binary payload length
            await ProtocolTestHelper.AssertU64Field(stack, packet, "data.len", (ulong)payload.Length).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_BinaryFrame_DataBytesMatchPayload()
    {
        // Arrange — binary frame with known byte pattern
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frame = BuildFrame(EncodeWsFrame(payload, WebSocketOpcode.Binary));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);

            // Assert — data.data bytes field holds the raw payload
            await ProtocolTestHelper.AssertBytesField(stack, packet, "data.data", payload).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_BinaryFrame_DataContainerPresent()
    {
        // Arrange — any non-empty binary payload triggers DataProtocol
        byte[] payload = [0xFF];
        byte[] frame = BuildFrame(EncodeWsFrame(payload, WebSocketOpcode.Binary));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);

            // Assert — the "data" container field must be present
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_BinaryFrame_SingleByte_DataLenIsOne()
    {
        byte[] payload = [0xAB];
        byte[] frame = BuildFrame(EncodeWsFrame(payload, WebSocketOpcode.Binary));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Binary).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "data.len", 1UL).ConfigureAwait(false);
        }
    }

    // === Text frame must NOT produce data.* fields ===

    [Test]
    public async Task Parse_TextFrame_NoDataFieldsPresent()
    {
        // A WebSocket text (opcode=1) frame dispatches to TextProtocol, not DataProtocol.
        // Ensure data.* fields are absent.
        byte[] payload = Encoding.UTF8.GetBytes("Hello");
        byte[] frame = BuildFrame(EncodeWsFrame(payload, WebSocketOpcode.Text));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 101).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "websocket.opcode", WebSocketOpcode.Text).ConfigureAwait(false);

            // data.len must NOT be present for text frames
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "data.len").ConfigureAwait(false);
        }
    }
}
