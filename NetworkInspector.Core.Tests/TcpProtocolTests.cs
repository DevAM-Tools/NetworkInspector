// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetworkInspector.Core.Index;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for TCP protocol parsing (RFC 793).
/// Verifies field extraction, flag parsing, checksum validation, and edge cases.
/// </summary>
internal sealed class TcpProtocolTests
{
    /// <summary>
    /// Builds a full stack and parses the given Ethernet frame data.
    /// </summary>
    private static (Stack Stack, Packet Packet) BuildAndParse(byte[] frameData)
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
    public async Task Parse_TcpSyn_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTcpSynFrame(
            srcPort: 12345, dstPort: 80, seq: 1000);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Verify TCP protocol is detected
            ProtocolId? tcpId = stack.GetProtocolId("tcp");
            await Assert.That(tcpId).IsNotNull();

            // Source port
            FieldId? srcPortId = stack.GetFieldId("tcp.srcport");
            await Assert.That(srcPortId).IsNotNull();
            bool hasSrcPort = packet.TryGetFieldValue(srcPortId!.Value, out FieldValue srcPortVal);
            await Assert.That(hasSrcPort).IsTrue();
            srcPortVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(12345UL);

            // Destination port
            FieldId? dstPortId = stack.GetFieldId("tcp.dstport");
            bool hasDstPort = packet.TryGetFieldValue(dstPortId!.Value, out FieldValue dstPortVal);
            await Assert.That(hasDstPort).IsTrue();
            dstPortVal.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(80UL);

            // Sequence number
            FieldId? seqId = stack.GetFieldId("tcp.seq");
            bool hasSeq = packet.TryGetFieldValue(seqId!.Value, out FieldValue seqVal);
            await Assert.That(hasSeq).IsTrue();
            seqVal.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(1000UL);

            // ACK number is 0 for SYN
            FieldId? ackId = stack.GetFieldId("tcp.ack");
            bool hasAck = packet.TryGetFieldValue(ackId!.Value, out FieldValue ackVal);
            await Assert.That(hasAck).IsTrue();
            ackVal.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(0UL);

            // Header length = 20 (data offset = 5)
            FieldId? hdrLenId = stack.GetFieldId("tcp.hdr_len");
            bool hasHdrLen = packet.TryGetFieldValue(hdrLenId!.Value, out FieldValue hdrLenVal);
            await Assert.That(hasHdrLen).IsTrue();
            hdrLenVal.Data.TryGetAsU64(out ulong u64Val5);
            await Assert.That(u64Val5).IsEqualTo(20UL);

            // SYN flag must be set
            FieldId? synFlagId = stack.GetFieldId("tcp.flags.syn");
            bool hasSynFlag = packet.TryGetFieldValue(synFlagId!.Value, out FieldValue synFlagVal);
            await Assert.That(hasSynFlag).IsTrue();
            synFlagVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // ACK flag must not be set for pure SYN
            FieldId? ackFlagId = stack.GetFieldId("tcp.flags.ack");
            bool hasAckFlag = packet.TryGetFieldValue(ackFlagId!.Value, out FieldValue ackFlagVal);
            await Assert.That(hasAckFlag).IsTrue();
            ackFlagVal.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsFalse();

            // Payload length should be 0
            FieldId? lenId = stack.GetFieldId("tcp.len");
            bool hasLen = packet.TryGetFieldValue(lenId!.Value, out FieldValue lenVal);
            await Assert.That(hasLen).IsTrue();
            lenVal.Data.TryGetAsU64(out ulong u64Val6);
            await Assert.That(u64Val6).IsEqualTo(0UL);

