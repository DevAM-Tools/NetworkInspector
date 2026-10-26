// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Direct tests of the pre-scanner for inputs that no loader run produces: vanished files and path spellings.</summary>
internal sealed class AssemblyPrescannerTests
{
    #region Inspect

    [Test]
    public async Task InspectingAVanishedFileGivesAResultWithoutEntryTypes()
    {
        using TempFolder folder = new("prescanner-vanished");

        PrescanResult result = AssemblyPrescanner.Inspect(folder.FilePath("gone.dll"));

        await Assert.That(result.IsPluginAssembly).IsFalse();
        await Assert.That(result.Length).IsEqualTo(0);
        await Assert.That(result.AssemblyName).IsNull();
    }

    [Test]
    public async Task InspectingTheSampleFindsItsEntryTypeAndAssemblyIdentity()
    {
        PrescanResult result = AssemblyPrescanner.Inspect(TestPluginFiles.Sample);

        await Assert.That(result.IsPluginAssembly).IsTrue();
        await Assert.That(result.AssemblyName).IsEqualTo("Sample.Protocol");
        PrescanEntry entry = result.Entries.Single();
        await Assert.That(entry.TypeName).IsEqualTo("Sample.Protocol.SampleProtocolPlugin");
        await Assert.That(entry.Tier).IsEqualTo(PluginTier.Core);
        await Assert.That(entry.Problem).IsNull();
    }

    [Test]
    public async Task InspectStampsAnUnchangedFileWithItsOwnLengthAndTime()
    {
        using TempFolder folder = new("prescanner-stamp-same");
        string path = TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder.Path);
        FileInfo file = new(path);

        PrescanResult result = AssemblyPrescanner.Inspect(path);

        await Assert.That(result.Length).IsEqualTo(file.Length);
        await Assert.That(result.LastWriteUtcTicks).IsEqualTo(file.LastWriteTimeUtc.Ticks);
    }

    [Test]
    public async Task InspectStampsAFileThatChangesWhileItIsReadWithTheStateOfBeforeTheRead()
    {
        using TempFolder folder = new("prescanner-stamp-race");
        string path = TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder.Path);
        FileInfo before = new(path);
        long lengthBefore = before.Length;
        long ticksBefore = before.LastWriteTimeUtc.Ticks;

        // The file is replaced after its content was read: longer and newer, as a build or an installer would leave it.
        PrescanResult result = AssemblyPrescanner.Inspect(path, () =>
        {
            using (FileStream append = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                append.WriteByte(0);
            }

            File.SetLastWriteTimeUtc(path, new DateTime(ticksBefore, DateTimeKind.Utc).AddHours(1));
        });

        // The result must not match the new file: the next scan sees different stamps and reads the file again.
        FileInfo after = new(path);
        await Assert.That(after.Length).IsEqualTo(lengthBefore + 1);
        await Assert.That(result.Length).IsEqualTo(lengthBefore);
        await Assert.That(result.LastWriteUtcTicks).IsEqualTo(ticksBefore);
        await Assert.That(result.LastWriteUtcTicks).IsNotEqualTo(after.LastWriteTimeUtc.Ticks);
    }

    [Test]
    public async Task EntryTypesOfOneAssemblyAreOrderedByFullName()
    {
        PrescanResult result = AssemblyPrescanner.Inspect(TestPluginFiles.TestPlugins);

        string[] names = [.. result.Entries.Select(static entry => entry.TypeName)];
        string[] sorted = [.. names.Order(StringComparer.Ordinal)];
        await Assert.That(names.Length).IsGreaterThan(10);
        await Assert.That(names).IsEquivalentTo(sorted, CollectionOrdering.Matching);
    }

    #endregion

    #region Scan

    [Test]
    public async Task ScanReportsMissingPathsOnceEvenWhenListedTwice()
    {
        using TempFolder folder = new("prescanner-missing");
        AssemblyPrescanner scanner = new(new PrescanCache(folder.FilePath("cache.json")));
        string missing = folder.FilePath("nope");

        PrescanOutcome outcome = scanner.Scan([missing, missing], CancellationToken.None);

        await Assert.That(outcome.MissingPaths.Count).IsEqualTo(1);
        await Assert.That(outcome.Summary.CandidateFiles).IsEqualTo(0);
    }

    #endregion
}
