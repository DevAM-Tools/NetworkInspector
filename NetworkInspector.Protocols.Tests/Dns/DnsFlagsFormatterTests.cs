// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Protocols.Dns;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="DnsFlagsFormatter"/> covering key flag combinations
/// and the synthetic 8-bit key extraction from the 16-bit DNS flags word.
/// </summary>
internal sealed class DnsFlagsFormatterTests
{
    // DNS flags word layout (RFC 1035 §4.1.1):
    // Bit 15: QR, bits 14-11: Opcode, bit 10: AA, bit 9: TC, bit 8: RD
    // Bit 7: RA, bit 6: Z, bit 5: AD, bit 4: CD, bits 3-0: RCODE

    [Test]
    public async Task Format_NoFlags_StandardQuery() =>
        // 0x0000 — pure standard query; all boolean flags clear; opcode 0; rcode 0
        await Assert.That(DnsFlagsFormatter.Format(0x0000)).IsEqualTo("[None]");

    [Test]
    public async Task Format_RecursionDesired() =>
        // 0x0100 — standard query with RD set (bit 8)
        await Assert.That(DnsFlagsFormatter.Format(0x0100)).IsEqualTo("[RD]");

    [Test]
    public async Task Format_ResponseWithRdAndRa() =>
        // 0x8180 — QR=1(bit15), RD=1(bit8), RA=1(bit7) — standard successful response
        await Assert.That(DnsFlagsFormatter.Format(0x8180)).IsEqualTo("[Response, RD, RA]");

    [Test]
    public async Task Format_AuthoritativeAnswer() =>
        // QR=1, AA=1, RA=1, RD=1 → 0x8580
        await Assert.That(DnsFlagsFormatter.Format(0x8580)).IsEqualTo("[Response, AA, RD, RA]");

    [Test]
    public async Task Format_Truncated() =>
        // QR=1, TC=1, RD=1, RA=1 → 0x8380
        await Assert.That(DnsFlagsFormatter.Format(0x8380)).IsEqualTo("[Response, TC, RD, RA]");

    [Test]
    public async Task Format_AllBooleanBitsSet() =>
        // Set QR(15), AA(10), TC(9), RD(8), RA(7), Z(6), AD(5), CD(4)
        // and opcode=0, rcode=0 → 0xFF70 but actually:
        // bit15=1, 14-11=0000, 10=1, 9=1, 8=1, 7=1, 6=1, 5=1, 4=1, 3-0=0000
        // = 1000 0111 1111 0000 = 0x87F0
        await Assert.That(DnsFlagsFormatter.Format(0x87F0)).IsEqualTo("[Response, AA, TC, RD, RA, Z, AD, CD]");

    [Test]
    public async Task Format_OpcodeAndRcodeIgnored() =>
        // Opcode bits (14-11) and RCODE bits (3-0) must be excluded from the bracket string.
        // 0xF80F has opcode=0b1111 (bits 14-11) and rcode=0b1111 (bits 3-0), all bool bits 0.
        await Assert.That(DnsFlagsFormatter.Format(0x780F)).IsEqualTo("[None]");

    [Test]
    public async Task Format_QrSetOnly() =>
        // QR=1 (bit 15), all others 0 → 0x8000
        await Assert.That(DnsFlagsFormatter.Format(0x8000)).IsEqualTo("[Response]");

    [Test]
    public async Task Format_AdAndCd() =>
        // AD=1 (bit 5 / key-bit 1), CD=1 (bit 4 / key-bit 0) → MSB-first: AD before CD
        await Assert.That(DnsFlagsFormatter.Format(0x0030)).IsEqualTo("[AD, CD]");
}
