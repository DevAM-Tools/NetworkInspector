// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Malformed persisted content and file-system failures end in warnings or a <see cref="PersistenceSettingsException"/>,
/// never in an exception that escapes <see cref="SettingsManager.Load"/> or <see cref="SettingsManager.RegisterSetting"/>
/// half-way.
/// </summary>
internal sealed class SettingsManagerRobustnessTests
{
    #region Helpers

    private static string _NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    #endregion

    #region Tests

    [Test]
    public async Task LoadRepeatedTopLevelPropertySkipsTheFileAndLoadsTheOthers()
    {
        string directory = _NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "first.json"), """{"first.flag": true, "first.flag": false}""").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "second.json"), """{"second.flag": true}""").ConfigureAwait(false);
            using SettingsManager manager = new(directory);
            Setting first = Setting.Bool("first.flag", "First", "first", false);
            Setting second = Setting.Bool("second.flag", "Second", "second", false);
            manager.RegisterSetting(first);
            manager.RegisterSetting(second);

            IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupFileSyntax);
            await Assert.That(warnings[0].GroupName).IsEqualTo("first");
            await Assert.That(warnings[0].Message).Contains("first.json");
            await Assert.That(first.Value).IsEqualTo(SettingValue.Bool(false));
            await Assert.That(second.Value).IsEqualTo(SettingValue.Bool(true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task LoadUndecodablePropertyNameSkipsTheFileWithASyntaxWarning()
    {
        string directory = _NewDirectory();
        try
        {
            // A lone high surrogate as property name cannot be decoded as UTF-16 text.
            await File.WriteAllTextAsync(Path.Combine(directory, "lone.json"), """{"\ud800": true}""").ConfigureAwait(false);
            using SettingsManager manager = new(directory);

            IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupFileSyntax);
            await Assert.That(warnings[0].GroupName).IsEqualTo("lone");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RegisterOrphanedEnumWithRepeatedPropertyReportsDeserializationError()
    {
        string directory = _NewDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "levels.json"),
                """{"levels.mode": {"name": "High", "value": 2, "value": 3}}""").ConfigureAwait(false);
            using SettingsManager manager = new(directory);
            _ = manager.Load();

            SettingRegistrationResult result = manager.RegisterSetting(
                Setting.Enum("levels.mode", "Mode", "levels", 0, [("Low", 0), ("High", 2)]));

            await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.DeserializationError);
            Setting? registered = manager.GetSetting("levels.mode");
            await Assert.That(registered).IsNotNull();
            await Assert.That(registered!.Value).IsEqualTo(SettingValue.Enum("Low", 0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RegisterPreloadedSettingWhileLoadRunsAppliesThePreload()
    {
        using SettingsManager manager = new();
        manager.PreloadValue("future.flag", SettingValue.Bool(true));
        System.Reflection.FieldInfo loading = typeof(SettingsManager).GetField(
            "_IsLoading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Setting setting = Setting.Bool("future.flag", "Flag", "future", false);

        SettingRegistrationResult result;
        loading.SetValue(manager, 1);
        try
        {
            result = manager.RegisterSetting(setting);
        }
        finally
        {
            loading.SetValue(manager, 0);
        }

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.Success);
        await Assert.That(setting.Value).IsEqualTo(SettingValue.Bool(true));
        await Assert.That(manager.GetSetting("future.flag")).IsNotNull();
    }

    [Test]
    public async Task SaveOntoADirectoryThrowsPersistenceSettingsException()
    {
        string directory = _NewDirectory();
        try
        {
            // The group file path is a directory: access denied on Windows, an I/O error elsewhere. Both are wrapped.
            Directory.CreateDirectory(Path.Combine(directory, "blocked.json"));
            using SettingsManager manager = new(directory);
            manager.RegisterSetting(Setting.Bool("blocked.flag", "Flag", "blocked", true));

            await Assert.That(manager.Save).Throws<PersistenceSettingsException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion
}
