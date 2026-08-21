// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for ICMP protocol parsing (RFC 792).
/// Verifies echo request/reply, destination unreachable, checksum validation, and edge cases.
/// </summary>
internal sealed class IcmpProtocolTests
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
    public async Task Parse_IcmpEchoRequest_FieldsCorrect()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frameData = FrameBuilders.GenerateIcmpEchoRequestFrame(
            identifier: 0x1234, sequence: 0x0001, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Verify ICMP protocol detected
            ProtocolId? icmpId = stack.GetProtocolId("icmp");
            await Assert.That(icmpId).IsNotNull();

            // Type = 8 (Echo Request)
            FieldId? typeId = stack.GetFieldId("icmp.type");
            bool hasType = packet.TryGetFieldValue(typeId!.Value, out FieldValue typeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(8UL);

            // Code = 0
            FieldId? codeId = stack.GetFieldId("icmp.code");
            bool hasCode = packet.TryGetFieldValue(codeId!.Value, out FieldValue codeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasCode).IsTrue();
            codeValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0UL);

            // Identifier
            FieldId? identId = stack.GetFieldId("icmp.ident");
            bool hasIdent = packet.TryGetFieldValue(identId!.Value, out FieldValue identValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasIdent).IsTrue();
            identValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(0x1234UL);

            // Sequence
            FieldId? seqId = stack.GetFieldId("icmp.seq");
            bool hasSeq = packet.TryGetFieldValue(seqId!.Value, out FieldValue seqValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasSeq).IsTrue();
            seqValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Parse_IcmpEchoReply_TypeCorrect()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frameData = FrameBuilders.GenerateIcmpEchoReplyFrame(
            identifier: 0xABCD, sequence: 0x0005, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Type = 0 (Echo Reply)
            FieldId? typeId = stack.GetFieldId("icmp.type");
            bool hasType = packet.TryGetFieldValue(typeId!.Value, out FieldValue typeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0UL);

            // Identifier preserved
            FieldId? identId = stack.GetFieldId("icmp.ident");
            bool hasIdent = packet.TryGetFieldValue(identId!.Value, out FieldValue identValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasIdent).IsTrue();
            identValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0xABCDUL);
        }
    }

    [Test]
    public async Task Parse_IcmpDestUnreachable_NoEchoFields()
    {
        byte[] frameData = FrameBuilders.GenerateIcmpDestUnreachFrame(code: 1); // Host unreachable

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Type = 3 (Dest Unreachable)
            FieldId? typeId = stack.GetFieldId("icmp.type");
            bool hasType = packet.TryGetFieldValue(typeId!.Value, out FieldValue typeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(3UL);

            // Code = 1 (Host Unreachable)
            FieldId? codeId = stack.GetFieldId("icmp.code");
            bool hasCode = packet.TryGetFieldValue(codeId!.Value, out FieldValue codeValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasCode).IsTrue();
            codeValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(1UL);

            // Echo fields should NOT be present for Dest Unreachable
            FieldId? identId = stack.GetFieldId("icmp.ident");
            bool hasIdent = packet.TryGetFieldValue(identId!.Value, out FieldValue _, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasIdent).IsFalse();
        }
    }

    [Test]
    public async Task Parse_IcmpEchoRequest_HasChecksum()
    {
        byte[] payload = [0x00, 0x01, 0x02, 0x03];
        byte[] frameData = FrameBuilders.GenerateIcmpEchoRequestFrame(
            identifier: 0x0001, sequence: 0x0001, payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Checksum field should be present
            FieldId? checksumId = stack.GetFieldId("icmp.checksum");
            bool hasChecksum = packet.TryGetFieldValue(checksumId!.Value, out FieldValue checksumValue, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(hasChecksum).IsTrue();
            // Value should be non-zero (valid checksum from FrameBuilder)
            checksumValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsNotEqualTo(0UL);
        }
    }

    [Test]
    public async Task Parse_IcmpFrame_ShortData_DoesNotCrash()
    {
        // Ethernet (14) + IPv4 (20) + only 2 bytes of ICMP (too short, needs at least 4)
        byte[] shortFrame = new byte[36];
        shortFrame[12] = 0x08;
        shortFrame[13] = 0x00; // eth type = IPv4
        shortFrame[14] = 0x45; // IPv4 version + IHL
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(16), 22); // total len
        shortFrame[23] = 1; // Protocol: ICMP

        (Stack stack, Packet packet) = _BuildAndParse(shortFrame);
        using (stack)
        {
            await Assert.That(packet.IsFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_IcmpFrame_IndexPresence()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frameData = FrameBuilders.GenerateIcmpEchoRequestFrame(
            identifier: 0x1234, sequence: 0x0001, payload: payload);

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

            // ICMP protocol should be in the index
            ProtocolId? icmpId = stack.GetProtocolId("icmp");
            await Assert.That(icmpId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(icmpId!.Value).Contains(0)).IsTrue();

            // Main ICMP fields should be indexed ("icmp" group)
            FieldId? typeId = stack.GetFieldId("icmp.type");
            await Assert.That(typeId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(typeId!.Value).Contains(0)).IsTrue();

            // Echo-specific fields should be indexed ("icmp.echo" group)
            FieldId? identId = stack.GetFieldId("icmp.ident");
            await Assert.That(identId).IsNotNull();
            await Assert.That(index.GetFieldBitmap(identId!.Value).Contains(0)).IsTrue();
        }
    }
}
