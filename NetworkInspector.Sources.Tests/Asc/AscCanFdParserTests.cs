// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscCanFdParser.TryParse"/> covering
/// CAN FD with BRS/ESI flags, extended IDs, DLC mapping, and edge cases.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscCanFdParserTests
{
    // ========================================================================
    // Basic CAN FD (hex base)
    // ========================================================================

    [Test]
    public async Task BasicCanFdHex_ParsedCorrectly()
    {
        // CANFD <ch> <dir> <id> <symbolic> <flags> <dlc> <data_len> <data...>
        bool ok = AscCanFdParser.TryParse(
            "0.100000 CANFD 1 Rx 200 1 0 8 8 01 02 03 04 05 06 07 08"u8,
            16, out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.1).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsGreaterThan(8);

        // SocketCAN FD: [4B CAN-ID BE] [1B DLC/len] [1B flags] [2B pad] [data...]
        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(0x200u);

        // Data
        await Assert.That(frame[8]).IsEqualTo((byte)0x01);
        await Assert.That(frame[15]).IsEqualTo((byte)0x08);
    }

    // ========================================================================
    // BRS and ESI flags
    // ========================================================================

    [Test]
    public async Task BrsFlag_SetInFrame()
    {
        // BRS=1, ESI=0 → flags byte should have BRS bit set
        bool ok = AscCanFdParser.TryParse(
            "0.200000 CANFD 1 Rx 100 1 0 8 8 AA BB CC DD EE FF 00 11"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        // SocketCAN CANFD_BRS = 0x01 in flags byte at offset 5
        await Assert.That((frame[5] & 0x01) != 0).IsTrue();
    }

    [Test]
    public async Task NoBrsNoEsi_FlagsClear()
    {
        // BRS=0, ESI=0
        bool ok = AscCanFdParser.TryParse(
            "0.300000 CANFD 1 Rx 100 0 0 8 8 AA BB CC DD EE FF 00 11"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That((frame[5] & 0x01)).IsEqualTo(0);
    }

    // ========================================================================
    // Extended ID
    // ========================================================================

    [Test]
    public async Task ExtendedId_HasEffFlag()
    {
        bool ok = AscCanFdParser.TryParse(
            "0.400000 CANFD 1 Rx 1ABCDEF0x 0 0 4 4 DE AD BE EF"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        // EFF bit (bit 31) set for extended IDs
        await Assert.That((canId & 0x80000000u) != 0).IsTrue();
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(0x1ABCDEF0u);
    }

    // ========================================================================
    // DLC to data length mapping
    // ========================================================================

    [Test]
    public async Task Dlc12_Maps16DataBytes()
    {
        // CAN FD DLC 12 → 16 bytes of data
        bool ok = AscCanFdParser.TryParse(
            "0.500000 CANFD 1 Rx 100 0 0 12 16 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        // 8 header + 16 data = 24 bytes
        await Assert.That(frame.Length).IsGreaterThanOrEqualTo(24);
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_IdParsedAsDecimal()
    {
        // ID 512 decimal = 0x200
        bool ok = AscCanFdParser.TryParse(
            "0.600000 CANFD 1 Rx 512 0 0 4 4 01 02 03 04"u8,
            10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0));
        await Assert.That(canId & 0x1FFFFFFFu).IsEqualTo(512u);
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public async Task EmptyLine_ReturnsFalse()
    {
        bool ok = AscCanFdParser.TryParse(""u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLine_ReturnsFalse()
    {
        bool ok = AscCanFdParser.TryParse("0.100000 CANFD"u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
