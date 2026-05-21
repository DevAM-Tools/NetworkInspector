// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscCanParser.TryParse"/> covering
/// standard CAN, extended CAN, RTR, decimal base, and edge cases.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscCanParserTests
{
    // ========================================================================
    // Standard CAN (11-bit ID, hex base)
    // ========================================================================

    [Test]
    public async Task StandardCanHex_ParsedCorrectly()
    {
        bool ok = AscCanParser.TryParse(
            "0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11"u8,
            16, out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.1).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);

        // SocketCAN layout: [4B CAN-ID BE] [1B DLC] [3B pad] [data...]
        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(0x123u);

        // DLC byte at offset 4
        await Assert.That(frame[4]).IsEqualTo((byte)8);

        // First data byte at offset 8
        await Assert.That(frame[8]).IsEqualTo((byte)0xAA);
        await Assert.That(frame[15]).IsEqualTo((byte)0x11);
    }

    // ========================================================================
    // Extended CAN (29-bit ID)
    // ========================================================================

    [Test]
    public async Task ExtendedId_HasEffFlag()
    {
        // 'x' suffix marks 29-bit extended frame format
        bool ok = AscCanParser.TryParse(
            "0.200000 1 1ABCDEF0x Rx d 4 01 02 03 04"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        // EFF bit (bit 31) must be set for extended frames
        await Assert.That((canId & 0x80000000u) != 0).IsTrue();
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(0x1ABCDEF0u);
    }

    // ========================================================================
    // Remote Transmission Request (RTR)
    // ========================================================================

    [Test]
    public async Task Rtr_HasRtrFlag()
    {
        bool ok = AscCanParser.TryParse(
            "0.300000 1 123 Rx r 0"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        // RTR bit (bit 30) must be set
        await Assert.That((canId & 0x40000000u) != 0).IsTrue();
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_IdAndDataParsedAsDecimal()
    {
        // ID 291 decimal = 0x123
        bool ok = AscCanParser.TryParse(
            "0.400000 1 291 Rx d 2 10 20"u8,
            10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(291u);

        // Data bytes are always decimal in base 10
        await Assert.That(frame[8]).IsEqualTo((byte)10);
        await Assert.That(frame[9]).IsEqualTo((byte)20);
    }

    // ========================================================================
    // DLC variations
    // ========================================================================

    [Test]
    public async Task Dlc0_ProducesFrameWithNoData()
    {
        bool ok = AscCanParser.TryParse(
            "0.500000 1 100 Rx d 0"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[4]).IsEqualTo((byte)0);
        // SocketCAN struct can_frame is always 16 bytes (4 id + 1 dlc + 3 pad + 8 data)
        await Assert.That(frame.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Dlc4_Produces4DataBytes()
    {
        bool ok = AscCanParser.TryParse(
            "0.600000 2 200 Rx d 4 DE AD BE EF"u8,
            16, out _, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ch).IsEqualTo(2);
        await Assert.That(frame[4]).IsEqualTo((byte)4);
        await Assert.That(frame[8]).IsEqualTo((byte)0xDE);
        await Assert.That(frame[11]).IsEqualTo((byte)0xEF);
    }

    // ========================================================================
    // Metadata suffixes (Length=, BitCount=, ID=)
    // ========================================================================

    [Test]
    public async Task MetadataSuffix_IgnoredDuringParsing()
    {
        // Some ASC files have trailing metadata like Length=, BitCount=, ID=
        bool ok = AscCanParser.TryParse(
            "0.700000 1 123 Rx d 2 01 02 Length = 77 BitCount = 108 ID = 123h"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[4]).IsEqualTo((byte)2);
        await Assert.That(frame[8]).IsEqualTo((byte)0x01);
        await Assert.That(frame[9]).IsEqualTo((byte)0x02);
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public async Task EmptyLine_ReturnsFalse()
    {
        bool ok = AscCanParser.TryParse(
            ""u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLine_ReturnsFalse()
    {
        bool ok = AscCanParser.TryParse(
            "0.100000 1"u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
