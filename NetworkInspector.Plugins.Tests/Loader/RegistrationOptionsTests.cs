// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Option texts of source and exporter requests go through the Core text codec.</summary>
internal sealed class RegistrationOptionsTests
{
    #region Helpers

    private static IReadOnlyList<KeyValuePair<string, string>> _Texts(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))];

    private static PluginError _Refusal(SettingsManager options, params (string Key, string Value)[] pairs)
    {
        if (RegistrationOptions.TryApplyTexts(options, _Texts(pairs), out PluginError? error))
        {
            throw new InvalidOperationException("The texts were accepted.");
        }

        return Expect.NotNull(error);
    }

    #endregion

    #region Accepted texts

    [Test]
    public async Task TextsAreParsedAndAppliedWithTheCoreCodec()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        bool applied = RegistrationOptions.TryApplyTexts(options, _Texts(("count", "10"), ("transport", "TCP")), out PluginError? error);

        await Assert.That(applied).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(options.GetI64Setting("count")).IsEqualTo(10L);
        await Assert.That(options.GetEnumSetting("transport")?.Name).IsEqualTo("tcp");
        await Assert.That(options.HasDirtySettings).IsFalse();
    }

    [Test]
    public async Task NoTextsChangeNothing()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        bool applied = RegistrationOptions.TryApplyTexts(options, [], out PluginError? error);

        await Assert.That(applied).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(options.GetI64Setting("count")).IsEqualTo(100L);
    }

    #endregion

    #region Refusals

    [Test]
    public async Task AMistypedKeyNamesTheClosestOption()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        PluginError error = _Refusal(options, ("cout", "1"));

        await Assert.That(error.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(error.Option).IsEqualTo("cout");
        await Assert.That(error.Message).IsEqualTo("Unknown option 'cout'; did you mean: count?");
    }

    [Test]
    public async Task AnUnrelatedKeyListsTheOptions()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        PluginError error = _Refusal(options, ("colour", "1"));

        await Assert.That(error.Option).IsEqualTo("colour");
        await Assert.That(error.Message).IsEqualTo("Unknown option 'colour'; options: count, transport, port, startCounter.");
    }

    [Test]
    public async Task AValueProblemUsesTheSettingProblemMessage()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        PluginError range = _Refusal(options, ("port", "0"));
        PluginError shape = _Refusal(options, ("count", "abc"));

        await Assert.That(range.Message).IsEqualTo("port: allowed 1 – 65535");
        await Assert.That(range.Option).IsEqualTo("port");
        await Assert.That(shape.Message).IsEqualTo("count: 'abc' is not a whole number; example: 100");
        await Assert.That(shape.Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task AKeyGivenTwiceIsRefused()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        PluginError error = _Refusal(options, ("count", "1"), ("count", "2"));

        await Assert.That(error.Message).IsEqualTo("Option 'count' is given twice.");
        await Assert.That(error.Option).IsEqualTo("count");
    }

    [Test]
    public async Task ARefusalLeavesTheManagerUnchanged()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        PluginError error = _Refusal(options, ("count", "10"), ("transport", "tcp"), ("port", "0"));

        await Assert.That(error.Option).IsEqualTo("port");
        await Assert.That(options.GetI64Setting("count")).IsEqualTo(100L);
        await Assert.That(options.GetEnumSetting("transport")?.Name).IsEqualTo("udp");
        await Assert.That(options.HasDirtySettings).IsFalse();
    }

    [Test]
    public async Task ARegistrationWithoutOptionsSaysSo()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(static _ => { });

        PluginError error = _Refusal(options, ("anything", "1"));

        await Assert.That(error.Message).IsEqualTo("Unknown option 'anything'; this registration has no options.");
    }

    [Test]
    public async Task NullArgumentsAreRefused()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions);

        await Assert.That(() => RegistrationOptions.TryApplyTexts(null!, [], out _)).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => RegistrationOptions.TryApplyTexts(options, null!, out _)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Stored limits as option defaults

    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    /// <summary>A byte limit of 1 KiB to 8 GiB with the default 128 MiB, as a source registration would declare it.</summary>
    private static readonly LimitDefinition _ContainerLimit = new(
        key: "demo.maxContainerSize",
        unit: LimitUnits.Bytes,
        defaultValue: 128 * _MiB,
        minimum: _KiB,
        maximum: 8L << 30,
        texts: new LimitTexts(
            Title: "Largest demo container",
            Description: "The largest size of one demo container.",
            Reason: "A hostile file could otherwise use all memory.",
            Benefit: "A broken file is refused with a clear message.",
            SmallerValues: "Containers above the value are refused: 1 MiB refuses a 2 MiB container.",
            LargerValues: "More memory may be used per container: 1 GiB lets a 900 MiB container through.",
            OffEffect: "Every container is accepted and a hostile file can use all memory."));

    /// <summary>Registers the limit as an option next to a plain option, as a registration with one limit would.</summary>
    private static void _RegisterLimitAndCount(SettingsRegistrar registrar)
    {
        registrar.RegisterLimitOption(_ContainerLimit);
        registrar.RegisterI64Setting("count", "Count", string.Empty, 100);
    }

    [Test]
    public async Task AStoredLimitIsTheDefaultOfTheOptionAndAnInlineTextOverridesIt()
    {
        List<string> asked = [];
        LimitValue? Stored(string key)
        {
            asked.Add(key);
            return LimitValue.Of(256 * _MiB);
        }

        using SettingsManager options = RegistrationOptions.CreateDefaults(_RegisterLimitAndCount, Stored,
            out IReadOnlyList<PluginError> ignored);

        await Assert.That(ignored).IsEmpty();
        await Assert.That(options.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)LimitValue.Of(256 * _MiB));
        await Assert.That(options.HasDirtySettings).IsFalse();

        // Only limit options ask for a stored value, under the full key of the limit and not the local name of the option.
        await Assert.That(asked).IsEquivalentTo(["demo.maxContainerSize"]);

        bool applied = RegistrationOptions.TryApplyTexts(options, _Texts(("maxContainerSize", "64MiB")), out PluginError? error);

        await Assert.That(applied).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(options.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)LimitValue.Of(64 * _MiB));
        await Assert.That(options.GetI64Setting("count")).IsEqualTo(100L);
    }

    [Test]
    public async Task AStoredOffIsTheDefaultOfTheOption()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(_RegisterLimitAndCount, static _ => LimitValue.Off,
            out IReadOnlyList<PluginError> ignored);

        await Assert.That(ignored).IsEmpty();
        await Assert.That(options.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)LimitValue.Off);
    }

    [Test]
    public async Task WithoutAStoredValueTheOptionKeepsTheDefinitionDefault()
    {
        using SettingsManager withLookup = RegistrationOptions.CreateDefaults(_RegisterLimitAndCount, static _ => null,
            out IReadOnlyList<PluginError> ignored);
        using SettingsManager withoutLookup = RegistrationOptions.CreateDefaults(_RegisterLimitAndCount);

        await Assert.That(ignored).IsEmpty();
        await Assert.That(withLookup.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)_ContainerLimit.DefaultValue);
        await Assert.That(withoutLookup.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)_ContainerLimit.DefaultValue);
    }

    [Test]
    [Arguments(5L)]
    [Arguments(9L << 30)]
    public async Task AStoredValueOutsideTheRangeIsIgnoredAndReportedAndTheDefaultApplies(long stored)
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(_RegisterLimitAndCount, _ => LimitValue.Of(stored),
            out IReadOnlyList<PluginError> ignored);

        await Assert.That(options.GetLimit("maxContainerSize")).IsEqualTo((LimitValue?)_ContainerLimit.DefaultValue);
        await Assert.That(ignored.Count).IsEqualTo(1);
        await Assert.That(ignored[0].Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(ignored[0].Option).IsEqualTo("maxContainerSize");
        await Assert.That(ignored[0].Message).Contains("demo.maxContainerSize");
        await Assert.That(ignored[0].Message).Contains("allowed 1 KiB – 8 GiB, or off");
    }

    [Test]
    public async Task ARegistrationWithoutLimitOptionsNeverAsksForAStoredValue()
    {
        int asked = 0;
        LimitValue? Stored(string key)
        {
            asked++;
            return null;
        }

        using SettingsManager options = RegistrationOptions.CreateDefaults(SampleSource.RegisterOptions, Stored, out _);

        await Assert.That(asked).IsEqualTo(0);
        await Assert.That(options.GetI64Setting("count")).IsEqualTo(100L);
    }

    [Test]
    public async Task TheStoredLimitCreateDefaultsRefusesNullArgumentsAndPassesOnWhatTheRegistrationThrows()
    {
        // null! is the input under test in both calls below.
        await Assert.That(() => RegistrationOptions.CreateDefaults(null!, static _ => null, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => RegistrationOptions.CreateDefaults(static _ => { }, null!, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => RegistrationOptions.CreateDefaults(
            static _ => throw new InvalidOperationException("plugin bug"), static _ => null, out _)).Throws<InvalidOperationException>();
    }

    #endregion

    #region Text length limit of the manager

    [Test]
    public async Task AnOptionTextLongerThanTheTextLimitOfTheManagerIsRefusedWithTheLimit()
    {
        using SettingsManager options = new(null, CoreSettingsLayout.Instance, new SettingsLimitValues { MaxTextLength = LimitValue.Of(4) });
        options.RegisterSetting(Setting.StringArray("tags", "Tags", string.Empty, []));

        PluginError refusal = _Refusal(options, ("tags", "a,b,c"));

        await Assert.That(refusal.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(refusal.Message).Contains("settings.maxTextLength");
        await Assert.That(refusal.Option).IsEqualTo("tags");
    }

    [Test]
    public async Task AnOptionTextAtTheTextLimitOfTheManagerIsAccepted()
    {
        using SettingsManager options = new(null, CoreSettingsLayout.Instance, new SettingsLimitValues { MaxTextLength = LimitValue.Of(5) });
        options.RegisterSetting(Setting.StringArray("tags", "Tags", string.Empty, []));

        bool applied = RegistrationOptions.TryApplyTexts(options, _Texts(("tags", "a,b,c")), out PluginError? error);

        await Assert.That(applied).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(options.GetStringArraySetting("tags")!.Length).IsEqualTo(3);
    }

    #endregion
}
