// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>The one text form of setting values: parsing per type, formatting, round trips, and the problem messages.</summary>
internal sealed class SettingValueTextTests
{
    #region Helpers

    private static Setting _Port() => Setting.U64("t.port", "Port", "t", 40001, 1, 65535);

    private static Setting _Verify() => Setting.Enum("t.verify", "Verify", "t", 0, [("off", 0UL), ("verify", 1UL), ("verify and mark", 2UL)]);

    private static Setting _Protocol() => Setting.Enum("t.protocol", "Protocol", "t", 0, [("udp", 0UL), ("tcp", 1UL)]);

    private static Setting _Parse(SettingType type) => type switch
    {
        SettingType.Bool => Setting.Bool("t.bool", "Bool", "t", false),
        SettingType.String => Setting.String("t.string", "String", "t", "default"),
        SettingType.F64 => Setting.F64("t.f64", "F64", "t", 1.5),
        SettingType.U64 => Setting.U64("t.u64", "U64", "t", 7),
        SettingType.I64 => Setting.I64("t.i64", "I64", "t", 40001),
        SettingType.Bytes => Setting.Bytes("t.bytes", "Bytes", "t", []),
        SettingType.Enum => _Protocol(),
        SettingType.BoolArray => Setting.BoolArray("t.bools", "Bools", "t", []),
        SettingType.StringArray => Setting.StringArray("t.strings", "Strings", "t", []),
        SettingType.F64Array => Setting.F64Array("t.f64s", "F64s", "t", []),
        SettingType.U64Array => Setting.U64Array("t.u64s", "U64s", "t", []),
        _ => Setting.I64Array("t.i64s", "I64s", "t", []),
    };

    private static SettingValue _Value(Setting setting, string text)
    {
        if (!SettingValueText.TryParse(setting, text, out SettingValue value, out SettingProblem? problem))
        {
            throw new InvalidOperationException(problem?.ToString());
        }

        return value;
    }

    private static SettingProblem _Problem(Setting setting, string text)
    {
        bool parsed = SettingValueText.TryParse(setting, text, out SettingValue value, out SettingProblem? problem);
        if (parsed || problem is null)
        {
            throw new InvalidOperationException("Expected a problem for '" + text + "' but parsed " + value);
        }

        return problem;
    }

    #endregion

    #region Parsing

    [Test]
    public async Task BoolParsesTrueAndFalseInAnyCase()
    {
        Setting setting = _Parse(SettingType.Bool);

        await Assert.That(_Value(setting, "true")).IsEqualTo(SettingValue.Bool(true));
        await Assert.That(_Value(setting, "FALSE")).IsEqualTo(SettingValue.Bool(false));
        await Assert.That(_Value(setting, "False")).IsEqualTo(SettingValue.Bool(false));
    }

    [Test]
    public async Task IntegersParseExactlyWithoutRoundingThroughDouble()
    {
        await Assert.That(_Value(_Parse(SettingType.I64), "-12")).IsEqualTo(SettingValue.I64(-12));
        await Assert.That(_Value(_Parse(SettingType.I64), "9223372036854775807")).IsEqualTo(SettingValue.I64(long.MaxValue));
        await Assert.That(_Value(_Parse(SettingType.U64), "18446744073709551615")).IsEqualTo(SettingValue.U64(ulong.MaxValue));
    }

    [Test]
    public async Task FloatsParseInTheInvariantCultureWhateverTheThreadCultureIs()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // German uses the comma as decimal separator; the text form must not.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Setting setting = _Parse(SettingType.F64);

