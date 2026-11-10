// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The composable word sets of the utility part: declaration rules, immutability, and the prefix ladders.</summary>
internal sealed class UnitWordSetTests
{
    #region Declaration rules

    [Test]
    [Arguments("")]
    [Arguments("abcdefghijklmnopq")]
    [Arguments("m b")]
    [Arguments("m1")]
    [Arguments("ms_")]
    [Arguments("bit-s")]
    [Arguments("ß")]
    [Arguments("e")]
    [Arguments("E")]
    [Arguments("each")]
    public async Task ABrokenWordIsRefusedAndTheMessageNamesIt(string word)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => UnitWordSet.Of(word));

        await Assert.That(exception.Message).Contains("Unit word");
    }

    [Test]
    [Arguments("%")]
    [Arguments("bit/s")]
    [Arguments("abcdefghijklmnop")]
    public async Task AWordOfTheAlphabetAndOfSixteenCharactersIsAccepted(string word)
    {
        UnitWordSet set = UnitWordSet.Of(word);

        await Assert.That(set.Words.Count).IsEqualTo(1);
        await Assert.That(set.Words[0]).IsEqualTo(new UnitWord(word, 1));
    }

    [Test]
    public async Task TheFirstWordMustHaveTheFactorOne()
    {
        await Assert.That(() => UnitWordSet.None.With("s", 1_000)).Throws<ArgumentException>();
        await Assert.That(() => UnitWordSet.None.With("s", 0)).Throws<ArgumentException>();
        await Assert.That(() => UnitWordSet.None.With("s", -1)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(1L)]
    [Arguments(5L)]
    public async Task AFactorThatDoesNotRiseIsRefused(long factor)
    {
        UnitWordSet set = UnitWordSet.Of("ms").With("s", 5);

        await Assert.That(() => set.With("min", factor)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("ms")]
    [Arguments("MS")]
    public async Task TwoWordsThatAreEqualIgnoringCaseAreRefused(string second)
    {
        UnitWordSet set = UnitWordSet.Of("ms");

        await Assert.That(() => set.With(second, 1_000)).Throws<ArgumentException>();
    }

    [Test]
    public async Task NoneIsValidHasNoWordsAndWithReturnsANewSetLeavingTheOldOneUnchanged()
    {
        UnitWordSet none = UnitWordSet.None;
        UnitWordSet one = UnitWordSet.Of("ms");

        UnitWordSet two = one.With("s", 1_000);

        await Assert.That(none.Words.Count).IsEqualTo(0);
        await Assert.That(one.Words.Count).IsEqualTo(1);
        await Assert.That(two.Words.Count).IsEqualTo(2);
        await Assert.That(two).IsNotSameReferenceAs(one);
        await Assert.That(two.Words[1]).IsEqualTo(new UnitWord("s", 1_000));
    }

    [Test]
    public async Task ANullWordIsRefusedWithTheNullException()
    {
        await Assert.That(() => UnitWordSet.Of(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Prefix ladders

    [Test]
    public async Task TheBinaryLadderHasTheBinaryWordsAndRefusesTheDecimalSpellings()
    {
        UnitWordSet set = UnitWordSet.Binary("B");

        string[] words = [.. set.Words.Select(word => word.Text)];
        long[] factors = [.. set.Words.Select(word => word.Factor)];

        await Assert.That(words).IsEquivalentTo(["B", "KiB", "MiB", "GiB", "TiB"], CollectionOrdering.Matching);
        await Assert.That(factors).IsEquivalentTo([1L, 1L << 10, 1L << 20, 1L << 30, 1L << 40], CollectionOrdering.Matching);
        await Assert.That(set.Refused.Select(refused => refused.Text)).IsEquivalentTo(["KB", "MB", "GB", "TB"], CollectionOrdering.Matching);
        await Assert.That(set.Refused.Select(refused => refused.Replacement)).IsEquivalentTo(["KiB", "MiB", "GiB", "TiB"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheDecimalLadderHasTheDecimalWordsAndRefusesNothing()
    {
        UnitWordSet set = UnitWordSet.Decimal("bit/s");

        string[] words = [.. set.Words.Select(word => word.Text)];
        long[] factors = [.. set.Words.Select(word => word.Factor)];

        await Assert.That(words).IsEquivalentTo(["bit/s", "kbit/s", "Mbit/s", "Gbit/s", "Tbit/s"], CollectionOrdering.Matching);
        await Assert.That(factors).IsEquivalentTo([1L, 1_000L, 1_000_000L, 1_000_000_000L, 1_000_000_000_000L], CollectionOrdering.Matching);
        await Assert.That(set.Refused.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASetBuiltFromABaseWordAndWithHasTheWordsInOrder()
    {
        UnitWordSet set = UnitWordSet.Of("ms").With("s", 1_000);

        await Assert.That(set.Words.Count).IsEqualTo(2);
        await Assert.That(set.Words[0].Text).IsEqualTo("ms");
        await Assert.That(set.Words[1].Factor).IsEqualTo(1_000L);
    }

    [Test]
    public async Task TheLadderKeepsItsRefusedSpellingsWhenAWordIsAdded()
    {
        UnitWordSet set = UnitWordSet.Binary("B").With("PiB", 1L << 50);

        await Assert.That(set.Words.Count).IsEqualTo(6);
        await Assert.That(set.Refused.Count).IsEqualTo(4);
    }

    #endregion
}
