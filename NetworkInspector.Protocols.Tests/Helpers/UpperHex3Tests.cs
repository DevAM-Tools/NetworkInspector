// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using UpperHex3 = NetworkInspector.Protocols.Helpers.UpperHex3;

namespace NetworkInspector.Protocols.Tests;

/// <summary><see cref="UpperHex3"/> prints the low 12 bits as three uppercase hexadecimal digits.</summary>
internal sealed class UpperHex3Tests
{
    [Test]
    [Arguments((ushort)0, "000")]
    [Arguments((ushort)0x7FF, "7FF")]
    [Arguments((ushort)0xABC, "ABC")]
    [Arguments((ushort)0xFABC, "ABC")] // bits above the twelfth are dropped
    public async Task FormatsThreeUppercaseDigits(ushort value, string expected)
    {
        UpperHex3 hex = new(value);
        char[] buffer = new char[UpperHex3.FormattedLength];

        bool ok = hex.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);

        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(new string(buffer)).IsEqualTo(expected);
        await Assert.That(hex.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task TwoCharDestinationIsRejected()
    {
        char[] buffer = new char[2];

        bool ok = new UpperHex3(1).TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);

        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }
}
