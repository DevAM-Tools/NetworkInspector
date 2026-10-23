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
}
