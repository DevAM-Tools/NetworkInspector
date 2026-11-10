// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using UpperHex8 = NetworkInspector.Protocols.Helpers.UpperHex8;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// <see cref="UpperHex8"/> always prints eight uppercase hexadecimal digits, as an interpolation argument and on its own.
/// </summary>
internal sealed class UpperHex8Tests
{
    [Test]
    [Arguments(0u, "00000000")]
    [Arguments(0xDEADBEEFu, "DEADBEEF")]
    [Arguments(uint.MaxValue, "FFFFFFFF")]
    [Arguments(0xABCDu, "0000ABCD")]
    public async Task FormatsEightUppercaseDigits(uint value, string expected)
    {
        UpperHex8 hex = new(value);
        char[] buffer = new char[UpperHex8.FormattedLength];

        bool ok = hex.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);

        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(8);
        await Assert.That(new string(buffer)).IsEqualTo(expected);
        await Assert.That(hex.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo(expected);
    }

    [Test]
    public async Task WorksInsideAnInterpolatedString()
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"0x{new UpperHex8(0xDEADBEEFu)}");

        await Assert.That(text).IsEqualTo("0xDEADBEEF");
    }

    [Test]
    public async Task SevenCharDestinationIsRejected()
    {
        char[] buffer = new char[7];

        bool ok = new UpperHex8(1u).TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);

        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }
}
