// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Plugins in the command line: one start and one stop per command, failures and timeouts as in the loader.</summary>
internal sealed class CliPluginLifecycleTests
{
    #region Helpers

    private const string _TestPluginsName = "NetworkInspector.Plugins.Tests.TestPlugins";

    private static string[] _LogLines(CliSandbox sandbox) =>
        File.ReadAllLines(Path.Combine(sandbox.Base, "logs", "ni.log"));

    #endregion

    #region Start and stop lines

    [Test]
    public async Task ACommandStartsAndStopsTheConfiguredPluginsOnce()
    {
        using CliSandbox sandbox = new();
        string sample = sandbox.CopyPlugin("Sample.Protocol", "sample");
        await sandbox.SetPluginPathsAsync(sample);

        CliResult first = await sandbox.RunAsync("export", "SampleGen:count=3", "-o", sandbox.Combine("out.json"));
        string[] afterFirst = _LogLines(sandbox);
        CliResult second = await sandbox.RunAsync("export", "SampleGen:count=3", "-o", sandbox.Combine("out.json"), "--overwrite");
        string[] afterSecond = _LogLines(sandbox);

        await Assert.That(first.Exit).IsEqualTo(0);
        await Assert.That(first.ErrorLines.Count(static line => line == "Info Sample.Protocol: started; greeting=Hello from Sample.Protocol")).IsEqualTo(1);
        await Assert.That(first.ErrorLines.Count(static line => line == "Info Sample.Protocol: stopped")).IsEqualTo(1);
        string[] started = [.. afterFirst.Where(static line => line.EndsWith("Info Sample.Protocol: started; greeting=Hello from Sample.Protocol", StringComparison.Ordinal))];
        string[] stopped = [.. afterFirst.Where(static line => line.EndsWith("Info Sample.Protocol: stopped", StringComparison.Ordinal))];
        await Assert.That(started.Length).IsEqualTo(1);
        await Assert.That(stopped.Length).IsEqualTo(1);
        await Assert.That(Array.IndexOf(afterFirst, started[0])).IsLessThan(Array.IndexOf(afterFirst, stopped[0]));

        // The next command appends its own start and stop.
        await Assert.That(second.Exit).IsEqualTo(0);
        await Assert.That(afterSecond.Length).IsEqualTo(afterFirst.Length + 2);
    }

    #endregion

    #region Failures

    [Test]
    public async Task AFailedStartIsOneErrorLineAndTheKindIsRefused()
    {
        using CliSandbox sandbox = new();
        string testPlugins = sandbox.CopyPlugin(_TestPluginsName, "test-plugins");
        await sandbox.SetPluginPathsAsync(testPlugins);

        CliResult list = await sandbox.RunAsync("plugins", "list");
        CliResult json = await sandbox.RunAsync("plugins", "list", "--json");
        CliResult export = await sandbox.RunAsync("export", "TestStartThrows:", "-o", sandbox.Combine("x.json"));

        await Assert.That(list.Exit).IsEqualTo(0);
        string[] mentions = [.. list.ErrorLines.Where(static line => line.Contains("Test.StartThrows", StringComparison.Ordinal))];
        await Assert.That(mentions).IsEquivalentTo(
            ["Error Test.StartThrows: StartAsync failed: InvalidOperationException: start boom"], CollectionOrdering.Matching);
        using JsonDocument document = JsonDocument.Parse(json.Out);
        JsonElement record = document.RootElement.GetProperty("plugins").EnumerateArray()
            .Single(static plugin => plugin.GetProperty("name").GetString() == "Test.StartThrows");
        await Assert.That(record.GetProperty("state").GetString()).IsEqualTo("Failed");
        await Assert.That(record.GetProperty("reason").GetString()).IsEqualTo("StartAsync failed: InvalidOperationException: start boom");
        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(export.ErrorMessages).IsEquivalentTo(
            ["Error: Source kind 'TestStartThrows' is not available: plugin Test.StartThrows failed to start."], CollectionOrdering.Matching);
    }

