// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for HTTP/2 frame-level protocol parsing (RFC 7540).
/// Verifies frame header field extraction: length, type, flags, stream ID.
/// </summary>
internal sealed class Http2ProtocolTests
{
    /// <summary>
    /// Generates an HTTP/2 frame wrapped in Ethernet + IPv4 + TCP (port 8443).
    /// </summary>
    private static byte[] _GenerateHttp2Frame(
        byte frameType = 4,
        byte flags = 0,
        uint streamId = 0,
        byte[]? payload = null)
    {
        payload ??= [];
        int payloadLen = payload.Length;

        // HTTP/2 frame header: 9 bytes
        byte[] h2Frame = new byte[9 + payloadLen];
        // 3-byte big-endian length
        h2Frame[0] = (byte)(payloadLen >> 16);
        h2Frame[1] = (byte)(payloadLen >> 8);
        h2Frame[2] = (byte)payloadLen;
        h2Frame[3] = frameType;
        h2Frame[4] = flags;
        // 4-byte big-endian stream ID (R bit = 0)
        BinaryPrimitives.WriteUInt32BigEndian(h2Frame.AsSpan(5), streamId & 0x7FFFFFFFU);
        payload.CopyTo(h2Frame.AsSpan(9));

        return _WrapInTcpFrame(h2Frame, srcPort: 50000, dstPort: 8443);
    }

    /// <summary>Wraps a payload in Ethernet + IPv4 + TCP frame (minimal valid header).</summary>
    private static byte[] _WrapInTcpFrame(byte[] payload, ushort srcPort, ushort dstPort)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int tcpSize = 20; // Minimal TCP header (no options)
        int totalSize = ethSize + ipv4Size + tcpSize + payload.Length;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + tcpSize + payload.Length);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);

        // IPv4 header
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        frame[ipOffset + 8] = 64; // TTL
        frame[ipOffset + 9] = 6;  // TCP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 93;
        frame[ipOffset + 17] = 184;
        frame[ipOffset + 18] = 216;
        frame[ipOffset + 19] = 34;
        // Compute IPv4 checksum
        uint sum = 0;
        for (int i = 0; i < ipv4Size; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(ipOffset + i));
        }
        while (sum > 0xFFFF)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 10), (ushort)~sum);

        // TCP header (minimal)
        int tcpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 2), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 4), 1000); // Seq
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 8), 0);    // Ack
        frame[tcpOffset + 12] = 0x50; // Data offset = 5 (20 bytes), no flags in high nibble
        frame[tcpOffset + 13] = 0x18; // ACK + PSH flags
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 14), 65535); // Window

        // Payload
        payload.CopyTo(frame.AsSpan(tcpOffset + tcpSize));

        return frame;
    }

    private static (Stack Stack, Packet Packet) _BuildAndParse(byte[] frameData)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    [Test]
    public async Task Parse_Http2Settings_TypeCorrect()
    {
        // SETTINGS frame (type=4, stream 0, no payload = empty SETTINGS)
        byte[] frameData = _GenerateHttp2Frame(frameType: 4, streamId: 0);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? typeField = stack.GetFieldId("http2.frame.type");
            await Assert.That(typeField).IsNotNull();
            bool has = packet.TryGetFieldValue(typeField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(4UL); // SETTINGS
        }
    }

    [Test]
    public async Task Parse_Http2Headers_StreamId()
    {
        // HEADERS frame (type=1, stream 1)
        byte[] payload = new byte[10]; // Minimal "header block" (garbage for Phase 1)
        byte[] frameData = _GenerateHttp2Frame(frameType: 1, flags: 0x04, streamId: 1, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? streamField = stack.GetFieldId("http2.frame.stream_id");
            await Assert.That(streamField).IsNotNull();
            bool has = packet.TryGetFieldValue(streamField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Parse_Http2Data_LengthCorrect()
    {
        // DATA frame (type=0, stream 3, 100 bytes payload)
        byte[] payload = new byte[100];
        byte[] frameData = _GenerateHttp2Frame(frameType: 0, streamId: 3, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lenField = stack.GetFieldId("http2.frame.length");
            await Assert.That(lenField).IsNotNull();
            bool has = packet.TryGetFieldValue(lenField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(100UL);
        }
    }

    [Test]
    public async Task Parse_Http2_FlagsCorrect()
    {
        // SETTINGS frame with ACK flag (0x01)
        byte[] frameData = _GenerateHttp2Frame(frameType: 4, flags: 0x01, streamId: 0);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? flagsField = stack.GetFieldId("http2.frame.flags");
            await Assert.That(flagsField).IsNotNull();
            bool has = packet.TryGetFieldValue(flagsField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Parse_Http2_PayloadExtracted()
    {
        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
        byte[] frameData = _GenerateHttp2Frame(frameType: 0, streamId: 5, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? payloadField = stack.GetFieldId("http2.frame.payload");
            await Assert.That(payloadField).IsNotNull();
            bool has = packet.TryGetFieldValue(payloadField!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
            await Assert.That(bytesVal.Length).IsEqualTo(4);
        }
    }

    [Test]
    public async Task Parse_Http2_ShortData_NoFields()
    {
        // Only 5 bytes of HTTP/2 data — need at least 9 bytes
        byte[] shortPayload = new byte[5];
        byte[] frameData = _WrapInTcpFrame(shortPayload, 50000, 8443);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? typeField = stack.GetFieldId("http2.frame.type");
            await Assert.That(typeField).IsNotNull();
            bool has = packet.TryGetFieldValue(typeField!.Value, out _, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_Http2_IndexPresence()
    {
        byte[] frameData = _GenerateHttp2Frame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            NetworkInspector.Core.Index.PacketIndex index = new(stack);

            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            ProtocolId? http2Id = stack.GetProtocolId("http2");
            await Assert.That(http2Id).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(http2Id!.Value).Contains(0)).IsTrue();
        }
    }
}
