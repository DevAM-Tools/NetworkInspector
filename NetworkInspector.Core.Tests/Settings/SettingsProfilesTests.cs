// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Profile folders under a settings base: list, create, copy, rename, delete, and their refusals.</summary>
internal sealed class SettingsProfilesTests
{
    #region Helpers

    private static string[] _Snapshot(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/') + (Directory.Exists(path) ? "/" : File.ReadAllText(path)))
            .Order(StringComparer.Ordinal)];

    private static string _Create(string settingsBase, string name, string? copyFrom = null)
    {
        if (!SettingsProfiles.TryCreate(settingsBase, name, copyFrom, out string? error))
        {
            throw new InvalidOperationException(error);
        }

        return name;
    }

    #endregion

    #region List and create

    [Test]
    public async Task AnEmptyOrMissingBaseHasNoProfiles()
    {
        using SettingsTempFolder folder = new();

        await Assert.That(SettingsProfiles.List(folder.Path)).IsEmpty();
        await Assert.That(SettingsProfiles.List(folder.Combine("missing"))).IsEmpty();
        await Assert.That(SettingsProfiles.Exists(folder.Path, "CAN")).IsFalse();
    }

    [Test]
    public async Task CreateMakesAFolderAndListShowsIt()
    {
        using SettingsTempFolder folder = new();

        bool created = SettingsProfiles.TryCreate(folder.Path, "CAN", null, out string? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(Directory.Exists(folder.Combine("CAN"))).IsTrue();
        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["CAN"], CollectionOrdering.Matching);
        await Assert.That(SettingsProfiles.Exists(folder.Path, "can")).IsTrue();
    }

    [Test]
    public async Task ReservedHiddenAndInvalidFoldersAreNeverListed()
    {
        using SettingsTempFolder folder = new();
        foreach (string name in new[] { "application", "logs", "plugins-data", "app", ".hidden", "bad@name", "Presets" })
        {
            Directory.CreateDirectory(folder.Combine(name));
        }

        _Create(folder.Path, "CAN");

        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["CAN"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProfilesAreListedSortedIgnoringCase()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "lab");
        _Create(folder.Path, "Bench");
        _Create(folder.Path, "CAN");

        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["Bench", "CAN", "lab"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ACreateOfAnExistingNameIgnoringCaseIsRefused()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "CAN");
        string[] before = _Snapshot(folder.Path);

        bool created = SettingsProfiles.TryCreate(folder.Path, "can", null, out string? error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsEqualTo("Profile 'can' already exists.");
        await Assert.That(_Snapshot(folder.Path)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidAndReservedNamesGiveTheRuleMessages()
    {
        using SettingsTempFolder folder = new();

        SettingsProfiles.TryCreate(folder.Path, "application", null, out string? reserved);
        SettingsProfiles.TryCreate(folder.Path, "a/b", null, out string? slash);
        SettingsProfiles.TryCreate(folder.Path, "NUL", null, out string? device);

        await Assert.That(reserved).IsEqualTo("Profile name 'application': application is reserved.");
        await Assert.That(slash).IsEqualTo("Profile name 'a/b': letters, digits, space, dot, dash, and underscore only.");
        await Assert.That(device).IsEqualTo("Profile name 'NUL': NUL is reserved by the operating system.");
        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Any()).IsFalse();
    }

    #endregion

    #region Copy

    [Test]
    public async Task ACopyHasEveryFileOfTheSourceByteEqual()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "CAN");
        File.WriteAllText(folder.Combine("CAN/tcp.json"), """{"tcp.x": 1}""");
        Directory.CreateDirectory(folder.Combine("CAN/deep/er"));
        File.WriteAllBytes(folder.Combine("CAN/deep/er/blob.bin"), [1, 2, 3, 0, 255]);
        File.WriteAllText(folder.Combine("CAN/.ni-settings.lock"), "locked");

        bool created = SettingsProfiles.TryCreate(folder.Path, "Lab", "CAN", out string? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.ReadAllText(folder.Combine("Lab/tcp.json"))).IsEqualTo("""{"tcp.x": 1}""");
        await Assert.That(File.ReadAllBytes(folder.Combine("Lab/deep/er/blob.bin"))).IsEquivalentTo(new byte[] { 1, 2, 3, 0, 255 },
            CollectionOrdering.Matching);
        await Assert.That(File.Exists(folder.Combine("Lab/.ni-settings.lock"))).IsFalse();
    }

