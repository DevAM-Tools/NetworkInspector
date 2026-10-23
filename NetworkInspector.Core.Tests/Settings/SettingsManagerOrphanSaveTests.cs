// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>A save keeps the values of settings that are not registered in the run (for example a plugin that is not loaded).</summary>
internal sealed class SettingsManagerOrphanSaveTests
{
    private static string _Name(int group) => "g" + group.ToString(CultureInfo.InvariantCulture);

    [Test]
    public async Task SavingWithoutAPluginLoadedKeepsItsStoredSettings()
    {
        using SettingsTempFolder folder = new();
        folder.Write("sample.json", """{"sample.greeting":"Hi"}""");
        folder.Write("tcp.json", """{"tcp.relative_sequence_numbers": false, "tcp.unknown": 7}""");
        folder.Write(".ni-settings-owned", "sample.json\ntcp.json\n");
        using (SettingsManager manager = new(folder.Path))
        {
            manager.RegisterSetting(Setting.Bool("tcp.relative_sequence_numbers", "Relative", "tcp", true));
            manager.RegisterSetting(Setting.U64("tcp.max_tracked_streams", "Streams", "tcp", 100));
            manager.Load();
            Setting streams = manager.GetSetting("tcp.max_tracked_streams") ?? throw new InvalidOperationException("missing");
            streams.SetPendingValue(SettingValue.U64(5000));
            manager.ApplyChanges();

            manager.Save();
        }

        // The orphaned group file stays although the manifest listed it as owned, and the orphan inside a registered group
        // is written back next to the new value.
        JsonObject sample = folder.ReadJson("sample.json");
        JsonObject tcp = folder.ReadJson("tcp.json");
        await Assert.That(sample["sample.greeting"]?.GetValue<string>()).IsEqualTo("Hi");
        await Assert.That(tcp["tcp.max_tracked_streams"]?.GetValue<long>()).IsEqualTo(5000L);
        await Assert.That(tcp["tcp.unknown"]?.GetValue<long>()).IsEqualTo(7L);
        await Assert.That(tcp["tcp.relative_sequence_numbers"]?.GetValue<bool>()).IsFalse();

        // The plugin comes back later and finds its value.
        using SettingsManager later = new(folder.Path);
        later.RegisterSetting(Setting.String("sample.greeting", "Greeting", "sample", "Hello"));
        later.Load();
        await Assert.That(later.GetStringSetting("sample.greeting")).IsEqualTo("Hi");
    }

    [Test]
    public async Task SaveKeepsAGroupFileThatAnotherManagerWroteAfterLoad()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager first = new(folder.Path);
        first.RegisterSetting(Setting.I64("a.x", "X", "a", 1));
        first.Load();

        // A second process (the CLI) stores a plugin setting after the first manager loaded.
        using (SettingsManager second = new(folder.Path))
        {
            second.RegisterSetting(Setting.String("sample.greeting", "G", "sample", "Hello"));
            second.Load();
            Setting greeting = second.GetSetting("sample.greeting") ?? throw new InvalidOperationException("missing");
            greeting.SetPendingValue(SettingValue.String("Hi"));
            second.ApplyChanges();
            second.SaveChanged(["sample.greeting"]);
        }

        Setting changed = first.GetSetting("a.x") ?? throw new InvalidOperationException("missing");
        changed.SetPendingValue(SettingValue.I64(5));
        first.ApplyChanges();
        first.Save();

        await Assert.That(File.Exists(folder.Combine("sample.json"))).IsTrue();
        await Assert.That(folder.ReadJson("a.json")["a.x"]?.GetValue<long>()).IsEqualTo(5L);
        await Assert.That(File.ReadAllText(folder.Combine(".ni-settings-owned"))).Contains("sample.json");
    }

    [Test]
    public async Task GroupFilesWrittenByParallelSaveChangedCallsAreAllKnownToTheNextSave()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path);
        for (int group = 0; group < 8; group++)
        {
            manager.RegisterSetting(Setting.I64(_Name(group) + ".x", "X", _Name(group), 1));
        }

        manager.Load();
        for (int group = 0; group < 8; group++)
        {
            Setting setting = manager.GetSetting(_Name(group) + ".x") ?? throw new InvalidOperationException("missing");
            setting.SetPendingValue(SettingValue.I64(5));
        }

        manager.ApplyChanges();

        // Eight writers record their group file at the same time; none of the records may be lost.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(group => Task.Run(() => manager.SaveChanged([_Name(group) + ".x"]))));
        for (int group = 0; group < 8; group++)
        {
            Setting setting = manager.GetSetting(_Name(group) + ".x") ?? throw new InvalidOperationException("missing");
            setting.SetPendingValue(SettingValue.I64(1));
        }

        manager.ApplyChanges();
        manager.Save();

        // Every value is back at its default, so every group file this manager wrote is removed.
        await Assert.That(Directory.GetFiles(folder.Path, "g*.json")).IsEmpty();
    }

    [Test]
    public async Task SaveStillDeletesAGroupFileThatThisManagerLoadedAndNoLongerHasContentFor()
    {
        using SettingsTempFolder folder = new();
        folder.Write("a.json", """{"a.x": 7}""");
        folder.Write(".ni-settings-owned", "a.json\n");
        using SettingsManager manager = new(folder.Path);
        manager.RegisterSetting(Setting.I64("a.x", "X", "a", 1));
        manager.Load();
        Setting setting = manager.GetSetting("a.x") ?? throw new InvalidOperationException("missing");
        setting.SetPendingValue(SettingValue.I64(1));
        manager.ApplyChanges();

        manager.Save();

        // The value is back at its default, so the owned file has no content and goes away.
        await Assert.That(File.Exists(folder.Combine("a.json"))).IsFalse();
    }
}