    [Test]
    [Category("Slow")]
    public async Task AHangingStartOrStopIsCutAtTheTimeoutAndShutdownContinues()
    {
        using CliSandbox sandbox = new();
        string sample = sandbox.CopyPlugin("Sample.Protocol", "sample");
        string testPlugins = sandbox.CopyPlugin(_TestPluginsName, "test-plugins");
        await sandbox.SetPluginPathsAsync(sample, testPlugins);

        // Start: the hanging plugin waits for its token; the lifecycle cancels it after one second.
        // The timeouts are set before the plugin is told to hang: every command of this test starts the plugins, so a command that
        // runs while the plugin hangs would wait for the default timeout.
        await sandbox.RunAsync("settings", "set", "plugins.startTimeoutSeconds", "1");
        await sandbox.RunAsync("settings", "set", "plugins.stopTimeoutSeconds", "1");
        await sandbox.RunAsync("settings", "set", "test.hanging.start", "true", "--scope", "application");
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        CliResult hangingStart = await sandbox.RunAsync("plugins", "list", "--json");
        TimeSpan startElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

        await Assert.That(hangingStart.Exit).IsEqualTo(0);
        await Assert.That(startElapsed).IsLessThan(TimeSpan.FromSeconds(10));
        string[] startErrors = [.. hangingStart.ErrorLines.Where(static line => line.StartsWith("Error Test.Hanging", StringComparison.Ordinal))];
        await Assert.That(startErrors.Length).IsEqualTo(1);
        await Assert.That(startErrors[0]).Contains("plugins.startTimeoutSeconds");
        using (JsonDocument document = JsonDocument.Parse(hangingStart.Out))
        {
            JsonElement record = document.RootElement.GetProperty("plugins").EnumerateArray()
                .Single(static plugin => plugin.GetProperty("name").GetString() == "Test.Hanging");
            await Assert.That(record.GetProperty("state").GetString()).IsEqualTo("Failed");
        }

        // Stop: the plugin starts, then waits for its token on stop; the other plugins still stop after it.
        await sandbox.RunAsync("settings", "set", "test.hanging.start", "false");
        await sandbox.RunAsync("settings", "set", "test.hanging.stop", "true");
        long stoppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        CliResult hangingStop = await sandbox.RunAsync("plugins", "list");
        TimeSpan stopElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(stoppedAt);

        await Assert.That(hangingStop.Exit).IsEqualTo(0);
        await Assert.That(stopElapsed).IsLessThan(TimeSpan.FromSeconds(10));
        string[] stopErrors = [.. hangingStop.ErrorLines.Where(static line => line.StartsWith("Error Test.Hanging", StringComparison.Ordinal))];
        await Assert.That(stopErrors.Length).IsEqualTo(1);
        await Assert.That(stopErrors[0]).Contains("plugins.stopTimeoutSeconds");

        // Plugins stop in reverse load order: the sample (configured first, loaded before) stops after the hanging one gave up.
        int errorIndex = Array.FindIndex(hangingStop.ErrorLines, static line => line.StartsWith("Error Test.Hanging", StringComparison.Ordinal));
        int sampleStopped = Array.FindIndex(hangingStop.ErrorLines, static line => line == "Info Sample.Protocol: stopped");
        await Assert.That(sampleStopped).IsGreaterThan(errorIndex);
    }

    #endregion

    #region Failed open

    private static PluginRecord _Record(string name) =>
        new(name, "1.0.0", PluginTier.Core, PluginLoadState.Loaded, null, string.Empty, false, string.Empty, TimeSpan.Zero, [],
            new Dictionary<ContributionKind, IReadOnlyList<ContributionEntry>>(), null);

    [Test]
    public async Task OnlyAttemptedKeepsThePluginsWithAContextInLoadOrder()
    {
        PluginRecord first = _Record("A");
        PluginRecord second = _Record("B");
        PluginRecord third = _Record("C");
        PluginLoadReport report = new(new PrescanSummary(0, 0, 0, 0, TimeSpan.Zero), [first, second, third]);

        PluginLoadReport result = CliPluginSession.OnlyAttempted(report, ["c", "A"]);

        await Assert.That(result.Plugins.Select(static record => record.Name).ToArray()).IsEquivalentTo(["A", "C"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OnlyAttemptedOfNoContextsKeepsNoPlugin()
    {
        PluginLoadReport report = new(new PrescanSummary(0, 0, 0, 0, TimeSpan.Zero), [_Record("A")]);

        PluginLoadReport result = CliPluginSession.OnlyAttempted(report, []);

        await Assert.That(result.Plugins).IsEmpty();
    }

    #endregion
}
