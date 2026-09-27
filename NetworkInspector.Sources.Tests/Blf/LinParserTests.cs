// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Direct tests for <see cref="LinParser"/> DLT_LIN reconstruction.
/// </summary>
internal sealed class LinParserTests
{
    [Test]
    public async Task TryParseLinMessageV1_BuildsEightByteDltHeader()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[2] = 0x10;
        payload[3] = 4;
        payload[4] = 0xAA;
        payload[5] = 0xBB;
        payload[6] = 0xCC;
        payload[7] = 0xDD;

        bool ok = LinParser.TryParseLinMessageV1(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        await Assert.That(frame[0]).IsEqualTo((byte)1);
        await Assert.That(frame[4]).IsEqualTo((byte)(4 << 4));
        await Assert.That(frame[5]).IsEqualTo((byte)0x10);
        await Assert.That(frame.AsSpan(8, 4).ToArray()).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        await Assert.That(frame.Length).IsEqualTo(12);
        await Assert.That(frame[6]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task TryParseLinMessageV1_CrcAtOffset16_CopiesLowByte()
    {
        byte[] payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[2] = 0x10;
        payload[3] = 4;
        payload[4] = 0xAA;
        payload[16] = 0xAB;
        payload[17] = 0x00;

        bool ok = LinParser.TryParseLinMessageV1(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[6]).IsEqualTo((byte)0xAB);
    }

    [Test]
    public async Task TryParseLinMessageV1_SeventeenBytePayload_ChecksumStaysZero()
    {
        byte[] payload = new byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[2] = 0x10;
        payload[3] = 2;
        payload[16] = 0xAB;

        bool ok = LinParser.TryParseLinMessageV1(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[6]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task TryParseLinMessageV1_ShortPayload_ReturnsFalse()
    {
        bool ok = LinParser.TryParseLinMessageV1(new byte[11], out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLinErrorV1_CrcSetsChecksumBit()
    {
        byte[] payload = new byte[4];
        payload[2] = 0x11;
        payload[3] = 0;

        bool ok = LinParser.TryParseLinErrorV1(payload, BlfConstants.LinErrorCrc, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x11);
        await Assert.That(frame[6]).IsEqualTo((byte)0);
        await Assert.That(frame[7]).IsEqualTo((byte)0x08);
        await Assert.That(frame.Length).IsEqualTo(12);
    }

    [Test]
    public async Task TryParseLinErrorV1_DlcEight_StillTwelveByteBuffer()
    {
        byte[] payload = new byte[4];
        payload[2] = 0x11;
        payload[3] = 8;

        bool ok = LinParser.TryParseLinErrorV1(payload, BlfConstants.LinErrorRcv, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(12);
        await Assert.That(frame[4]).IsEqualTo((byte)(8 << 4));
        await Assert.That(frame[5]).IsEqualTo((byte)0x11);
        await Assert.That(frame[7]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task TryParseLinErrorV1MaskedIdOmitsParityBits()
    {
        byte[] payload = new byte[4];
        payload[2] = 0x01;
        payload[3] = 0;

        bool ok = LinParser.TryParseLinErrorV1(payload, BlfConstants.LinErrorSnd, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x01);
        await Assert.That(frame[6]).IsEqualTo((byte)0);
        await Assert.That(frame[7]).IsEqualTo(BlfConstants.LinErrorSnd);
    }

    [Test]
    public async Task TryParseLinErrorV2IdAtOffset37IsMasked()
    {
        byte[] payload = new byte[38];
        payload[37] = 0x11;

        bool ok = LinParser.TryParseLinErrorV2(payload, BlfConstants.LinErrorCrc, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x11);
        await Assert.That(frame[6]).IsEqualTo((byte)0);
        await Assert.That(frame[7]).IsEqualTo(BlfConstants.LinErrorCrc);
    }

    [Test]
    public async Task TryParseLinErrorV2MaskedIdOmitsParityBits()
    {
        byte[] payload = new byte[38];
        payload[37] = 0x01;

        bool ok = LinParser.TryParseLinErrorV2(payload, BlfConstants.LinErrorRcv, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x01);
        await Assert.That(frame[7]).IsEqualTo(BlfConstants.LinErrorRcv);
    }

    [Test]
    public async Task TryParseLinSleep_GoToSleepFrame_WritesEventCode1()
    {
        byte[] payload = [0x01, 0x00, 0x01, 0x00];

        bool ok = LinParser.TryParseLinSleep(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        await Assert.That(frame[4]).IsEqualTo((byte)(3 << 2));
        await Assert.That(frame[8]).IsEqualTo((byte)0xB0);
        await Assert.That(frame[11]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task TryParseLinWakeup_WritesEventCode4()
    {
        byte[] payload = [0x02, 0x00, 0x00, 0x00];

        bool ok = LinParser.TryParseLinWakeup(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)2);
        await Assert.That(frame[11]).IsEqualTo((byte)0x04);
        await Assert.That(frame.Length).IsEqualTo(12);
    }

    [Test]
    public async Task TryParseLinWakeup2_ChannelAtOffset12()
    {
        byte[] payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 9);

        bool ok = LinParser.TryParseLinWakeup2(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)9);
        await Assert.That(frame[11]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task TryParseLinSleep_TooShort_ReturnsFalse()
    {
        bool ok = LinParser.TryParseLinSleep([0x01, 0x00], out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLinMessageV2ChecksumModelEnhancedSetsDltBits()
    {
        byte[] payload = new byte[121];
        payload[37] = 0x05;
        payload[38] = 2;
        payload[39] = 1;
        payload[112] = 0xAA;
        payload[113] = 0xBB;
        payload[120] = 0x42;

        bool ok = LinParser.TryParseLinMessageV2(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x05);
        await Assert.That((byte)(frame[4] & 0x03)).IsEqualTo((byte)2);
        await Assert.That(frame[6]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task TryParseLinMessageV2ChecksumModelClassicSetsDltBits()
    {
        byte[] payload = new byte[121];
        payload[37] = 0x05;
        payload[38] = 0;
        payload[39] = 0;

        bool ok = LinParser.TryParseLinMessageV2(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo((byte)0x05);
        await Assert.That((byte)(frame[4] & 0x03)).IsEqualTo((byte)1);
    }
}
