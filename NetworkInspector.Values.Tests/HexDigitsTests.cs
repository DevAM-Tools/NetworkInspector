// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// The shared uppercase hex-pair writer: digit values, offsets, and the text of the three value types that use it.
/// </summary>
internal sealed class HexDigitsTests
{
    [Test]
    [Arguments(0x00, "00")]
    [Arguments(0x0A, "0A")]
    [Arguments(0xFF, "FF")]
    [Arguments(0x1234, "34")] // only the low byte is written
    public async Task WriteUpperPairWritesTwoUppercaseDigits(int value, string expected)
    {
        char[] buffer = ['.', '.', '.', '.'];

        HexDigits.WriteUpperPair(buffer, 1, value);

        await Assert.That(new string(buffer)).IsEqualTo("." + expected + ".");
    }

    [Test]
    public async Task MacEui64AndUuidTextIsUnchanged()
    {
        MacAddress mac = new(0xABCDEF012345UL);
        Eui64 eui = new(0x0011AABBCCDDEEFFUL);
        Uuid uuid = new(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

        string macText = mac.Format();
        string euiText = eui.Format();
        string uuidText = uuid.Format();

        await Assert.That(macText).IsEqualTo("AB:CD:EF:01:23:45");
        await Assert.That(euiText).IsEqualTo("00:11:AA:BB:CC:DD:EE:FF");
        await Assert.That(euiText.Split(':').Length).IsEqualTo(8);
        await Assert.That(uuidText).IsEqualTo("01234567-89AB-CDEF-FEDC-BA9876543210");
        await Assert.That(uuidText.Length).IsEqualTo(36);
    }

    [Test]
    public async Task MacTryFormatIntoALargerBufferWritesOnlyTheText()
    {
        MacAddress mac = new(0x001122334455UL);
        char[] buffer = new char[MacAddress.FormattedLength + 3];
        Array.Fill(buffer, '#');

        bool ok = mac.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);

        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(MacAddress.FormattedLength);
        await Assert.That(new string(buffer)).IsEqualTo("00:11:22:33:44:55###");
    }
}
