// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces.Tests;

/// <summary>The cache protocol of the trace kit: reuse, new files for new parameters, parallel requests, and bad arguments.</summary>
internal sealed class TraceKitTests
{
    #region Cache

    [Test]
    public async Task TheSameRecipeReusesTheFileAndNewParametersMakeANewOne()
    {
        using TempFolder folder = new("kit-cache");
        TraceKit kit = new(folder.Path);

        TraceFile first = await kit.GetAsync(new MixedEthernetRecipe(200, 1), CancellationToken.None);
        DateTime writtenAt = File.GetLastWriteTimeUtc(first.Path);
        long length = new FileInfo(first.Path).Length;
        TraceFile second = await kit.GetAsync(new MixedEthernetRecipe(200, 1), CancellationToken.None);
        TraceFile other = await kit.GetAsync(new MixedEthernetRecipe(200, 2), CancellationToken.None);

        await Assert.That(second.Path).IsEqualTo(first.Path);
        await Assert.That(File.GetLastWriteTimeUtc(second.Path)).IsEqualTo(writtenAt);
        await Assert.That(new FileInfo(second.Path).Length).IsEqualTo(length);
        await Assert.That(other.Path).IsNotEqualTo(first.Path);
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(first.Path), "^mixed-ethernet-[0-9a-f]{16}\\.pcapng$")).IsTrue();
        await Assert.That(first.FrameCount).IsEqualTo(200);
        using Stack stack = TraceReading.BuildStack(withSample: false);
        await Assert.That(TraceReading.ReadFrames(first.Path, stack).Count).IsEqualTo(200);
    }

    [Test]
    public async Task ANewVersionOrANewParameterGetsAnotherKey()
    {
        string key = TraceCache.KeyOf(new OutOfOrderRecipe(100));

        await Assert.That(TraceCache.KeyOf(new OutOfOrderRecipe(100))).IsEqualTo(key);
        await Assert.That(TraceCache.KeyOf(new OutOfOrderRecipe(101))).IsNotEqualTo(key);
        await Assert.That(key.Length).IsEqualTo(16);
    }

    [Test]
    public async Task ARecipeWithSideFilesKeepsThemNextToTheTrace()
    {
        using TempFolder folder = new("kit-side");
        TraceKit kit = new(folder.Path);

        TraceFile trace = await kit.GetAsync(new CanSignalsRecipe(10), CancellationToken.None);

        string config = trace.ProfileSettings[CanSignalTrace.ConfigFileSetting];
        await Assert.That(File.Exists(config)).IsTrue();
        await Assert.That(Path.GetDirectoryName(config)).IsEqualTo(Path.ChangeExtension(trace.Path, null) + ".files");
        TraceFile again = await kit.GetAsync(new CanSignalsRecipe(10), CancellationToken.None);
        await Assert.That(again.ProfileSettings[CanSignalTrace.ConfigFileSetting]).IsEqualTo(config);
    }

    #endregion

    #region Parallel

    [Test]
    public async Task EightParallelRequestsOnTwoKitsProduceOneValidFile()
    {
        using TempFolder folder = new("kit-parallel");
        TraceKit[] kits = [new(folder.Path), new(folder.Path)];

        // Two kit instances with four tasks each: the lock file must work across instances, not only inside one.
        Task<TraceFile>[] tasks = [.. Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
            await kits[index % 2].GetAsync(new MixedEthernetRecipe(300, 7), CancellationToken.None)))];
        TraceFile[] results = await Task.WhenAll(tasks);

        await Assert.That(results.Select(static result => result.Path).Distinct().Count()).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.pcapng").Count()).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFiles(folder.Path, "*.tmp").Any()).IsFalse();
        using Stack stack = TraceReading.BuildStack(withSample: false);
        await Assert.That(TraceReading.ReadFrames(results[0].Path, stack).Count).IsEqualTo(300);
    }

    #endregion

    #region Arguments

    [Test]
    public async Task AHeldLockIsWaitedForUntilTheTokenIsCancelledAndThenReleased()
    {
        using TempFolder folder = new("kit-lock");
        TraceKit kit = new(folder.Path);
        OutOfOrderRecipe recipe = new(10);
        string lockPath = Path.Combine(folder.Path, recipe.Name + "-" + TraceCache.KeyOf(recipe) + ".lock");
        bool cancelled = false;

        // Another writer (here: a handle on the lock file) holds the lock; the call waits and honors its token.
        using (FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));
            try
            {
                await kit.GetAsync(recipe, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            await Assert.That(Directory.EnumerateFiles(folder.Path, "*.pcapng").Any()).IsFalse();
        }

        TraceFile trace = await kit.GetAsync(recipe, CancellationToken.None);

        await Assert.That(cancelled).IsTrue();
        await Assert.That(File.Exists(trace.Path)).IsTrue();
    }

    [Test]
    public async Task ABadFolderOrANullRecipeIsRefused()
    {
        using TempFolder folder = new("kit-args");
        TraceKit kit = new(folder.Path);

        await Assert.That(() => new TraceKit(string.Empty)).Throws<ArgumentException>();
        await Assert.That(() => new TraceKit("relative/folder")).Throws<ArgumentException>();
        await Assert.That(async () => await kit.GetAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ACancelledTokenLeavesNoTraceFile()
    {
        using TempFolder folder = new("kit-cancel");
        TraceKit kit = new(folder.Path);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.That(async () => await kit.GetAsync(new MixedEthernetRecipe(100, 1), cancelled.Token)).Throws<OperationCanceledException>();

        await Assert.That(Directory.Exists(folder.Path) && Directory.EnumerateFiles(folder.Path, "*.pcapng").Any()).IsFalse();
    }

    [Test]
    public async Task AFailedWriteLeavesNothingBehindAndTheNextCallWritesAgain()
    {
        using TempFolder folder = new("kit-failed");
        TraceKit kit = new(folder.Path);

        await Assert.That(async () => await kit.GetAsync(new FailingRecipe(), CancellationToken.None)).Throws<InvalidOperationException>();

        string[] files = [.. Directory.EnumerateFiles(folder.Path).Where(static file => !file.EndsWith(".lock", StringComparison.Ordinal))];
        await Assert.That(files.Length).IsEqualTo(0);
        await Assert.That(Directory.EnumerateDirectories(folder.Path).Any()).IsFalse();
    }

    #endregion

    #region Helpers

    /// <summary>A recipe that writes half a trace and a side file, then fails.</summary>
    private sealed class FailingRecipe : TraceRecipe
    {
        public override string Name => "failing";

        public override int Version => 1;

        public override string Extension => ".pcapng";

        public override int FrameCount => 1;

        public override string CanonicalParameters => "none";

        protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
            CancellationToken cancellationToken)
        {
            File.WriteAllText(tracePath, "half");
            File.WriteAllText(Path.Combine(sideFileDirectory, "side.txt"), "half");
            throw new InvalidOperationException("the writer failed");
        }
    }

    #endregion
}
