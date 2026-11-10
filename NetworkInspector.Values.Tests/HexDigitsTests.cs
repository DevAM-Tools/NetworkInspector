// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// The shared hex-pair writer and the letter-case choice of the four value types that use it: digit values, offsets, the
/// default (upper case), the lower-case opt-in through <see cref="HexCase"/> and through the format specifier, and the
/// rejection of unknown specifiers.
/// </summary>
internal sealed class HexDigitsTests
{
    private static readonly MacAddress _Mac = new(0xABCDEF012345UL);
    private static readonly Eui64 _Eui = new(0x0011AABBCCDDEEFFUL);
    private static readonly Uuid _Uuid = new(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);
    private static readonly IPv6Address _Ipv6 = new(0x20010DB8_ABCDEF00UL, 0x0000_0000_0000_00AFUL);

    #region Pair writer

    [Test]
    [Arguments(0x00, "00")]
    [Arguments(0x0A, "0A")]
    [Arguments(0xFF, "FF")]
    [Arguments(0x1234, "34")] // only the low byte is written
    public async Task WritePairWritesTwoUppercaseDigits(int value, string expected)
    {
        char[] buffer = ['.', '.', '.', '.'];

        HexDigits.WritePair(buffer, 1, value, HexDigits.Table(HexCase.Upper));

        await Assert.That(new string(buffer)).IsEqualTo("." + expected + ".");
    }

    [Test]
    [Arguments(0x00, "00")]
    [Arguments(0x0A, "0a")]
    [Arguments(0xFF, "ff")]
    [Arguments(0x1234, "34")]
    public async Task WritePairWritesTwoLowercaseDigits(int value, string expected)
    {
        char[] buffer = ['.', '.', '.', '.'];

        HexDigits.WritePair(buffer, 1, value, HexDigits.Table(HexCase.Lower));

        await Assert.That(new string(buffer)).IsEqualTo("." + expected + ".");
    }

    #endregion

    #region Specifier

    [Test]
    [Arguments("", HexCase.Upper)]
    [Arguments("X", HexCase.Upper)]
    [Arguments("x", HexCase.Lower)]
    public async Task ParseCaseReadsTheSpecifier(string format, HexCase expected) =>
        await Assert.That(HexDigits.ParseCase(format)).IsEqualTo(expected);

    [Test]
    [Arguments("G")]
    [Arguments("D")]
    [Arguments("xx")]
    [Arguments(" ")]
    public async Task ParseCaseRejectsAnUnknownSpecifier(string format) =>
        await Assert.That(() => HexDigits.ParseCase(format)).Throws<FormatException>();

    #endregion

    #region Default text is unchanged

    [Test]
    public async Task MacEui64AndUuidTextIsUnchanged()
    {
        string macText = _Mac.Format();
        string euiText = _Eui.Format();
        string uuidText = _Uuid.Format();

        await Assert.That(macText).IsEqualTo("AB:CD:EF:01:23:45");
        await Assert.That(euiText).IsEqualTo("00:11:AA:BB:CC:DD:EE:FF");
        await Assert.That(euiText.Split(':').Length).IsEqualTo(8);
        await Assert.That(uuidText).IsEqualTo("01234567-89AB-CDEF-FEDC-BA9876543210");
        await Assert.That(uuidText.Length).IsEqualTo(36);
    }

    [Test]
    public async Task Ipv6DefaultTextIsUppercase() =>
        await Assert.That(_Ipv6.Format()).IsEqualTo("2001:DB8:ABCD:EF00::AF");

