// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Unit tests for <see cref="TsharkCsvUat"/> emission rules aligned with Tshark
/// <c>epan/uat_load.l</c> (quoted vs hex-binstring fields).
/// </summary>
internal sealed class TsharkCsvUatTests
{
    [Test]
    public async Task Hex32Upper_FormatsEightNibblesUppercase_and_FilesuffixIsEmpty()
    {
        await Assert.That(TsharkCsvUat.Filesuffix).IsEqualTo(string.Empty);
        await Assert.That(TsharkCsvUat.Hex32Upper(0x20)).IsEqualTo("00000020");
        await Assert.That(TsharkCsvUat.Hex32Upper(0x100)).IsEqualTo("00000100");
    }

    [Test]
    public async Task UatQuoted_wrapsSimpleStrings()
    {
        await Assert.That(TsharkCsvUat.UatQuoted("BenchPdu")).IsEqualTo("\"BenchPdu\"");
        await Assert.That(TsharkCsvUat.UatQuoted(TsharkCsvUat.Bool(false))).IsEqualTo("\"FALSE\"");
    }

    [Test]
    public async Task UatQuoted_int_and_ports_useInvariantDigits()
    {
        await Assert.That(TsharkCsvUat.UatQuoted(-1)).IsEqualTo("\"-1\"");
        await Assert.That(TsharkCsvUat.UatQuoted(65536)).IsEqualTo("\"65536\"");
        await Assert.That(TsharkCsvUat.UatQuoted((ushort)47290)).IsEqualTo("\"47290\"");
    }

    [Test]
    public async Task UatQuoted_escapesQuotesAndBackslashes()
    {
        await Assert.That(TsharkCsvUat.UatQuoted("a\"b")).IsEqualTo("\"a\\\"b\"");
        await Assert.That(TsharkCsvUat.UatQuoted("a\\b")).IsEqualTo("\"a\\\\b\"");
    }
}
