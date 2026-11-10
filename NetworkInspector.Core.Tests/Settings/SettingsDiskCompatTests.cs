// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// A settings folder as the previous release left it must load and save with the layout of NetworkInspector without stranding
/// or losing a file; the same folder under another layout must not delete what that layout does not own.
/// </summary>
/// <remarks>
/// Sync: the fixture writes the two bookkeeping file names of <see cref="CoreSettingsLayout"/> (<c>.ni-settings-owned</c>,
/// <c>.ni-settings.lock</c>); a change of that layout is made here too.
/// </remarks>
internal sealed class SettingsDiskCompatTests
{
    #region Fixture

    /// <summary>The manifest name of the previous release.</summary>
    private const string _OldManifest = ".ni-settings-owned";

    /// <summary>The lock file name of the previous release.</summary>
    private const string _OldLock = ".ni-settings.lock";

    /// <summary>Writes a folder like the previous release left it: two group files the manifest names, and one unrelated file.</summary>
    /// <param name="folder">The folder to fill.</param>
    private static void _WritePreviousRelease(SettingsTempFolder folder)
    {
        folder.Write("demo.json", "{ \"demo.name\": \"stored\" }");
        folder.Write("empty.json", "{}");
        folder.Write(_OldManifest, "demo.json\nempty.json\n");
        folder.Write("notes.json", "[1, 2, 3]");
    }

    /// <summary>Loads the folder with <paramref name="layout"/>, changes the one registered setting, and saves.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="layout">The layout of the manager.</param>
    /// <returns>The value the manager read at load.</returns>
    private static string? _LoadChangeAndSave(SettingsTempFolder folder, SettingsStorageLayout layout)
    {
        using SettingsManager manager = new(folder.Path, layout);
        Setting setting = Setting.String("demo.name", "Name", "demo", "default");
        manager.RegisterSetting(setting);
        manager.Load();
        string? loaded = manager.GetStringSetting("demo.name");

        setting.SetPendingValue(SettingValue.String("changed"));
        manager.ApplyChanges();
        manager.Save();
        return loaded;
    }

    #endregion

    #region Layout of NetworkInspector

    [Test]
    public async Task AFolderOfThePreviousReleaseLoadsItsValues()
    {
        using SettingsTempFolder folder = new();
        _WritePreviousRelease(folder);

        string? loaded = _LoadChangeAndSave(folder, CoreSettingsLayout.Instance);

        await Assert.That(loaded).IsEqualTo("stored");
    }

    [Test]
    public async Task AfterASaveTheManifestKeepsItsOldNameAndListsTheGroupFilesAndNoNewNameExists()
    {
        using SettingsTempFolder folder = new();
        _WritePreviousRelease(folder);

        _LoadChangeAndSave(folder, CoreSettingsLayout.Instance);

        string[] listed = [.. File.ReadAllLines(folder.Combine(_OldManifest)).Where(static line => line.Length > 0)];
        await Assert.That(listed).Contains("demo.json");
        await Assert.That(File.Exists(folder.Combine(SettingsStorageLayout.DefaultOwnedManifestFileName))).IsFalse();
        await Assert.That(File.Exists(folder.Combine(_OldLock))).IsTrue();
        await Assert.That(File.Exists(folder.Combine(SettingsStorageLayout.DefaultLockFileName))).IsFalse();
        await Assert.That(folder.ReadJson("demo.json")["demo.name"]?.GetValue<string>()).IsEqualTo("changed");
    }

    [Test]
    public async Task AStaleGroupFileThatTheManifestNamesIsRemovedAndAnUnrelatedFileStays()
    {
        using SettingsTempFolder folder = new();
        _WritePreviousRelease(folder);

        _LoadChangeAndSave(folder, CoreSettingsLayout.Instance);

        await Assert.That(File.Exists(folder.Combine("empty.json"))).IsFalse();
        await Assert.That(File.ReadAllText(folder.Combine("notes.json"))).IsEqualTo("[1, 2, 3]");
        await Assert.That(File.ReadAllLines(folder.Combine(_OldManifest))).DoesNotContain("empty.json");
    }

    #endregion

    #region Another layout

    [Test]
    public async Task TheSameFolderUnderAnotherLayoutDeletesNothingItDoesNotOwn()
    {
        using SettingsTempFolder folder = new();
        _WritePreviousRelease(folder);
        SettingsStorageLayout other = SettingsStorageLayout.Create("Other", [], ".other.lock", ".other-owned");

        _LoadChangeAndSave(folder, other);

        // The other layout has no manifest of its own, so it owns nothing: the group files of the previous release stay, and the
        // old manifest is neither read nor changed.
        await Assert.That(File.Exists(folder.Combine("empty.json"))).IsTrue();
        await Assert.That(File.ReadAllText(folder.Combine("notes.json"))).IsEqualTo("[1, 2, 3]");
        await Assert.That(File.ReadAllText(folder.Combine(_OldManifest))).IsEqualTo("demo.json\nempty.json\n");
        await Assert.That(File.Exists(folder.Combine(".other-owned"))).IsTrue();
        await Assert.That(File.Exists(folder.Combine(".other.lock"))).IsTrue();
    }

    #endregion
}
