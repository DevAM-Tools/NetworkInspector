// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Unit tests for <see cref="FlexRayParser"/> covering Types 29/41/50/66,
/// payload-length validation, and oversize rejection.
/// </summary>
internal sealed class FlexRayParserTests
{
    // ========================================================================
    // Type 29 — FLEXRAY_DATA
    // ========================================================================

    [Test]
    public async Task Type29_ValidPayload_Parsed()
    {
        byte[] payload = new byte[9 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[2] = 0x01; // mux channel A
        payload[3] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x0A);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 0x1234);
        payload[9] = 0x11;
        payload[10] = 0x22;
        payload[11] = 0x33;
        payload[12] = 0x44;

        bool ok = FlexRayParser.TryParseFlexRayData(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        await Assert.That(frame.Length).IsGreaterThanOrEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> data);
        byte[] dataBytes = data.ToArray();
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x0A);
        await Assert.That(dataBytes.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Type29_DeclaredLength255_ReturnsFalse()
    {
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[3] = 255;

        bool ok = FlexRayParser.TryParseFlexRayData(payload, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Type29_HeaderTooShort_ReturnsFalse()
    {
        bool ok = FlexRayParser.TryParseFlexRayData([0x01, 0x00, 0x01], out _, out _);

        await Assert.That(ok).IsFalse();
    }

    // ========================================================================
    // Type 41 — FLEXRAY_MESSAGE
    // ========================================================================

    [Test]
    public async Task Type41_ValidPayload_Parsed()
    {
        byte[] payload = new byte[32 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 0xABCD);
        payload[26] = 2;
        payload[27] = 5;
        payload[28] = 0x08; // sync
        payload[32] = 0xDE;
        payload[33] = 0xAD;

        bool ok = FlexRayParser.TryParseFlexRayMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)2);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> data);
        byte[] dataBytes = data.ToArray();
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x20);
        await Assert.That(fields.Cycle).IsEqualTo((byte)5);
        await Assert.That(fields.Sfi).IsTrue();
        await Assert.That(dataBytes.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Type41_DeclaredLength300_ReturnsFalse()
    {
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[26] = 255;
        payload[27] = 0;
        payload[28] = 0;

        bool ok = FlexRayParser.TryParseFlexRayMessage(payload, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    // ========================================================================
    // Type 50 — FLEXRAY_RCVMESSAGE
    // ========================================================================

    [Test]
    public async Task Type50_ValidPayload_Parsed()
    {
        byte[] payload = new byte[44 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26), 7);
        payload[44] = 0x11;
        payload[51] = 0x88;

        bool ok = FlexRayParser.TryParseFlexRayRcvMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> data);
        byte[] dataBytes = data.ToArray();
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)42);
        await Assert.That(fields.Cycle).IsEqualTo((byte)7);
        await Assert.That(dataBytes.Length).IsEqualTo(8);
    }

    [Test]
    public async Task Type50_EmptyPayload_Parsed()
    {
        byte[] payload = new byte[44];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 99);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 0);

        bool ok = FlexRayParser.TryParseFlexRayRcvMessage(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
    }

    [Test]
    public async Task Type50_PayloadLength300_ReturnsFalse()
    {
        byte[] payload = new byte[44];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 300);

        bool ok = FlexRayParser.TryParseFlexRayRcvMessage(payload, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    // ========================================================================
    // Type 66 — FLEXRAY_RCVMESSAGE_EX
    // ========================================================================

    [Test]
    public async Task Type66_ValidPayload_Parsed()
    {
        byte[] payload = new byte[60 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x0002); // channel B
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 0x55);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), 0x777);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(28), 4);
        payload[32] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36), 0x01); // PPI
        payload[60] = 0xAA;
        payload[63] = 0xBB;

        bool ok = FlexRayParser.TryParseFlexRayRcvMessageEx(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)3);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> data);
        byte[] dataBytes = data.ToArray();
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.ChannelB).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x55);
        await Assert.That(fields.Cycle).IsEqualTo((byte)3);
        await Assert.That(fields.Ppi).IsTrue();
        await Assert.That(dataBytes.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Type66_PayloadLength400_ReturnsFalse()
    {
        byte[] payload = new byte[60];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(28), 400);

        bool ok = FlexRayParser.TryParseFlexRayRcvMessageEx(payload, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
