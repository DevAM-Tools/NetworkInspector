// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Access to the stored values of settings that are not registered in the run.</summary>
internal sealed class SettingsOrphansTests
{
    [Test]
    public async Task OrphansListTheLoadedValuesAsClonedJson()
    {
        using SettingsTempFolder folder = new();
        folder.Write("plugin.json", """{"plugin.x": 5}""");
        folder.Write("default.json", """{"loose": "text"}""");
        using SettingsManager manager = new(folder.Path);
        manager.Load();

        IReadOnlyList<OrphanedSettingValue> orphans = manager.Orphans;

        await Assert.That(orphans.Count).IsEqualTo(2);
        await Assert.That(orphans[0].GroupName).IsEqualTo(string.Empty);
        await Assert.That(orphans[0].Name).IsEqualTo("loose");
        await Assert.That(orphans[0].Value.GetString()).IsEqualTo("text");
        await Assert.That(orphans[1].GroupName).IsEqualTo("plugin");
        await Assert.That(orphans[1].Name).IsEqualTo("plugin.x");
        await Assert.That(orphans[1].Value.GetInt32()).IsEqualTo(5);
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(2);
    }

    [Test]
    public async Task ChangingAnOrphanWritesItAndARegistrationConsumesIt()
    {
        using SettingsTempFolder folder = new();
        folder.Write("plugin.json", """{"plugin.x": 5}""");
        using SettingsManager manager = new(folder.Path);
        manager.Load();
        using JsonDocument seven = JsonDocument.Parse("7");

        bool changed = manager.TrySetOrphanValue("plugin.x", seven.RootElement);
        manager.SaveChanged(["plugin.x"]);
        manager.RegisterSetting(Setting.I64("plugin.x", "X", "plugin", 0));

        await Assert.That(changed).IsTrue();
        await Assert.That(folder.ReadJson("plugin.json")["plugin.x"]?.GetValue<long>()).IsEqualTo(7L);
        await Assert.That(manager.GetI64Setting("plugin.x")).IsEqualTo(7L);
        await Assert.That(manager.Orphans).IsEmpty();
    }

    [Test]
    public async Task OnlyAnExistingOrphanCanBeChanged()
    {
        using SettingsTempFolder folder = new();
        folder.Write("plugin.json", """{"plugin.x": 5}""");
        using SettingsManager manager = new(folder.Path);
        manager.RegisterSetting(Setting.I64("plugin.y", "Y", "plugin", 0));
        manager.Load();
        using JsonDocument seven = JsonDocument.Parse("7");
        using JsonDocument nothing = JsonDocument.Parse("null");

        await Assert.That(manager.TrySetOrphanValue("plugin.y", seven.RootElement)).IsFalse();
        await Assert.That(manager.TrySetOrphanValue("nope", seven.RootElement)).IsFalse();
        await Assert.That(manager.TrySetOrphanValue("plugin.x", nothing.RootElement)).IsFalse();
        await Assert.That(manager.TrySetOrphanValue("plugin.x", default)).IsFalse();
        await Assert.That(manager.Orphans[0].Value.GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task OrphanedEntryCountAndClearBehaveAsBefore()
    {
        using SettingsTempFolder folder = new();
        folder.Write("plugin.json", """{"plugin.x": 5, "plugin.z": 6}""");
        using SettingsManager manager = new(folder.Path);
        manager.Load();

        int before = manager.OrphanedEntryCount;
        manager.ClearOrphanedEntries();

        await Assert.That(before).IsEqualTo(2);
        await Assert.That(manager.OrphanedEntryCount).IsEqualTo(0);
        await Assert.That(manager.Orphans).IsEmpty();
    }
}
