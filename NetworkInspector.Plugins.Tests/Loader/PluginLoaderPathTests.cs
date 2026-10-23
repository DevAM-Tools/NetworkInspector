// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Configured paths: nested folders, file entries, missing entries, overlapping entries, and no entries at all.</summary>
internal sealed class PluginLoaderPathTests
{
    #region Constants

    private static readonly string[] _BuiltInNames = ["A", "B", "C", "D"];

    #endregion

    #region Folders and files

    [Test]
    public async Task APluginInANestedSubfolderLoadsAndKeepsItsNestedPath()
    {
        using LoaderHarness harness = new("paths-nested");
        string root = harness.Folder.FilePath("root");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, Path.Combine(root, "x", "y", "z"));

        PluginLoadReport report = harness.Load([root]);

        PluginRecord sample = report.Plugins.Single(static record => record.Name == "Sample.Protocol");
        await Assert.That(sample.State).IsEqualTo(PluginLoadState.Loaded);
        string expectedSuffix = Path.Combine("x", "y", "z", "Sample.Protocol.dll");
        await Assert.That(sample.Path.EndsWith(expectedSuffix, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AFilePathLoadsTheAssemblyNextToAFolderEntry()
    {
        using LoaderHarness harness = new("paths-file");
        string root = harness.Folder.FilePath("root");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, root);
        string testPlugins = TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, harness.Folder.FilePath("elsewhere"));

        PluginLoadReport report = harness.Load([root, testPlugins]);

        await Assert.That(report.Plugins.Single(static record => record.Name == "Test.Healthy").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins.Single(static record => record.Name == "Sample.Protocol").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AnUpperCaseExtensionIsAccepted()
    {
        using LoaderHarness harness = new("paths-upper");
        string root = harness.Folder.FilePath("root");
        Directory.CreateDirectory(root);
        string upper = Path.Combine(root, "Sample.Protocol.DLL");
        File.Copy(TestPluginFiles.Sample, upper);

        PluginLoadReport asFile = harness.Load([upper]);
        PluginLoadReport asFolder = harness.Load([root]);

        await Assert.That(asFile.Plugins.Single().Name).IsEqualTo("Sample.Protocol");
        await Assert.That(asFolder.Plugins.Single().Name).IsEqualTo("Sample.Protocol");
    }

    #endregion

    #region Missing and overlapping entries

    [Test]
    public async Task AMissingFolderBecomesAFailedRecordAndTheRestStillLoads()
    {
        using LoaderHarness harness = new("paths-missing");
        string existing = harness.Folder.FilePath("existing");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, existing);
        string missing = harness.Folder.FilePath("missing");

        PluginLoadReport report = harness.Load([missing, existing]);

        PluginRecord failed = report.Plugins.Single(static record => record.State == PluginLoadState.Failed);
        await Assert.That(failed.Path).IsEqualTo(Path.GetFullPath(missing));
        await Assert.That(failed.Reason).IsEqualTo("Configured plugin path not found.");
        await Assert.That(report.Plugins.Single(static record => record.Name == "Sample.Protocol").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AMissingFileBehavesLikeAMissingFolder()
    {
        using LoaderHarness harness = new("paths-missing-file");
        string missing = harness.Folder.FilePath("nope.dll");

        PluginLoadReport report = harness.Load([missing]);

        PluginRecord failed = report.Plugins.Single();
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("Configured plugin path not found.");
        await Assert.That(failed.Name).IsEqualTo("nope.dll");
    }

    [Test]
    public async Task OverlappingEntriesCountOneFileOnce()
    {
        using LoaderHarness harness = new("paths-overlap");
        string root = harness.Folder.FilePath("root");
        string sample = TestPluginFiles.CopyInto(TestPluginFiles.Sample, Path.Combine(root, "sub"));

        // Folder, subfolder, the file itself, and the folder again in a different spelling.
        PluginLoadReport report = harness.Load([root, Path.Combine(root, "sub"), sample, root + Path.DirectorySeparatorChar]);

        await Assert.That(report.Prescan.CandidateFiles).IsEqualTo(1);
        await Assert.That(report.Plugins.Count(static record => record.Name == "Sample.Protocol")).IsEqualTo(1);
        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Loaded)).IsTrue();
    }

    #endregion

    #region No entries

    [Test]
    public async Task NoPathsLoadsOnlyTheBuiltInsAndWritesNoCache()
    {
        using LoaderHarness harness = new("paths-empty");
        List<string> calls = [];
        IPlugin[] builtIns = [.. _BuiltInNames.Select(name => new LifePlugin(name, calls))];

        PluginLoadReport report = harness.Load([], builtIns);

        await Assert.That(report.Plugins.Count).IsEqualTo(4);
        await Assert.That(report.Plugins.All(static record => record.BuiltIn && record.State == PluginLoadState.Loaded)).IsTrue();
        await Assert.That(report.Prescan.CandidateFiles).IsEqualTo(0);
        await Assert.That(report.Prescan.InspectedFiles).IsEqualTo(0);
        await Assert.That(report.Prescan.CachedFiles).IsEqualTo(0);
        await Assert.That(report.Prescan.PluginAssemblies).IsEqualTo(0);
        await Assert.That(report.Prescan.InspectionTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(File.Exists(harness.CacheFile)).IsFalse();
    }

    [Test]
    public async Task AnEmptyFolderHasNoCandidates()
    {
        using LoaderHarness harness = new("paths-empty-folder");
        string empty = harness.Folder.FilePath("empty");
        Directory.CreateDirectory(empty);

        PluginLoadReport report = harness.Load([empty]);

        await Assert.That(report.Prescan.CandidateFiles).IsEqualTo(0);
        await Assert.That(report.Plugins).IsEmpty();
    }

    #endregion
}
