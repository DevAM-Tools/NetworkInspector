// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The value of a limit: a number in the base unit or off, with off as a state of its own.</summary>
internal sealed class LimitValueTests
{
    #region Off

    [Test]
    public async Task OffIsOffAndItsCeilingIsTheLargestLong()
    {
        LimitValue off = LimitValue.Off;

        await Assert.That(off.IsOff).IsTrue();
        await Assert.That(off.Ceiling).IsEqualTo(long.MaxValue);
        await Assert.That(off.ToString()).IsEqualTo("off");
    }

    [Test]
    public async Task ValueOfOffThrows()
    {
        await Assert.That(() => LimitValue.Off.Value).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AnInputAboveTheCeilingIsNeverTrueWhenOff()
    {
        // The comparison hot paths use: "size > ceiling". Even the largest size must pass when the limit is off.
        long ceiling = LimitValue.Off.Ceiling;

        await Assert.That(long.MaxValue > ceiling).IsFalse();
    }

    #endregion

    #region Active values

    [Test]
    [Arguments(0L)]
    [Arguments(5L)]
    [Arguments(long.MaxValue)]
    public async Task OfKeepsTheNumberAndIsNotOff(long number)
    {
        LimitValue value = LimitValue.Of(number);

        await Assert.That(value.IsOff).IsFalse();
        await Assert.That(value.Value).IsEqualTo(number);
        await Assert.That(value.Ceiling).IsEqualTo(number);
        await Assert.That(value.ToString()).IsEqualTo(number.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments(-1L)]
    [Arguments(long.MinValue)]
    public async Task OfRefusesANegativeNumber(long number)
    {
        await Assert.That(() => LimitValue.Of(number)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Encoding

    [Test]
    [Arguments(0L)]
    [Arguments(5L)]
    [Arguments(long.MaxValue)]
    public async Task TheEncodingOfAnActiveValueIsTheNumberAndRoundTrips(long number)
    {
        LimitValue value = LimitValue.Of(number);

        await Assert.That(value.Bits).IsEqualTo(number);
        await Assert.That(LimitValue.FromBits(value.Bits)).IsEqualTo(value);
    }

    [Test]
    public async Task TheEncodingOfOffIsMinusOneAndRoundTrips()
    {
        await Assert.That(LimitValue.Off.Bits).IsEqualTo(-1L);
        await Assert.That(LimitValue.FromBits(-1)).IsEqualTo(LimitValue.Off);
        await Assert.That(default(LimitValue).Bits).IsEqualTo(0L);
    }

    #endregion

    #region Equality

    [Test]
    public async Task OffEqualsOffAndDiffersFromTheLargestValue()
    {
        await Assert.That(LimitValue.Off == LimitValue.Off).IsTrue();
        await Assert.That(LimitValue.Off != LimitValue.Of(long.MaxValue)).IsTrue();
        await Assert.That(LimitValue.Off.Equals((object)LimitValue.Off)).IsTrue();
        await Assert.That(LimitValue.Off.GetHashCode()).IsEqualTo(LimitValue.Off.GetHashCode());
    }

    [Test]
    public async Task EqualNumbersAreEqualAndDifferentNumbersAreNot()
    {
        await Assert.That(LimitValue.Of(7)).IsEqualTo(LimitValue.Of(7));
        await Assert.That(LimitValue.Of(7) == LimitValue.Of(8)).IsFalse();
        await Assert.That(LimitValue.Of(7).Equals("7")).IsFalse();
    }

    [Test]
    public async Task TheDefaultValueIsAnActiveZeroAndNeverOff()
    {
        // An uninitialized field must fail closed (refuse everything), not open (accept everything).
        LimitValue value = default;

        await Assert.That(value).IsEqualTo(LimitValue.Of(0));
        await Assert.That(value.IsOff).IsFalse();
    }

    #endregion
}
