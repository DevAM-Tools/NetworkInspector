// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for WebSocket protocol parsing (RFC 6455).
/// Verifies frame header fields, masking/unmasking, and variable-length handling.
/// </summary>
internal sealed class WebSocketProtocolTests
{
    /// <summary>
    /// Generates a WebSocket frame wrapped in Ethernet + IPv4 + TCP (port 80).
    /// </summary>
    private static byte[] _GenerateWebSocketFrame(
        byte opcode = 1,
        bool fin = true,
        bool masked = false,
        byte[]? payload = null,
        uint maskingKey = 0xAABBCCDD)
    {
        payload ??= [];

        // Build WebSocket frame header
        List<byte> wsFrame = [];

        // First byte: FIN + RSV(000) + opcode
        byte firstByte = (byte)((fin ? 0x80 : 0x00) | (opcode & 0x0F));
        wsFrame.Add(firstByte);

        // Second byte: MASK + payload length indicator
        int payloadLen = payload.Length;
        byte maskBit = masked ? (byte)0x80 : (byte)0x00;

        if (payloadLen <= 125)
        {
            wsFrame.Add((byte)(maskBit | payloadLen));
        }
        else if (payloadLen <= 65535)
        {
            wsFrame.Add((byte)(maskBit | 126));
            wsFrame.Add((byte)(payloadLen >> 8));
            wsFrame.Add((byte)payloadLen);
        }
        else
        {
            wsFrame.Add((byte)(maskBit | 127));
            ulong len = (ulong)payloadLen;
            for (int i = 56; i >= 0; i -= 8)
            {
                wsFrame.Add((byte)(len >> i));
            }
        }

        // Masking key (4 bytes, if masked)
        if (masked)
        {
            wsFrame.Add((byte)(maskingKey >> 24));
            wsFrame.Add((byte)(maskingKey >> 16));
            wsFrame.Add((byte)(maskingKey >> 8));
            wsFrame.Add((byte)maskingKey);
        }

        // Payload (XOR-masked if masked flag is set)
        if (masked && payloadLen > 0)
        {
            byte[] maskBytes =
            [
                (byte)(maskingKey >> 24),
                (byte)(maskingKey >> 16),
                (byte)(maskingKey >> 8),
                (byte)maskingKey
            ];
            for (int i = 0; i < payloadLen; i++)
            {
                wsFrame.Add((byte)(payload[i] ^ maskBytes[i & 3]));
            }
        }
        else
        {
            wsFrame.AddRange(payload);
        }

        return _WrapInTcpFrame([.. wsFrame], srcPort: 50000, dstPort: 80);
    }

