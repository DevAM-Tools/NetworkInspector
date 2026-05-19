// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for LIN protocol parsing (ISO 17987, DLT_LIN format).
/// </summary>
internal sealed class LinProtocolTests
{
    /// <summary>
    /// Builds a stack and parses a LIN frame (link type 212).
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
            LinkType.Lin,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    [Test]
    public async Task Parse_Lin_PidCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateLinFrame(pid: 0x3C);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? pidField = stack.GetFieldId("lin.pid");
            await Assert.That(pidField).IsNotNull();
            bool has = packet.TryGetFieldValue(pidField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x3CUL);
        }
    }

    [Test]
    public async Task Parse_Lin_FrameIdExtracted()
    {
        // PID = 0x3C → Frame ID = 0x3C & 0x3F = 0x3C
        byte[] frameData = FrameBuilders.GenerateLinFrame(pid: 0x3C);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? idField = stack.GetFieldId("lin.id");
            await Assert.That(idField).IsNotNull();
            bool has = packet.TryGetFieldValue(idField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x3CUL);
        }
    }

    [Test]
    public async Task Parse_Lin_ParityBits()
    {
        // PID = 0xFC → Parity = (0xFC >> 6) & 0x03 = 3
        byte[] frameData = FrameBuilders.GenerateLinFrame(pid: 0xFC);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? parityField = stack.GetFieldId("lin.parity");
            await Assert.That(parityField).IsNotNull();
            bool has = packet.TryGetFieldValue(parityField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(3UL);
        }
    }

    [Test]
    public async Task Parse_Lin_DataLength()
    {
        byte[] payload = [0x11, 0x22, 0x33, 0x44];
        byte[] frameData = FrameBuilders.GenerateLinFrame(payload: payload);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lengthField = stack.GetFieldId("lin.length");
            await Assert.That(lengthField).IsNotNull();
            bool has = packet.TryGetFieldValue(lengthField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(4UL);
        }
    }

    [Test]
    public async Task Parse_Lin_Checksum()
    {
        byte[] frameData = FrameBuilders.GenerateLinFrame(checksum: 0xAB);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? checksumField = stack.GetFieldId("lin.checksum");
            await Assert.That(checksumField).IsNotNull();
            bool has = packet.TryGetFieldValue(checksumField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xABUL);
        }
    }

    [Test]
    public async Task Parse_Lin_ShortData_NoFields()
    {
        byte[] shortFrame = [0x00, 0x3C]; // Only 2 bytes

        (Stack stack, Packet packet) = BuildAndParse(shortFrame);
        using (stack)
        {
            FieldId? pidField = stack.GetFieldId("lin.pid");
            await Assert.That(pidField).IsNotNull();
            bool has = packet.TryGetFieldValue(pidField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_Lin_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateLinFrame();

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
                LinkType.Lin,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            ProtocolId? linId = stack.GetProtocolId("lin");
            await Assert.That(linId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(linId!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_Lin_ChecksumType_Enhanced()
    {
        // Default frame generator uses checksum type 2 (enhanced)
        byte[] frameData = FrameBuilders.GenerateLinFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? field = stack.GetFieldId("lin.checksum_type");
            await Assert.That(field).IsNotNull();
            bool has = packet.TryGetFieldValue(field!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("Enhanced");
        }
    }

    [Test]
    public async Task Parse_Lin_ParityValid_CorrectParity()
    {
        // PID 0x3C: frame ID = 0x3C (6 bits), parity bits = 0 (bits 7-6)
        // P0 = ID0^ID1^ID2^ID4 = 0^0^1^1 = 0
        // P1 = !(ID1^ID3^ID4^ID5) = !(0^1^1^1) = !(1) = 0
        // So parity = 0b00, PID upper bits = 0 → parity is correct for 0x3C
        byte[] frameData = FrameBuilders.GenerateLinFrame(pid: 0x3C);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? field = stack.GetFieldId("lin.parity.valid");
            await Assert.That(field).IsNotNull();
            bool has = packet.TryGetFieldValue(field!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_Lin_ParityValid_WrongParity()
    {
        // PID with deliberately wrong parity:
        // Frame ID = 0x01 (bits 0-5 = 000001)
        // P0 = ID0^ID1^ID2^ID4 = 1^0^0^0 = 1
        // P1 = !(ID1^ID3^ID4^ID5) = !(0^0^0^0) = 1
        // Correct PID should be 0b11_000001 = 0xC1
        // Use 0x01 (parity=0b00) which is wrong
        byte[] frameData = FrameBuilders.GenerateLinFrame(pid: 0x01);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? field = stack.GetFieldId("lin.parity.valid");
            await Assert.That(field).IsNotNull();
            bool has = packet.TryGetFieldValue(field!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
        }
    }
}