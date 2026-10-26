// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Regression tests for <see cref="SettingsManager"/> edge cases: refused stored values that coexist with a registered
/// setting, the result of a refused preload, argument validation, invalid storage paths, and a reader that holds a group file
/// while it is saved.
/// </summary>
internal sealed class SettingsManagerHardeningTests
{
    #region Save with a refused stored value

    [Test]
    public async Task SaveWritesTheValueOfARegisteredSettingNotTheRefusedStoredValue()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.port": 80}""");
        using SettingsManager manager = new(folder.Path);
        manager.Load();

        // The stored 80 is below the minimum: the registration refuses it and the value stays in the orphan table.
        SettingRegistrationResult result = manager.RegisterSetting(Setting.U64("g.port", "Port", "g", 8080UL, min: 1024UL, max: 65535UL));
        Setting port = manager.GetSetting("g.port") ?? throw new InvalidOperationException("missing");
        port.SetPendingValue(SettingValue.U64(2000));
        manager.ApplyChanges();

        manager.Save();

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.OutOfRange);
        await Assert.That(folder.ReadJson("g.json")["g.port"]?.GetValue<long>()).IsEqualTo(2000L);
    }

    [Test]
    public async Task SaveDoesNotWriteARefusedStoredValueBackWhenTheSettingIsAtItsDefault()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.port": 80, "g.unknown": 1}""");
        using SettingsManager manager = new(folder.Path);
        manager.Load();
        manager.RegisterSetting(Setting.U64("g.port", "Port", "g", 8080UL, min: 1024UL, max: 65535UL));

        manager.Save();

        // The setting holds its default, so its key is not stored; the real orphan next to it stays.
        JsonObject saved = folder.ReadJson("g.json");
        await Assert.That(saved.ContainsKey("g.port")).IsFalse();
        await Assert.That(saved["g.unknown"]?.GetValue<long>()).IsEqualTo(1L);
    }

    #endregion

    #region Refused preload

    [Test]
    public async Task APreloadOfTheWrongTypeIsReportedAsTypeMismatch()
    {
        using SettingsManager manager = new();
        manager.PreloadValue("g.port", SettingValue.String("text"));

        SettingRegistrationResult result = manager.RegisterSetting(Setting.U64("g.port", "Port", "g", 8080UL));

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.TypeMismatch);
        await Assert.That(result.Value).IsEqualTo(SettingValue.U64(8080UL));
    }

    #endregion

    #region Argument validation

    [Test]
    public async Task ThePathConstructorRejectsNull()
    {
        await Assert.That(() => new SettingsManager(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    [Test]
    public async Task EnumWithMetadataRejectsNullMetadata()
    {
        await Assert.That(() => Setting.EnumWithMetadata("g.e", "E", "g", 0, null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Invalid storage paths

    [Test]
    [Arguments("")]
    [Arguments("bad\0folder")]
    public async Task SaveWithAnInvalidStoragePathThrowsAPersistenceException(string path)
    {
        using SettingsManager manager = new(path);
        manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1));
        manager.GetSetting("g.x")!.SetPendingValue(SettingValue.I64(2)); // registered one line above
        manager.ApplyChanges();

        await Assert.That(() => manager.Save()).Throws<PersistenceSettingsException>();
        await Assert.That(() => manager.SaveChanged(["g.x"])).Throws<PersistenceSettingsException>();
    }

    #endregion

    #region Reader that holds a group file

    [Test]
    public async Task SaveReplacesAGroupFileThatAReaderHasOpen()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.x": 2}""");
        using SettingsManager manager = new(folder.Path);
        manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1));
        manager.Load();
        manager.GetSetting("g.x")!.SetPendingValue(SettingValue.I64(3)); // registered one line above
        manager.ApplyChanges();

        // A second process (or a Load on another thread) is reading the file while this manager saves.
        using (FileStream reader = SettingsFileAccess.OpenSharedRead(folder.Combine("g.json")))
        {
            manager.Save();
        }

        await Assert.That(folder.ReadJson("g.json")["g.x"]?.GetValue<long>()).IsEqualTo(3L);
    }

    #endregion

    #region Reset to defaults

    [Test]
    public async Task ResetAllToDefaultsRaisesNoEventForAChangeOfTheCurrentValueAlone()
    {
        // Documented behavior (see ResetAllToDefaults): only a change of the pending value raises PendingChanged, and
        // SettingsApplied is never raised by this member.
        using SettingsManager manager = new();
        Setting setting = Setting.I64("g.x", "X", "g", 1);
        manager.RegisterSetting(setting);
        setting.SetPendingValue(SettingValue.I64(5));
        manager.ApplyChanges();

        // The current value is 5 and the pending value is back at the default: the reset changes only the current value.
        setting.SetPendingValue(SettingValue.I64(1));
        List<string> pending = [];
        List<string> applied = [];
        manager.PendingChanged += (_, e) => pending.Add(e.SettingName);
        manager.SettingsApplied += (_, e) => applied.AddRange(e.ChangedSettings);

        manager.ResetAllToDefaults();

        await Assert.That(setting.Value).IsEqualTo(SettingValue.I64(1));
        await Assert.That(pending).IsEmpty();
        await Assert.That(applied).IsEmpty();
    }

    #endregion
}
