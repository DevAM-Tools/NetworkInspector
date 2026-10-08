// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscTimestamp"/> integer parsing and addition.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscTimestampTests
{
    [Test]
    public async Task FractionPadsToNanoseconds()
    {
        bool ok = AscTimestamp.TryParse("1.5"u8, out AscTimestamp value);

        await Assert.That(ok).IsTrue();
        await Assert.That(value.WholeSeconds).IsEqualTo(1);
        await Assert.That(value.Nanoseconds).IsEqualTo(500_000_000);
    }

    [Test]
    public async Task NineDigitFractionIsExact()
    {
        bool ok = AscTimestamp.TryParse("1.000000001"u8, out AscTimestamp value);

        await Assert.That(ok).IsTrue();
        await Assert.That(value.WholeSeconds).IsEqualTo(1);
        await Assert.That(value.Nanoseconds).IsEqualTo(1);
    }

    [Test]
    public async Task TenthDigitRoundsHalfUp()
    {
        bool ok = AscTimestamp.TryParse("1.0000000015"u8, out AscTimestamp value);

        await Assert.That(ok).IsTrue();
        await Assert.That(value.WholeSeconds).IsEqualTo(1);
        await Assert.That(value.Nanoseconds).IsEqualTo(2);
    }

    [Test]
    public async Task NegativeFractionBorrowsASecond()
    {
        bool ok = AscTimestamp.TryParse("-1.5"u8, out AscTimestamp value);

        await Assert.That(ok).IsTrue();
        await Assert.That(value.WholeSeconds).IsEqualTo(-2);
        await Assert.That(value.Nanoseconds).IsEqualTo(500_000_000);
    }

    [Test]
    public async Task RelativeFoldAddsDeltas()
    {
        AscTimestamp running = default;
        bool first = AscTimestamp.TryFold(ref running, true, "0.001"u8, out AscTimestamp firstClock);
        bool second = AscTimestamp.TryFold(ref running, true, "0.001"u8, out AscTimestamp secondClock);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(firstClock.Nanoseconds).IsEqualTo(1_000_000);
        await Assert.That(secondClock.Nanoseconds).IsEqualTo(2_000_000);
    }
}
