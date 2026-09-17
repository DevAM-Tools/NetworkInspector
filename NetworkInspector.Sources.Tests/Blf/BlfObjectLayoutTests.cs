// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Hex-level fixtures for BLF object layouts (offset and field packing).
/// These tests fail if a parser regresses to a private packing.
/// </summary>
internal sealed class BlfObjectLayoutTests
{
    [Test]
    public async Task ClassicCan_Type1_FlagsAt2DlcAt3EffInIdBit31()
    {
        // channel=1, flags=0x80 (RTR), dlc=8, id=0x80000123 LE, data ignored for RTR
        byte[] payload =
        [
            0x01, 0x00, 0x80, 0x08,
            0x23, 0x01, 0x00, 0x80,
            0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA,
        ];

        bool parsed = CanParser.TryParseCanMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x4000_0000u).IsEqualTo(0x4000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x123u);
    }

    [Test]
    public async Task CanFd_Type101_40ByteHeader_DataAt40()
    {
        byte[] payload = new byte[40 + 8];
        payload[0] = 2;
        payload[1] = 8;
        payload[2] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), BlfConstants.CanFd64FlagEdl);
        payload.AsSpan(40, 8).Fill(0x11);

        bool parsed = CanParser.TryParseCanFdMessage64(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)2);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[4]).IsEqualTo((byte)8);
        await Assert.That(frame.AsSpan(8, 8).ToArray()).IsEquivalentTo(new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11 });
    }

    [Test]
    public async Task CanXl_Type139_PayloadStartsAt104()
    {
        byte[] payload = new byte[104 + 2];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 2);
        payload[26] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), BlfConstants.BlfCanXlFlagXlf);
        payload[104] = 0xAB;
        payload[105] = 0xCD;

        bool parsed = CanParser.TryParseCanXlChannelFrame(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(frame.AsSpan(12, 2).ToArray()).IsEquivalentTo(new byte[] { 0xAB, 0xCD });
    }

    [Test]
    public async Task EthernetType71_EthertypeLittleEndianInStruct()
    {
        byte[] payload = new byte[32 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 2);
        payload[32] = 0x11;
        payload[33] = 0x22;

        bool ok = EthernetParser.TryParseType71(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)3);
        await Assert.That(frame[12]).IsEqualTo((byte)0x08);
        await Assert.That(frame[13]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task FlexRayType50_ChannelAt0_PayloadAt44()
    {
        byte[] payload = new byte[44 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26), 7);
        // Type 50 frameFlags at offset 36: NULL=0x01, PAYLOAD_PREAM=0x10
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36), 0x11);

        bool ok = FlexRayParser.TryParseFlexRayRcvMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out _);
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)42);
        await Assert.That(fields.Ppi).IsTrue();
        await Assert.That(fields.Nfi).IsFalse();
    }

    [Test]
    public async Task LinType11_BuildsDltLinHeader()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 4);
        payload[2] = 0x10;
        payload[3] = 2;
        payload[4] = 0x01;
        payload[5] = 0x02;

        bool ok = LinParser.TryParseLinMessageV1(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)4);
        await Assert.That(frame[0]).IsEqualTo((byte)1);
        await Assert.That(frame.Length).IsEqualTo(12);
    }

    [Test]
    public async Task AppText_Source1_ReservedPacking_SecondTokenIsName()
    {
        byte[] text = Encoding.UTF8.GetBytes("unusedPath;ClusterName;\0");
        byte[] payload = new byte[16 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        uint reserved = (1u << 16) | (2u << 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        payload[12] = 0xFF;
        payload[13] = 0xFF;
        payload[14] = 0xFF;
        payload[15] = 0xFF;
        text.CopyTo(payload.AsSpan(16));

        bool ok = AppTextParser.TryParseChannelName(payload, out byte channel, out byte busType, out string? name);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((byte)2);
        await Assert.That(busType).IsEqualTo((byte)1);
        await Assert.That(name).IsEqualTo("ClusterName");
    }

    [Test]
    public async Task AppText_SourceNotChannel_Ignored()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 2);

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AppText_ShortPayload_ReturnsFalse()
    {
        bool tooShortForOldHeader = AppTextParser.TryParseChannelName(new byte[11], out _, out _, out _);
        bool fifteenBytes = AppTextParser.TryParseChannelName(new byte[15], out _, out _, out _);

        await Assert.That(tooShortForOldHeader).IsFalse();
        await Assert.That(fifteenBytes).IsFalse();
    }

    [Test]
    public async Task AppText_TwelveByteHeader_ReturnsFalse()
    {
        byte[] text = Encoding.UTF8.GetBytes("CAN1;\0");
        byte[] payload = new byte[12 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        text.CopyTo(payload.AsSpan(12));

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AppText_EmptySecondToken_ReturnsFalse()
    {
        byte[] text = Encoding.UTF8.GetBytes("path;\0");
        byte[] payload = new byte[16 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        text.CopyTo(payload.AsSpan(16));

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AppText_TextLengthExceedsPayload_ReturnsFalse()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 32);

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AppText_ZeroTextLength_ReturnsFalse()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AppText_NoSemicolon_ReturnsFalse()
    {
        byte[] text = Encoding.UTF8.GetBytes("CAN1\0");
        byte[] payload = new byte[16 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        uint reserved = (1u << 16) | (2u << 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        text.CopyTo(payload.AsSpan(16));

        bool ok = AppTextParser.TryParseChannelName(payload, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
