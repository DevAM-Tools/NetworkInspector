// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// <c>Save</c> writes only values that differ from their defaults and the values of settings that are not registered in the
/// run (orphans), so a later change of a default reaches every folder and a plugin's stored settings are never lost.
/// </summary>
internal sealed class SettingsSaveSparseTests
{
    #region Helpers

    private static void _Register(SettingsManager manager)
    {
        manager.RegisterSetting(Setting.I64("g.a", "A", "g", 1));
        manager.RegisterSetting(Setting.I64("g.b", "B", "g", 2));
        manager.RegisterSetting(Setting.I64("h.x", "X", "h", 3));
    }

    private static void _Set(SettingsManager manager, string name, SettingValue value)
    {
        Setting setting = manager.GetSetting(name) ?? throw new InvalidOperationException(name);
        setting.SetPendingValue(value);
        manager.ApplyChanges();
    }

    #endregion

    #region Sparse files

    [Test]
    public async Task OnlyNonDefaultValuesAreWrittenAndGroupsWithoutContentHaveNoFile()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path);
        _Register(manager);
        _Set(manager, "g.b", SettingValue.I64(20));

        manager.Save();

        JsonObject group = folder.ReadJson("g.json");
        await Assert.That(group.Count).IsEqualTo(1);
        await Assert.That(group["g.b"]?.GetValue<long>()).IsEqualTo(20L);
        await Assert.That(File.Exists(folder.Combine("h.json"))).IsFalse();
    }

    [Test]
    public async Task AnOwnedGroupFileThatLostItsContentIsDeleted()
    {
        using SettingsTempFolder folder = new();
        folder.Write("h.json", """{"h.x": 9}""");
        folder.Write(".ni-settings-owned", "h.json\n");
        using SettingsManager manager = new(folder.Path);
        _Register(manager);

        manager.Save();

        await Assert.That(File.Exists(folder.Combine("h.json"))).IsFalse();
    }

    [Test]
    public async Task AFreshManagerLoadsTheSameValues()
    {
        using SettingsTempFolder folder = new();
        using (SettingsManager first = new(folder.Path))
        {
            _Register(first);
            _Set(first, "g.b", SettingValue.I64(20));
            _Set(first, "h.x", SettingValue.I64(30));
            first.Save();
        }

        using SettingsManager second = new(folder.Path);
        _Register(second);
        IReadOnlyList<SettingsLoadWarning> warnings = second.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(second.GetI64Setting("g.a")).IsEqualTo(1L);
        await Assert.That(second.GetI64Setting("g.b")).IsEqualTo(20L);
        await Assert.That(second.GetI64Setting("h.x")).IsEqualTo(30L);
    }

    [Test]
    public async Task ReturningToTheDefaultDeletesTheOwnedFile()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path);
        _Register(manager);
        _Set(manager, "g.b", SettingValue.I64(20));
        manager.Save();
        await Assert.That(File.Exists(folder.Combine("g.json"))).IsTrue();

        _Set(manager, "g.b", SettingValue.I64(2));
        manager.Save();

        await Assert.That(File.Exists(folder.Combine("g.json"))).IsFalse();
    }

    [Test]
    public async Task AGroupThatHoldsOrphansKeepsItsFile()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.b": 20, "g.later": true}""");
        folder.Write(".ni-settings-owned", "g.json\n");
        using SettingsManager manager = new(folder.Path);
        _Register(manager);
        manager.Load();
        _Set(manager, "g.b", SettingValue.I64(2));

        manager.Save();

        JsonObject group = folder.ReadJson("g.json");
        await Assert.That(group.Count).IsEqualTo(1);
        await Assert.That(group["g.later"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task SavingTwiceLeavesNoTemporaryFile()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path);
        _Register(manager);
        _Set(manager, "g.b", SettingValue.I64(20));

        manager.Save();
        manager.Save();

        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.tmp").Any()).IsFalse();
    }

    #endregion
}