    /// <summary>Wraps a payload in Ethernet + IPv4 + TCP frame (minimal valid header).</summary>
    private static byte[] _WrapInTcpFrame(byte[] payload, ushort srcPort, ushort dstPort)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int tcpSize = 20;
        int totalSize = ethSize + ipv4Size + tcpSize + payload.Length;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + tcpSize + payload.Length);

        // Ethernet
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

        // IPv4
        int ipOffset = ethSize;
        frame[ipOffset] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipTotalLen);
        frame[ipOffset + 8] = 64;
        frame[ipOffset + 9] = 6; // TCP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 93;
        frame[ipOffset + 17] = 184;
        frame[ipOffset + 18] = 216;
        frame[ipOffset + 19] = 34;
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

        // TCP
        int tcpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 2), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 4), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(tcpOffset + 8), 0);
        frame[tcpOffset + 12] = 0x50;
        frame[tcpOffset + 13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 14), 65535);

        payload.CopyTo(frame.AsSpan(tcpOffset + tcpSize));
        return frame;
    }

    private static (Stack Stack, Packet Packet) _BuildAndParse(byte[] frameData)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);

        // Register WebSocket at tcp.port=80 for direct dispatch testing.
        // In production, WebSocket is dispatched via http.upgrade after an HTTP 101 handshake.
        ProtocolId wsId = builder.GetProtocolId("websocket")!.Value;
        builder.RegisterParserInU64TableByName("tcp.port", 80, wsId);

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
    public async Task Parse_TextFrame_OpcodeCorrect()
    {
        byte[] payload = "Hello"u8.ToArray();
        byte[] frameData = _GenerateWebSocketFrame(opcode: 1, fin: true, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? opcodeField = stack.GetFieldId("websocket.opcode");
            await Assert.That(opcodeField).IsNotNull();
            bool has = packet.TryGetFieldValue(opcodeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL); // Text
        }
    }

    [Test]
    public async Task Parse_TextFrame_FinFlag()
    {
        byte[] frameData = _GenerateWebSocketFrame(opcode: 1, fin: true, payload: "Hi"u8.ToArray());

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? finField = stack.GetFieldId("websocket.fin");
            await Assert.That(finField).IsNotNull();
            bool has = packet.TryGetFieldValue(finField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_TextFrame_PayloadLength()
    {
        byte[] payload = new byte[50];
        byte[] frameData = _GenerateWebSocketFrame(opcode: 2, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lenField = stack.GetFieldId("websocket.payload_length");
            await Assert.That(lenField).IsNotNull();
            bool has = packet.TryGetFieldValue(lenField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(50UL);
        }
    }

    [Test]
    public async Task Parse_MaskedFrame_MaskingKeyPresent()
    {
        byte[] payload = "Hello"u8.ToArray();
        byte[] frameData = _GenerateWebSocketFrame(opcode: 1, masked: true, payload: payload, maskingKey: 0x12345678);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? maskField = stack.GetFieldId("websocket.mask");
            await Assert.That(maskField).IsNotNull();
            bool hasMask = packet.TryGetFieldValue(maskField!.Value, out FieldValue maskVal);
            await Assert.That(hasMask).IsTrue();
            maskVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            FieldId? keyField = stack.GetFieldId("websocket.masking_key");
            await Assert.That(keyField).IsNotNull();
            bool hasKey = packet.TryGetFieldValue(keyField!.Value, out FieldValue keyVal);
            await Assert.That(hasKey).IsTrue();
            keyVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x12345678UL);
        }
    }

    [Test]
    public async Task Parse_MaskedFrame_PayloadUnmasked()
    {
        // "Hello" = [0x48, 0x65, 0x6C, 0x6C, 0x6F]
        byte[] payload = "Hello"u8.ToArray();
        uint maskingKey = 0xAABBCCDD;
        byte[] frameData = _GenerateWebSocketFrame(opcode: 1, masked: true, payload: payload, maskingKey: maskingKey);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? payloadField = stack.GetFieldId("websocket.payload");
            await Assert.That(payloadField).IsNotNull();
            bool has = packet.TryGetFieldValue(payloadField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
            // After unmasking, we should get back the original "Hello"
            await Assert.That(bytesVal.Span.SequenceEqual("Hello"u8)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CloseFrame_Opcode()
    {
        byte[] frameData = _GenerateWebSocketFrame(opcode: 8, fin: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? opcodeField = stack.GetFieldId("websocket.opcode");
            await Assert.That(opcodeField).IsNotNull();
            bool has = packet.TryGetFieldValue(opcodeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(8UL); // Close
        }
    }

    [Test]
    public async Task Parse_Extended16BitLength()
    {
        // 200 bytes payload triggers 16-bit extended length (> 125)
        byte[] payload = new byte[200];
        byte[] frameData = _GenerateWebSocketFrame(opcode: 2, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lenField = stack.GetFieldId("websocket.payload_length");
            await Assert.That(lenField).IsNotNull();
            bool has = packet.TryGetFieldValue(lenField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(200UL);
        }
    }

    [Test]
    public async Task Parse_ShortData_NoFields()
    {
        // Only 1 byte — need at least 2
        byte[] shortPayload = [0x81]; // Looks like FIN + text opcode but no second byte
        byte[] frameData = _WrapInTcpFrame(shortPayload, 50000, 80);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? opcodeField = stack.GetFieldId("websocket.opcode");
            await Assert.That(opcodeField).IsNotNull();
            bool has = packet.TryGetFieldValue(opcodeField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_IndexPresence()
    {
        byte[] frameData = _GenerateWebSocketFrame(opcode: 1, payload: "Hi"u8.ToArray());

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);

        // Register WebSocket at tcp.port=80 for direct dispatch testing.
        // In production, WebSocket is dispatched via http.upgrade after an HTTP 101 handshake.
        ProtocolId wsProtocolId = builder.GetProtocolId("websocket")!.Value;
        builder.RegisterParserInU64TableByName("tcp.port", 80, wsProtocolId);

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

            ProtocolId? wsId = stack.GetProtocolId("websocket");
            await Assert.That(wsId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(wsId!.Value).Contains(0)).IsTrue();
        }
    }
}