            await Assert.That(_Value(setting, "2.5")).IsEqualTo(SettingValue.F64(2.5));
            await Assert.That(_Value(setting, "1e3")).IsEqualTo(SettingValue.F64(1000));
            await Assert.That(SettingValueText.Format(setting, SettingValue.F64(2.5))).IsEqualTo("2.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task BytesParseAsHexWithOrWithoutPrefix()
    {
        Setting setting = _Parse(SettingType.Bytes);

        SettingValue prefixed = _Value(setting, "0x0a1B");
        SettingValue plain = _Value(setting, "0A1B");
        SettingValue empty = _Value(setting, string.Empty);

        prefixed.TryGetAsBytes(out byte[] prefixedBytes);
        plain.TryGetAsBytes(out byte[] plainBytes);
        empty.TryGetAsBytes(out byte[] emptyBytes);
        await Assert.That(prefixedBytes).IsEquivalentTo(new byte[] { 0x0A, 0x1B }, CollectionOrdering.Matching);
        await Assert.That(plainBytes).IsEquivalentTo(new byte[] { 0x0A, 0x1B }, CollectionOrdering.Matching);
        await Assert.That(emptyBytes).IsEmpty();
    }

    [Test]
    public async Task EnumParsesByNameInAnyCaseOrByAnAllowedNumber()
    {
        Setting setting = _Protocol();

        SettingValue byName = _Value(setting, "TCP");
        SettingValue byNumber = _Value(setting, "1");

        byName.TryGetAsEnum(out (string Name, ulong Value) named);
        byNumber.TryGetAsEnum(out (string Name, ulong Value) numbered);
        await Assert.That(named.Name).IsEqualTo("tcp");
        await Assert.That(named.Value).IsEqualTo(1UL);
        await Assert.That(numbered.Name).IsEqualTo("tcp");
    }

    [Test]
    public async Task StringKeepsInnerAndOuterSpaces()
    {
        SettingValue value = _Value(_Parse(SettingType.String), "  a  b ");

        value.TryGetAsString(out string text);
        await Assert.That(text).IsEqualTo("  a  b ");
    }

    [Test]
    public async Task ArraysParseAsJsonArrayOrCommaListAndEmptyTextIsTheEmptyArray()
    {
        Setting numbers = _Parse(SettingType.U64Array);
        Setting strings = _Parse(SettingType.StringArray);

        _Value(numbers, "[1,2]").TryGetAsU64Array(out ulong[] json);
        _Value(numbers, "1, 2").TryGetAsU64Array(out ulong[] list);
        _Value(strings, """["a,b","c"]""").TryGetAsStringArray(out string[] jsonStrings);
        _Value(strings, "a, c").TryGetAsStringArray(out string[] listStrings);
        _Value(strings, string.Empty).TryGetAsStringArray(out string[] none);
        _Value(numbers, "  ").TryGetAsU64Array(out ulong[] noNumbers);

        await Assert.That(json).IsEquivalentTo(new ulong[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(list).IsEquivalentTo(new ulong[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(jsonStrings).IsEquivalentTo(["a,b", "c"], CollectionOrdering.Matching);
        await Assert.That(listStrings).IsEquivalentTo(["a", "c"], CollectionOrdering.Matching);
        await Assert.That(none).IsEmpty();
        await Assert.That(noNumbers).IsEmpty();
    }

    [Test]
    public async Task BoundsAndEnumerationAreCheckedLikeEveryOtherPath()
    {
        Setting port = _Port();

        await Assert.That(_Value(port, "65535")).IsEqualTo(SettingValue.U64(65535));
        await Assert.That(_Problem(port, "65536").Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
    }

    #endregion

    #region Formatting

    [Test]
    public async Task FormatUsesTheDocumentedFormPerType()
    {
        await Assert.That(SettingValueText.Format(_Parse(SettingType.Bool), SettingValue.Bool(true))).IsEqualTo("true");
        await Assert.That(SettingValueText.Format(_Parse(SettingType.F64), SettingValue.F64(0.1))).IsEqualTo("0.1");
        await Assert.That(SettingValueText.Format(_Parse(SettingType.Bytes), SettingValue.Bytes([0xA, 0xB]))).IsEqualTo("0A0B");
        await Assert.That(SettingValueText.Format(_Protocol(), SettingValue.Enum("TCP", 1))).IsEqualTo("tcp");
        await Assert.That(SettingValueText.Format(_Parse(SettingType.String), SettingValue.String(" x "))).IsEqualTo(" x ");
        await Assert.That(SettingValueText.Format(_Parse(SettingType.StringArray), SettingValue.StringArray(["a", "b'c"])))
            .IsEqualTo("""["a","b'c"]""");
        await Assert.That(SettingValueText.Format(_Parse(SettingType.F64Array), SettingValue.F64Array([1.5, 2]))).IsEqualTo("[1.5,2]");
    }

    [Test]
    public async Task FormattedTextParsesBackToTheSameValueForEveryType()
    {
        (Setting Setting, SettingValue Value)[] cases =
        [
            (_Parse(SettingType.Bool), SettingValue.Bool(true)),
            (_Parse(SettingType.String), SettingValue.String(" spaced, text ")),
            (_Parse(SettingType.F64), SettingValue.F64(0.1 + 0.2)),
            (_Parse(SettingType.U64), SettingValue.U64(ulong.MaxValue)),
            (_Parse(SettingType.I64), SettingValue.I64(long.MinValue)),
            (_Parse(SettingType.Bytes), SettingValue.Bytes([0, 255, 16])),
            (_Protocol(), SettingValue.Enum("tcp", 1)),
            (_Parse(SettingType.BoolArray), SettingValue.BoolArray([true, false])),
            (_Parse(SettingType.StringArray), SettingValue.StringArray(["a,b", "c d", "é"])),
            (_Parse(SettingType.F64Array), SettingValue.F64Array([1e-7, 123456789.125])),
            (_Parse(SettingType.U64Array), SettingValue.U64Array([0, ulong.MaxValue])),
            (_Parse(SettingType.I64Array), SettingValue.I64Array([-1, long.MaxValue])),
        ];

        foreach ((Setting setting, SettingValue value) in cases)
        {
            string text = SettingValueText.Format(setting, value);
            await Assert.That(_Value(setting, text)).IsEqualTo(value);
        }
    }

    #endregion

    #region Problems

    [Test]
    public async Task WrongShapesGiveTheDocumentedMessagesWithAnExample()
    {
        SettingProblem whole = _Problem(_Parse(SettingType.I64), "abc");
        SettingProblem number = _Problem(_Parse(SettingType.F64), "x");
        SettingProblem flag = _Problem(_Parse(SettingType.Bool), "maybe");
        SettingProblem hex = _Problem(_Parse(SettingType.Bytes), "0xZZ");
        SettingProblem odd = _Problem(_Parse(SettingType.Bytes), "0xA");

        await Assert.That(whole.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(whole.Message).IsEqualTo("'abc' is not a whole number; example: 40001");
        await Assert.That(whole.Setting).IsEqualTo("t.i64");
        await Assert.That(whole.ToString()).IsEqualTo("t.i64: 'abc' is not a whole number; example: 40001");
        await Assert.That(number.Message).IsEqualTo("'x' is not a number; example: 1.5");
        await Assert.That(flag.Message).IsEqualTo("'maybe' is not true or false");
        await Assert.That(hex.Message).IsEqualTo("'0xZZ' is not hex bytes; example: 0A1B");
        await Assert.That(odd.Message).IsEqualTo("'0xA' is not hex bytes; example: 0A1B");
        await Assert.That(hex.Kind).IsEqualTo(SettingProblemKind.WrongType);
    }

    [Test]
    public async Task NumbersOutsideTheBoundsNameTheAllowedRange()
    {
        Setting port = _Port();
        Setting unbounded = _Parse(SettingType.U64);

        SettingProblem above = _Problem(port, "70000");
        SettingProblem zero = _Problem(port, "0");
        SettingProblem negative = _Problem(port, "-1");
        SettingProblem unboundedNegative = _Problem(unbounded, "-1");
        SettingProblem huge = _Problem(_Parse(SettingType.I64), "9223372036854775808");

        await Assert.That(above.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(above.Message).IsEqualTo("allowed 1 – 65535");
        await Assert.That(zero.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(zero.Message).IsEqualTo("allowed 1 – 65535");
        await Assert.That(negative.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(negative.Message).IsEqualTo("allowed 1 – 65535");
        await Assert.That(unboundedNegative.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(unboundedNegative.Message).IsEqualTo("allowed 0 – 18446744073709551615");
        await Assert.That(huge.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(huge.Message).IsEqualTo("allowed -9223372036854775808 – 9223372036854775807");
    }

    [Test]
    public async Task AVeryLongDigitStringIsARangeProblemWithoutBigIntegerWork()
    {
        Setting signed = _Parse(SettingType.I64);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        SettingProblem above = _Problem(signed, new string('9', 200_000));
        SettingProblem below = _Problem(signed, "  -" + new string('7', 200_000) + "\t");
        SettingProblem plusAbove = _Problem(signed, "+" + new string('1', 40));
        SettingProblem signOnly = _Problem(signed, "-");
        SettingProblem mixed = _Problem(signed, new string('1', 30) + "x");
        watch.Stop();

        await Assert.That(above.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(below.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(plusAbove.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(signOnly.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(mixed.Kind).IsEqualTo(SettingProblemKind.WrongType);

        // A quadratic parse of 200,000 digits takes seconds; the digit scan takes well under one.
        await Assert.That(watch.ElapsedMilliseconds).IsLessThan(1_000L);
    }

    [Test]
    public async Task NotFiniteNumbersAreRefused()
    {
        Setting setting = _Parse(SettingType.F64);

        foreach (string text in new[] { "NaN", "Infinity", "1e400", "-Infinity" })
        {
            SettingProblem problem = _Problem(setting, text);
            await Assert.That(problem.Kind).IsEqualTo(SettingProblemKind.NotFinite);
            await Assert.That(problem.Message).IsEqualTo($"'{text}' is not a finite number");
        }
    }

    [Test]
    public async Task UnknownEnumerationValuesListTheAllowedNames()
    {
        SettingProblem problem = _Problem(_Verify(), "check");

        await Assert.That(problem.Kind).IsEqualTo(SettingProblemKind.UnknownEnum);
        await Assert.That(problem.Message).IsEqualTo("allowed: off, verify, verify and mark");
        await Assert.That(_Value(_Verify(), "Verify And Mark")).IsEqualTo(SettingValue.Enum("verify and mark", 2));
    }

    [Test]
    public async Task ArrayProblemsNameTheElementAndTheJsonShape()
    {
        Setting setting = _Parse(SettingType.U64Array);

        SettingProblem brokenJson = _Problem(setting, "[40001,");
        SettingProblem badElement = _Problem(setting, "40001, abc");
        SettingProblem notArray = _Problem(setting, "[1] 2");
        SettingProblem nested = _Problem(_Parse(SettingType.StringArray), "[1]");

        await Assert.That(brokenJson.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(brokenJson.Message).IsEqualTo("'[40001,' is not a valid JSON array");
        await Assert.That(badElement.Message).IsEqualTo("element 2: 'abc' is not a whole number; example: 1");
        await Assert.That(notArray.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(nested.Message).IsEqualTo("element 1: 1 is not a JSON string");
    }

    [Test]
    public async Task ElementsOutsideTheBoundsKeepTheirPosition()
    {
        Setting setting = Setting.U64Array("t.ports", "Ports", "t", [], 1, 65535);

        SettingProblem problem = _Problem(setting, "80, 70000");

        await Assert.That(problem.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(problem.Message).IsEqualTo("element 2: allowed 1 – 65535");
    }

    [Test]
    public async Task ProblemKindsAreExactlyTheDocumentedOnes()
    {
        string[] names = [.. Enum.GetNames<SettingProblemKind>()];

        await Assert.That(names).IsEquivalentTo(
            ["WrongType", "BelowMinimum", "AboveMaximum", "NotFinite", "UnknownEnum", "InvalidPath", "UnknownSetting"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task RandomTextsNeverThrowForAnyType()
    {
        Random random = new(42);
        const string alphabet = "0123456789abcdefXx.,-+eE[]\"' \t{}NaInfty:é";
        foreach (SettingType type in Enum.GetValues<SettingType>())
        {
            Setting setting = _Parse(type);
            for (int i = 0; i < 1000; i++)
            {
                char[] chars = new char[random.Next(0, 24)];
                for (int c = 0; c < chars.Length; c++)
                {
                    chars[c] = alphabet[random.Next(alphabet.Length)];
                }

                // The only contract is "never throws"; a success must also validate against the setting.
                bool parsed = SettingValueText.TryParse(setting, new string(chars), out SettingValue value, out SettingProblem? problem);
                await Assert.That(parsed == (problem is null)).IsTrue();
                if (parsed)
                {
                    await Assert.That(setting.TryValidate(value, out _)).IsTrue();
                }
            }
        }
    }

    [Test]
    public async Task NullArgumentsAreRefused()
    {
        Setting setting = _Parse(SettingType.Bool);

        await Assert.That(() => SettingValueText.TryParse(setting, null!, out _, out _)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => SettingValueText.TryParse(null!, "x", out _, out _)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => SettingValueText.Format(null!, SettingValue.Bool(true))).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion
}