    [Test]
    public async Task ACopyOfAMissingProfileIsRefusedAndNothingChanges()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "CAN");
        string[] before = _Snapshot(folder.Path);

        bool created = SettingsProfiles.TryCreate(folder.Path, "Lab", "Nope", out string? error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsEqualTo("Profile 'Nope' does not exist.");
        await Assert.That(_Snapshot(folder.Path)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    #endregion

    #region Rename

    [Test]
    public async Task RenameMovesTheProfile()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "Lab");
        File.WriteAllText(folder.Combine("Lab/tcp.json"), "{}");

        bool renamed = SettingsProfiles.TryRename(folder.Path, "Lab", "Bench", out string? error);

        await Assert.That(renamed).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(SettingsProfiles.Exists(folder.Path, "Bench")).IsTrue();
        await Assert.That(SettingsProfiles.Exists(folder.Path, "Lab")).IsFalse();
        await Assert.That(File.Exists(folder.Combine("Bench/tcp.json"))).IsTrue();
    }

    [Test]
    public async Task RenameToAnExistingNameOrFromAMissingOneIsRefused()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "Lab");
        _Create(folder.Path, "Bench");
        string[] before = _Snapshot(folder.Path);

        bool toExisting = SettingsProfiles.TryRename(folder.Path, "Lab", "bench", out string? existing);
        bool fromMissing = SettingsProfiles.TryRename(folder.Path, "Nope", "X", out string? missing);
        bool toInvalid = SettingsProfiles.TryRename(folder.Path, "Lab", "logs", out string? invalid);

        await Assert.That(toExisting).IsFalse();
        await Assert.That(existing).IsEqualTo("Profile 'bench' already exists.");
        await Assert.That(fromMissing).IsFalse();
        await Assert.That(missing).IsEqualTo("Profile 'Nope' does not exist.");
        await Assert.That(toInvalid).IsFalse();
        await Assert.That(invalid).IsEqualTo("Profile name 'logs': logs is reserved.");
        await Assert.That(_Snapshot(folder.Path)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ACaseOnlyRenameWorksOnEveryOperatingSystem()
    {
        using SettingsTempFolder folder = new();
        _Create(folder.Path, "Bench");
        File.WriteAllText(folder.Combine("Bench/tcp.json"), "{}");

        bool renamed = SettingsProfiles.TryRename(folder.Path, "Bench", "BENCH", out string? error);

        await Assert.That(renamed).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["BENCH"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(folder.Combine("BENCH/tcp.json"))).IsTrue();
    }

    #endregion

    #region Long names

    [Test]
    public async Task ALongNameCreatesAMappedFolderWithItsSidecarAndRenameRemovesTheOldSidecar()
    {
        using SettingsTempFolder folder = new();
        string longName = string.Concat(Enumerable.Repeat("long name ", 26))[..256].TrimEnd();
        string storage = StorageNames.ToStorageName(longName);

        bool created = SettingsProfiles.TryCreate(folder.Path, longName, null, out string? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(Directory.Exists(folder.Combine(storage))).IsTrue();
        await Assert.That(File.Exists(folder.Combine(storage + ".name"))).IsTrue();
        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo([longName], CollectionOrdering.Matching);

        bool renamed = SettingsProfiles.TryRename(folder.Path, longName, "Short", out string? renameError);

        await Assert.That(renamed).IsTrue();
        await Assert.That(renameError).IsNull();
        await Assert.That(File.Exists(folder.Combine(storage + ".name"))).IsFalse();
        await Assert.That(Directory.Exists(folder.Combine(storage))).IsFalse();
        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["Short"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AMappedFolderWithoutAReadableSidecarIsNotListed()
    {
        using SettingsTempFolder folder = new();
        string longName = new('z', 200);
        string storage = StorageNames.ToStorageName(longName);
        SettingsProfiles.TryCreate(folder.Path, longName, null, out _);
        File.Delete(folder.Combine(storage + ".name"));

        await Assert.That(SettingsProfiles.List(folder.Path)).IsEmpty();
    }

    #endregion

    #region Delete

    [Test]
    public async Task DeleteRemovesFolderAndSidecarButNotTheLastProfile()
    {
        using SettingsTempFolder folder = new();
        string longName = new('q', 150);
        _Create(folder.Path, "CAN");
        _Create(folder.Path, longName);
        string storage = StorageNames.ToStorageName(longName);

        bool deletedLong = SettingsProfiles.TryDelete(folder.Path, longName, out string? error);
        bool deletedLast = SettingsProfiles.TryDelete(folder.Path, "CAN", out string? lastError);
        bool deletedMissing = SettingsProfiles.TryDelete(folder.Path, "Nope", out string? missingError);

        await Assert.That(deletedLong).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.Exists(folder.Combine(storage + ".name"))).IsFalse();
        await Assert.That(deletedLast).IsFalse();
        await Assert.That(lastError).IsEqualTo("Profile 'CAN' is the last profile and cannot be deleted.");
        await Assert.That(Directory.Exists(folder.Combine("CAN"))).IsTrue();
        await Assert.That(deletedMissing).IsFalse();
        await Assert.That(missingError).IsEqualTo("Profile 'Nope' does not exist.");
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task TwoThreadsCreatingOneNameEndWithExactlyOneSuccess()
    {
        using SettingsTempFolder folder = new();
        using Barrier barrier = new(2);
        string?[] errors = new string?[2];
        bool[] results = new bool[2];

        Task[] tasks = [.. Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            results[index] = SettingsProfiles.TryCreate(folder.Path, "X", null, out errors[index]);
        }))];
        await Task.WhenAll(tasks);

        await Assert.That(results.Count(static r => r)).IsEqualTo(1);
        await Assert.That(errors.Count(static e => e is null)).IsEqualTo(1);
        await Assert.That(SettingsProfiles.List(folder.Path)).IsEquivalentTo(["X"], CollectionOrdering.Matching);
        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task TwoThreadsDeletingTheLastTwoProfilesNeverLeaveNone()
    {
        // The last-profile check and the delete are separate steps: without care both deleters count two profiles and both
        // delete. The round repeats because the interleaving cannot be controlled; it can only fail, never hang.
        int emptyRounds = 0;
        for (int round = 0; round < 25; round++)
        {
            using SettingsTempFolder folder = new();
            _Create(folder.Path, "A");
            _Create(folder.Path, "B");
            using Barrier barrier = new(2);

            Task first = Task.Run(() =>
            {
                barrier.SignalAndWait();
                _ = SettingsProfiles.TryDelete(folder.Path, "A", out _);
            });
            Task second = Task.Run(() =>
            {
                barrier.SignalAndWait();
                _ = SettingsProfiles.TryDelete(folder.Path, "B", out _);
            });
            await Task.WhenAll(first, second);

            if (SettingsProfiles.List(folder.Path).Count == 0)
            {
                emptyRounds++;
            }
        }

        await Assert.That(emptyRounds).IsEqualTo(0);
    }

    [Test]
    public async Task ARenameRollbackThatCannotPutTheFolderBackReportsFailureInsteadOfThrowing()
    {
        using SettingsTempFolder folder = new();
        string work = folder.Combine(".work-moved");
        string occupied = folder.Combine("old");
        Directory.CreateDirectory(work);
        File.WriteAllText(occupied, "a file occupies the old path");

        bool restored = SettingsProfiles.TryRestoreFolder(work, occupied);

        await Assert.That(restored).IsFalse();
        await Assert.That(Directory.Exists(work)).IsTrue();
    }

    #endregion

    #region Arguments

    [Test]
    public async Task AnEmptyBaseIsAnArgumentError()
    {
        await Assert.That(() => SettingsProfiles.List(" ")).Throws<ArgumentException>();
        await Assert.That(() => SettingsProfiles.Exists(string.Empty, "x")).Throws<ArgumentException>();
        await Assert.That(() => SettingsProfiles.TryCreate(" ", "x", null, out _)).Throws<ArgumentException>();
    }

    #endregion
}
