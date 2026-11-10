// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The table unit and the ready units: accepted spellings, refusals with a suggestion, overflow, formatting, and composition.</summary>
internal sealed class TableUnitTests
{
    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    private const long _GiB = 1L << 30;

    private const long _TiB = 1L << 40;

    #region Helpers

    /// <summary>Selects a ready unit by name; a unit is an object and cannot be an attribute argument.</summary>
    private static TableUnit _Unit(string name) => name switch
    {
        "bytes" => LimitUnits.Bytes,
        "duration" => LimitUnits.Duration,
        "count" => LimitUnits.Count,
        _ => throw new ArgumentException("Unknown test unit " + name, nameof(name)),
    };

    /// <summary>Declares a byte unit with the given example; the example is checked at construction.</summary>
    private static TableUnit _Bytes(string example) => new("bytes", example, UnitWordSet.Binary("B"));

    private static long _Parse(TableUnit unit, string text)
    {
        if (!unit.TryParse(text, long.MaxValue, out long value, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        return value;
    }

    private static string _Refusal(TableUnit unit, string text)
    {
        bool parsed = unit.TryParse(text, long.MaxValue, out long value, out string? problem);
        if (parsed || problem is null)
        {
            throw new InvalidOperationException("Expected a refusal for '" + text + "' but parsed " + value);
        }

        return problem;
    }

    #endregion

    #region Accepted input

    [Test]
    [Arguments("256MiB", 256 * _MiB)]
    [Arguments("256 MiB", 256 * _MiB)]
    [Arguments("256 mib", 256 * _MiB)]
    [Arguments("256\tMiB", 256 * _MiB)]
    [Arguments("268435456", 256 * _MiB)]
    [Arguments("268_435_456", 256 * _MiB)]
    [Arguments("1TiB", _TiB)]
    [Arguments("1024B", _KiB)]
    [Arguments("  512 KiB  ", 512 * _KiB)]
    public async Task ByteValuesAcceptEverySpellingOfTheFamily(string text, long expected)
    {
        await Assert.That(_Parse(LimitUnits.Bytes, text)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("90s", 90_000L)]
    [Arguments("1500ms", 1_500L)]
    [Arguments("2min", 120_000L)]
    [Arguments("1h", 3_600_000L)]
    [Arguments("2 MIN", 120_000L)]
    [Arguments("90d", 90L * 86_400_000L)]
    [Arguments("2w", 2L * 604_800_000L)]
    [Arguments("1 D", 86_400_000L)]
    public async Task DurationValuesAcceptEveryWordOfTheFamily(string text, long expected)
    {
        await Assert.That(_Parse(LimitUnits.Duration, text)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("10000", 10_000L)]
    [Arguments("10_000", 10_000L)]
    [Arguments(" 7 ", 7L)]
    public async Task CountValuesAcceptPlainNumbers(string text, long expected)
    {
        await Assert.That(_Parse(LimitUnits.Count, text)).IsEqualTo(expected);
    }

    #endregion

    #region Refused input

    [Test]
    [Arguments("bytes", "1.5GiB", "whole number expected; use 1536 MiB")]
    [Arguments("bytes", "2.0MiB", "whole number expected; use 2 MiB")]
    [Arguments("duration", "1.5s", "whole number expected; use 1500 ms")]
    [Arguments("duration", "1.5min", "whole number expected; use 90 s")]
    [Arguments("duration", "1.5d", "whole number expected; use 36 h")]
    [Arguments("bytes", "0.0001GiB", "write a whole number in a smaller unit")]
    [Arguments("bytes", "512MB", "'MB' is a decimal unit and is not accepted; use 512 MiB")]
    [Arguments("bytes", "512 mb", "use 512 MiB")]
    [Arguments("bytes", "1GB", "use 1 GiB")]
    [Arguments("bytes", "3 kB", "use 3 KiB")]
    [Arguments("bytes", "10s", "unknown unit 's'; this value takes bytes: B, KiB, MiB, GiB, TiB")]
    [Arguments("duration", "256MiB", "unknown unit 'MiB'; this value takes durations: ms, s, min, h, d, w")]
    [Arguments("count", "5 KiB", "unknown unit 'KiB'; this value takes counts: whole numbers without a unit")]
    [Arguments("duration", "30", "add a unit: 30 s or 30 ms")]
    [Arguments("bytes", "-1", "no sign")]
    [Arguments("bytes", "+5", "no sign")]
    [Arguments("bytes", "1e6", "no exponent form")]
    [Arguments("bytes", "0x10", "no hexadecimal form")]
    [Arguments("bytes", "1,5GiB", "thousands separator")]
    [Arguments("bytes", "5 Mi B", "unknown unit")]
    [Arguments("bytes", "256MiB extra", "unexpected text 'extra'")]
    [Arguments("bytes", "_1", "digit separator")]
    [Arguments("bytes", "1__0", "digit separator")]
    [Arguments("bytes", "1_", "digit separator")]
    [Arguments("bytes", "1_MiB", "digit separator")]
    [Arguments("bytes", "abc", "expected a whole number with an optional unit; for example 256 MiB")]
    [Arguments("duration", "abc", "expected a whole number with a unit; for example 90 s")]
    public async Task AmbiguousOrInexactInputIsRefusedWithTheTextToWriteInstead(string unit, string text, string expectedInProblem)
    {
        string problem = _Refusal(_Unit(unit), text);

        await Assert.That(problem).Contains(expectedInProblem);
    }

    [Test]
    [Arguments("2month", "unknown unit 'month'; this value takes durations: ms, s, min, h, d, w")]
    [Arguments("3years", "unknown unit 'years'; this value takes durations: ms, s, min, h, d, w")]
    [Arguments("1y", "unknown unit 'y'")]
    public async Task AMonthOrYearIsAnUnknownWordThatListsTheDurationWords(string text, string expectedInProblem)
    {
        await Assert.That(_Refusal(LimitUnits.Duration, text)).Contains(expectedInProblem);
    }

    [Test]
    public async Task AnEmptyTextDirectlyAtTheUnitIsRefusedWithTheExample()
    {
        await Assert.That(_Refusal(LimitUnits.Bytes, "  ")).IsEqualTo("empty value; write a whole number such as 256 MiB");
    }

    #endregion

    #region Overflow and hostile input

    [Test]
    public async Task TheLargestNumberOfTebibytesThatFitsParsesAndOneMoreIsTooLarge()
    {
        await Assert.That(_Parse(LimitUnits.Bytes, "8388607TiB")).IsEqualTo(8_388_607L * _TiB);
        await Assert.That(_Refusal(LimitUnits.Bytes, "8388608TiB")).Contains("too large; the largest value is");
        await Assert.That(_Refusal(LimitUnits.Bytes, "99999999999999999999")).Contains("too large; the largest value is");
    }

    [Test]
    public async Task TheLargestLongParsesAsACountAndOneMoreIsTooLarge()
    {
        await Assert.That(_Parse(LimitUnits.Count, "9223372036854775807")).IsEqualTo(long.MaxValue);
        await Assert.That(_Refusal(LimitUnits.Count, "9223372036854775808")).Contains("too large");
    }

    [Test]
    public async Task TheTooLargeRefusalNamesTheMaximumOfTheDefinitionInItsUnit()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(maximum: 8 * _GiB);

        bool parsed = definition.TryParse("99999999999TiB", out _, out string? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem).Contains("too large; the largest value is 8 GiB");
        await Assert.That(problem).StartsWith("demo.maxSize: ");
    }

    [Test]
    public async Task AWeekCountThatOverflowsMillisecondsIsTooLargeBecauseTheProductIsCheckedBeforeTheMultiplication()
    {
        await Assert.That(_Refusal(LimitUnits.Duration, "20000000000w")).Contains("too large");
    }

    [Test]
    public async Task ARefusalQuotesAtMostAShortPartOfAHostileInputAndFinishesInOnePass()
    {
        // Security: a megabyte of unit letters must not become a megabyte of message.
        string letters = "5 " + new string('x', 1_000_000);
        string digits = new('9', 1_000_000);

        string letterProblem = _Refusal(LimitUnits.Bytes, letters);
        string digitProblem = _Refusal(LimitUnits.Count, digits);

        await Assert.That(letterProblem.Length).IsLessThan(300);
        await Assert.That(digitProblem.Length).IsLessThan(300);
        await Assert.That(digitProblem).Contains("too large");
    }

    [Test]
    public async Task AFractionWithMoreThanEighteenDigitsGetsTheNoSuggestionText()
    {
        string text = "1." + new string('0', 19) + "GiB";

        await Assert.That(_Refusal(LimitUnits.Bytes, text)).IsEqualTo("whole number expected; write a whole number in a smaller unit");
    }

    #endregion

    #region Formatting

    [Test]
    [Arguments("bytes", 268_435_456L, "256 MiB")]
    [Arguments("bytes", 1_024L, "1 KiB")]
    [Arguments("bytes", 1_000_001L, "1000001 B")]
    [Arguments("bytes", 0L, "0 B")]
    [Arguments("bytes", 1_099_511_627_776L, "1 TiB")]
    [Arguments("bytes", 1_536L * _MiB, "1536 MiB")]
    [Arguments("duration", 90_000L, "90 s")]
    [Arguments("duration", 120_000L, "2 min")]
    [Arguments("duration", 3_600_000L, "1 h")]
    [Arguments("duration", 86_400_000L, "1 d")]
    [Arguments("duration", 129_600_000L, "36 h")]
    [Arguments("duration", 604_800_000L, "1 w")]
    [Arguments("duration", 1_500L, "1500 ms")]
    [Arguments("duration", 0L, "0 ms")]
    [Arguments("count", 10_000L, "10000")]
    [Arguments("count", 0L, "0")]
    public async Task FormattingUsesTheLargestWordThatIsExact(string unit, long number, string expected)
    {
        await Assert.That(_Unit(unit).Format(number)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("bytes", 268_435_456L)]
    [Arguments("bytes", 1_000_001L)]
    [Arguments("bytes", 0L)]
    [Arguments("bytes", 5L * _TiB)]
    [Arguments("duration", 1_500L)]
    [Arguments("duration", 7_200_000L)]
    [Arguments("duration", 1_209_600_000L)]
    [Arguments("duration", 90_000L)]
    [Arguments("duration", 604_800_000L)]
    [Arguments("count", 12_345L)]
    public async Task EveryFormattedTextParsesBackToTheSameNumber(string unit, long number)
    {
        TableUnit tableUnit = _Unit(unit);
        string text = tableUnit.Format(number);

        bool parsed = tableUnit.TryParse(text, long.MaxValue, out long value, out string? problem);

        await Assert.That(parsed).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(value).IsEqualTo(number);
    }

    [Test]
    public async Task EveryWordOfEveryReadyUnitFormatsAndParsesBack()
    {
        foreach (TableUnit unit in new[] { LimitUnits.Bytes, LimitUnits.Duration })
        {
            UnitWordSet set = unit == LimitUnits.Bytes
                ? UnitWordSet.Binary("B")
                : UnitWordSet.Of("ms").With("s", 1_000).With("min", 60_000).With("h", 3_600_000).With("d", 86_400_000).With("w", 604_800_000);
            foreach (UnitWord word in set.Words)
            {
                long number = 3 * word.Factor;
                await Assert.That(_Parse(unit, unit.Format(number))).IsEqualTo(number);
            }
        }
    }

    #endregion

    #region Composition

    private static TableUnit _BitRate() => new("bit rates", "100 Mbit/s", UnitWordSet.Decimal("bit/s"), wordRequired: true, smallestMinimum: 1);

    [Test]
    public async Task AnApplicationUnitFromPartsParsesFormatsAndRefusesLikeTheReadyUnits()
    {
        TableUnit bitRate = _BitRate();

        await Assert.That(_Parse(bitRate, "100 Mbit/s")).IsEqualTo(100_000_000L);
        await Assert.That(_Parse(bitRate, "1gbit/s")).IsEqualTo(1_000_000_000L);
        await Assert.That(_Refusal(bitRate, "1.5 Gbit/s")).Contains("use 1500 Mbit/s");
        await Assert.That(_Refusal(bitRate, "100")).IsEqualTo("add a unit: 100 kbit/s or 100 bit/s");
        await Assert.That(_Refusal(bitRate, "5 MiB")).Contains("unknown unit 'MiB'; this value takes bit rates: bit/s, kbit/s, Mbit/s, Gbit/s, Tbit/s");
        await Assert.That(bitRate.Format(1_500_000_000)).IsEqualTo("1500 Mbit/s");
    }

    [Test]
    public async Task ADefinitionWithAnApplicationUnitUsesTheUnitForKeysRangeAndText()
    {
        TableUnit bitRate = new("bit rates", "100 Mbit/s", UnitWordSet.Decimal("bit/s"), wordRequired: true, smallestMinimum: 1,
            forbiddenKeySuffixes: ["Rate"]);

        LimitDefinition definition = new("net.maxSpeed", bitRate, 1_000_000_000, 1_000_000, 100_000_000_000, LimitTestDefinitions.Texts());

        await Assert.That(definition.Format(definition.DefaultValue)).IsEqualTo("1 Gbit/s");
        await Assert.That(definition.FormatRange()).IsEqualTo("1 Mbit/s – 100 Gbit/s, or off");
        await Assert.That(() => new LimitDefinition("net.maxRate", bitRate, 1_000_000_000, 1_000_000, 100_000_000_000,
            LimitTestDefinitions.Texts())).Throws<ArgumentException>();
        await Assert.That(() => new LimitDefinition("net.maxSpeed", bitRate, 1_000_000_000, 0, 100_000_000_000,
            LimitTestDefinitions.Texts())).Throws<ArgumentException>();
    }

    [Test]
    public async Task ABrokenUnitDeclarationIsRefusedAndTheMessageNamesTheUnit()
    {
        await Assert.That(() => new TableUnit("", "1", UnitWordSet.None)).Throws<ArgumentException>();
        await Assert.That(() => new TableUnit(new string('n', 33), "1", UnitWordSet.None)).Throws<ArgumentException>();
        await Assert.That(() => new TableUnit("counts", "1", UnitWordSet.None, wordRequired: true)).Throws<ArgumentException>();
        await Assert.That(() => new TableUnit("counts", "1", UnitWordSet.None, forbiddenKeySuffixes: ["a b"])).Throws<ArgumentException>();
        await Assert.That(() => new TableUnit("counts", "1", UnitWordSet.None, smallestMinimum: -1)).Throws<ArgumentOutOfRangeException>();

        ArgumentException exampleProblem = Assert.Throws<ArgumentException>(() => _Bytes("5 s"));
        await Assert.That(exampleProblem.Message).Contains("bytes").And.Contains("example");
    }

    [Test]
    public async Task ANullArgumentOfTheUnitIsRefusedWithTheNullException()
    {
        await Assert.That(() => new TableUnit(null!, "1", UnitWordSet.None)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => new TableUnit("counts", null!, UnitWordSet.None)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => new TableUnit("counts", "1", null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Ready units

    [Test]
    public async Task TheReadyUnitsBehaveAsDocumented()
    {
        await Assert.That(LimitUnits.Count.Name).IsEqualTo("counts");
        await Assert.That(LimitUnits.Count.SmallestMinimum).IsEqualTo(0L);
        await Assert.That(_Refusal(LimitUnits.Count, "5 B")).Contains("whole numbers without a unit");
        await Assert.That(_Parse(LimitUnits.Bytes, "5")).IsEqualTo(5L);
        await Assert.That(_Refusal(LimitUnits.Bytes, "1 s")).Contains("bytes: B, KiB, MiB, GiB, TiB");
        await Assert.That(LimitUnits.Duration.SmallestMinimum).IsEqualTo(1L);
        await Assert.That(_Refusal(LimitUnits.Duration, "5")).Contains("add a unit");
    }

    [Test]
    [Arguments("bytes")]
    [Arguments("duration")]
    [Arguments("count")]
    public async Task EveryExampleOfAReadyUnitParsesAsAValueOfItsOwnUnit(string name)
    {
        TableUnit unit = _Unit(name);

        bool parsed = unit.TryParse(unit.Example, long.MaxValue, out _, out string? problem);

        await Assert.That(parsed).IsTrue();
        await Assert.That(problem).IsNull();
    }

    [Test]
    public async Task TheForbiddenKeySuffixesOfTheReadyUnitsNameTheWordsOfTheUnit()
    {
        await Assert.That(LimitUnits.Count.ForbiddenKeySuffixes.Count).IsEqualTo(0);
        await Assert.That(LimitUnits.Bytes.ForbiddenKeySuffixes).IsEquivalentTo(
            ["Bytes", "_bytes", "KiB", "MiB", "GiB", "TiB", "Kib", "Mib", "Gib", "Tib"], CollectionOrdering.Matching);
        await Assert.That(LimitUnits.Duration.ForbiddenKeySuffixes).IsEquivalentTo(
            ["Seconds", "_seconds", "Secs", "Sec", "Ms", "_ms", "Millis", "Minutes", "Mins", "Hours", "Hrs", "Days", "Weeks"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARefusalQuotesOnlyThirtyTwoCharactersAndReplacesControlCharacters()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();
        string hostile = "5 MiB\u001b[31m" + new string('x', 1_000_000);

        bool parsed = definition.TryParse(hostile, out _, out string? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem!.Length).IsLessThan(300);
        await Assert.That(problem.Any(char.IsControl)).IsFalse();
        await Assert.That(problem).Contains("�");
    }

    #endregion
}
