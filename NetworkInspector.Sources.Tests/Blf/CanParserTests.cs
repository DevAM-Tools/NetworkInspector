// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Direct tests for <see cref="CanParser"/> object layouts (Type 1 / 86 / 100 / 101 / 139).
/// </summary>
internal sealed class CanParserTests
{
    [Test]
    public async Task TryParseCanMessage_RtrAndEff_YieldsSocketCanFlagsAndEmptyData()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[2] = 0x80;
        payload[3] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x8000_0123u);
        payload.AsSpan(8).Fill(0xAA);

        bool parsed = CanParser.TryParseCanMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x4000_0000u).IsEqualTo(0x4000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x123u);
        await Assert.That(frame.AsSpan(8).ToArray()).IsEquivalentTo(new byte[8]);
    }

    [Test]
    public async Task TryParseCanMessage_ShortPayload_ReturnsFalse()
    {
        bool parsed = CanParser.TryParseCanMessage(new byte[15], out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
        await Assert.That(channel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TryParseCanMessage_DlcHighNibbleMasked()
    {
        byte[] payload = new byte[16];
        payload[3] = 0x18;
        payload[8] = 0x11;
        payload[9] = 0x22;

        bool parsed = CanParser.TryParseCanMessage(payload, out byte[] frame, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(frame[4]).IsEqualTo((byte)8);
        await Assert.That(frame[8]).IsEqualTo((byte)0x11);
        await Assert.That(frame[9]).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task TryParseCanMessage2_IgnoresTrailerAfter16Bytes()
    {
        byte[] payload = new byte[24];
        payload[3] = 2;
        payload[8] = 0xAB;
        payload[9] = 0xCD;
        payload[16] = 0xFF;

        bool parsed = CanParser.TryParseCanMessage2(payload, out byte[] frame, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(frame[8]).IsEqualTo((byte)0xAB);
        await Assert.That(frame[9]).IsEqualTo((byte)0xCD);
    }

    [Test]
    public async Task TryParseCanMessage_IdWithoutBit31_DoesNotSetEff()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x1ABC_DEF0u);

        bool parsed = CanParser.TryParseCanMessage(payload, out byte[] frame, out _);

        await Assert.That(parsed).IsTrue();
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x1ABC_DEF0u);
    }

    [Test]
    public async Task TryParseCanXlChannelFrame_104ByteHeader_ReconstructsSocketCanXl()
    {
        byte[] payload = new byte[104 + 4];
        payload[0] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0x7ABu);
        payload[16] = 0x42;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 4);
        payload[26] = 0x1A;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28), 0xDEAD_BEEFu);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48),
            BlfConstants.BlfCanXlFlagXlf | BlfConstants.BlfCanXlFlagSec | BlfConstants.BlfCanXlFlagRrs);
        payload[104] = 0xAA;
        payload[105] = 0xBB;
        payload[106] = 0xCC;
        payload[107] = 0xDD;

        bool parsed = CanParser.TryParseCanXlChannelFrame(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)3);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[0]).IsEqualTo((byte)0);
        await Assert.That(frame[1]).IsEqualTo((byte)0x1A);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))).IsEqualTo((ushort)0x7AB);
        await Assert.That(frame[4]).IsEqualTo((byte)(BlfConstants.SocketCanXlXlf | BlfConstants.SocketCanXlSec | BlfConstants.SocketCanXlRrs));
        await Assert.That(frame[5]).IsEqualTo((byte)0x42);
        await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6))).IsEqualTo((ushort)4);
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8))).IsEqualTo(0xDEAD_BEEFu);
        await Assert.That(frame.AsSpan(12).ToArray()).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
    }

    [Test]
    public async Task TryParseCanXlChannelFrame_ShortHeader_ReturnsFalse()
    {
        bool parsed = CanParser.TryParseCanXlChannelFrame(new byte[103], out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
        await Assert.That(channel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TryParseCanXlChannelFrame_XlfClear_ReturnsFalse()
    {
        byte[] payload = new byte[104];
        payload[0] = 1;

        bool parsed = CanParser.TryParseCanXlChannelFrame(payload, out _, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParseCanXlChannelFrame_ClampsDataLengthToAvailableBytes()
    {
        byte[] payload = new byte[104 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), BlfConstants.BlfCanXlFlagXlf);
        payload[104] = 0x11;
        payload[105] = 0x22;

        bool parsed = CanParser.TryParseCanXlChannelFrame(payload, out byte[] frame, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6))).IsEqualTo((ushort)2);
        await Assert.That(frame[12]).IsEqualTo((byte)0x11);
        await Assert.That(frame[13]).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task TryParseCanXlChannelFrame_EmptyPayload_HeaderOnly()
    {
        byte[] payload = new byte[104];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), BlfConstants.BlfCanXlFlagXlf);

        bool parsed = CanParser.TryParseCanXlChannelFrame(payload, out byte[] frame, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(12);
        await Assert.That(frame[4]).IsEqualTo(BlfConstants.SocketCanXlXlf);
    }

    [Test]
    public async Task TryParseCanFdMessage_20ByteHeader_ReconstructsSocketCanFd()
    {
        byte[] payload = new byte[20 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 2);
        payload[3] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x8000_0200u);
        payload[13] = (byte)(BlfConstants.BlfCanFdEdl | BlfConstants.BlfCanFdBrs);
        payload[14] = 8;
        payload.AsSpan(20, 8).Fill(0xBB);

        bool parsed = CanParser.TryParseCanFdMessage(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)2);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x200u);
        await Assert.That(frame[4]).IsEqualTo((byte)8);
        await Assert.That(frame[5] & BlfConstants.SocketCanFdFdf).IsEqualTo(BlfConstants.SocketCanFdFdf);
        await Assert.That(frame[5] & BlfConstants.SocketCanFdBrs).IsEqualTo(BlfConstants.SocketCanFdBrs);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame.AsSpan(8, 8).ToArray()).IsEquivalentTo(new byte[] { 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB });
    }

    [Test]
    public async Task TryParseCanFdMessage_ShortHeader_ReturnsFalse()
    {
        bool parsed = CanParser.TryParseCanFdMessage(new byte[19], out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
        await Assert.That(channel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TryParseCanFdMessage64_40ByteHeader_ReconstructsSocketCanFd()
    {
        byte[] payload = new byte[40 + 4];
        payload[0] = 3;
        payload[1] = 4;
        payload[2] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x8000_0456u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12),
            BlfConstants.CanFd64FlagEdl | BlfConstants.CanFd64FlagBrs);
        payload[40] = 0x01;
        payload[41] = 0x02;
        payload[42] = 0x03;
        payload[43] = 0x04;

        bool parsed = CanParser.TryParseCanFdMessage64(payload, out byte[] frame, out ushort channel);

        await Assert.That(parsed).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)3);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x456u);
        await Assert.That(frame[4]).IsEqualTo((byte)4);
        await Assert.That(frame.Length).IsEqualTo(12);
        await Assert.That(frame[5] & BlfConstants.SocketCanFdFdf).IsEqualTo(BlfConstants.SocketCanFdFdf);
        await Assert.That(frame.AsSpan(8, 4).ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Test]
    public async Task TryParseCanFdMessage64_ShortHeader_ReturnsFalse()
    {
        bool parsed = CanParser.TryParseCanFdMessage64(new byte[39], out _, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParseCanFdError64_Requires44Bytes()
    {
        bool tooShort = CanParser.TryParseCanFdError64(new byte[43], out _, out _);
        bool ok = CanParser.TryParseCanFdError64(new byte[44], out byte[] frame, out ushort channel);

        await Assert.That(tooShort).IsFalse();
        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)0);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & BlfConstants.SocketCanErr).IsEqualTo(BlfConstants.SocketCanErr);
    }

    [Test]
    public async Task TryParseCanErrorExt_Requires24Bytes()
    {
        bool tooShort = CanParser.TryParseCanErrorExt(new byte[23], out _, out _);
        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 5);
        bool ok = CanParser.TryParseCanErrorExt(payload, out byte[] frame, out ushort channel);

        await Assert.That(tooShort).IsFalse();
        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)5);
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame);
        await Assert.That(id & BlfConstants.SocketCanErr).IsEqualTo(BlfConstants.SocketCanErr);
    }
}
