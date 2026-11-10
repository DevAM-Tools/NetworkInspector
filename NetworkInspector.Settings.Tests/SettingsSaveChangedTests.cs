// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>Merge saving of named keys, removal of stored values, and the cross-process folder lock.</summary>
internal sealed class SettingsSaveChangedTests
{
    #region Helpers

    private static SettingsManager _Manager(string folder, SettingsLimitValues? limits = null)
    {
        SettingsManager manager = new(folder, TestLayout.Default, limits ?? SettingsLimitValues.Default);
        manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1));
        manager.RegisterSetting(Setting.I64("g.y", "Y", "g", 2));
        return manager;
    }

    private static void _Set(SettingsManager manager, string name, long value)
    {
        Setting setting = manager.GetSetting(name) ?? throw new InvalidOperationException(name);
        setting.SetPendingValue(SettingValue.I64(value));
        manager.ApplyChanges();
    }

    #endregion

    #region SaveChanged

    [Test]
    public async Task TwoManagersMergeTheirKeysWithoutLostUpdates()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager a = _Manager(folder.Path);
        using SettingsManager b = _Manager(folder.Path);
        a.Load();
        b.Load();

        _Set(a, "g.x", 10);
        a.SaveChanged(["g.x"]);
        _Set(b, "g.y", 20);
        b.SaveChanged(["g.y"]);

        JsonObject group = folder.ReadJson("g.json");
        await Assert.That(group["g.x"]?.GetValue<long>()).IsEqualTo(10L);
        await Assert.That(group["g.y"]?.GetValue<long>()).IsEqualTo(20L);
    }

    [Test]
    public async Task AFailedReplaceLeavesNoTemporaryFile()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.x", 10);

        // A folder in the place of the group file makes the final move fail after the temporary file was written.
        Directory.CreateDirectory(Path.Combine(folder.Path, "g.json"));

        await Assert.That(() => manager.SaveChanged(["g.x"])).Throws<PersistenceSettingsException>();
        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.tmp").Any()).IsFalse();
    }

    [Test]
    public async Task AnErrorInTheSecondGroupStillRecordsTheFirstGroupFileInTheManifest()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.RegisterSetting(Setting.I64("a.x", "X", "a", 1));
        manager.RegisterSetting(Setting.I64("b.y", "Y", "b", 2));
        _Set(manager, "a.x", 10);
        _Set(manager, "b.y", 20);

        // A folder in the place of the second group file makes its final move fail after the first file was written.
        Directory.CreateDirectory(folder.Combine("b.json"));

        await Assert.That(() => manager.SaveChanged(["a.x", "b.y"])).Throws<PersistenceSettingsException>();

        await Assert.That(File.Exists(folder.Combine("a.json"))).IsTrue();
        await Assert.That(File.ReadAllLines(folder.Combine(".settings-owned"))).Contains("a.json");
    }

    [Test]
    public async Task AValueEqualToTheDefaultRemovesTheKeyAndAnEmptyFileIsDeleted()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.x", 10);
        _Set(manager, "g.y", 20);
        manager.SaveChanged(["g.x", "g.y"]);

        _Set(manager, "g.x", 1);
        manager.SaveChanged(["g.x"]);
        JsonObject afterFirst = folder.ReadJson("g.json");
        _Set(manager, "g.y", 2);
        manager.SaveChanged(["g.y"]);

        await Assert.That(afterFirst.ContainsKey("g.x")).IsFalse();
        await Assert.That(afterFirst["g.y"]?.GetValue<long>()).IsEqualTo(20L);
        await Assert.That(File.Exists(folder.Combine("g.json"))).IsFalse();
        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.tmp").Any()).IsFalse();
    }

    [Test]
    public async Task AnUnknownNameThrowsArgumentExceptionAndWritesNothing()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);

        await Assert.That(() => manager.SaveChanged(["nope"])).Throws<ArgumentException>();
        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.json").Any()).IsFalse();
    }

    [Test]
    public async Task WithoutAStoragePathBothMembersThrowPersistenceErrors()
    {
        using SettingsManager manager = new();
        manager.RegisterSetting(Setting.I64("g.x", "X", "g", 1));

        await Assert.That(() => manager.SaveChanged(["g.x"])).Throws<PersistenceSettingsException>();
        await Assert.That(() => manager.Unset("g.x")).Throws<PersistenceSettingsException>();
    }

    #endregion

    #region Unset

    [Test]
    public async Task UnsetReturnsASettingToItsDefaultAndRemovesTheKey()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.x", 10);
        _Set(manager, "g.y", 20);
        manager.SaveChanged(["g.x", "g.y"]);
        List<IReadOnlyList<string>> applied = [];
        manager.SettingsApplied += (_, e) => applied.Add(e.ChangedSettings);

        bool result = manager.Unset("g.x");

        await Assert.That(result).IsTrue();
        await Assert.That(manager.GetI64Setting("g.x")).IsEqualTo(1L);
        await Assert.That(manager.GetSetting("g.x")?.PendingValue).IsEqualTo(SettingValue.I64(1));
        await Assert.That(applied.Count).IsEqualTo(1);
        await Assert.That(applied[0]).IsEquivalentTo(["g.x"], CollectionOrdering.Matching);
        JsonObject group = folder.ReadJson("g.json");
        await Assert.That(group.ContainsKey("g.x")).IsFalse();
        await Assert.That(group.ContainsKey("g.y")).IsTrue();
    }

    [Test]
    public async Task UnsetOfAnUnchangedSettingRaisesNoAppliedEvent()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        int raised = 0;
        manager.SettingsApplied += (_, _) => raised++;

        bool result = manager.Unset("g.x");

        await Assert.That(result).IsTrue();
        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task UnsetOfAnOrphanDropsItFromMemoryAndFile()
    {
        using SettingsTempFolder folder = new();
        folder.Write("plugin.json", """{"plugin.x": 5, "plugin.y": 6}""");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        manager.Load();

        bool result = manager.Unset("plugin.x");

        await Assert.That(result).IsTrue();
        await Assert.That(manager.Orphans.Select(static o => o.Name).ToArray()).IsEquivalentTo(["plugin.y"], CollectionOrdering.Matching);
        await Assert.That(folder.ReadJson("plugin.json").ContainsKey("plugin.x")).IsFalse();
    }

    [Test]
    public async Task UnsetOfAnUnstoredSettingCreatesNoLockOrManifest()
    {
        using SettingsTempFolder folder = new();
        folder.Write("other.json", """{"other.x": 1}""");
        using SettingsManager manager = _Manager(folder.Path);
        string[] before = [.. Directory.EnumerateFileSystemEntries(folder.Path).Select(static path => Path.GetFileName(path))];

        bool result = manager.Unset("g.x");

        await Assert.That(result).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Select(static path => Path.GetFileName(path)).ToArray())
            .IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnsetOfAnUnknownNameReturnsFalseAndWritesNothing()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);

        bool result = manager.Unset("nope");

        await Assert.That(result).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Any()).IsFalse();
    }

    #endregion

    #region Lock

    [Test]
    public async Task EightManagersOnEightThreadsKeepAllTheirKeys()
    {
        using SettingsTempFolder folder = new();
        const int threads = 8;
        const int rounds = 10;
        using Barrier barrier = new(threads);
        Task[] tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int index = t;
            tasks[t] = Task.Run(() =>
            {
                string name = "g.k" + index.ToString(CultureInfo.InvariantCulture);
                using SettingsManager manager = new(folder.Path, TestLayout.Default);
                manager.RegisterSetting(Setting.I64(name, "Key", "g", 0));
                barrier.SignalAndWait();
                for (int round = 1; round <= rounds; round++)
                {
                    _Set(manager, name, round);

                    // The lock polls without fairness, so on a loaded machine a thread can run out of its two-second budget; the
                    // documented answer is "try again". This test proves that no key is lost, not how long the lock takes.
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            manager.SaveChanged([name]);
                            break;
                        }
                        catch (PersistenceSettingsException) when (attempt < 10)
                        {
                        }
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        JsonObject group = folder.ReadJson("g.json");
        await Assert.That(group.Count).IsEqualTo(threads);
        for (int t = 0; t < threads; t++)
        {
            await Assert.That(group["g.k" + t.ToString(CultureInfo.InvariantCulture)]?.GetValue<long>()).IsEqualTo((long)rounds);
        }

        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.tmp").Any()).IsFalse();
    }

    // The wait is the smallest the limit allows (100 ms), so three refusals take a third of a second: slower than the 100 ms of a unit
    // test, hence the marker.
    [Test]
    [Category("Slow")]
    public async Task AHeldLockGivesAPersistenceErrorAfterTheLockWaitAndChangesNothing()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path, new SettingsLimitValues { FolderLockTimeout = LimitValue.Of(100) });
        _Set(manager, "g.x", 10);
        manager.SaveChanged(["g.x"]);
        string before = File.ReadAllText(folder.Combine("g.json"));
        _Set(manager, "g.x", 11);

        using (FileStream held = new(folder.Combine(".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            long start = Stopwatch.GetTimestamp();
            PersistenceSettingsException save = Assert.Throws<PersistenceSettingsException>(() => manager.SaveChanged(["g.x"]));
            PersistenceSettingsException wholeSave = Assert.Throws<PersistenceSettingsException>(manager.Save);
            PersistenceSettingsException unset = Assert.Throws<PersistenceSettingsException>(() => manager.Unset("g.x"));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            await Assert.That(save.Message).IsEqualTo(
                Invariant($"The settings folder '{folder.Path}' is in use by another process and was not free in time. ")
                + "Limit: 100 ms (setting settings.folderLockTimeout). "
                + "Change the setting settings.folderLockTimeout, or the value the application passes when it creates the settings manager.");
            await Assert.That(wholeSave.Message).IsEqualTo(save.Message);
            await Assert.That(unset.Message).IsEqualTo(save.Message);

            // Three refusals take three waits of 100 ms each; the wait must hold and must not run to the old two seconds.
            await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(5));
            await Assert.That(held.CanWrite).IsTrue();
        }

        await Assert.That(File.ReadAllText(folder.Combine("g.json"))).IsEqualTo(before);
        await Assert.That(manager.GetI64Setting("g.x")).IsEqualTo(11L);
    }

    [Test]
    public async Task LoadTakesNoLock()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", """{"g.x": 4}""");
        using SettingsManager manager = _Manager(folder.Path);

        using FileStream held = new(folder.Combine(".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();

        await Assert.That(warnings).IsEmpty();
        await Assert.That(manager.GetI64Setting("g.x")).IsEqualTo(4L);
    }

    #endregion

    #region File that cannot be merged

    [Test]
    public async Task ARepeatedKeyInTheFileGivesAPersistenceErrorFromSaveChangedAndUnsetAndLeavesTheFileAsItIs()
    {
        using SettingsTempFolder folder = new();
        const string content = "{\"g.x\":1,\"g.x\":2}";
        folder.Write("g.json", content);
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.y", 5);

        PersistenceSettingsException save = Assert.Throws<PersistenceSettingsException>(() => manager.SaveChanged(["g.y"]));
        PersistenceSettingsException unset = Assert.Throws<PersistenceSettingsException>(() => manager.Unset("g.x"));

        await Assert.That(save.Message).Contains("g.json");
        await Assert.That(unset.Message).Contains("g.json");
        await Assert.That(File.ReadAllText(folder.Combine("g.json"))).IsEqualTo(content);
    }

    [Test]
    public async Task AFileDeeperThanTheLimitNamesTheDepthLimitWhenAMergeRefusesIt()
    {
        using SettingsTempFolder folder = new();
        folder.Write("g.json", "{\"a\":{\"a\":{\"a\":{\"a\":{\"a\":1}}}}}");
        using SettingsManager manager = _Manager(folder.Path, new SettingsLimitValues { MaxJsonDepth = LimitValue.Of(4) });
        _Set(manager, "g.y", 5);

        PersistenceSettingsException save = Assert.Throws<PersistenceSettingsException>(() => manager.SaveChanged(["g.y"]));

        await Assert.That(save.Message).Contains("settings.maxJsonDepth");
    }

    #endregion

    #region Lock contention

    [Test]
    [Arguments(-2147024864)]
    [Arguments(-2147024863)]
    public async Task AWindowsSharingOrLockViolationIsContention(int hresult)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.That(_IsContention(new IOException("held", hresult))).IsTrue();
    }

    [Test]
    [Arguments(11)]
    [Arguments(35)]
    public async Task AnEwouldblockErrorNumberIsContentionOnUnix(int errorNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.That(_IsContention(new IOException("held", errorNumber))).IsTrue();
    }

    [Test]
    [Arguments(-2147024784)]
    [Arguments(-2147024690)]
    [Arguments(5)]
    public async Task AFullDiskATooLongPathAndAnOtherErrorAreNotContention(int hresult)
    {
        await Assert.That(_IsContention(new IOException("not held", hresult))).IsFalse();
    }

    /// <summary>Calls the private contention test of the folder lock.</summary>
    /// <param name="exception">The error of an open of the lock file.</param>
    /// <returns>Whether the library takes the error for a holder of the lock.</returns>
    private static bool _IsContention(IOException exception)
    {
        Type lockType = typeof(SettingsManager).GetNestedType("FolderLock", BindingFlags.NonPublic)!;
        MethodInfo method = lockType.GetMethod("_IsContention", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [exception])!;
    }

    [Test]
    [Category("Slow")]
    public async Task ASaveWaitsWithoutEndForAHolderWhenTheLockTimeoutIsOffAndFinishesWhenTheHolderLetsGo()
    {
        // Slow by design: the save must wait through several pauses of the back-off (1, 2, 4, … 64 ms) before the holder releases.
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path, new SettingsLimitValues { FolderLockTimeout = LimitValue.Off });
        _Set(manager, "g.x", 9);
        FileStream held = new(folder.Combine(".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task save;
        try
        {
            save = Task.Run(() => manager.SaveChanged(["g.x"]));
            await Task.Delay(300);
            await Assert.That(save.IsCompleted).IsFalse();
        }
        finally
        {
            held.Dispose();
        }

        await save.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(folder.ReadJson("g.json")["g.x"]!.GetValue<long>()).IsEqualTo(9L);
    }

    #endregion

    #region Asynchronous merge

    [Test]
    public async Task SaveChangedAsyncWritesTheKeysLikeTheSynchronousMember()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.x", 12);

        await manager.SaveChangedAsync(["g.x"]);

        await Assert.That(folder.ReadJson("g.json")["g.x"]!.GetValue<long>()).IsEqualTo(12L);
    }

    [Test]
    public async Task SaveChangedAsyncThrowsForAnUnknownNameBeforeAnyFileIsTouched()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);

        await Assert.That(async () => await manager.SaveChangedAsync(["nope.x"])).Throws<ArgumentException>();

        await Assert.That(Directory.GetFiles(folder.Path).Length).IsEqualTo(0);
    }

    [Test]
    [Category("Slow")]
    public async Task SaveChangedAsyncStopsWhileWaitingForAHeldLockWhenTheTokenIsCanceled()
    {
        // Slow by design: the wait has to be running, in the back-off loop, when the token is canceled.
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path, new SettingsLimitValues { FolderLockTimeout = LimitValue.Off });
        _Set(manager, "g.x", 9);
        using FileStream held = new(folder.Combine(".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource cancellation = new();

        Task waiting = manager.SaveChangedAsync(["g.x"], cancellation.Token).AsTask();
        await Task.Delay(150);
        bool stillWaiting = !waiting.IsCompleted;
        await cancellation.CancelAsync();

        await Assert.That(stillWaiting).IsTrue();
        await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(folder.Combine("g.json"))).IsFalse();
    }

    [Test]
    [Category("Slow")]
    public async Task SaveChangedAsyncNamesTheLimitWhenTheHolderDoesNotLetGoInTime()
    {
        // Slow by design: the timeout of 100 ms has to pass.
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path, new SettingsLimitValues { FolderLockTimeout = LimitValue.Of(100) });
        _Set(manager, "g.x", 9);
        using FileStream held = new(folder.Combine(".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        PersistenceSettingsException failure = (await Assert
            .That(async () => await manager.SaveChangedAsync(["g.x"]))
            .Throws<PersistenceSettingsException>())!;

        await Assert.That(failure.Message).Contains("settings.folderLockTimeout");
    }

    #endregion

    #region Format of a written file

    [Test]
    public async Task AWrittenGroupFileIsIndentedUtf8WithoutAByteOrderMark()
    {
        using SettingsTempFolder folder = new();
        using SettingsManager manager = _Manager(folder.Path);
        _Set(manager, "g.x", 12);

        manager.SaveChanged(["g.x"]);

        byte[] bytes = File.ReadAllBytes(folder.Combine("g.json"));
        string text = Encoding.UTF8.GetString(bytes);
        await Assert.That(bytes[0]).IsEqualTo((byte)'{');
        await Assert.That(text).Contains("\"g.x\": 12");
        await Assert.That(text.Contains('\n')).IsTrue();
        await Assert.That(Directory.GetFiles(folder.Path, "*.tmp").Length).IsEqualTo(0);
    }

    #endregion
}
