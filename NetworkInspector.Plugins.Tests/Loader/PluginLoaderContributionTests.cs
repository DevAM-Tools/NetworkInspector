// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>What a loaded plugin's record says: identity, path, time, instance, and contributions by kind.</summary>
internal sealed class PluginLoaderContributionTests
{
    #region Constants

    private static readonly string[] _ProtocolNames = ["sample", "sample.post"];

    #endregion

    #region Record

    [Test]
    public async Task TheSampleRecordNamesItsContributionsByKind()
    {
        using LoaderHarness harness = new("contributions");
        string folder = harness.Folder.FilePath("plugins");
        string path = TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);

        // The standard protocols come first, as they do in every application; the sample then joins their tables.
        ScriptedPlugin standard = new("Test.Standard", static builder => builder.AddStackContribution(static stack => stack.RegisterStandardProtocols()));
        PluginLoadReport report = harness.Load([folder], [standard]);

        PluginRecord sample = report.Plugins.Single(static record => record.Name == "Sample.Protocol");
        await Assert.That(sample.State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(sample.Version).IsEqualTo("0.1.0");
        await Assert.That(sample.License).IsEqualTo("MIT");
        await Assert.That(sample.BuiltIn).IsFalse();
        await Assert.That(sample.Tier).IsEqualTo(PluginTier.Core);
        await Assert.That(sample.LoadTime).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(sample.Path).IsEqualTo(Path.GetFullPath(path));
        await Assert.That(sample.Instance).IsNotNull();

        await Assert.That(_Names(sample, ContributionKind.Protocol)).IsEquivalentTo(_ProtocolNames);
        await Assert.That(_Names(sample, ContributionKind.Heuristic)).IsEquivalentTo(["sample.heuristic"]);
        await Assert.That(_Names(sample, ContributionKind.PostParser)).IsEquivalentTo(["sample.post"]);
        await Assert.That(_Names(sample, ContributionKind.Source)).IsEquivalentTo(["SampleGen"]);
        await Assert.That(_Names(sample, ContributionKind.Exporter)).IsEquivalentTo(["SampleText"]);
        await Assert.That(_Names(sample, ContributionKind.Setting)).IsEquivalentTo(["sample.greeting"]);
        await Assert.That(sample.Contributions.Keys.Count).IsEqualTo(6);
        await Assert.That(sample.Contributions.Values.SelectMany(static entries => entries).All(static entry => entry.Description.Length > 0)).IsTrue();
    }

    [Test]
    public async Task AFailedPluginHasNoInstanceAndNoContributions()
    {
        using LoaderHarness harness = new("contributions-failed");
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folder);

        PluginLoadReport report = harness.Load([folder]);

        PluginRecord failed = report.Plugins.Single(static record => record.Name == "Test.ConfigureThrows");
        await Assert.That(failed.Instance).IsNull();
        await Assert.That(failed.Contributions).IsEmpty();
        await Assert.That(failed.Reason).IsNotNull();
    }

    #endregion

    #region Helpers

    private static string[] _Names(PluginRecord record, ContributionKind kind) =>
        record.Contributions.TryGetValue(kind, out IReadOnlyList<ContributionEntry>? entries)
            ? [.. entries.Select(static entry => entry.Name)]
            : [];

    #endregion
}
