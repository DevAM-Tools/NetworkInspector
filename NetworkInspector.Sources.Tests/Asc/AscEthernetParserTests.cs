// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Asc.Format;

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscEthernetParser.TryParse"/> covering
/// basic Ethernet parsing, AFDX variant, larger frames, and edge cases.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscEthernetParserTests
{
    // ========================================================================
    // Basic Ethernet (14 bytes: dst MAC + src MAC + EtherType)
    // ========================================================================

    [Test]
    public async Task BasicEthernet_ParsedCorrectly()
    {
        // ETH <ch> <dir> <len>:<hex_data>
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx 14:001122334455667788990A0B0C0D"u8,
            out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.5).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(14);

        // Destination MAC starts at byte 0
        await Assert.That(frame[0]).IsEqualTo((byte)0x00);
        await Assert.That(frame[1]).IsEqualTo((byte)0x11);
    }

    // ========================================================================
    // AFDX variant
    // ========================================================================

    [Test]
    public async Task AfdxVariant_ParsedAsEthernet()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.600000 AFDX 1 Rx 14:AABBCCDDEEFF112233445566ABCD"u8,
            out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.6).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(frame[0]).IsEqualTo((byte)0xAA);
    }

    // ========================================================================
    // Larger frame with payload
    // ========================================================================

    [Test]
    public async Task LargerFrame_AllBytesParsed()
    {
        // 18 bytes of data (36 hex chars)
        bool ok = AscEthernetParser.TryParse(
            "0.700000 ETH 1 Rx 18:AABBCCDDEEFF112233445566080045000014"u8,
            out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(18);
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public async Task EmptyLine_ReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(""u8, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task MissingColon_ReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx 14 AABBCCDDEEFF112233445566ABCD"u8,
            out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLine_ReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH"u8, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
