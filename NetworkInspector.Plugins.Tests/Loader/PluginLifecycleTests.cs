// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Start and stop of loaded plugins: order, failures, and timeouts. A plugin that throws or hangs is marked failed (start) or
/// logged (stop), its contributions are refused, and no other plugin is affected.
/// </summary>
internal sealed class PluginLifecycleTests
{
    #region Constants

    private static readonly string[] _OrderedCalls =
    [
        "A.Configure", "B.Configure", "C.Configure", "A.Start", "B.Start", "C.Start", "C.Stop", "B.Stop", "A.Stop",
    ];

    private static readonly string[] _StopCalls = ["Life.C.Stop", "Life.StopHangs.Stop", "Life.StopThrows.Stop", "Life.A.Stop"];

    private static readonly string[] _SingleCalls = ["A.Configure", "A.Start", "A.Stop"];

    #endregion

    #region Start

    [Test]
    public async Task AThrowingStartMarksThePluginFailedLogsOneErrorAndRefusesItsSource()
    {
        using LoaderHarness harness = new("life-start-throws");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.A", calls),
            new LifePlugin("Life.StartThrows", calls, StartBehavior.Throw, sourceKind: "LifeStartThrows"),
            new LifePlugin("Life.C", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);

        PluginRecord failed = started.Plugins.Single(static record => record.Name == "Life.StartThrows");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("StartAsync failed: InvalidOperationException: start boom");
        await Assert.That(failed.Instance).IsNull();
        IReadOnlyList<LogEntry> log = contexts.LogOf("Life.StartThrows");
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Level).IsEqualTo("Error");
        await Assert.That(harness.Registry.TryGetSource("LifeStartThrows", out _, out string? problem)).IsFalse();
        await Assert.That(problem).IsEqualTo("Source kind 'LifeStartThrows' is not available: plugin Life.StartThrows failed to start.");
        await Assert.That(contexts.LogOf("Life.A").Single().Text).IsEqualTo("started");
        await Assert.That(contexts.LogOf("Life.C").Single().Text).IsEqualTo("started");

