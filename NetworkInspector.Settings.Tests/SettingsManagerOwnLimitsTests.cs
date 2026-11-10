// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// The limits of the settings library as settings of its own manager (<see cref="SettingsManager.RegisterOwnLimits"/>), the
/// live holders the loader enforces through, and the loader limits at the limit, above it, off, and at the default.
/// </summary>
internal sealed class SettingsManagerOwnLimitsTests
{
    private const long _MiB = 1L << 20;

    #region Registration

    [Test]
    public async Task RegisterOwnLimitsMakesTheFiveLimitsSettingsOfTheGroupSettings()
    {
        using SettingsManager manager = new();

        manager.RegisterOwnLimits();

        await Assert.That(manager.SettingCount).IsEqualTo(5);
        await Assert.That(manager.GetSettingsInGroup(SettingsLimits.Group).Count).IsEqualTo(5);
        foreach (LimitDefinition definition in SettingsLimits.All)
        {
            await Assert.That(definition.Group).IsEqualTo(SettingsLimits.Group);
            await Assert.That(manager.GetLimit(definition.Key)).IsEqualTo((LimitValue?)definition.DefaultValue);
            await Assert.That(manager.GetLiveLimit(definition.Key)).IsNotNull();
        }
    }

    [Test]
    public async Task AManagerThatDoesNotRegisterItsOwnLimitsListsNoLimitSetting()
    {
        using SettingsManager manager = new();

        await Assert.That(manager.SettingCount).IsEqualTo(0);
        await Assert.That(manager.AllGroups).IsEmpty();
        await Assert.That(manager.GetLimit("settings.maxFileSize")).IsNull();
        await Assert.That(manager.GetLiveLimit("settings.maxFileSize")).IsNull();
    }