            // Window size
            FieldId? windowId = stack.GetFieldId("tcp.window_size_value");
            bool hasWindow = packet.TryGetFieldValue(windowId!.Value, out FieldValue windowVal);
            await Assert.That(hasWindow).IsTrue();
            windowVal.Data.TryGetAsU64(out ulong u64Val7);
            await Assert.That(u64Val7).IsEqualTo(65535UL);
        }
    }

    [Test]
    public async Task Parse_TcpSynAck_FlagsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTcpSynAckFrame(
            srcPort: 80, dstPort: 12345, seq: 2000, ack: 1001);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // SYN flag
            FieldId? synFlagId = stack.GetFieldId("tcp.flags.syn");
            packet.TryGetFieldValue(synFlagId!.Value, out FieldValue synFlagVal);
            synFlagVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // ACK flag
            FieldId? ackFlagId = stack.GetFieldId("tcp.flags.ack");
            packet.TryGetFieldValue(ackFlagId!.Value, out FieldValue ackFlagVal);
            ackFlagVal.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();

            // FIN, RST, PSH must be false
            FieldId? finFlagId = stack.GetFieldId("tcp.flags.fin");
            packet.TryGetFieldValue(finFlagId!.Value, out FieldValue finFlagVal);
            finFlagVal.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal3).IsFalse();

            FieldId? rstFlagId = stack.GetFieldId("tcp.flags.reset");
            packet.TryGetFieldValue(rstFlagId!.Value, out FieldValue rstFlagVal);
            rstFlagVal.Data.TryGetAsBool(out bool boolVal4);
            await Assert.That(boolVal4).IsFalse();
        }
    }

    [Test]
    public async Task Parse_TcpData_PayloadLenCorrect()
    {
        byte[] payload = new byte[100];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        byte[] frameData = FrameBuilders.GenerateTcpDataFrame(
            srcPort: 12345, dstPort: 443, seq: 5000, ack: 6000, payload: payload);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Payload length
            FieldId? lenId = stack.GetFieldId("tcp.len");
            packet.TryGetFieldValue(lenId!.Value, out FieldValue lenVal);
            lenVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(100UL);

            // PSH flag set
            FieldId? pshFlagId = stack.GetFieldId("tcp.flags.push");
            packet.TryGetFieldValue(pshFlagId!.Value, out FieldValue pshFlagVal);
            pshFlagVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // ACK flag set
            FieldId? ackFlagId = stack.GetFieldId("tcp.flags.ack");
            packet.TryGetFieldValue(ackFlagId!.Value, out FieldValue ackFlagVal);
            ackFlagVal.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();

            // Sequence number
            FieldId? seqId = stack.GetFieldId("tcp.seq");
            packet.TryGetFieldValue(seqId!.Value, out FieldValue seqVal);
            seqVal.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(5000UL);

            // Ack number
            FieldId? ackId = stack.GetFieldId("tcp.ack");
            packet.TryGetFieldValue(ackId!.Value, out FieldValue ackVal);
            ackVal.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(6000UL);
        }
    }

    [Test]
    public async Task Parse_TcpFinAck_FlagsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTcpFinAckFrame(
            srcPort: 12345, dstPort: 80, seq: 1001, ack: 2001);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? finFlagId = stack.GetFieldId("tcp.flags.fin");
            packet.TryGetFieldValue(finFlagId!.Value, out FieldValue finFlagVal);
            finFlagVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            FieldId? ackFlagId = stack.GetFieldId("tcp.flags.ack");
            packet.TryGetFieldValue(ackFlagId!.Value, out FieldValue ackFlagVal);
            ackFlagVal.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();
        }
    }

    [Test]
    public async Task Parse_TcpRst_FlagCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTcpRstFrame(
            srcPort: 12345, dstPort: 80, seq: 1001);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? rstFlagId = stack.GetFieldId("tcp.flags.reset");
            packet.TryGetFieldValue(rstFlagId!.Value, out FieldValue rstFlagVal);
            rstFlagVal.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            FieldId? synFlagId = stack.GetFieldId("tcp.flags.syn");
            packet.TryGetFieldValue(synFlagId!.Value, out FieldValue synFlagVal);
            synFlagVal.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsFalse();
        }
    }

    [Test]
    public async Task Parse_TcpFrame_ShortData_DoesNotCrash()
    {
        // Ethernet + IPv4 header but only 10 bytes of TCP data (too short for 20-byte header)
        byte[] shortFrame = new byte[44]; // 14 eth + 20 ip + 10 tcp (incomplete)
        // Ethernet
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(12), 0x0800);
        // IPv4
        shortFrame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(16), 30); // total len = 20+10
        shortFrame[23] = 6; // Protocol: TCP
        shortFrame[26] = 192;
        shortFrame[27] = 168;
        shortFrame[28] = 1;
        shortFrame[29] = 1;
        shortFrame[30] = 192;
        shortFrame[31] = 168;
        shortFrame[32] = 1;
        shortFrame[33] = 2;

        (Stack stack, Packet packet) = BuildAndParse(shortFrame);
        using (stack)
        {
            // TCP should not have parsed any fields (data too short)
            FieldId? srcPortId = stack.GetFieldId("tcp.srcport");
            bool hasSrcPort = packet.TryGetFieldValue(srcPortId!.Value, out _);
            await Assert.That(hasSrcPort).IsFalse();
        }
    }

    [Test]
    public async Task Parse_TcpFrame_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateTcpSynFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // TCP fields should be indexed
            FieldId? srcPortId = stack.GetFieldId("tcp.srcport");
            await Assert.That(srcPortId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(srcPortId!.Value).Contains(0)).IsTrue();

            // Payload should NOT be present for SYN (no payload)
            FieldId? payloadId = stack.GetFieldId("tcp.payload");
            await Assert.That(payloadId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(payloadId!.Value).Contains(0)).IsFalse();
        }
    }

    [Test]
    public async Task Parse_TcpData_HasPayloadIndex()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frameData = FrameBuilders.GenerateTcpDataFrame(
            srcPort: 4000, dstPort: 5000, seq: 100, ack: 200, payload: payload);

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // Payload IS present since we have data
            FieldId? payloadId = stack.GetFieldId("tcp.payload");
            await Assert.That(index.GetFieldBitmap(payloadId!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_TcpSyn_ChecksumFieldPresent()
    {
        byte[] frameData = FrameBuilders.GenerateTcpSynFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Checksum field should always be present
            FieldId? csumId = stack.GetFieldId("tcp.checksum");
            await Assert.That(csumId).IsNotNull();
            bool hasCsum = packet.TryGetFieldValue(csumId!.Value, out FieldValue csumVal);
            await Assert.That(hasCsum).IsTrue();
            // Checksum value should be non-zero (correctly computed)
            csumVal.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsNotEqualTo(0UL);
        }
    }
}