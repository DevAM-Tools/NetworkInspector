// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Tests for <see cref="Ids.ArrayIndexIdRange"/> boundary validation.</summary>
internal sealed class ArrayIndexIdRangeTests
{
    [Test]
    public async Task IsValidIndex_Zero_IsTrue()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(0)).IsTrue();
    }

    [Test]
    public async Task IsValidIndex_MaxValue_IsTrue()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(Ids.ArrayIndexIdRange.MaxValue)).IsTrue();
    }

    [Test]
    public async Task IsValidIndex_InvalidSentinel_IsFalse()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(Ids.ArrayIndexIdRange.InvalidValue)).IsFalse();
    }

    [Test]
    public async Task IsValidIndex_MaxValuePlusOne_IsFalse()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(Ids.ArrayIndexIdRange.MaxValue + 1)).IsFalse();
    }

    [Test]
    public async Task IsValidIndex_IntMaxValue_IsFalse()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(int.MaxValue)).IsFalse();
    }

    [Test]
    public async Task ValidateIndexOrThrow_Valid_DoesNotThrow()
    {
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(0, nameof(ArrayIndexIdRangeTests));
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(Ids.ArrayIndexIdRange.MaxValue, nameof(ArrayIndexIdRangeTests));
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(0)).IsTrue();
    }

    [Test]
    public async Task ValidateIndexOrThrow_OutOfRange_Throws()
    {
        await Assert
            .That(() => Ids.ArrayIndexIdRange.ValidateIndexOrThrow(-2, "value"))
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(() => Ids.ArrayIndexIdRange.ValidateIndexOrThrow(Ids.ArrayIndexIdRange.MaxValue + 1, "value"))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task IsInvalidSentinel_MatchesMinusOne()
    {
        await Assert.That(Ids.ArrayIndexIdRange.IsInvalidSentinel(-1)).IsTrue();
        await Assert.That(Ids.ArrayIndexIdRange.IsInvalidSentinel(0)).IsFalse();
    }

    [Test]
    public async Task MaxCount_EqualsArrayMaxLength()
    {
        await Assert.That(Ids.ArrayIndexIdRange.MaxCount).IsEqualTo(Array.MaxLength);
        await Assert.That(Ids.ArrayIndexIdRange.MaxCount).IsEqualTo(Ids.ArrayIndexIdRange.MaxValue + 1);
    }

    [Test]
    public async Task ThrowIfInvalidNextIndex_Valid_DoesNotThrow()
    {
        Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(0, "frame");
        Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(Ids.ArrayIndexIdRange.MaxValue, "frame");
        await Assert.That(Ids.ArrayIndexIdRange.IsValidIndex(0)).IsTrue();
    }

    [Test]
    public async Task ThrowIfInvalidNextIndex_OutOfRange_ThrowsInvalidOperationException()
    {
        await Assert
            .That(() => Ids.ArrayIndexIdRange.ThrowIfInvalidNextIndex(Ids.ArrayIndexIdRange.MaxValue + 1, "frame"))
            .Throws<InvalidOperationException>();
    }
}