        // The input report is unchanged; the result is a new instance.
        await Assert.That(loaded.Plugins.Single(static record => record.Name == "Life.StartThrows").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(started).IsNotSameReferenceAs(loaded);

        // A failed plugin is not stopped later.
        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromSeconds(5));
        await Assert.That(calls.Contains("Life.StartThrows.Stop")).IsFalse();
    }

    [Test]
    [Category("Slow")]
    public async Task AStartThatExceedsTheTimeoutIsFailedWithTheSettingNamedInTheError()
    {
        using LoaderHarness harness = new("life-start-timeout");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.StartHangs", calls, StartBehavior.WaitForToken, sourceKind: "LifeStartHangs"),
            new LifePlugin("Life.StartIgnoresToken", calls, StartBehavior.NeverComplete),
            new LifePlugin("Life.After", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For,
            TimeSpan.FromMilliseconds(200), CancellationToken.None);

        foreach (string name in (string[])["Life.StartHangs", "Life.StartIgnoresToken"])
        {
            string expected = $"Plugin '{name}' did not start within 0.2 s. Limit: 0.2 (setting plugins.startTimeoutSeconds). "
                + "Change it in Settings ▸ Plugins ▸ Start timeout (s) or with ni settings set plugins.startTimeoutSeconds <value>.";
            await Assert.That(started.Plugins.Single(record => record.Name == name).State).IsEqualTo(PluginLoadState.Failed);
            await Assert.That(started.Plugins.Single(record => record.Name == name).Reason).IsEqualTo(expected);
            IReadOnlyList<LogEntry> log = contexts.LogOf(name);
            await Assert.That(log.Count).IsEqualTo(1);
            await Assert.That(log[0].Text).IsEqualTo(expected);
        }

        await Assert.That(harness.Registry.TryGetSource("LifeStartHangs", out _, out _)).IsFalse();
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(contexts.LogOf("Life.After").Single().Text).IsEqualTo("started");
    }

    [Test]
    public async Task ACancelledStartTokenStopsTheWholeStart()
    {
        using LoaderHarness harness = new("life-start-cancel");
        using LifecycleContexts contexts = new();
        PluginLoadReport loaded = harness.Load([], [new LifePlugin("Life.A", [])]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.That(async () => await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task StartRefusesAnInvalidTimeoutAndMissingArguments()
    {
        using LoaderHarness harness = new("life-start-args");
        using LifecycleContexts contexts = new();
        PluginLoadReport loaded = harness.Load([]);

        await Assert.That(async () => await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.Zero,
            CancellationToken.None)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await PluginLifecycle.StartAsync(null!, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(1), // deliberate null: proves the guard
            CancellationToken.None)).Throws<ArgumentNullException>();
    }

    #endregion

    #region Stop

    [Test]
    [Category("Slow")]
    public async Task AThrowingOrHangingStopIsLoggedAndTheOthersStillStopInReverseOrder()
    {
        using LoaderHarness harness = new("life-stop");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.A", calls),
            new LifePlugin("Life.StopThrows", calls, stop: StopBehavior.Throw),
            new LifePlugin("Life.StopHangs", calls, stop: StopBehavior.WaitForToken),
            new LifePlugin("Life.C", calls),
        ]);
        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromMilliseconds(200));

        LogEntry thrown = contexts.LogOf("Life.StopThrows").Single(static entry => entry.Level == "Error");
        await Assert.That(thrown.Text).IsEqualTo("StopAsync failed: InvalidOperationException: stop boom");
        string expectedTimeout = "Plugin 'Life.StopHangs' did not stop within 0.2 s. Limit: 0.2 (setting plugins.stopTimeoutSeconds). "
            + "Change it in Settings ▸ Plugins ▸ Stop timeout (s) or with ni settings set plugins.stopTimeoutSeconds <value>.";
        await Assert.That(contexts.LogOf("Life.StopHangs").Single(static entry => entry.Level == "Error").Text).IsEqualTo(expectedTimeout);

        // Reverse load order, every plugin visited: C, StopHangs, StopThrows, A.
        string[] stops = [.. calls.Where(static call => call.EndsWith(".Stop", StringComparison.Ordinal))];
        await Assert.That(stops).IsEquivalentTo(_StopCalls, CollectionOrdering.Matching);

    }

    #endregion

    #region Isolation

    // A context whose log fails on every call; everything else is never used by the lifecycle tests.
    private sealed class ThrowingLogContext : IPluginContext
    {
        private sealed class ThrowingLog : IPluginLog
        {
            public void Info(string text) => throw new InvalidOperationException("log boom");

            public void Warning(string text) => throw new InvalidOperationException("log boom");

            public void Error(string text, Exception? exception = null) => throw new InvalidOperationException("log boom");
        }

        public string PluginName => "Life.LogThrows";

        public IPluginLog Log { get; } = new ThrowingLog();

        public string DataDirectory => throw new NotSupportedException();

        public ReadOnlySettingsManagerView ApplicationSettings => throw new NotSupportedException();

        public ReadOnlySettingsManagerView ProfileSettings => throw new NotSupportedException();

        public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied
        {
            add { }
            remove { }
        }

        public CancellationToken Stopping => CancellationToken.None;
    }

    [Test]
    [Category("Slow")]
    public async Task AStartThatBlocksItsThreadIsCutAtTheTimeoutAndTheOthersStillStart()
    {
        using LoaderHarness harness = new("life-start-blocks");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.Before", calls),
            new LifePlugin("Life.BlocksThread", calls, StartBehavior.BlockThread),
            new LifePlugin("Life.After", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For,
            TimeSpan.FromMilliseconds(200), CancellationToken.None);

        PluginRecord blocked = started.Plugins.Single(static record => record.Name == "Life.BlocksThread");
        await Assert.That(blocked.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(blocked.Reason)).Contains("did not start within 0.2 s");
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.Before").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(contexts.LogOf("Life.After").Single().Text).IsEqualTo("started");
    }

    [Test]
    [Category("Slow")]
    public async Task AThrowingCallbackOnTheStartTokenIsALogEntryAndNeverEndsTheProcess()
    {
        using LoaderHarness harness = new("life-start-callback");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.CallbackThrows", calls, StartBehavior.ThrowingCallbackThenWait),
            new LifePlugin("Life.After", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For,
            TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.CallbackThrows").State).IsEqualTo(PluginLoadState.Failed);
        string[] errors = [.. contexts.LogOf("Life.CallbackThrows").Where(static entry => entry.Level == "Error").Select(static entry => entry.Text)];
        await Assert.That(errors.Length).IsEqualTo(2);
        await Assert.That(errors).Contains("A cancellation callback failed: InvalidOperationException: callback boom");
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AContextThatCannotBeMadeFailsOnlyThatPlugin()
    {
        using LoaderHarness harness = new("life-start-context");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.NoContext", calls, sourceKind: "LifeNoContext"),
            new LifePlugin("Life.After", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable,
            record => record.Name == "Life.NoContext"
                ? throw new IOException("disk gone")
                : contexts.For(record),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        PluginRecord failed = started.Plugins.Single(static record => record.Name == "Life.NoContext");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("context could not be created: IOException: disk gone");
        await Assert.That(calls.Contains("Life.NoContext.Start")).IsFalse();
        await Assert.That(harness.Registry.TryGetSource("LifeNoContext", out _, out _)).IsFalse();
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task ALogThatThrowsDoesNotKeepAFailedPluginFromBeingDisabledOrTheOthersFromStarting()
    {
        using LoaderHarness harness = new("life-start-log");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.LogThrows", calls, StartBehavior.Throw, sourceKind: "LifeLogThrows"),
            new LifePlugin("Life.After", calls),
        ]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable,
            record => record.Name == "Life.LogThrows"
                ? new ThrowingLogContext()
                : contexts.For(record),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.LogThrows").State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(harness.Registry.TryGetSource("LifeLogThrows", out _, out _)).IsFalse();
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    [Category("Slow")]
    public async Task AStopThatBlocksItsThreadIsCutAtTheTimeoutAndTheOthersStillStop()
    {
        using LoaderHarness harness = new("life-stop-blocks");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.A", calls),
            new LifePlugin("Life.BlocksThread", calls, stop: StopBehavior.BlockThread),
            new LifePlugin("Life.C", calls),
        ]);
        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromMilliseconds(200));

        string[] stops = [.. calls.Where(static call => call.EndsWith(".Stop", StringComparison.Ordinal))];
        await Assert.That(stops).IsEquivalentTo(["Life.C.Stop", "Life.BlocksThread.Stop", "Life.A.Stop"], CollectionOrdering.Matching);
        await Assert.That(contexts.LogOf("Life.BlocksThread").Single(static entry => entry.Level == "Error").Text)
            .Contains("did not stop within 0.2 s");
        await Assert.That(contexts.LogOf("Life.A").Any(static entry => entry.Text == "stopped")).IsTrue();
    }

    [Test]
    public async Task AThrowingStoppingCallbackDoesNotKeepTheOtherPluginsFromStopping()
    {
        using LoaderHarness harness = new("life-stop-callback");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        PluginLoadReport loaded = harness.Load([], [
            new LifePlugin("Life.A", calls),
            new LifePlugin("Life.CallbackThrows", calls),
            new LifePlugin("Life.C", calls),
        ]);
        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);
        PluginRecord thrower = started.Plugins.Single(static record => record.Name == "Life.CallbackThrows");
        using CancellationTokenRegistration registration = contexts.For(thrower).Stopping.Register(
            static () => throw new InvalidOperationException("callback boom"));

        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromSeconds(5));

        string[] stops = [.. calls.Where(static call => call.EndsWith(".Stop", StringComparison.Ordinal))];
        await Assert.That(stops).IsEquivalentTo(["Life.C.Stop", "Life.CallbackThrows.Stop", "Life.A.Stop"], CollectionOrdering.Matching);
        await Assert.That(contexts.LogOf("Life.CallbackThrows").Any(static entry => entry.Text.StartsWith("A Stopping callback failed", StringComparison.Ordinal)))
            .IsTrue();
    }

    #endregion

    #region Order

    [Test]
    public async Task ConfigureAllThenStartInLoadOrderThenStopInReverseOrder()
    {
        using LoaderHarness harness = new("life-order");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        LifePlugin[] plugins = [new("A", calls), new("B", calls), new("C", calls)];
        PluginLoadReport loaded = harness.Load([], plugins);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromSeconds(5));

        await Assert.That(calls).IsEquivalentTo(_OrderedCalls, CollectionOrdering.Matching);

        // Every plugin heard "stopping" before its StopAsync ran.
        await Assert.That(plugins.All(static plugin => plugin.StoppingWasCancelledAtStop == true)).IsTrue();
    }

    [Test]
    public async Task FailedAndSkippedRecordsAreNeitherStartedNorStoppedAndAnEmptyReportDoesNothing()
    {
        using LoaderHarness harness = new("life-skip");
        using LifecycleContexts contexts = new();
        List<string> calls = [];
        string missing = harness.Folder.FilePath("missing");
        PluginLoadReport loaded = harness.Load([missing], [new LifePlugin("A", calls)]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await PluginLifecycle.StopAsync(started, contexts.For, TimeSpan.FromSeconds(5));
        PluginLoadReport empty = harness.Load([]);
        await PluginLifecycle.StartAsync(empty, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5), CancellationToken.None);
        await PluginLifecycle.StopAsync(empty, contexts.For, TimeSpan.FromSeconds(5));

        string[] aCalls = [.. calls.Where(static call => call.StartsWith("A.", StringComparison.Ordinal))];
        await Assert.That(aCalls).IsEquivalentTo(_SingleCalls, CollectionOrdering.Matching);
        await Assert.That(started.Plugins.Count(static record => record.State == PluginLoadState.Failed)).IsEqualTo(1);
    }

    #endregion
}
