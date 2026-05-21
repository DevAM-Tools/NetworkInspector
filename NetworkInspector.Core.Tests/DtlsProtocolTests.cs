// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for DTLS protocol parsing (RFC 6347/9147).
/// Verifies record layer field extraction with epoch and sequence number.
/// </summary>
internal sealed class DtlsProtocolTests
{
    /// <summary>
    /// Generates a DTLS record wrapped in Ethernet + IPv4 + UDP (port 443).
    /// </summary>
    private static byte[] GenerateDtlsRecord(
        byte contentType = 22,
        ushort version = 0xFEFD,
        ushort epoch = 0,
        ulong sequenceNumber = 0,
        byte[]? payload = null)
    {
        payload ??= new byte[20]; // Minimal handshake-like payload

        // DTLS record header: 13 bytes
        byte[] dtlsRecord = new byte[13 + payload.Length];
        dtlsRecord[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(dtlsRecord.AsSpan(1), version);
        BinaryPrimitives.WriteUInt16BigEndian(dtlsRecord.AsSpan(3), epoch);
        // 48-bit sequence number (big-endian in bytes 5-10)
        dtlsRecord[5] = (byte)(sequenceNumber >> 40);
        dtlsRecord[6] = (byte)(sequenceNumber >> 32);
        dtlsRecord[7] = (byte)(sequenceNumber >> 24);
        dtlsRecord[8] = (byte)(sequenceNumber >> 16);
        dtlsRecord[9] = (byte)(sequenceNumber >> 8);
        dtlsRecord[10] = (byte)sequenceNumber;
        BinaryPrimitives.WriteUInt16BigEndian(dtlsRecord.AsSpan(11), (ushort)payload.Length);
        payload.CopyTo(dtlsRecord.AsSpan(13));

        // Wrap in Ethernet + IPv4 + UDP(port 443)
        return WrapInUdpFrame(dtlsRecord, srcPort: 54321, dstPort: 443);
    }

    /// <summary>
    /// Generates a DTLS Client Hello record for testing.
    /// </summary>
    private static byte[] GenerateDtlsClientHello()
    {
        // Minimal DTLS Client Hello handshake body
        // DTLS handshake header: type(1) + length(3) + message_seq(2) + fragment_offset(3) + fragment_length(3) = 12 bytes
        byte[] hsBody = new byte[12];
        hsBody[0] = 1; // Client Hello
        hsBody[1] = 0;
        hsBody[2] = 0;
        hsBody[3] = 0; // Length = 0 (minimal)
        // message_seq, fragment_offset, fragment_length left as 0

        return GenerateDtlsRecord(contentType: 22, payload: hsBody);
    }

    /// <summary>Wraps a payload in Ethernet + IPv4 + UDP frame.</summary>
    private static byte[] WrapInUdpFrame(byte[] payload, ushort srcPort, ushort dstPort)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        int totalSize = ethSize + ipv4Size + udpSize + payload.Length;
        byte[] frame = new byte[totalSize];

        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payload.Length);
        ushort udpLen = (ushort)(udpSize + payload.Length);

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
        frame[ipOffset + 9] = 17; // UDP
        frame[ipOffset + 12] = 192;
        frame[ipOffset + 13] = 168;
        frame[ipOffset + 14] = 1;
        frame[ipOffset + 15] = 100;
        frame[ipOffset + 16] = 93;
        frame[ipOffset + 17] = 184;
        frame[ipOffset + 18] = 216;
        frame[ipOffset + 19] = 34;
        // Compute IPv4 header checksum inline
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

        // UDP header
        int udpOffset = ipOffset + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4), udpLen);

        // Payload
        payload.CopyTo(frame.AsSpan(udpOffset + udpSize));

        return frame;
    }

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
    public async Task Parse_DtlsRecord_ContentTypeCorrect()
    {
        byte[] frameData = GenerateDtlsRecord(contentType: 22); // Handshake

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? ctField = stack.GetFieldId("dtls.record.content_type");
            await Assert.That(ctField).IsNotNull();
            bool has = packet.TryGetFieldValue(ctField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(22UL);
        }
    }

    [Test]
    public async Task Parse_DtlsRecord_VersionCorrect()
    {
        byte[] frameData = GenerateDtlsRecord(version: 0xFEFD); // DTLS 1.2

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? verField = stack.GetFieldId("dtls.record.version");
            await Assert.That(verField).IsNotNull();
            bool has = packet.TryGetFieldValue(verField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xFEFDUL);
        }
    }

    [Test]
    public async Task Parse_DtlsRecord_EpochCorrect()
    {
        byte[] frameData = GenerateDtlsRecord(epoch: 1);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? epochField = stack.GetFieldId("dtls.record.epoch");
            await Assert.That(epochField).IsNotNull();
            bool has = packet.TryGetFieldValue(epochField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Parse_DtlsRecord_SequenceNumber()
    {
        byte[] frameData = GenerateDtlsRecord(sequenceNumber: 42);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? seqField = stack.GetFieldId("dtls.record.sequence_number");
            await Assert.That(seqField).IsNotNull();
            bool has = packet.TryGetFieldValue(seqField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(42UL);
        }
    }

    [Test]
    public async Task Parse_DtlsHandshake_ClientHello()
    {
        byte[] frameData = GenerateDtlsClientHello();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? hsTypeField = stack.GetFieldId("dtls.handshake.type");
            await Assert.That(hsTypeField).IsNotNull();
            bool has = packet.TryGetFieldValue(hsTypeField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL); // Client Hello
        }
    }

    [Test]
    public async Task Parse_DtlsRecord_ShortData_NoFields()
    {
        // Only 5 bytes of DTLS data — need at least 13 for DTLS record header
        byte[] shortPayload = new byte[5];
        byte[] frameData = WrapInUdpFrame(shortPayload, 54321, 443);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? ctField = stack.GetFieldId("dtls.record.content_type");
            await Assert.That(ctField).IsNotNull();
            bool has = packet.TryGetFieldValue(ctField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_DtlsRecord_IndexPresence()
    {
        byte[] frameData = GenerateDtlsRecord();

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

            ProtocolId? dtlsId = stack.GetProtocolId("dtls");
            await Assert.That(dtlsId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(dtlsId!.Value).Contains(0)).IsTrue();
        }
    }
}
