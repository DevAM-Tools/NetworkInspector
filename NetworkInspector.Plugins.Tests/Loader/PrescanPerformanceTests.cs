// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Cost of the cold pre-scan. Runs only on demand (category Perf): timing assertions do not belong in the default run.</summary>
internal sealed class PrescanPerformanceTests
{
    [Test]
    [Explicit]
    [Category("Perf")]
    [ClassDataSource<RuntimeDllFolder>(Shared = SharedType.PerTestSession)]
    public async Task TwoHundredAndOneColdCandidatesAreInspectedInAtMostOneSecond(RuntimeDllFolder runtime)
    {
        // One warm-up scan of a different copy pays the one-time JIT and file cache costs; the measured scan is cold for its files.
        using LoaderHarness warmUp = new("perf-warmup");
        string warmFolder = warmUp.Folder.FilePath("plugins");
        runtime.CopyTo(warmFolder);
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, warmFolder);
        warmUp.Load([warmFolder]);

        using LoaderHarness harness = new("perf");
        string folder = harness.Folder.FilePath("plugins");
        runtime.CopyTo(folder);
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);

        PluginLoadReport report = harness.Load([folder]);

        TestContext.Current?.OutputWriter.WriteLine(
            Invariant($"Pre-scan of {report.Prescan.CandidateFiles} cold candidates: {report.Prescan.InspectionTime.TotalMilliseconds:0.0} ms"));
        await Assert.That(report.Prescan.InspectionTime).IsLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }
}
