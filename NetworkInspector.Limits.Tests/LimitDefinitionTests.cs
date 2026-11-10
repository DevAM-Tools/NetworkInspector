// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>Limit definitions: construction rules, default in range, range, the off switch, and the text form.</summary>
internal sealed class LimitDefinitionTests
{
    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    private const long _GiB = 1L << 30;

    #region Declaration data

    [Test]
    public async Task AValidDefinitionExposesItsKeyPartsAndData()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "blf.maxContainerSize");

        await Assert.That(definition.Key).IsEqualTo("blf.maxContainerSize");
        await Assert.That(definition.Group).IsEqualTo("blf");
        await Assert.That(definition.LocalName).IsEqualTo("maxContainerSize");
        await Assert.That(definition.Unit).IsEqualTo(LimitUnits.Bytes);
        await Assert.That(definition.AllowOff).IsTrue();
        await Assert.That(definition.ReadOnly).IsFalse();
        await Assert.That(definition.Default).IsEqualTo(128 * _MiB);
        await Assert.That(definition.DefaultValue).IsEqualTo(LimitValue.Of(128 * _MiB));
        await Assert.That(definition.Texts.Title).IsEqualTo("Largest demo size");
    }

    [Test]
    [Arguments("ip.reassembly.maxFragments", "ip.reassembly", "maxFragments")]
    [Arguments("a.b.c1.maxItems", "a.b.c1", "maxItems")]
    [Arguments("default.maxItems", "default", "maxItems")]
    [Arguments("http.max_decompressed_size", "http", "max_decompressed_size")]
    [Arguments("someip2.maxPayload", "someip2", "maxPayload")]
    public async Task KeysWithDottedGroupsAndSnakeCaseNamesAreAccepted(string key, string group, string localName)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: key);

        await Assert.That(definition.Group).IsEqualTo(group);
        await Assert.That(definition.LocalName).IsEqualTo(localName);
    }

    #endregion

    #region Default in range

    [Test]
    [Arguments(_KiB - 1)]
    [Arguments(_GiB + 1)]
    public async Task ADefaultOutsideTheRangeIsRefusedAndTheMessageNamesTheKeyAndTheParameter(long defaultValue)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => LimitTestDefinitions.Bytes(defaultValue: defaultValue, minimum: _KiB, maximum: _GiB));

        await Assert.That(exception.Message).Contains("demo.maxSize");
        await Assert.That(exception.Message).Contains("defaultValue");
        await Assert.That(exception.ParamName).IsEqualTo("defaultValue");
    }

    [Test]
    public async Task ADefaultEqualToTheMinimumAndTheMaximumIsAccepted()
    {
        LimitDefinition atMinimum = LimitTestDefinitions.Bytes(defaultValue: _KiB, minimum: _KiB, maximum: _GiB);
        LimitDefinition atMaximum = LimitTestDefinitions.Bytes(defaultValue: _GiB, minimum: _KiB, maximum: _GiB);
        LimitDefinition single = LimitTestDefinitions.Bytes(defaultValue: _MiB, minimum: _MiB, maximum: _MiB);

        await Assert.That(atMinimum.DefaultValue).IsEqualTo(LimitValue.Of(_KiB));
        await Assert.That(atMaximum.DefaultValue).IsEqualTo(LimitValue.Of(_GiB));
        await Assert.That(single.DefaultValue).IsEqualTo(LimitValue.Of(_MiB));
    }

    [Test]
    public async Task ANegativeMinimumIsRefused()
    {
        await Assert.That(() => LimitTestDefinitions.Bytes(minimum: -1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AMinimumAboveTheMaximumIsRefused()
    {
        await Assert.That(() => LimitTestDefinitions.Bytes(defaultValue: 10, minimum: 10, maximum: 9)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AMinimumBelowTheSmallestMinimumOfTheUnitIsRefused()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => LimitTestDefinitions.Duration(minimum: 0));

        await Assert.That(exception.Message).Contains("at least 1");
        await Assert.That(exception.ParamName).IsEqualTo("minimum");
    }

    [Test]
    public async Task ANullArgumentIsRefusedWithTheNullException()
    {
        await Assert.That(() => new LimitDefinition(null!, LimitUnits.Count, 1, 0, 2,
            LimitTestDefinitions.Texts())).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => new LimitDefinition("a.b", null!, 1, 0, 2,
            LimitTestDefinitions.Texts())).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => new LimitDefinition("a.b", LimitUnits.Count, 1, 0, 2,
            null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Key and texts

    [Test]
    [Arguments("max")]
    [Arguments(".max")]
    [Arguments("blf.")]
    [Arguments("Blf.maxSize")]
    [Arguments("blf.MaxSize")]
    [Arguments("1blf.maxSize")]
    [Arguments("blf..maxSize")]
    [Arguments("a..b.maxSize")]
    [Arguments("maxSize.1x")]
    [Arguments("blf.1maxSize")]
    [Arguments("blf.max-size")]
    [Arguments("blf.maxGröße")]
    [Arguments("bläf.maxSize")]
    [Arguments("blf.max size")]
    [Arguments("blf.maxContainerBytes")]
    [Arguments("blf.maxContainer_bytes")]
    [Arguments("x.sizeMib")]
    [Arguments("x.sizeKib")]
    [Arguments("x.sizeMiB")]
    [Arguments("x.sizeKiB")]
    [Arguments("x.sizeGiB")]
    [Arguments("x.sizeGib")]
    public async Task AnInvalidKeyIsRefusedAndTheMessageNamesTheKey(string key)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => LimitTestDefinitions.Bytes(key: key));

        await Assert.That(exception.Message).Contains(key);
        await Assert.That(exception.ParamName).IsEqualTo("key");
    }

    [Test]
    [Arguments("x.timeoutSeconds")]
    [Arguments("x.wait_seconds")]
    [Arguments("x.waitMs")]
    [Arguments("x.wait_ms")]
    [Arguments("x.waitMillis")]
    [Arguments("x.waitMinutes")]
    [Arguments("x.keepHours")]
    [Arguments("x.keepDays")]
    [Arguments("x.keepWeeks")]
    [Arguments("x.timeoutSecs")]
    [Arguments("x.timeoutSec")]
    [Arguments("x.keepMins")]
    [Arguments("x.keepHrs")]
    public async Task ADurationKeyThatEndsWithAUnitWordIsRefused(string key)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => LimitTestDefinitions.Duration(key: key));

        await Assert.That(exception.Message).Contains(key);
        await Assert.That(exception.ParamName).IsEqualTo("key");
    }

    [Test]
    public async Task TheForbiddenEndingsBelongToTheUnitOfTheLimit()
    {
        // Ms is a duration word: a byte limit of that name is not a lie, a duration limit of that name is.
        LimitDefinition bytes = LimitTestDefinitions.Bytes(key: "x.waitMs");

        await Assert.That(bytes.LocalName).IsEqualTo("waitMs");
        await Assert.That(() => LimitTestDefinitions.Duration(key: "x.waitMs")).Throws<ArgumentException>();
    }

    [Test]
    public async Task ALimitNamedItemsOrTermsIsNotMistakenForAUnitWord()
    {
        // "Ms" and "_ms" are case-sensitive suffixes: plural words that merely end in "ms" are fine.
        LimitDefinition items = LimitTestDefinitions.Count(key: "demo.maxItems");
        LimitDefinition terms = LimitTestDefinitions.Duration(key: "filter.maxTerms");

        await Assert.That(items.LocalName).IsEqualTo("maxItems");
        await Assert.That(terms.LocalName).IsEqualTo("maxTerms");
    }

    [Test]
    [Arguments("Title", "")]
    [Arguments("Title", "   ")]
    [Arguments("Title", "Ends with a period.")]
    [Arguments("Title", "Line\nbreak")]
    [Arguments("Description", "")]
    [Arguments("Description", "No period at the end")]
    [Arguments("Description", "Has a\ttab inside.")]
    [Arguments("Reason", "")]
    [Arguments("Reason", "No period")]
    [Arguments("Benefit", "")]
    [Arguments("Benefit", "No period")]
    [Arguments("SmallerValues", "")]
    [Arguments("SmallerValues", "No period")]
    [Arguments("LargerValues", "")]
    [Arguments("LargerValues", "No period")]
    [Arguments("OffEffect", "")]
    [Arguments("OffEffect", "No period")]
    public async Task AnIncompleteOrMalformedTextIsRefusedAndTheMessageNamesTheField(string field, string text)
    {
        LimitTexts good = LimitTestDefinitions.Texts();
        LimitTexts broken = field switch
        {
            "Title" => good with { Title = text },
            "Description" => good with { Description = text },
            "Reason" => good with { Reason = text },
            "Benefit" => good with { Benefit = text },
            "SmallerValues" => good with { SmallerValues = text },
            "LargerValues" => good with { LargerValues = text },
            _ => good with { OffEffect = text },
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => LimitTestDefinitions.Bytes(texts: broken));

        await Assert.That(exception.Message).Contains(field);
        await Assert.That(exception.Message).Contains("demo.maxSize");
    }

    [Test]
    public async Task ATitleOfSixtyCharactersIsAcceptedAndOneMoreIsRefused()
    {
        LimitTexts good = LimitTestDefinitions.Texts();

        LimitDefinition accepted = LimitTestDefinitions.Bytes(texts: good with { Title = new string('t', 60) });

        await Assert.That(accepted.Texts.Title.Length).IsEqualTo(60);
        await Assert.That(() => LimitTestDefinitions.Bytes(texts: good with { Title = new string('t', 61) })).Throws<ArgumentException>();
    }

    #endregion

    #region Range

    [Test]
    [Arguments(_KiB, true)]
    [Arguments(128 * _GiB, true)]
    [Arguments(_KiB - 1, false)]
    [Arguments(128 * _GiB + 1, false)]
    [Arguments(0L, false)]
    public async Task TheRangeIncludesBothEndsAndNothingOutside(long number, bool valid)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: 128 * _GiB);

        bool result = definition.TryValidate(LimitValue.Of(number), out string? problem);

        await Assert.That(result).IsEqualTo(valid);
        await Assert.That(problem is null).IsEqualTo(valid);
    }

    [Test]
    public async Task TheRefusalNamesTheKeyAndTheAllowedRange()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: 128 * _GiB);

        definition.TryValidate(LimitValue.Of(1_023), out string? problem);

        await Assert.That(problem).IsEqualTo("demo.maxSize: allowed 1 KiB – 128 GiB, or off");
    }

    [Test]
    public async Task ParsingAValueOutsideTheRangeNamesTheRange()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: 128 * _GiB);

        bool parsed = definition.TryParse("129GiB", out _, out string? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem).Contains("allowed 1 KiB – 128 GiB, or off");
    }

    [Test]
    public async Task ThrowIfInvalidThrowsTheRangeTextForAValueOutsideTheRange()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: 128 * _GiB);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => definition.ThrowIfInvalid(LimitValue.Of(1_023), "option"));

        await Assert.That(exception.ParamName).IsEqualTo("option");
        await Assert.That(exception.Message).Contains("allowed 1 KiB – 128 GiB, or off");
        definition.ThrowIfInvalid(LimitValue.Of(_KiB), "option");
        definition.ThrowIfInvalid(LimitValue.Off, "option");
    }

    #endregion

    #region Off switch

    [Test]
    public async Task OffIsInsideTheRangeOfALimitThatMayBeSwitchedOff()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        await Assert.That(definition.TryValidate(LimitValue.Off, out string? problem)).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(definition.FormatRange()).EndsWith(", or off");
    }

    [Test]
    public async Task OffIsRefusedByALimitThatCannotBeSwitchedOffAndTheRangeTextLeavesItOut()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: 128 * _GiB, allowOff: false);

        bool valid = definition.TryValidate(LimitValue.Off, out string? problem);

        await Assert.That(valid).IsFalse();
        await Assert.That(problem).IsEqualTo("demo.maxSize: off is not accepted; allowed 1 KiB – 128 GiB");
        await Assert.That(definition.FormatRange()).IsEqualTo("1 KiB – 128 GiB");
        await Assert.That(definition.TryValidate(LimitValue.Of(_KiB), out _)).IsTrue();
    }

    [Test]
    public async Task ThrowIfInvalidRefusesOffOfALimitThatCannotBeSwitchedOff()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(allowOff: false);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => definition.ThrowIfInvalid(LimitValue.Off, "option"));

        await Assert.That(exception.Message).Contains("off is not accepted");
    }

    [Test]
    public async Task OffIsRefusedWithTheExampleWhenTheDefinitionDoesNotAllowItInParseAndInParseShape()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(allowOff: false);

        bool parsed = definition.TryParse("off", out _, out string? problem);
        bool shaped = definition.TryParseShape("off", out _, out string? shapeProblem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem).IsEqualTo("demo.maxSize: off is not accepted for this limit; write a whole number such as 256 MiB");
        await Assert.That(shaped).IsFalse();
        await Assert.That(shapeProblem).IsEqualTo("off is not accepted for this limit; write a whole number such as 256 MiB");
    }

    [Test]
    public async Task TheEmptyValueHintDoesNotOfferOffWhenTheDefinitionDoesNotAllowIt()
    {
        LimitDefinition withOff = LimitTestDefinitions.Bytes();
        LimitDefinition withoutOff = LimitTestDefinitions.Bytes(allowOff: false);

        bool offParsed = withOff.TryParseShape("", out _, out string? offProblem);
        bool noOffParsed = withoutOff.TryParseShape("", out _, out string? noOffProblem);

        await Assert.That(offParsed).IsFalse();
        await Assert.That(noOffParsed).IsFalse();
        await Assert.That(offProblem).IsEqualTo("empty value; write a whole number such as 256 MiB, or off");
        await Assert.That(noOffProblem).DoesNotContain("off");
    }

    [Test]
    [Arguments("off")]
    [Arguments("OFF")]
    [Arguments(" Off ")]
    public async Task OffIsAcceptedInAnyCaseWithSpacesWhenAllowed(string text)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        bool parsed = definition.TryParse(text, out LimitValue value, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(value).IsEqualTo(LimitValue.Off);
    }

    #endregion

    #region Text form

    /// <summary>A unit that is not table-shaped: proves the definition depends on the interface only.</summary>
    private sealed class FakeUnit : ILimitUnit
    {
        public string? LastText { get; private set; }

        public long LastMaximum { get; private set; }

        public string Example => "seven";

        public long SmallestMinimum => 0;

        public IReadOnlyList<string> ForbiddenKeySuffixes { get; } = ["Seven"];

        public bool TryParse(ReadOnlySpan<char> text, long maximum, out long value, [NotNullWhen(false)] out string? problem)
        {
            LastText = text.ToString();
            LastMaximum = maximum;
            if (text.Equals("seven", StringComparison.Ordinal))
            {
                value = 7;
                problem = null;
                return true;
            }

            value = 0;
            problem = "say seven";
            return false;
        }

        public string Format(long value) => value == 7
            ? "seven"
            : "other " + value.ToString(CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task TheDefinitionTrimsAndDelegatesEveryOtherTextToTheUnit()
    {
        FakeUnit unit = new();
        LimitDefinition definition = new("fake.maxThings", unit, 7, 1, 100, LimitTestDefinitions.Texts());

        bool parsed = definition.TryParse("  seven  ", out LimitValue value, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(value).IsEqualTo(LimitValue.Of(7));
        await Assert.That(unit.LastText).IsEqualTo("seven");
        await Assert.That(unit.LastMaximum).IsEqualTo(100L);
        await Assert.That(definition.Format(value)).IsEqualTo("seven");
        await Assert.That(definition.FormatRange()).IsEqualTo("other 1 – other 100, or off");
    }

    [Test]
    public async Task TheDefinitionDoesNotAskTheUnitForOffOrAnEmptyText()
    {
        FakeUnit unit = new();
        LimitDefinition definition = new("fake.maxThings", unit, 7, 1, 100, LimitTestDefinitions.Texts());

        bool off = definition.TryParse("off", out LimitValue offValue, out _);
        bool empty = definition.TryParse("  ", out _, out string? emptyProblem);

        await Assert.That(off).IsTrue();
        await Assert.That(offValue).IsEqualTo(LimitValue.Off);
        await Assert.That(empty).IsFalse();
        await Assert.That(emptyProblem).IsEqualTo("fake.maxThings: empty value; write a whole number such as seven, or off");
        await Assert.That(unit.LastText).IsNull();
    }

    [Test]
    public async Task TheUnitProblemGetsTheKeyInFrontOfTryParseAndNotOfTryParseShape()
    {
        LimitDefinition definition = new("fake.maxThings", new FakeUnit(), 7, 1, 100, LimitTestDefinitions.Texts());

        bool parsed = definition.TryParse("six", out _, out string? withKey);
        bool shaped = definition.TryParseShape("six", out _, out string? withoutKey);

        await Assert.That(parsed).IsFalse();
        await Assert.That(shaped).IsFalse();
        await Assert.That(withKey).IsEqualTo("fake.maxThings: say seven");
        await Assert.That(withoutKey).IsEqualTo("say seven");
    }

    [Test]
    public async Task TheShapeIsParsedBeforeTheRangeIsChecked()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: _GiB);

        bool shaped = definition.TryParseShape("2GiB", out LimitValue value, out string? problem);
        bool parsed = definition.TryParse("2GiB", out _, out string? parseProblem);

        await Assert.That(shaped).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(value).IsEqualTo(LimitValue.Of(2 * _GiB));
        await Assert.That(parsed).IsFalse();
        await Assert.That(parseProblem).Contains("allowed 1 KiB – 1 GiB");
    }

    [Test]
    public async Task AKeyThatEndsWithAForbiddenSuffixOfAnImplementedUnitIsRefused()
    {
        await Assert.That(() => new LimitDefinition("fake.maxSeven", new FakeUnit(), 7, 1, 100, LimitTestDefinitions.Texts()))
            .Throws<ArgumentException>();
    }

    #endregion
}
