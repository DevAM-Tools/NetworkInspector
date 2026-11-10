// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// The settings storage layout of NetworkInspector: the application folder, the folders of the hosts that are no profiles,
/// and the names of the two bookkeeping files.
/// </summary>
/// <remarks>
/// The two file names (<c>.ni-settings.lock</c>, <c>.ni-settings-owned</c>) are the ones every earlier release wrote. A changed
/// name would strand the files an earlier release owned: the manager would not find them in the manifest and would leave them in
/// the folder forever. Never change a name of this layout; add a migration first.
/// <para>Sync: the fixture of the disk-compatibility test writes the same two file names and the same folders; a change of this
/// layout is made there too.</para>
/// </remarks>
/// <seealso cref="SettingsStorageLayout"/>
public static class CoreSettingsLayout
{
    /// <summary>The layout every NetworkInspector front end passes to the settings manager, the factory, and the profile functions.</summary>
    public static SettingsStorageLayout Instance { get; } = SettingsStorageLayout.Create(
        "NetworkInspector",
        ["logs", "plugins", "plugins-data", "plugin-scan-cache", "host", "app", "presets"],
        lockFileName: ".ni-settings.lock",
        ownedManifestFileName: ".ni-settings-owned");
}