    [Test]
    public async Task ToStringWithoutFormatIsUppercase()
    {
        await Assert.That(_Mac.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("AB:CD:EF:01:23:45");
        await Assert.That(_Eui.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("00:11:AA:BB:CC:DD:EE:FF");
        await Assert.That(_Uuid.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("01234567-89AB-CDEF-FEDC-BA9876543210");
        await Assert.That(_Ipv6.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("2001:DB8:ABCD:EF00::AF");
    }

    #endregion

    #region Lower case

    [Test]
    public async Task FormatWithHexCaseLowerWritesLowercaseDigits()
    {
        await Assert.That(_Mac.Format(HexCase.Lower)).IsEqualTo("ab:cd:ef:01:23:45");
        await Assert.That(_Eui.Format(HexCase.Lower)).IsEqualTo("00:11:aa:bb:cc:dd:ee:ff");
        await Assert.That(_Uuid.Format(HexCase.Lower)).IsEqualTo("01234567-89ab-cdef-fedc-ba9876543210");
        await Assert.That(_Ipv6.Format(HexCase.Lower)).IsEqualTo("2001:db8:abcd:ef00::af");
    }

    [Test]
    public async Task FormatWithHexCaseUpperEqualsTheDefault()
    {
        await Assert.That(_Mac.Format(HexCase.Upper)).IsEqualTo(_Mac.Format());
        await Assert.That(_Eui.Format(HexCase.Upper)).IsEqualTo(_Eui.Format());
        await Assert.That(_Uuid.Format(HexCase.Upper)).IsEqualTo(_Uuid.Format());
        await Assert.That(_Ipv6.Format(HexCase.Upper)).IsEqualTo(_Ipv6.Format());
    }

    [Test]
    public async Task ToStringAndInterpolationHonourTheSpecifier()
    {
        await Assert.That(_Mac.ToString("x", CultureInfo.InvariantCulture)).IsEqualTo("ab:cd:ef:01:23:45");
        await Assert.That(_Mac.ToString("X", CultureInfo.InvariantCulture)).IsEqualTo("AB:CD:EF:01:23:45");
        await Assert.That(Invariant($"{_Ipv6:x}")).IsEqualTo("2001:db8:abcd:ef00::af");
        await Assert.That(Invariant($"{_Uuid:x}")).IsEqualTo("01234567-89ab-cdef-fedc-ba9876543210");
        await Assert.That(Invariant($"{_Eui:X}")).IsEqualTo("00:11:AA:BB:CC:DD:EE:FF");
    }

    [Test]
    public async Task TryFormatWithTheSpecifierWritesIntoTheBuffer()
    {
        char[] buffer = new char[MacAddress.FormattedLength];

        bool ok = _Mac.TryFormat(buffer, out int written, "x", CultureInfo.InvariantCulture);

        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(MacAddress.FormattedLength);
        await Assert.That(new string(buffer)).IsEqualTo("ab:cd:ef:01:23:45");
    }

    [Test]
    public async Task FormatIntoHonoursTheCase()
    {
        char[] buffer = new char[Eui64.FormattedLength];

        int written = _Eui.FormatInto(buffer, HexCase.Lower);

        await Assert.That(written).IsEqualTo(Eui64.FormattedLength);
        await Assert.That(new string(buffer)).IsEqualTo("00:11:aa:bb:cc:dd:ee:ff");
    }

    [Test]
    public async Task Utf8FormattingHonoursTheSpecifier()
    {
        byte[] buffer = new byte[Uuid.FormattedLength];

        bool ok = _Uuid.TryFormat(buffer, out int written, "x", CultureInfo.InvariantCulture);

        await Assert.That(ok).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(buffer, 0, written)).IsEqualTo("01234567-89ab-cdef-fedc-ba9876543210");
    }

    [Test]
    public async Task LowerCaseTextParsesBackToTheSameValue()
    {
        await Assert.That(MacAddress.TryParse(_Mac.Format(HexCase.Lower), out MacAddress mac)).IsTrue();
        await Assert.That(mac).IsEqualTo(_Mac);
        await Assert.That(Eui64.TryParse(_Eui.Format(HexCase.Lower), out Eui64 eui)).IsTrue();
        await Assert.That(eui).IsEqualTo(_Eui);
        await Assert.That(Uuid.TryParse(_Uuid.Format(HexCase.Lower), out Uuid uuid)).IsTrue();
        await Assert.That(uuid).IsEqualTo(_Uuid);
        await Assert.That(IPv6Address.TryParse(_Ipv6.Format(HexCase.Lower), out IPv6Address ipv6)).IsTrue();
        await Assert.That(ipv6).IsEqualTo(_Ipv6);
    }

    [Test]
    public async Task DigitsWithoutLettersAreTheSameInBothCases()
    {
        MacAddress digitsOnly = new(0x001122334455UL);

        await Assert.That(digitsOnly.Format(HexCase.Lower)).IsEqualTo(digitsOnly.Format(HexCase.Upper));
    }

    #endregion

    #region Errors

    [Test]
    public async Task AnUnknownSpecifierThrowsEvenWhenTheBufferIsTooSmall()
    {
        char[] tooSmall = new char[1];

        await Assert.That(() => _Mac.TryFormat(tooSmall, out _, "D", null)).Throws<FormatException>();
        await Assert.That(() => _Uuid.ToString("N", CultureInfo.InvariantCulture)).Throws<FormatException>();
        await Assert.That(() => _Ipv6.ToString("G", CultureInfo.InvariantCulture)).Throws<FormatException>();
    }

    [Test]
    public async Task TryFormatIntoABufferThatIsTooSmallReturnsFalse()
    {
        char[] tooSmall = new char[3];

        await Assert.That(_Mac.TryFormat(tooSmall, out int written, "x", null)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
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

    #endregion
}
