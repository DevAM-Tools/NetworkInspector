// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The one-line description of plugin exceptions, and the containment of a plugin exception that cannot even be described.
/// </summary>
internal sealed class PluginActivatorTests
{
    #region Describe

    [Test]
    public async Task DescribeGivesTheShortTypeNameAndTheMessage()
    {
        string text = PluginActivator.Describe(new InvalidOperationException("boom"));

        await Assert.That(text).IsEqualTo("InvalidOperationException: boom");
    }

    [Test]
    public async Task DescribeFallsBackToTheTypeNameWhenTheMessageGetterThrows()
    {
        string text = PluginActivator.Describe(new HostileException());

        await Assert.That(text).IsEqualTo("HostileException: (the message could not be read)");
    }

    [Test]
    public async Task DescribeRefusesNull()
    {
        await Assert.That(() => PluginActivator.Describe(null!)).Throws<ArgumentNullException>(); // deliberate null: proves the guard
    }

    #endregion

    #region Containment

    [Test]
    public async Task AConfigureThatThrowsAnExceptionWithAHostileMessageFailsOnlyThatPlugin()
    {
        using LoaderHarness harness = new("activator-hostile");
        ScriptedPlugin hostile = new("Hostile.Configure", static _ => throw new HostileException());
        ScriptedPlugin healthy = new("Healthy.Configure", static _ => { });

        PluginLoadReport report = harness.Load([], [hostile, healthy]);

        PluginRecord failed = report.Plugins.Single(static record => record.Name == "Hostile.Configure");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("Configure failed: HostileException: (the message could not be read)");
        await Assert.That(report.Plugins.Single(static record => record.Name == "Healthy.Configure").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AStartThatThrowsAnExceptionWithAHostileMessageFailsOnlyThatPlugin()
    {
        using LoaderHarness harness = new("activator-hostile-start");
        using LifecycleContexts contexts = new();
        PluginLoadReport loaded = harness.Load([], [new HostileStartPlugin(), new LifePlugin("Life.After", [])]);

        PluginLoadReport started = await PluginLifecycle.StartAsync(loaded, harness.Registry.Disable, contexts.For, TimeSpan.FromSeconds(5),
            CancellationToken.None);

        PluginRecord failed = started.Plugins.Single(static record => record.Name == "Hostile.Start");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("StartAsync failed: HostileException: (the message could not be read)");
        await Assert.That(started.Plugins.Single(static record => record.Name == "Life.After").State).IsEqualTo(PluginLoadState.Loaded);
    }

    // A plugin whose start throws the hostile exception.
    private sealed class HostileStartPlugin : IPlugin
    {
        public PluginInfo Info => new("Hostile.Start", "1.0.0", "MIT", "Throws a hostile exception at start.", []);

        public void Configure(IPluginBuilder builder)
        {
        }

        public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken) => throw new HostileException();
    }

    #endregion
}
