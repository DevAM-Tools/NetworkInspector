// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Unit tests for <see cref="WiresharkCsvUat"/> emission rules aligned with Wireshark
/// <c>epan/uat_load.l</c> (quoted vs hex-binstring fields).
/// </summary>
internal sealed class WiresharkCsvUatTests
{
    [Test]
    public async Task Hex32Upper_FormatsEightNibblesUppercase_and_FilesuffixIsEmpty()
    {
        await Assert.That(WiresharkCsvUat.Filesuffix).IsEqualTo(string.Empty);
        await Assert.That(WiresharkCsvUat.Hex32Upper(0x20)).IsEqualTo("00000020");
        await Assert.That(WiresharkCsvUat.Hex32Upper(0x100)).IsEqualTo("00000100");
    }

    [Test]
    public async Task UatQuoted_wrapsSimpleStrings()
    {
        await Assert.That(WiresharkCsvUat.UatQuoted("BenchPdu")).IsEqualTo("\"BenchPdu\"");
        await Assert.That(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false))).IsEqualTo("\"FALSE\"");
    }

    [Test]
    public async Task UatQuoted_int_and_ports_useInvariantDigits()
    {
        await Assert.That(WiresharkCsvUat.UatQuoted(-1)).IsEqualTo("\"-1\"");
        await Assert.That(WiresharkCsvUat.UatQuoted(65536)).IsEqualTo("\"65536\"");
        await Assert.That(WiresharkCsvUat.UatQuoted((ushort)47290)).IsEqualTo("\"47290\"");
    }

    [Test]
    public async Task UatQuoted_escapesQuotesAndBackslashes()
    {
        await Assert.That(WiresharkCsvUat.UatQuoted("a\"b")).IsEqualTo("\"a\\\"b\"");
        await Assert.That(WiresharkCsvUat.UatQuoted("a\\b")).IsEqualTo("\"a\\\\b\"");
    }
}