    [Test]
    public async Task RegisteringTheOwnLimitsTwiceThrowsAndKeepsTheValueInEffect()
    {
        using SettingsManager manager = new();
        manager.RegisterOwnLimits();
        Setting setting = manager.GetSetting("settings.maxFileSize")!; // registered above
        setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(2 * _MiB)));
        manager.ApplyChanges();

        await Assert.That(() => manager.RegisterOwnLimits()).Throws<DuplicateNameSettingsException>();

        await Assert.That(manager.Limits.MaxFileSize).IsEqualTo(LimitValue.Of(2 * _MiB));
        await Assert.That(manager.GetLiveLimit("settings.maxFileSize")!.Ceiling).IsEqualTo(2 * _MiB);
    }

    [Test]
    public async Task AnApplicationThatRegisteredOneOfTheKeysItselfGetsTheDuplicateAndNothingIsAdded()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterLimit(SettingsLimits.MaxJsonDepth);

        await Assert.That(() => manager.RegisterOwnLimits()).Throws<DuplicateNameSettingsException>();

        await Assert.That(manager.SettingCount).IsEqualTo(1);
    }

    [Test]
    public async Task TheValueGivenAtConstructionIsTheStartValueAndTheDefaultOfTheSetting()
    {
        SettingsLimitValues limits = SettingsLimitValues.Default with { MaxFileSize = LimitValue.Of(8 * _MiB) };
        using SettingsManager manager = new(null, TestLayout.Default, limits);

        manager.RegisterOwnLimits();
        Setting setting = manager.GetSetting("settings.maxFileSize")!; // registered above

        await Assert.That(manager.GetLimit("settings.maxFileSize")).IsEqualTo((LimitValue?)LimitValue.Of(8 * _MiB));
        await Assert.That(setting.DefaultValue).IsEqualTo(SettingValue.Limit(LimitValue.Of(8 * _MiB)));
        await Assert.That(manager.GetLiveLimit("settings.maxFileSize")!.Ceiling).IsEqualTo(8 * _MiB);
    }

    [Test]
    public async Task ADefaultGivenToTheRegistrationIsTheDefaultOfTheSettingWhileTheConstructorValueEnforcedTheLoad()
    {
        SettingsLimitValues enforced = SettingsLimitValues.Default with { MaxFileSize = LimitValue.Of(8 * _MiB) };
        using SettingsManager manager = new(null, TestLayout.Default, enforced);
        long duringLoad = manager.GetLiveLimit("settings.maxFileSize") is null
            ? manager.Limits.MaxFileSize.Value
            : -1;

        IReadOnlyList<SettingsLoadWarning> warnings = manager.RegisterOwnLimits(SettingsLimitValues.Default);

        await Assert.That(duringLoad).IsEqualTo(8 * _MiB);
        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.GetSetting("settings.maxFileSize")!.DefaultValue) // registered above
            .IsEqualTo(SettingValue.Limit(SettingsLimits.MaxFileSize.DefaultValue));
        await Assert.That(manager.Limits.MaxFileSize).IsEqualTo(SettingsLimits.MaxFileSize.DefaultValue);
    }

    [Test]
    public async Task RegisteringAfterLoadReturnsTheRefusalTextOfAStoredValueThatCannotBeUsed()
    {
        using SettingsTempFolder folder = new();
        folder.Write("settings.json", """{"settings.maxFileSize": "256 MB", "settings.maxJsonDepth": 16}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.Load();

        IReadOnlyList<SettingsLoadWarning> warnings = manager.RegisterOwnLimits();

        await Assert.That(warnings.Single().Message).Contains("use 256 MiB");
        await Assert.That(manager.Limits.MaxJsonDepth).IsEqualTo(LimitValue.Of(16));
        await Assert.That(manager.Limits.MaxFileSize).IsEqualTo(SettingsLimits.MaxFileSize.DefaultValue);
    }

    [Test]
    public async Task ADefaultOutsideTheRangeIsRefusedAndNothingIsRegistered()
    {
        using SettingsManager manager = new();

        await Assert.That(() => manager.RegisterOwnLimits(new SettingsLimitValues { MaxJsonDepth = LimitValue.Of(1) }))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(manager.SettingCount).IsEqualTo(0);
    }

    #endregion

    #region Effect on the loader

    [Test]
    public async Task AStoredOwnLimitIsReadFirstAndBoundsTheOtherFiles()
    {
        using SettingsTempFolder folder = new();
        folder.Write("settings.json", """{"settings.maxFileSize": 2048}""");
        folder.Write("app.json", "{\"app.x\": 1" + new string(' ', 4096) + "}");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterOwnLimits();
        manager.RegisterSetting(Setting.I64("app.x", "X", "app", 0));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(manager.GetLimit("settings.maxFileSize")).IsEqualTo((LimitValue?)LimitValue.Of(2048));
        await Assert.That(warnings.Single().Kind).IsEqualTo(SettingsLoadWarningKind.FileTooLarge);
        await Assert.That(warnings.Single().Message).Contains("settings.maxFileSize");
    }

    [Test]
    public async Task AStoredOwnLimitIsIgnoredByAManagerThatDidNotRegisterItsOwnLimits()
    {
        using SettingsTempFolder folder = new();
        folder.Write("settings.json", """{"settings.maxFileSize": 2048}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.Limits.MaxFileSize).IsEqualTo(SettingsLimits.MaxFileSize.DefaultValue);
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(1);
    }

    [Test]
    public async Task AnAppliedOwnLimitChangesTheLiveHolderAndTheLimitsSnapshot()
    {
        using SettingsManager manager = new();
        manager.RegisterOwnLimits();
        Limit live = manager.GetLiveLimit("settings.maxTextLength")!; // registered above
        Setting setting = manager.GetSetting("settings.maxTextLength")!; // registered above

        setting.SetPendingValue(SettingValue.Limit(LimitValue.Of(100)));
        long beforeApply = live.Ceiling;
        manager.ApplyChanges();

        await Assert.That(beforeApply).IsEqualTo(SettingsLimits.MaxTextLength.DefaultValue.Value);
        await Assert.That(live.Ceiling).IsEqualTo(100L);
        await Assert.That(manager.Limits.MaxTextLength).IsEqualTo(LimitValue.Of(100));
        await Assert.That(manager.GetLiveLimit("settings.maxTextLength")).IsSameReferenceAs(live);
    }

    [Test]
    public async Task ATextLongerThanTheLiveTextLimitIsRefusedByTheParseOfTheManagerValue()
    {
        using SettingsManager manager = new();
        manager.RegisterOwnLimits();
        manager.GetSetting("settings.maxTextLength")!.SetPendingValue(SettingValue.Limit(LimitValue.Of(4))); // registered above
        manager.ApplyChanges();
        Setting target = Setting.StringArray("t.tags", "Tags", "t", []);

        bool parsed = SettingValueText.TryParse(target, "a,b,c", manager.Limits.MaxTextLength, out _, out SettingProblem? problem);

        await Assert.That(parsed).IsFalse();
        await Assert.That(problem!.Kind).IsEqualTo(SettingProblemKind.TextTooLong);
        await Assert.That(problem.Message).Contains("settings.maxTextLength");
    }

    #endregion

    #region Persistence of the own limits

    [Test]
    public async Task SaveStoresAnOwnLimitOnlyWhenItDiffersFromTheStartValueAndLoadReadsItBack()
    {
        using SettingsTempFolder folder = new();
        using (SettingsManager first = new(folder.Path, TestLayout.Default))
        {
            first.RegisterOwnLimits();
            first.Save();
            await Assert.That(File.Exists(folder.Combine("settings.json"))).IsFalse();

            first.GetSetting("settings.maxJsonDepth")!.SetPendingValue(SettingValue.Limit(LimitValue.Of(16))); // registered above
            first.ApplyChanges();
            first.Save();
        }

        JsonObject stored = folder.ReadJson("settings.json");
        using SettingsManager second = new(folder.Path, TestLayout.Default);
        second.RegisterOwnLimits();
        second.Load();

        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(stored.ContainsKey("settings.maxJsonDepth")).IsTrue();
        await Assert.That(second.GetLimit("settings.maxJsonDepth")).IsEqualTo((LimitValue?)LimitValue.Of(16));
        await Assert.That(second.GetLiveLimit("settings.maxJsonDepth")!.Ceiling).IsEqualTo(16L);
    }

    [Test]
    public async Task UnsetRemovesAStoredOwnLimitAndTheValueReturnsToTheStartValue()
    {
        using SettingsTempFolder folder = new();
        folder.Write("settings.json", """{"settings.maxJsonDepth": 16}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterOwnLimits();
        manager.Load();

        bool removed = manager.Unset("settings.maxJsonDepth");

        await Assert.That(removed).IsTrue();
        await Assert.That(manager.Limits.MaxJsonDepth).IsEqualTo(SettingsLimits.MaxJsonDepth.DefaultValue);
        await Assert.That(File.Exists(folder.Combine("settings.json"))).IsFalse();
    }

    [Test]
    public async Task ARefusedStoredOwnLimitKeepsTheStartValueAndSaysWhatToWrite()
    {
        using SettingsTempFolder folder = new();
        folder.Write("settings.json", """{"settings.maxFileSize": "256 MB"}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterOwnLimits();

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Single().Message).Contains("use 256 MiB");
        await Assert.That(manager.Limits.MaxFileSize).IsEqualTo(SettingsLimits.MaxFileSize.DefaultValue);
    }

    #endregion

    #region Late registration keeps the refusal text

    [Test]
    public async Task ALimitRegisteredAfterLoadReturnsTheRefusalTextOfTheStoredValue()
    {
        using SettingsTempFolder folder = new();
        folder.Write("demo.json", """{"demo.maxSize": "256 MB"}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.Load();

        SettingRegistrationResult result = manager.RegisterSetting(Setting.FromLimit(LimitTestDefinitions.Bytes()));

        await Assert.That(result.LoadResult).IsNotEqualTo(SettingLoadResult.Success);
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!.Value.Message).Contains("use 256 MiB");
        await Assert.That(result.Warning!.Value.SettingName).IsEqualTo("demo.maxSize");
    }

    [Test]
    public async Task ASettingRegisteredAfterLoadWithAnOutOfRangeValueReturnsTheValidationText()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.x": 500}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.Load();

        SettingRegistrationResult result = manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1, min: 0, max: 10));

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.OutOfRange);
        await Assert.That(result.Warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
    }

    [Test]
    public async Task ASuccessfulRegistrationAfterLoadHasNoWarning()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.x": 5}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.Load();

        SettingRegistrationResult result = manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1));

        await Assert.That(result.WasLoaded).IsTrue();
        await Assert.That(result.Warning).IsNull();
    }

    #endregion

    #region File limits at the limit, above, off, and the default

    [Test]
    public async Task LoadFileOfExactlyTheSizeLimitIsRead()
    {
        using SettingsTempFolder folder = new();
        string json = "{\"test.flag\":true}";
        folder.Write("default.json", json + new string(' ', 1024 - json.Length));
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });
        manager.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.GetBoolSetting("test.flag") == true).IsTrue();
    }

    [Test]
    public async Task LoadFileOneByteAboveTheSizeLimitIsSkippedAndNamesTheLimit()
    {
        using SettingsTempFolder folder = new();
        string json = "{\"test.flag\":true}";
        folder.Write("default.json", json + new string(' ', 1025 - json.Length));
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });
        manager.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Single().Kind).IsEqualTo(SettingsLoadWarningKind.FileTooLarge);
        await Assert.That(manager.GetBoolSetting("test.flag") == false).IsTrue();
    }

    [Test]
    public async Task LoadJsonOfExactlyTheDepthLimitIsRead()
    {
        using SettingsTempFolder folder = new();
        folder.Write("default.json", _NestedJsonObject(4));
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxJsonDepth = LimitValue.Of(4) });

        await Assert.That(manager.Load()).IsEmpty();
    }

    [Test]
    public async Task LoadJsonDeeperThanTheDefaultDepthIsSkippedAndNamesTheKey()
    {
        using SettingsTempFolder folder = new();
        folder.Write("default.json", _NestedJsonObject(65));
        using SettingsManager manager = new(folder.Path, TestLayout.Default);

        SettingsLoadWarning warning = manager.Load().Single();

        await Assert.That(warning.Kind).IsEqualTo(SettingsLoadWarningKind.JsonTooDeep);
        await Assert.That(warning.Message).Contains("settings.maxJsonDepth");
        await Assert.That(warning.Message).Contains("Limit: 64 (setting settings.maxJsonDepth)");
    }

    [Test]
    public async Task LoadJsonOfAnyDepthIsReadWhenTheDepthLimitIsOff()
    {
        using SettingsTempFolder folder = new();
        folder.Write("default.json", _NestedJsonObject(300));
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxJsonDepth = LimitValue.Off });

        await Assert.That(manager.Load()).IsEmpty();
    }

    [Test]
    public async Task ASyntaxErrorAtAShallowDepthStaysASyntaxWarning()
    {
        using SettingsTempFolder folder = new();
        folder.Write("default.json", "{ \"a\": [1, 2, }");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);

        await Assert.That(manager.Load().Single().Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupFileSyntax);
    }

    [Test]
    public async Task ABracketInsideAStringDoesNotCountAsNestingWhenTheFileIsBroken()
    {
        using SettingsTempFolder folder = new();
        folder.Write("default.json", "{ \"a\": \"[[[[[[[[[[\\\"[[[[[[\", }");
        using SettingsManager manager = new(
            folder.Path, TestLayout.Default, new SettingsLimitValues { MaxJsonDepth = LimitValue.Of(4) });

        await Assert.That(manager.Load().Single().Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupFileSyntax);
    }

    [Test]
    public async Task TheDefaultsOfTheFiveLoaderLimitsAreTheDocumentedNumbers()
    {
        SettingsLimitValues defaults = SettingsLimitValues.Default;

        await Assert.That(defaults.MaxFileSize).IsEqualTo(LimitValue.Of(64 * _MiB));
        await Assert.That(defaults.MaxJsonDepth).IsEqualTo(LimitValue.Of(64));
        await Assert.That(defaults.FolderLockTimeout).IsEqualTo(LimitValue.Of(2_000));
        await Assert.That(defaults.MaxGroupFiles).IsEqualTo(LimitValue.Of(1_024));
        await Assert.That(defaults.MaxTextLength).IsEqualTo(LimitValue.Of(1_048_576));
        await Assert.That(SettingsLimits.All.Count).IsEqualTo(5);
    }

    #endregion

    #region Number of group files

    [Test]
    public async Task LoadReadsExactlyTheAllowedNumberOfGroupFiles()
    {
        using SettingsTempFolder folder = new();
        _WriteGroupFiles(folder, 3);
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxGroupFiles = LimitValue.Of(3) });

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(3);
    }

    [Test]
    public async Task LoadStopsOneFileAboveTheLimitWithOneWarningThatNamesTheKey()
    {
        using SettingsTempFolder folder = new();
        _WriteGroupFiles(folder, 4);
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxGroupFiles = LimitValue.Of(3) });

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TooManyGroupFiles);
        await Assert.That(warnings[0].Message).Contains("settings.maxGroupFiles");
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(3);
    }

    [Test]
    public async Task LoadReadsEveryFileWhenTheGroupFileLimitIsOff()
    {
        using SettingsTempFolder folder = new();
        _WriteGroupFiles(folder, 12);
        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxGroupFiles = LimitValue.Off });

        await Assert.That(manager.Load()).IsEmpty();
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(12);
    }

    [Test]
    public async Task ASaveAfterALoadThatStoppedDoesNotDeleteTheFilesItDidNotRead()
    {
        using SettingsTempFolder folder = new();
        _WriteGroupFiles(folder, 3);
        using (SettingsManager writer = new(folder.Path, TestLayout.Default))
        {
            foreach (string stem in new[] { "g0", "g1", "g2" })
            {
                writer.RegisterSetting(Setting.I64(stem + ".x", "X", stem, 0));
                writer.GetSetting(stem + ".x")!.SetPendingValue(SettingValue.I64(5)); // registered above
            }

            writer.ApplyChanges();
            writer.Save();
        }

        using SettingsManager manager = new(folder.Path, TestLayout.Default, new SettingsLimitValues { MaxGroupFiles = LimitValue.Of(1) });
        manager.Load();
        manager.Save();

        await Assert.That(Directory.GetFiles(folder.Path, "g*.json").Length).IsEqualTo(3);
    }

    #endregion

    #region Key of another group

    [Test]
    public async Task AKeyInTheFileOfAnotherGroupIsNotAppliedAndAWarningNamesIt()
    {
        using SettingsTempFolder folder = new();
        folder.Write("ui.json", """{"net.timeout": 7}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(Setting.I64("net.timeout", "Timeout", "net", 1));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings.Single().Kind).IsEqualTo(SettingsLoadWarningKind.WrongGroupFile);
        await Assert.That(manager.GetI64Setting("net.timeout")).IsEqualTo(1L);
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(1);
    }

    [Test]
    public async Task TheKeyInTheRightFileWinsOverTheOneInAnotherGroupFileWhateverTheOrder()
    {
        using SettingsTempFolder folder = new();
        folder.Write("net.json", """{"net.timeout": 9}""");
        folder.Write("ui.json", """{"net.timeout": 7}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(Setting.I64("net.timeout", "Timeout", "net", 1));

        manager.Load();

        await Assert.That(manager.GetI64Setting("net.timeout")).IsEqualTo(9L);
    }

    [Test]
    public async Task ASaveKeepsTheKeyThatStandsInTheFileOfAnotherGroup()
    {
        using SettingsTempFolder folder = new();
        folder.Write("ui.json", """{"net.timeout": 7, "ui.x": 1}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(Setting.I64("net.timeout", "Timeout", "net", 1));
        manager.Load();

        manager.Save();

        JsonObject ui = folder.ReadJson("ui.json");
        await Assert.That(ui.ContainsKey("net.timeout")).IsTrue();
        await Assert.That(ui.ContainsKey("ui.x")).IsTrue();
    }

    #endregion

    #region Vanished file and concurrent registration

    [Test]
    public async Task AGroupFileThatVanishesAfterTheListingIsSkippedWithoutAWarning()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        MethodInfo load = typeof(SettingsManager).GetMethod("_LoadGroupFile", BindingFlags.NonPublic | BindingFlags.Instance)!;
        List<SettingsLoadWarning> warnings = [];

        load.Invoke(manager, [folder.Combine("gone.json"), warnings]);

        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task ARegistrationDuringALoadStillGetsItsStoredValue()
    {
        const int settingCount = 40;
        for (int round = 0; round < 15; round++)
        {
            using SettingsTempFolder folder = new();
            JsonObject content = [];
            for (int index = 0; index < settingCount; index++)
            {
                content["g.k" + index.ToString(CultureInfo.InvariantCulture)] = 5;
            }

            folder.Write("g.json", content.ToJsonString());
            using SettingsManager manager = new(folder.Path, TestLayout.Default);
            using ManualResetEventSlim start = new(false);

            Task loading = Task.Run(() =>
            {
                start.Wait();
                manager.Load();
            });
            Task registering = Task.Run(() =>
            {
                start.Wait();
                for (int index = 0; index < settingCount; index++)
                {
                    string name = "g.k" + index.ToString(CultureInfo.InvariantCulture);
                    manager.RegisterSetting(Setting.I64(name, "K", "g", 0));
                }
            });
            start.Set();
            await Task.WhenAll(loading, registering);

            long[] values = [.. Enumerable.Range(0, settingCount).Select(index =>
                manager.GetI64Setting("g.k" + index.ToString(CultureInfo.InvariantCulture)) ?? -1L)];
            await Assert.That(values.All(static value => value == 5L)).IsTrue();
        }
    }

    #endregion

    #region Helpers

    /// <summary>Builds a JSON object that nests <paramref name="depth"/> levels deep.</summary>
    /// <param name="depth">The number of nested objects.</param>
    /// <returns>The JSON text.</returns>
    private static string _NestedJsonObject(int depth)
    {
        StringBuilder builder = new();
        for (int index = 0; index < depth; index++)
        {
            builder.Append("{\"x\":");
        }

        builder.Append('1');
        builder.Append('}', depth);
        return builder.ToString();
    }

    /// <summary>Writes <paramref name="count"/> group files <c>g0.json</c>, <c>g1.json</c>, … with one key each.</summary>
    /// <param name="folder">The folder to write into.</param>
    /// <param name="count">The number of files.</param>
    private static void _WriteGroupFiles(SettingsTempFolder folder, int count)
    {
        for (int index = 0; index < count; index++)
        {
            string stem = "g" + index.ToString(CultureInfo.InvariantCulture);
            folder.Write(stem + ".json", "{\"" + stem + ".x\": 5}");
        }
    }

    #endregion
}
