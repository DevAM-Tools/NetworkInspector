// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>The settings storage layout of NetworkInspector: the names it keeps from every earlier release.</summary>
internal sealed class CoreSettingsLayoutTests
{
    [Test]
    public async Task TheLayoutKeepsTheApplicationFolderAndTheTwoFileNamesOfEveryEarlierRelease()
    {
        SettingsStorageLayout layout = CoreSettingsLayout.Instance;

        await Assert.That(layout.ApplicationName).IsEqualTo("NetworkInspector");
        await Assert.That(layout.LockFileName).IsEqualTo(".ni-settings.lock");
        await Assert.That(layout.OwnedManifestFileName).IsEqualTo(".ni-settings-owned");
    }

    [Test]
    public async Task TheLayoutReservesTheFoldersOfTheHostsAndThePluginSystem()
    {
        await Assert.That(CoreSettingsLayout.Instance.ReservedFolderNames).IsEquivalentTo(
            ["application", "logs", "plugins", "plugins-data", "plugin-scan-cache", "host", "app", "presets"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task AManagerWithTheLayoutWritesTheOldNamesAndNoneOfTheNeutralDefaults()
    {
        using SettingsTempFolder folder = new();
        using (SettingsManager manager = new(folder.Path, CoreSettingsLayout.Instance))
        {
            Setting setting = Setting.String("demo.name", "Name", "demo", "default");
            manager.RegisterSetting(setting);
            manager.Load();
            setting.SetPendingValue(SettingValue.String("changed"));
            manager.ApplyChanges();
            manager.Save();
        }

        string[] names = [.. Directory.EnumerateFiles(folder.Path).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

        await Assert.That(names).IsEquivalentTo([".ni-settings-owned", ".ni-settings.lock", "demo.json"]);
    }

    [Test]
    public async Task TheDefaultApplicationFolderIsTheNetworkInspectorFolderUnderTheApplicationData()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetworkInspector");

        await Assert.That(SettingsManagerFactory.ResolvePath(CoreSettingsLayout.Instance)).IsEqualTo(expected);
    }
}
