// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The pre-scan cache: hits, changes, damaged files, and concurrent writers.</summary>
internal sealed class PrescanCacheTests
{
    #region Constants

    private const string _VersionSnippet = "\"version\":1";

    #endregion

    #region Helpers

    // 200 runtime DLLs plus the sample in one folder of the harness.
    private static string _Prepare(LoaderHarness harness, RuntimeDllFolder runtime)
    {
        string folder = harness.Folder.FilePath("plugins");
        runtime.CopyTo(folder);
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);
        return folder;
    }

    // A folder with the sample only: fast cases that do not need many files.
    private static string _PrepareSmall(LoaderHarness harness)
    {
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);
        return folder;
    }

    #endregion

    #region Locked files

    [Test]
    public async Task AFileThatWasLockedDuringTheFirstScanIsInspectedAgainAndFoundTheSecondTime()
    {
        using LoaderHarness harness = new("cache-locked");
        string folder = _PrepareSmall(harness);
        string dll = Path.Combine(folder, "Sample.Protocol.dll");

        PluginLoadReport first;
        using (FileStream locked = new(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = harness.Load([folder]);
        }

        PluginLoadReport second = harness.Load([folder]);

        // The first scan could not read the file; that result must not be remembered as "not a plugin".
        await Assert.That(first.Prescan.InspectedFiles).IsEqualTo(1);
        await Assert.That(first.Prescan.PluginAssemblies).IsEqualTo(0);
        await Assert.That(second.Prescan.InspectedFiles).IsEqualTo(1);
        await Assert.That(second.Prescan.CachedFiles).IsEqualTo(0);
        await Assert.That(second.Prescan.PluginAssemblies).IsEqualTo(1);
    }

    [Test]
    public async Task ADamagedImageStaysRememberedAsNotAPlugin()
    {
        using LoaderHarness harness = new("cache-damaged");
        string folder = harness.Folder.FilePath("plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Broken.dll"), [0x4D, 0x5A, 0x00, 0x01, 0x02]);

        PluginLoadReport first = harness.Load([folder]);
        PluginLoadReport second = harness.Load([folder]);

        await Assert.That(first.Prescan.InspectedFiles).IsEqualTo(1);
        await Assert.That(second.Prescan.InspectedFiles).IsEqualTo(0);
        await Assert.That(second.Prescan.CachedFiles).IsEqualTo(1);
    }

    #endregion

    #region Hit and change

    [Test]
    [ClassDataSource<RuntimeDllFolder>(Shared = SharedType.PerTestSession)]
    [Category("Slow")]
    public async Task AnUnchangedSecondScanInspectsNothingAndWritesNothing(RuntimeDllFolder runtime)
    {
        using LoaderHarness harness = new("cache-hit");
        string folder = _Prepare(harness, runtime);
        PluginLoadReport first = harness.Load([folder]);
        DateTime written = File.GetLastWriteTimeUtc(harness.CacheFile);

        PluginLoadReport second = harness.Load([folder]);

        await Assert.That(first.Prescan.InspectedFiles).IsEqualTo(RuntimeDllFolder.FileCount + 1);
        await Assert.That(second.Prescan.InspectedFiles).IsEqualTo(0);
        await Assert.That(second.Prescan.CachedFiles).IsEqualTo(RuntimeDllFolder.FileCount + 1);
        await Assert.That(second.Prescan.PluginAssemblies).IsEqualTo(1);
        await Assert.That(second.Plugins.Single().State).IsEqualTo(PluginLoadState.Loaded);

        // Nothing changed, so nothing is written: the file keeps its time.
        await Assert.That(File.GetLastWriteTimeUtc(harness.CacheFile)).IsEqualTo(written);
    }

    [Test]
    [ClassDataSource<RuntimeDllFolder>(Shared = SharedType.PerTestSession)]
    [Category("Slow")]
    public async Task AChangedLengthOrTimeIsInspectedAgain(RuntimeDllFolder runtime)
    {
        using LoaderHarness harness = new("cache-change");
        string folder = _Prepare(harness, runtime);
        harness.Load([folder]);
        string[] files =
        [
            .. Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories)
                .Where(static file => !file.EndsWith("Sample.Protocol.dll", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        // One file gets other content (another length); another keeps its bytes but gets a later write time.
        File.Copy(typeof(object).Assembly.Location, files[0], overwrite: true);
        File.SetLastWriteTimeUtc(files[1], File.GetLastWriteTimeUtc(files[1]).AddSeconds(2));
        PluginLoadReport changed = harness.Load([folder]);

        await Assert.That(changed.Prescan.InspectedFiles).IsEqualTo(2);
        await Assert.That(changed.Prescan.CachedFiles).IsEqualTo(RuntimeDllFolder.FileCount - 1);
    }

    [Test]
    [ClassDataSource<RuntimeDllFolder>(Shared = SharedType.PerTestSession)]
    [Category("Slow")]
    public async Task ADeletedFileDisappearsFromTheRewrittenCache(RuntimeDllFolder runtime)
    {
        using LoaderHarness harness = new("cache-delete");
        string folder = _Prepare(harness, runtime);
        harness.Load([folder]);
        string victim = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).First(static f => !f.EndsWith("Sample.Protocol.dll", StringComparison.Ordinal));
        string json = JsonSerializer.Serialize(victim);
        await Assert.That((await File.ReadAllTextAsync(harness.CacheFile)).Contains(json, StringComparison.Ordinal)).IsTrue();

        File.Delete(victim);
        harness.Load([folder]);

        await Assert.That((await File.ReadAllTextAsync(harness.CacheFile)).Contains(json, StringComparison.Ordinal)).IsFalse();
    }

    #endregion

    #region Damaged cache

    [Test]
    [Arguments("{not json")]
    [Arguments("[]")]
    [Arguments("{\"version\":999,\"entries\":[]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"path\":\"/x.dll\",\"length\":-1,\"lastWriteUtcTicks\":1,\"assemblyName\":null,\"assemblyVersion\":null,\"entries\":[]}]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"path\":\"/x.dll\",\"length\":1,\"lastWriteUtcTicks\":1,\"assemblyName\":\"x\",\"assemblyVersion\":\"1.0.0.0\",\"entries\":[{\"typeName\":\"X\",\"tier\":9,\"problem\":null}]}]}")]
    public async Task ADamagedCacheIsIgnoredAndRebuilt(string content)
    {
        using LoaderHarness harness = new("cache-corrupt");
        string folder = _PrepareSmall(harness);
        await File.WriteAllTextAsync(harness.CacheFile, content);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Prescan.InspectedFiles).IsEqualTo(1);
        await Assert.That(report.Plugins.Single().State).IsEqualTo(PluginLoadState.Loaded);
        string rebuilt = await File.ReadAllTextAsync(harness.CacheFile);
        await Assert.That(rebuilt.Contains(_VersionSnippet, StringComparison.Ordinal)).IsTrue();
        using JsonDocument document = JsonDocument.Parse(rebuilt);
        await Assert.That(document.RootElement.GetProperty("entries").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task EightMebibytesOfRandomBytesAreRebuiltWithoutALimit()
    {
        using LoaderHarness harness = new("cache-random");
        string folder = _PrepareSmall(harness);
        await File.WriteAllBytesAsync(harness.CacheFile, System.Security.Cryptography.RandomNumberGenerator.GetBytes(8 * 1024 * 1024));

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Prescan.InspectedFiles).IsEqualTo(1);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(harness.CacheFile));
        await Assert.That(document.RootElement.GetProperty("version").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ADirectoryInPlaceOfTheCacheFileDoesNotStopLoading()
    {
        using LoaderHarness harness = new("cache-directory");
        string folder = _PrepareSmall(harness);
        Directory.CreateDirectory(harness.CacheFile);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins.Single().State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(Directory.Exists(harness.CacheFile)).IsTrue();
    }

    #endregion

    #region Concurrent writers

    [Test]
    [Category("Slow")]
    public async Task FourLoadersWritingOneCacheFileLeaveItValid()
    {
        using TempFolder shared = new("cache-concurrent");
        string cacheFile = shared.FilePath("scan-cache.json");
        string[] folders = new string[4];
        for (int index = 0; index < folders.Length; index++)
        {
            folders[index] = shared.FilePath("plugins" + index.ToString(CultureInfo.InvariantCulture));
            TestPluginFiles.CopyInto(TestPluginFiles.Sample, folders[index]);
            TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folders[index]);
        }

        using Barrier start = new(folders.Length);
        Task<PluginLoadReport>[] loads = [.. folders.Select(folder => Task.Factory.StartNew(() =>
        {
            using PluginRegistry registry = new();
            PluginLoader loader = new(new PluginLoaderOptions(cacheFile, []));
            start.SignalAndWait();
            return loader.Load(PluginTier.Core, [folder], registry, CancellationToken.None);
        }, TaskCreationOptions.LongRunning))];
        PluginLoadReport[] reports = await Task.WhenAll(loads);

        using JsonDocument cache = JsonDocument.Parse(await File.ReadAllTextAsync(cacheFile));
        await Assert.That(cache.RootElement.GetProperty("entries").GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(Directory.EnumerateFiles(shared.Path, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
        await Assert.That(reports.All(static report => report.Plugins.Any(static record => record.Name == "Sample.Protocol"))).IsTrue();

        // A fifth scan of any copy succeeds on the surviving cache.
        using PluginRegistry fifthRegistry = new();
        PluginLoadReport fifth = new PluginLoader(new PluginLoaderOptions(cacheFile, []))
            .Load(PluginTier.Core, [folders[0]], fifthRegistry, CancellationToken.None);
        await Assert.That(fifth.Plugins.Any(static record => record.Name == "Sample.Protocol" && record.State == PluginLoadState.Loaded)).IsTrue();
    }

    #endregion
}
