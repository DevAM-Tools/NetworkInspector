// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>Limits as settings: registration, validation, the text and JSON codecs, the file form, and sparse persistence.</summary>
internal sealed class LimitSettingTests
{
    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    private const long _GiB = 1L << 30;

    private const long _TiB = 1L << 40;

    #region Helpers

    private static Setting _Setting(LimitDefinition? definition = null) =>
        Setting.FromLimit(definition ?? LimitTestDefinitions.Bytes());

    private static JsonElement _Element(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SettingValue _Parsed(Setting setting, string text)
    {
        if (!SettingValueText.TryParse(setting, text, out SettingValue value, out SettingProblem? problem))
        {
            throw new InvalidOperationException(problem?.ToString());
        }

        return value;
    }

    private static string _WrittenJson(Setting setting, SettingValue value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            SettingValueJson.Write(writer, setting, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Registration

    [Test]
    public async Task ARegisteredLimitIsASettingOfTheLimitTypeBuiltFromItsDefinition()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "blf.maxContainerSize");
        using SettingsManager manager = new();

        SettingRegistrationResult result = new SettingsRegistrar(manager).RegisterLimit(definition);

        Setting setting = manager.GetSetting("blf.maxContainerSize") ?? throw new InvalidOperationException("not registered");
        await Assert.That(setting.Type).IsEqualTo(SettingType.Limit);
        await Assert.That(setting.Name).IsEqualTo("blf.maxContainerSize");
        await Assert.That(setting.UiName).IsEqualTo(definition.Texts.Title);
        await Assert.That(setting.Description).IsEqualTo(definition.Texts.Description);
        await Assert.That(setting.GroupName).IsEqualTo("blf");
        await Assert.That(setting.LimitDefinition).IsSameReferenceAs(definition);
        await Assert.That(setting.DefaultValue).IsEqualTo(SettingValue.Limit(definition.DefaultValue));
        await Assert.That(result.TryGetAsLimit(out LimitValue registered)).IsTrue();
        await Assert.That(registered).IsEqualTo(definition.DefaultValue);
    }

    [Test]
    public async Task GetLimitReturnsTheValueAndNullForAnOtherTypeOrAnUnknownKey()
    {
        using SettingsManager manager = new();
        SettingsRegistrar registrar = new(manager);
        registrar.RegisterLimit(LimitTestDefinitions.Bytes());
        registrar.RegisterI64Setting("demo.plain", "Plain", "demo", 5);

        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
        await Assert.That(manager.GetLimit("demo.plain")).IsNull();
        await Assert.That(manager.GetLimit("demo.unknown")).IsNull();

        // The read-only view of the manager answers the same.
        ReadOnlySettingsManagerView view = manager.AsReadOnlyView();
        await Assert.That(view.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
        await Assert.That(view.GetLimit("demo.plain")).IsNull();
    }

    [Test]
    public async Task ALimitOptionHasTheLocalNameAndNoGroup()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "blf.maxContainerSize");
        using SettingsManager options = new();

        new SettingsRegistrar(options).RegisterLimitOption(definition);

        Setting option = options.GetSetting("maxContainerSize") ?? throw new InvalidOperationException("not registered");
        await Assert.That(option.GroupName).IsEqualTo(string.Empty);
        await Assert.That(option.LimitDefinition).IsSameReferenceAs(definition);
        await Assert.That(options.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)definition.DefaultValue);
    }

    [Test]
    public async Task TheReadOnlyViewExposesTheDefinitionAndTheValue()
    {
        Setting setting = _Setting();

        ReadOnlySettingView view = setting.AsReadOnlyView();

        await Assert.That(view.LimitDefinition).IsSameReferenceAs(setting.LimitDefinition);
        await Assert.That(view.TryGetAsLimit(out LimitValue value)).IsTrue();
        await Assert.That(value).IsEqualTo(LimitValue.Of(128 * _MiB));
    }

    [Test]
    public async Task ASettingThatIsNotALimitHasNoDefinitionAndNoLimitValue()
    {
        Setting plain = Setting.I64("demo.plain", "Plain", "demo", 5);

        await Assert.That(plain.LimitDefinition).IsNull();
        await Assert.That(plain.TryGetAsLimit(out _)).IsFalse();
    }

    [Test]
    public async Task ARegistrationWithoutADefinitionIsRefused()
    {
        await Assert.That(() => Setting.FromLimit(null!)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => Setting.FromLimitOption(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    [Test]
    public async Task ALimitInTheGroupDefaultIsAValidDefinitionButNotAValidSetting()
    {
        // The limits library keeps no reserved group; the settings reserve "default" because the file default.json holds the
        // default group, so the refusal happens where the limit becomes a setting.
        LimitDefinition definition = LimitTestDefinitions.Count(key: "default.maxItems");

        await Assert.That(definition.Group).IsEqualTo("default");

        InvalidNameSettingsException fromLimit = Assert.Throws<InvalidNameSettingsException>(() => Setting.FromLimit(definition));
        using SettingsManager manager = new();
        InvalidNameSettingsException fromRegistrar = Assert.Throws<InvalidNameSettingsException>(() =>
        {
            SettingsRegistrar registrar = new(manager);
            registrar.RegisterLimit(definition);
        });

        await Assert.That(fromLimit.Message).Contains("default");
        await Assert.That(fromRegistrar.Message).Contains("default");
    }

    #endregion

    #region Range

    [Test]
    [Arguments(1_023L, SettingProblemKind.BelowMinimum)]
    [Arguments(0L, SettingProblemKind.BelowMinimum)]
    [Arguments(_TiB + 1, SettingProblemKind.AboveMaximum)]
    public async Task AValueOutsideTheRangeIsRefusedWithTheRangeAndTheCurrentValueStays(long number, SettingProblemKind kind)
    {
        Setting setting = _Setting();

        bool stored = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Of(number)), out SettingProblem? problem);

        await Assert.That(stored).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(kind);
        await Assert.That(problem.Message).IsEqualTo("allowed 1 KiB – 1 TiB, or off");
        await Assert.That(problem.ToString()).StartsWith("demo.maxSize: allowed");
        await Assert.That(setting.PendingValue).IsEqualTo(setting.Value);
    }

    [Test]
    [Arguments(_KiB)]
    [Arguments(_TiB)]
    public async Task BothEndsOfTheRangeAreAccepted(long number)
    {
        Setting setting = _Setting();

        bool stored = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Of(number)), out SettingProblem? problem);

        await Assert.That(stored).IsTrue();
        await Assert.That(problem).IsNull();
    }

    [Test]
    public async Task OffIsAcceptedAndTakesEffectOnApply()
    {
        Setting setting = _Setting();

        setting.SetPendingValue(SettingValue.Limit(LimitValue.Off));
        setting.Apply();

        await Assert.That(setting.TryGetAsLimit(out LimitValue value)).IsTrue();
        await Assert.That(value.IsOff).IsTrue();
    }

    [Test]
    public async Task AnotherTypeOfValueIsRefusedAsTheWrongType()
    {
        Setting setting = _Setting();

        bool stored = setting.TrySetPendingValue(SettingValue.I64(5), out SettingProblem? problem);

        await Assert.That(stored).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.WrongType);
    }

    #endregion

    #region A limit that cannot be switched off

    [Test]
    public async Task OffIsRefusedAsTheWrongTypeAndTheCurrentValueStaysWhenTheDefinitionDoesNotAllowIt()
    {
        Setting setting = _Setting(LimitTestDefinitions.Bytes(allowOff: false));

        bool stored = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Off), out SettingProblem? problem);

        await Assert.That(stored).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(problem.Message).IsEqualTo("off is not accepted; allowed 1 KiB – 1 TiB");
        await Assert.That(setting.PendingValue).IsEqualTo(setting.Value);
    }

    [Test]
    public async Task TheTextFormRefusesOffWithTheExampleWhenTheDefinitionDoesNotAllowIt()
    {
        Setting setting = _Setting(LimitTestDefinitions.Bytes(allowOff: false));

        bool parsed = SettingValueText.TryParse(setting, "off", out _, out SettingProblem? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(problem.Message).IsEqualTo("off is not accepted for this limit; write a whole number such as 256 MiB");
    }

    [Test]
    public async Task AStoredOffIsIgnoredWithAWarningWhenTheDefinitionDoesNotAllowIt()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": "off"}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting(LimitTestDefinitions.Bytes(allowOff: false)));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("off is not accepted for this limit");
        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
    }

    #endregion

    #region Text form

    [Test]
    [Arguments("256MiB", 256 * _MiB)]
    [Arguments("268435456", 256 * _MiB)]
    [Arguments("1_024", 1 * _KiB)]
    [Arguments("1 TiB", _TiB)]
    public async Task TheTextFormAcceptsTheUnitSpellings(string text, long expected)
    {
        await Assert.That(_Parsed(_Setting(), text)).IsEqualTo(SettingValue.Limit(LimitValue.Of(expected)));
    }

    [Test]
    public async Task TheTextFormAcceptsOff()
    {
        await Assert.That(_Parsed(_Setting(), " OFF ")).IsEqualTo(SettingValue.Limit(LimitValue.Off));
    }

    [Test]
    [Arguments("1.5GiB", "1536 MiB")]
    [Arguments("512MB", "512 MiB")]
    [Arguments("10s", "bytes: B, KiB, MiB, GiB, TiB")]
    [Arguments("-1", "no sign")]
    [Arguments("1e6", "whole number")]
    [Arguments("", "empty")]
    [Arguments("256MiB extra", "unexpected")]
    [Arguments("99999999999999999999", "too large")]
    public async Task TheTextFormRefusesAmbiguousInputWithTheSuggestionAndTheSettingName(string text, string expected)
    {
        Setting setting = _Setting();

        bool parsed = SettingValueText.TryParse(setting, text, out _, out SettingProblem? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(problem.Message).Contains(expected);
        await Assert.That(problem.ToString()).StartsWith("demo.maxSize: ");
    }

    [Test]
    public async Task TheTextFormRefusesAValueOutsideTheRangeWithTheRange()
    {
        bool parsed = SettingValueText.TryParse(_Setting(), "1B", out _, out SettingProblem? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(problem.Message).IsEqualTo("allowed 1 KiB – 1 TiB, or off");
    }

    [Test]
    [Arguments(268_435_456L, "256 MiB")]
    [Arguments(1_000_001L, "1000001 B")]
    public async Task TheTextFormFormatsInTheLargestExactUnitAndParsesBack(long number, string expected)
    {
        Setting setting = _Setting(LimitTestDefinitions.Bytes(minimum: 1));
        SettingValue value = SettingValue.Limit(LimitValue.Of(number));

        string text = SettingValueText.Format(setting, value);

        await Assert.That(text).IsEqualTo(expected);
        await Assert.That(_Parsed(setting, text)).IsEqualTo(value);
        await Assert.That(SettingValueText.Format(setting, SettingValue.Limit(LimitValue.Off))).IsEqualTo("off");
    }

    [Test]
    public async Task ADurationLimitFormatsAndParsesWithItsUnit()
    {
        Setting setting = _Setting(LimitTestDefinitions.Duration());

        await Assert.That(_Parsed(setting, "90s")).IsEqualTo(SettingValue.Limit(LimitValue.Of(90_000)));
        await Assert.That(SettingValueText.Format(setting, SettingValue.Limit(LimitValue.Of(120_000)))).IsEqualTo("2 min");
        await Assert.That(SettingValueText.TryParse(setting, "30", out _, out SettingProblem? problem)).IsFalse();
        await Assert.That(problem!.Message).Contains("30 s or 30 ms");
    }

    #endregion

    #region JSON form

    [Test]
    [Arguments("268435456", 268_435_456L)]
    [Arguments("\"256 MiB\"", 256 * _MiB)]
    [Arguments("\"1TiB\"", _TiB)]
    public async Task TheJsonFormReadsANumberOfTheBaseUnitAndAUnitString(string json, long expected)
    {
        bool read = SettingValueJson.TryRead(_Setting(), _Element(json), out SettingValue value, out SettingProblem? problem);

        await Assert.That(read).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(value).IsEqualTo(SettingValue.Limit(LimitValue.Of(expected)));
    }

    [Test]
    [Arguments("\"off\"")]
    [Arguments("\"OFF\"")]
    public async Task TheJsonFormReadsOff(string json)
    {
        bool read = SettingValueJson.TryRead(_Setting(), _Element(json), out SettingValue value, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(value).IsEqualTo(SettingValue.Limit(LimitValue.Off));
    }

    [Test]
    [Arguments("-5", "negative")]
    [Arguments("1.5", "whole number")]
    [Arguments("9223372036854775808", "whole number")]
    [Arguments("\"512MB\"", "512 MiB")]
    [Arguments("\"\"", "empty")]
    [Arguments("true", "expected Limit, got JSON boolean")]
    [Arguments("{}", "expected Limit, got JSON object")]
    [Arguments("[1]", "expected Limit, got JSON array")]
    [Arguments("null", "got JSON null")]
    public async Task TheJsonFormRefusesAnInvalidElementWithAProblem(string json, string expected)
    {
        bool read = SettingValueJson.TryRead(_Setting(), _Element(json), out _, out SettingProblem? problem);

        await Assert.That(read).IsFalse();
        await Assert.That(problem!.Message).Contains(expected);
    }

    [Test]
    public async Task TheJsonFormRefusesAValueOutsideTheRange()
    {
        bool read = SettingValueJson.TryRead(_Setting(), _Element("5"), out _, out SettingProblem? problem);

        await Assert.That(read).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
    }

    [Test]
    public async Task TheJsonFormWritesTheBaseNumberOrOff()
    {
        Setting setting = _Setting();

        await Assert.That(_WrittenJson(setting, SettingValue.Limit(LimitValue.Of(268_435_456)))).IsEqualTo("268435456");
        await Assert.That(_WrittenJson(setting, SettingValue.Limit(LimitValue.Off))).IsEqualTo("\"off\"");
    }

    #endregion

    #region Settings file

    [Test]
    public async Task AFileWithADecimalUnitLoadsWithAWarningThatNamesTheKeyAndTheSuggestionAndKeepsTheDefault()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": "512MB"}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting());

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.DeserializationError);
        await Assert.That(warnings[0].SettingName).IsEqualTo("demo.maxSize");
        await Assert.That(warnings[0].Message).Contains("512 MiB");
        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
    }

    [Test]
    public async Task AFileWithANumberAUnitStringAndOffLoadsEachValue()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.a": 536870912, "demo.b": "256 MiB", "demo.c": "off"}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting(LimitTestDefinitions.Bytes(key: "demo.a")));
        manager.RegisterSetting(_Setting(LimitTestDefinitions.Bytes(key: "demo.b")));
        manager.RegisterSetting(_Setting(LimitTestDefinitions.Bytes(key: "demo.c")));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.GetLimit("demo.a")).IsEqualTo((LimitValue?)LimitValue.Of(512 * _MiB));
        await Assert.That(manager.GetLimit("demo.b")).IsEqualTo((LimitValue?)LimitValue.Of(256 * _MiB));
        await Assert.That(manager.GetLimit("demo.c")).IsEqualTo((LimitValue?)LimitValue.Off);
    }

    [Test]
    public async Task AStoredValueOutsideTheRangeIsIgnoredWithAnOutOfRangeWarning()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": 5}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting());

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
        await Assert.That(warnings[0].Message).Contains("allowed 1 KiB – 1 TiB, or off");
        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
    }

    [Test]
    [Arguments("true")]
    [Arguments("{}")]
    [Arguments("[1]")]
    public async Task AStoredValueOfAnotherJsonKindIsAnIncompatibleTypeWarning(string json)
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", "{\"demo.maxSize\": " + json + "}");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting());

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
    }

    #endregion

    #region Persistence

    [Test]
    public async Task ALimitSetInTheTextFormWithAUnitSurvivesSaveAndLoad()
    {
        using SettingsTempFolder folder = new();
        using (SettingsManager manager = new(folder.Path, TestLayout.Default))
        {
            Setting setting = _Setting();
            manager.RegisterSetting(setting);
            manager.Load();
            setting.SetPendingValue(_Parsed(setting, "256 MiB"));
            manager.ApplyChanges();
            manager.Save();
        }

        using SettingsManager later = new(folder.Path, TestLayout.Default);
        later.RegisterSetting(_Setting());
        later.Load();

        await Assert.That(later.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(256 * _MiB));
    }

    [Test]
    public async Task ALimitSwitchedOffIsStoredAsOffAndComesBackOff()
    {
        using SettingsTempFolder folder = new();
        using (SettingsManager manager = new(folder.Path, TestLayout.Default))
        {
            manager.RegisterSetting(_Setting());
            manager.Load();
            manager.GetSetting("demo.maxSize")!.SetPendingValue(SettingValue.Limit(LimitValue.Off)); // registered above
            manager.ApplyChanges();
            manager.Save();
        }

        await Assert.That(folder.ReadJson("demo.json")["demo.maxSize"]?.GetValue<string>()).IsEqualTo("off");

        using SettingsManager later = new(folder.Path, TestLayout.Default);
        later.RegisterSetting(_Setting());
        later.Load();

        await Assert.That(later.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Off);
    }

    #endregion

    #region Live holder

    private static Limit _Live(SettingsManager manager, string key = "demo.maxSize") =>
        manager.GetLiveLimit(key)
        ?? throw new InvalidOperationException("no live limit");

    [Test]
    public async Task TheLiveHolderFollowsApplyAndResetButNotAPendingValue()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterLimit(LimitTestDefinitions.Bytes());
        Limit live = _Live(manager);
        Setting setting = manager.GetSetting("demo.maxSize") ?? throw new InvalidOperationException("missing");
        long defaultNumber = 128 * _MiB;

        await Assert.That(live.Ceiling).IsEqualTo(defaultNumber);

        setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(256 * _MiB)));
        await Assert.That(live.Ceiling).IsEqualTo(defaultNumber);

        setting.Apply();
        await Assert.That(live.Ceiling).IsEqualTo(256 * _MiB);

        setting.ResetToDefault();
        await Assert.That(live.Ceiling).IsEqualTo(defaultNumber);
        await Assert.That(_Live(manager)).IsSameReferenceAs(live);
    }

    [Test]
    public async Task TheLiveHolderFollowsTheManagerBulkApplyAndReset()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterLimit(LimitTestDefinitions.Bytes());
        Limit live = _Live(manager);

        manager.GetSetting("demo.maxSize")!.SetPendingValue(SettingValue.Limit(LimitValue.Off)); // registered above
        manager.ApplyChanges();
        await Assert.That(live.IsOff).IsTrue();

        manager.ResetAllToDefaults();
        await Assert.That(live.Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task ALoadedValueIsShownByTheHolderOfThatManager()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": 5242880}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_Setting());

        manager.Load();

        await Assert.That(_Live(manager).Ceiling).IsEqualTo(5 * _MiB);
    }

    [Test]
    public async Task AStoredValueOutsideTheRangeLeavesTheHolderAtTheDefaultAndStoredOffLoadsAsOff()
    {
        using SettingsTempFolder outside = new();
        outside.Write("demo.json", """{"demo.maxSize": 5}""");
        using SettingsManager rejected = new(outside.Path, TestLayout.Default);
        rejected.RegisterSetting(_Setting());
        IReadOnlyList<SettingsLoadWarning> warnings = rejected.Load();

        using SettingsTempFolder off = new();
        off.Write("demo.json", """{"demo.maxSize": "off"}""");
        using SettingsManager accepted = new(off.Path, TestLayout.Default);
        accepted.RegisterSetting(_Setting());
        accepted.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(_Live(rejected).Ceiling).IsEqualTo(128 * _MiB);
        await Assert.That(_Live(accepted).IsOff).IsTrue();
        await Assert.That(_Live(accepted).Ceiling).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task TwoManagersThatRegisterTheSameDefinitionHaveIndependentHolders()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();
        using SettingsManager first = new();
        using SettingsManager second = new();
        new SettingsRegistrar(first).RegisterLimit(definition);
        new SettingsRegistrar(second).RegisterLimit(definition);

        first.GetSetting("demo.maxSize")!.SetPendingValue(SettingValue.Limit(LimitValue.Of(256 * _MiB))); // registered above
        first.ApplyChanges();

        await Assert.That(_Live(first).Ceiling).IsEqualTo(256 * _MiB);
        await Assert.That(_Live(second).Ceiling).IsEqualTo(128 * _MiB);
        await Assert.That(_Live(first)).IsNotSameReferenceAs(_Live(second));
    }

    [Test]
    public async Task TheHolderIsTheOneOfTheSettingAndUnknownOrNonLimitKeysHaveNone()
    {
        using SettingsManager manager = new();
        SettingsRegistrar registrar = new(manager);
        registrar.RegisterLimit(LimitTestDefinitions.Bytes());
        registrar.RegisterI64Setting("demo.plain", "Plain", "demo", 5);
        Setting limit = manager.GetSetting("demo.maxSize") ?? throw new InvalidOperationException("missing");

        await Assert.That(limit.LiveLimit).IsSameReferenceAs(manager.GetLiveLimit("demo.maxSize"));
        await Assert.That(limit.LiveLimit!.Definition).IsSameReferenceAs(limit.LimitDefinition);
        await Assert.That(manager.GetLiveLimit("demo.plain")).IsNull();
        await Assert.That(manager.GetLiveLimit("demo.unknown")).IsNull();
        await Assert.That(manager.AsReadOnlyView().GetLiveLimit("demo.maxSize")).IsSameReferenceAs(limit.LiveLimit);
        await Assert.That(manager.AsReadOnlyView().GetLiveLimit("demo.plain")).IsNull();
        await Assert.That(Setting.I64("demo.plain", "Plain", "demo", 5).LiveLimit).IsNull();
    }

    [Test]
    public async Task AnOptionLimitHasItsOwnHolderAtTheDefault()
    {
        using SettingsManager options = new();
        new SettingsRegistrar(options).RegisterLimitOption(LimitTestDefinitions.Bytes());

        await Assert.That(_Live(options, "maxSize").Ceiling).IsEqualTo(128 * _MiB);
    }

    #endregion

    #region Read-only

    private static Setting _ReadOnlySetting() => _Setting(LimitTestDefinitions.Bytes(readOnly: true));

    [Test]
    public async Task AReadOnlyLimitRefusesAnotherValueAsPendingValueAndTheDefaultIsAccepted()
    {
        Setting setting = _ReadOnlySetting();

        bool other = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Of(256 * _MiB)), out SettingProblem? problem);
        bool sameAsDefault = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Of(128 * _MiB)), out SettingProblem? defaultProblem);
        ValidationSettingsException thrown = Assert.Throws<ValidationSettingsException>(
            () => setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(256 * _MiB))));

        await Assert.That(other).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.ReadOnly);
        await Assert.That(problem.Message).IsEqualTo("the limit is read-only");
        await Assert.That(problem.ToString()).IsEqualTo("demo.maxSize: the limit is read-only");
        await Assert.That(sameAsDefault).IsTrue();
        await Assert.That(defaultProblem).IsNull();
        await Assert.That(thrown.Message).Contains("the limit is read-only");
        await Assert.That(setting.LiveLimit!.Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task AReadOnlyLimitRefusesOffEvenWhenTheDefinitionAllowsIt()
    {
        Setting setting = _ReadOnlySetting();

        bool stored = setting.TrySetPendingValue(SettingValue.Limit(LimitValue.Off), out SettingProblem? problem);

        await Assert.That(stored).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.ReadOnly);
    }

    [Test]
    public async Task AFileThatStoresAnotherValueOfAReadOnlyLimitLoadsWithOneWarningAndTheDefaultStays()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": 5242880}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(_ReadOnlySetting());

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(manager.GetLimit("demo.maxSize")).IsEqualTo((LimitValue?)LimitValue.Of(128 * _MiB));
        await Assert.That(_Live(manager).Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task TheTextFormOfAReadOnlyLimitRefusesAnotherValueAndAcceptsTheDefault()
    {
        Setting setting = _ReadOnlySetting();

        bool other = SettingValueText.TryParse(setting, "256 MiB", out _, out SettingProblem? problem);
        bool sameAsDefault = SettingValueText.TryParse(setting, "128 MiB", out SettingValue parsed, out _);

        await Assert.That(other).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.ReadOnly);
        await Assert.That(problem.ToString()).IsEqualTo("demo.maxSize: the limit is read-only");
        await Assert.That(sameAsDefault).IsTrue();
        await Assert.That(parsed).IsEqualTo(SettingValue.Limit(LimitValue.Of(128 * _MiB)));
    }

    #endregion

    #region Names

    [Test]
    public async Task TheLongestTitleAndTheKeyGrammarOfTheLimitsLibraryRegisterAsASetting()
    {
        LimitTexts texts = LimitTestDefinitions.Texts() with { Title = new string('t', 60) };
        LimitDefinition dotted = LimitTestDefinitions.Bytes(key: "ip.reassembly.maxFragments", texts: texts);
        LimitDefinition snake = LimitTestDefinitions.Bytes(key: "http.max_decompressed_size", texts: texts);
        LimitDefinition camel = LimitTestDefinitions.Bytes(key: "someip2.maxPayload", texts: texts);
        using SettingsManager manager = new();
        SettingsRegistrar registrar = new(manager);

        registrar.RegisterLimit(dotted);
        registrar.RegisterLimit(snake);
        registrar.RegisterLimit(camel);

        await Assert.That(manager.GetSetting("ip.reassembly.maxFragments")!.UiName.Length).IsEqualTo(60);
        await Assert.That(manager.GetSetting("http.max_decompressed_size")).IsNotNull();
        await Assert.That(manager.GetSetting("someip2.maxPayload")).IsNotNull();
    }

    #endregion

    #region Setting value

    [Test]
    public async Task ALimitValueRoundTripsThroughTheSettingValueAndDiffersFromTheSameNumberOfAnotherType()
    {
        SettingValue five = SettingValue.Limit(LimitValue.Of(5));

        await Assert.That(five.Type).IsEqualTo(SettingType.Limit);
        await Assert.That(five.TryGetAsLimit(out LimitValue number)).IsTrue();
        await Assert.That(number).IsEqualTo(LimitValue.Of(5));
        await Assert.That(SettingValue.Limit(LimitValue.Off).TryGetAsLimit(out LimitValue off)).IsTrue();
        await Assert.That(off.IsOff).IsTrue();
        await Assert.That(five).IsNotEqualTo(SettingValue.I64(5));
        await Assert.That(five.GetHashCode()).IsEqualTo(SettingValue.Limit(LimitValue.Of(5)).GetHashCode());
        await Assert.That(SettingValue.I64(5).TryGetAsLimit(out _)).IsFalse();
        await Assert.That(SettingValue.Limit(LimitValue.Off)).IsNotEqualTo(SettingValue.Limit(LimitValue.Of(0)));
    }

    [Test]
    [Arguments(false, 0L, "0")]
    [Arguments(false, 268_435_456L, "268435456")]
    [Arguments(true, 0L, "off")]
    public async Task TheDisplayFormOfALimitValueIsTheBaseNumberOrOffAndItsSizeIsExact(bool off, long number, string expected)
    {
        SettingValue value = SettingValue.Limit(off
            ? LimitValue.Off
            : LimitValue.Of(number));

        await Assert.That(value.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo(expected);
        await Assert.That(value.TryGetStringSize(default, CultureInfo.InvariantCulture, out int size)).IsTrue();
        await Assert.That(size).IsEqualTo(expected.Length);
        await Assert.That(_FitsInOneCharLess(value, expected.Length)).IsFalse();
        await Assert.That(_Utf8Text(value)).IsEqualTo(expected);
    }

    /// <summary>Formats into a buffer that is one character too small; a span cannot live in an async test method.</summary>
    /// <param name="value">The value to format.</param>
    /// <param name="length">The exact formatted length.</param>
    /// <returns>Whether the too-small buffer was accepted (it must not be) with zero characters written.</returns>
    private static bool _FitsInOneCharLess(SettingValue value, int length)
    {
        Span<char> small = stackalloc char[length - 1];

        return value.TryFormat(small, out int written, default, CultureInfo.InvariantCulture) || written != 0;
    }

    /// <summary>Formats a value through the UTF-8 member.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The formatted text.</returns>
    private static string _Utf8Text(SettingValue value)
    {
        Span<byte> utf8 = stackalloc byte[32];
        value.TryFormat(utf8, out int bytes, default, CultureInfo.InvariantCulture);
        return Encoding.UTF8.GetString(utf8[..bytes]);
    }

    #endregion

    #region Read side of the live limit

    [Test]
    public async Task TheReadOnlyViewAndTheInterfaceHandOutTheSameReadSideAsTheManager()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterLimit(LimitTestDefinitions.Bytes());
        Limit fromManager = _Live(manager);

        ReadOnlySettingsManagerView view = manager.ReadOnly;
        Limit? fromView = view.GetLiveLimit("demo.maxSize");
        Limit? fromInterface = _LiveThroughTheInterface(view, "demo.maxSize");
        Limit? fromSettingView = view.GetSetting("demo.maxSize")!.Value.LiveLimit; // registered above

        await Assert.That(fromView).IsSameReferenceAs(fromManager);
        await Assert.That(fromInterface).IsSameReferenceAs(fromManager);
        await Assert.That(fromSettingView).IsSameReferenceAs(fromManager);
        await Assert.That(view.GetLiveLimit("unknown.key")).IsNull();
    }

    /// <summary>Reads a live limit through the interface, as a generic consumer that only has read access does.</summary>
    /// <typeparam name="TSettings">The read-only manager type.</typeparam>
    /// <param name="settings">The manager or its view.</param>
    /// <param name="key">The key of the limit.</param>
    /// <returns>The read side, or <see langword="null"/>.</returns>
    private static Limit? _LiveThroughTheInterface<TSettings>(TSettings settings, string key)
        where TSettings : IReadOnlySettingsManager =>
        settings.GetLiveLimit(key);

    [Test]
    public async Task NoMemberReachableThroughTheReadOnlySurfaceCanChangeALimit()
    {
        MethodInfo[] limitMethods = typeof(Limit).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        await Assert.That(limitMethods.Any(static method => !method.IsSpecialName)).IsFalse();
        await Assert.That(typeof(IReadOnlySetting).GetProperty(nameof(IReadOnlySetting.LiveLimit))!.PropertyType).IsEqualTo(typeof(Limit));
        await Assert.That(typeof(IReadOnlySettingsManager).GetMethod(nameof(IReadOnlySettingsManager.GetLiveLimit))!.ReturnType)
            .IsEqualTo(typeof(Limit));
    }

    #endregion

    #region Live limit against concurrent changes

    [Test]
    public async Task ReadersSeeOnlyValidValuesWhileApplyChangesTheLiveLimit()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterLimit(LimitTestDefinitions.Bytes());
        Setting setting = manager.GetSetting("demo.maxSize")!; // registered above
        Limit live = _Live(manager);
        long[] allowed = [128 * _MiB, 4 * _MiB, 64 * _MiB];

        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < 20_000; index++)
            {
                long next = index % 2 == 0
                    ? 4 * _MiB
                    : 64 * _MiB;
                setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(next)));
                setting.Apply();
            }
        });
        Task<long> reader = Task.Run(() =>
        {
            long invalid = 0;
            while (!writer.IsCompleted)
            {
                if (Array.IndexOf(allowed, live.Ceiling) < 0)
                {
                    invalid++;
                }
            }

            return invalid;
        });

        await writer;

        await Assert.That(await reader).IsEqualTo(0L);
        await Assert.That(live.Ceiling).IsEqualTo(64 * _MiB);
    }

    #endregion
}
