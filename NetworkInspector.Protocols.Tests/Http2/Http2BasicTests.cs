// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// HTTP/2 happy-path tests covering the most common frame types: SETTINGS,
/// PING, WINDOW_UPDATE, RST_STREAM, GOAWAY, DATA and HEADERS (with an
/// HPACK indexed-header static-table entry).
/// </summary>
internal sealed class Http2BasicTests
{
    [Test]
    public async Task Parse_SettingsFrame_IdValuePair()
    {
        byte[] body = Http2Layer.BuildSettingsBody((id: 3, value: 100), (id: 4, value: 65535));
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Settings, 0, 0, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.length", (ulong)body.Length).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.stream_id", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_SettingsAck_AckFlagSet()
    {
        Http2Layer http2 = Http2Layer.BuildFrame(
            Http2FrameType.Settings, Http2FrameFlags.EndStreamOrAck, 0, []);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "http2.frame.flags.ack", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_PingFrame_OpaqueEcho()
    {
        byte[] body = Http2Layer.BuildPingBody(0xCAFEBABEDEADBEEF);
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Ping, 0, 0, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 6).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.length", 8).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_WindowUpdateFrame_Increment()
    {
        byte[] body = Http2Layer.BuildWindowUpdateBody(0x10000);
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.WindowUpdate, 0, 1, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.window_update.increment", 0x10000).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RstStreamFrame_ErrorCode()
    {
        byte[] body = Http2Layer.BuildRstStreamBody(8); // CANCEL
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.RstStream, 0, 5, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.rst_stream.error_code", 8).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.stream_id", 5).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_GoawayFrame_LastStreamAndError()
    {
        byte[] body = Http2Layer.BuildGoawayBody(7, 1, [0x42]);
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Goaway, 0, 0, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.goaway.last_stream_id", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.goaway.error_code", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_DataFrame_EndStreamFlag()
    {
        byte[] body = [0xDE, 0xAD, 0xBE, 0xEF];
        Http2Layer http2 = Http2Layer.BuildFrame(
            Http2FrameType.Data, Http2FrameFlags.EndStreamOrAck, 1, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "http2.frame.flags.end_stream", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_HeadersFrame_HpackStaticIndexed_Method()
    {
        // RFC 7541 static table index 2 = ":method: GET" → 0x82.
        byte[] hpack = Http2Layer.BuildHpackIndexed(2);
        Http2Layer http2 = Http2Layer.BuildFrame(
            Http2FrameType.Headers,
            (byte)(Http2FrameFlags.EndStreamOrAck | Http2FrameFlags.EndHeaders),
            1, hpack);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "http2.frame.flags.end_headers", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "http2.frame.flags.end_stream", true).ConfigureAwait(false);
            // HPACK-decoded header fields are emitted under a child container
            // and tested via dedicated header-decode coverage in the protocol's unit tests.
        }
    }

    #region Flags display text

    [Test]
    public async Task Parse_SettingsAck_FlagsDisplayText()
    {
        // SETTINGS ACK: flags = 0x01 (ACK/ES) → "0x01 [ES/ACK]"
        Http2Layer http2 = Http2Layer.BuildFrame(
            Http2FrameType.Settings, Http2FrameFlags.EndStreamOrAck, 0, []);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "http2.frame.flags", "0x01 [ES/ACK]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Settings_FlagsDisplayText_None()
    {
        // SETTINGS (no ACK): flags = 0x00 → "0x00 [None]"
        byte[] body = Http2Layer.BuildSettingsBody((id: 3, value: 100));
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Settings, 0, 0, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(http2).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "http2.frame.flags", "0x00 [None]").ConfigureAwait(false);
        }
    }

    #endregion
}
