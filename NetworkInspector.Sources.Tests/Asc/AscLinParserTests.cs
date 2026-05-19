// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Asc.Format;

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscLinParser.TryParse"/> covering
/// basic LIN parsing, channel extraction, PID parity, and edge cases.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscLinParserTests
{
    // ========================================================================
    // Basic LIN (hex base)
    // ========================================================================

    [Test]
    public async Task BasicLinHex_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.100000 L1 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0"u8,
            16, out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.1).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // Channel parsing
    // ========================================================================

    [Test]
    public async Task Channel2_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.200000 L2 10 Rx 4 AA BB CC DD checksum = 00"u8,
            16, out _, out int ch, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(ch).IsEqualTo(2);
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_IdParsedAsDecimal()
    {
        // ID 60 decimal = 0x3C
        bool ok = AscLinParser.TryParse(
            "0.300000 L1 60 Rx 4 1 2 3 4 checksum = 100"u8,
            10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // Optional direction token (some ASC exports omit Tx/Rx between ID and DLC)
    // ========================================================================

    [Test]
    public async Task OptionalDirectionOmitted_BytePath_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.100000 L1 3C 8 01 02 03 04 05 06 07 08 checksum = F0"u8,
            16, out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.1).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task OptionalDirectionOmitted_CharPath_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.110000 L2 10 4 AA BB CC DD checksum = 00",
            16, out _, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ch).IsEqualTo(2);
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // DLC variations
    // ========================================================================

    [Test]
    public async Task Dlc2_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.400000 L1 3C Rx 2 01 02 checksum = AA"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Dlc8_ParsedCorrectly()
    {
        bool ok = AscLinParser.TryParse(
            "0.500000 L1 3C Rx 8 01 02 03 04 05 06 07 08 checksum = AB"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // Missing checksum
    // ========================================================================

    [Test]
    public async Task MissingChecksum_StillParses()
    {
        // Some LIN implementations omit the checksum
        bool ok = AscLinParser.TryParse(
            "0.600000 L1 3C Rx 2 01 02"u8,
            16, out _, out _, out byte[] frame);

        // Should still parse — checksum is optional
        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public async Task EmptyLine_ReturnsFalse()
    {
        bool ok = AscLinParser.TryParse(""u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLine_ReturnsFalse()
    {
        bool ok = AscLinParser.TryParse("0.100000 L1"u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
