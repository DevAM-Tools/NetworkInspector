// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscCanXlParser"/>.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscCanXlParserTests
{
    [Test]
    public async Task XlffBuildsCompactHeader()
    {
        bool ok = AscCanXlParser.TryParse(
            "0.100000 CANXL 1 Rx XLFF 0 0 123 05 0 3 4 0 0 01 0000000A 11 22 33 44 0 00400000"u8,
            out _, out int channel, out AscBusType busType, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(busType).IsEqualTo(AscBusType.CanXl);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[0]).IsEqualTo((byte)0x00);
        await Assert.That(frame[1]).IsEqualTo((byte)0x01);
        await Assert.That(frame[4]).IsEqualTo((byte)0x80);
        await Assert.That(frame[5]).IsEqualTo((byte)0x05);
        await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6))).IsEqualTo((ushort)4);
        await Assert.That(frame[12]).IsEqualTo((byte)0x11);
        await Assert.That(frame[15]).IsEqualTo((byte)0x44);
    }

    [Test]
    public async Task SymbolicNameIsSkipped()
    {
        bool ok = AscCanXlParser.TryParse(
            "0.100000 CANXL 1 Rx XLFF 0 0 123 Engine 05 0 3 4 0 0 01 0000000A 11 22 33 44 0 00400000"u8,
            out _, out _, out AscBusType busType, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(busType).IsEqualTo(AscBusType.CanXl);
        await Assert.That(frame[12]).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task DataLengthMustBeDlcPlusOne()
    {
        bool ok = AscCanXlParser.TryParse(
            "0.100000 CANXL 1 Rx XLFF 0 0 123 05 0 3 5 0 0 01 0000000A 11 22 33 44 55 0 00400000"u8,
            out _, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task CbffBuildsClassicSocketCan()
    {
        bool ok = AscCanXlParser.TryParse(
            "0.200000 CANXL 1 Rx CBFF 0 0 123 8 8 01 02 03 04 05 06 07 08 0 00000000"u8,
            out _, out _, out AscBusType busType, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(busType).IsEqualTo(AscBusType.Can);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[4]).IsEqualTo((byte)8);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame) & 0x1FFFFFFFu).IsEqualTo(0x123u);
        await Assert.That(frame[8]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task FbffStoresPayloadLength()
    {
        bool ok = AscCanXlParser.TryParse(
            "0.300000 CANXL 1 Rx FBFF 0 0 100 9 12 01 02 03 04 05 06 07 08 09 0A 0B 0C 0 0 00003000"u8,
            out _, out _, out AscBusType busType, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(busType).IsEqualTo(AscBusType.CanFd);
        await Assert.That(frame[4]).IsEqualTo((byte)12);
        await Assert.That(frame.Length).IsEqualTo(20);
        await Assert.That(frame[5]).IsEqualTo((byte)0x05);
    }
}
