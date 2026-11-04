// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for <see cref="FieldPresence"/>: the guard of its size, the first-occurrence answer of <see cref="FieldPresence.Mark"/>,
/// the undo of a mark, and the merge into the trailer bitmap.
/// </summary>
internal sealed class FieldPresenceTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    [Arguments(int.MaxValue - 6)]
    public async Task ASizeThatIsNegativeOrOverflowsIsRefusedAndNamed(int maxFieldId)
    {
        await Assert.That(() => new FieldPresence(maxFieldId))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("maxFieldId");
    }

    [Test]
    public async Task ASizeOfZeroTracksNothingAndTreatsEveryIdAsNew()
    {
        FieldPresence presence = new(0);

        bool first = presence.Mark(0);
        bool second = presence.Mark(0);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(presence.AsBytes().Length).IsEqualTo(0);
    }

    [Test]
    public async Task MarkReportsTheFirstOccurrenceOnlyAndTheBitmapHoldsTheUsedPrefix()
    {
        FieldPresence presence = new(64);

        bool first = presence.Mark(9);
        bool repeated = presence.Mark(9);
        bool other = presence.Mark(3);

        await Assert.That(first).IsTrue();
        await Assert.That(repeated).IsFalse();
        await Assert.That(other).IsTrue();
        await Assert.That(presence.AsBytes().ToArray()).IsEquivalentTo(new byte[] { 0b0000_1000, 0b0000_0010 });
    }

    [Test]
    public async Task UnmarkMakesTheNextMarkANewOccurrenceAgain()
    {
        FieldPresence presence = new(64);
        _ = presence.Mark(5);

        presence.Unmark(5);

        await Assert.That(presence.Mark(5)).IsTrue();
    }

    [Test]
    public async Task AnIdAtTheBoundIsNotTrackedAndOneBelowIs()
    {
        FieldPresence presence = new(16);

        bool belowBound = presence.Mark(15);
        bool belowBoundAgain = presence.Mark(15);
        bool atBound = presence.Mark(16);
        bool atBoundAgain = presence.Mark(16);

        await Assert.That(belowBound).IsTrue();
        await Assert.That(belowBoundAgain).IsFalse();
        await Assert.That(atBound).IsTrue();
        await Assert.That(atBoundAgain).IsTrue();
    }

    [Test]
    public async Task ANegativeIdIsRefused()
    {
        FieldPresence presence = new(16);

        await Assert.That(() => presence.Mark(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MergeIntoOrsTheUsedPrefixIntoTheTarget()
    {
        FieldPresence presence = new(32);
        _ = presence.Mark(1);
        _ = presence.Mark(10);
        byte[] target = [0b1000_0000, 0, 0, 0];

        presence.MergeInto(target);

        await Assert.That(target).IsEquivalentTo(new byte[] { 0b1000_0010, 0b0000_0100, 0, 0 });
    }

    [Test]
    public async Task ClearForgetsEveryMark()
    {
        FieldPresence presence = new(32);
        _ = presence.Mark(20);

        presence.Clear();

        await Assert.That(presence.AsBytes().Length).IsEqualTo(0);
        await Assert.That(presence.Mark(20)).IsTrue();
    }
}
