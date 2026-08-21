// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for ICMPv6 protocol parsing (RFC 4443).
/// Verifies echo request/reply, field extraction, and edge cases.
/// </summary>
internal sealed class Icmpv6ProtocolTests
{
    /// <summary>
    /// Builds a full stack with standard protocols and parses the given frame data.
    /// </summary>
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
    public async Task Parse_Icmpv6EchoRequest_FieldsCorrect()
    {
        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
        byte[] frameData = FrameBuilders.GenerateIcmpv6EchoRequestFrame(
            identifier: 0x5678, sequence: 0x000A, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Verify ICMPv6 protocol detected
            ProtocolId? icmpv6Id = stack.GetProtocolId("icmpv6");
            await Assert.That(icmpv6Id).IsNotNull();

            // Type = 128 (Echo Request)
            FieldId? typeId = stack.GetFieldId("icmpv6.type");
            bool hasType = packet.TryGetFieldValue(typeId!.Value, out FieldValue typeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(128UL);

            // Code = 0
            FieldId? codeId = stack.GetFieldId("icmpv6.code");
            bool hasCode = packet.TryGetFieldValue(codeId!.Value, out FieldValue codeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasCode).IsTrue();
            codeValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0UL);

            // Identifier
            FieldId? identId = stack.GetFieldId("icmpv6.echo.identifier");
            bool hasIdent = packet.TryGetFieldValue(identId!.Value, out FieldValue identValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasIdent).IsTrue();
            identValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(0x5678UL);

            // Sequence number
            FieldId? seqId = stack.GetFieldId("icmpv6.echo.sequence_number");
            bool hasSeq = packet.TryGetFieldValue(seqId!.Value, out FieldValue seqValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasSeq).IsTrue();
            seqValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(10UL);
        }
    }

    [Test]
    public async Task Parse_Icmpv6EchoReply_TypeCorrect()
    {
        byte[] payload = [0x01, 0x02];
        byte[] frameData = FrameBuilders.GenerateIcmpv6EchoReplyFrame(
            identifier: 0x9999, sequence: 0x0003, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Type = 129 (Echo Reply)
            FieldId? typeId = stack.GetFieldId("icmpv6.type");
            bool hasType = packet.TryGetFieldValue(typeId!.Value, out FieldValue typeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(129UL);
        }
    }

    [Test]
    public async Task Parse_Icmpv6EchoRequest_HasChecksum()
    {
        byte[] payload = [0x00, 0x01, 0x02, 0x03];
        byte[] frameData = FrameBuilders.GenerateIcmpv6EchoRequestFrame(
            identifier: 0x0001, sequence: 0x0001, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? checksumId = stack.GetFieldId("icmpv6.checksum");
            bool hasChecksum = packet.TryGetFieldValue(checksumId!.Value, out FieldValue checksumValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasChecksum).IsTrue();
            checksumValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsNotEqualTo(0UL);
        }
    }

    [Test]
    public async Task Parse_Icmpv6Frame_ShortData_DoesNotCrash()
    {
        // Ethernet (14) + IPv6 (40) + only 2 bytes of ICMPv6 (too short, needs at least 4)
        byte[] shortFrame = new byte[56];
        // Ethernet type = IPv6
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(12), 0x86DD);
        // IPv6 version
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(shortFrame.AsSpan(14), 0x60000000);
        // Payload length = 2
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(18), 2);
        // Next header = 58 (ICMPv6)
        shortFrame[20] = 58;
        shortFrame[21] = 64; // Hop limit

        (Stack stack, Packet packet) = _BuildAndParse(shortFrame);
        using (stack)
        {
            await Assert.That(packet.IsFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_Icmpv6Frame_IndexPresence()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frameData = FrameBuilders.GenerateIcmpv6EchoRequestFrame(
            identifier: 0x5678, sequence: 0x000A, payload: payload);

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0), Timestamp.FromSecs(0), frameData,
                LinkType.Ethernet, FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            NetworkInspector.Core.Index.PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // ICMPv6 protocol should be in the index
            ProtocolId? icmpv6Id = stack.GetProtocolId("icmpv6");
            await Assert.That(icmpv6Id).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(icmpv6Id!.Value).Contains(0)).IsTrue();

            // Main ICMPv6 fields should be indexed
            FieldId? typeId = stack.GetFieldId("icmpv6.type");
            await Assert.That(typeId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(typeId!.Value).Contains(0)).IsTrue();
        }
    }
}
