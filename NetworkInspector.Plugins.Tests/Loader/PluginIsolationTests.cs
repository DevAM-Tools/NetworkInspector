// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// One misbehaving plugin never takes the application down: every failure kind of the test plugin assembly becomes a Failed
/// record with a reason, the healthy plugin next to it loads, and nothing of a failed plugin is published.
/// </summary>
internal sealed class PluginIsolationTests
{
    #region Constants

    private const string _Namespace = "NetworkInspector.Plugins.Tests.TestPlugins.";

    #endregion

    #region Helpers

    // Loads the whole test plugin assembly from a private copy.
    private static PluginLoadReport _LoadAll(LoaderHarness harness)
    {
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folder);
        return harness.Load([folder]);
    }

    #endregion

    #region Failure kinds

    [Test]
    public async Task AThrowingStaticConstructorFailsOnlyThatPlugin()
    {
        using LoaderHarness harness = new("isolation-cctor");

        PluginLoadReport report = _LoadAll(harness);

        PluginRecord failed = report.Plugins.Single(static record => record.Name == _Namespace + "StaticCtorThrowsPlugin");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(failed.Reason)).Contains("static constructor threw");
        await Assert.That(Expect.NotNull(failed.Reason)).Contains("static boom");
        await Assert.That(report.Plugins.Single(static record => record.Name == "Test.Healthy").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task AThrowingConfigureFailsThePluginAndPublishesNothing()
    {
        using LoaderHarness harness = new("isolation-configure");

        PluginLoadReport report = _LoadAll(harness);

        PluginRecord failed = report.Plugins.Single(static record => record.Name == "Test.ConfigureThrows");
        await Assert.That(failed.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(failed.Reason).IsEqualTo("Configure failed: InvalidOperationException: configure boom");
        await Assert.That(failed.Contributions).IsEmpty();
        await Assert.That(harness.Registry.TryGetSource("TestConfigureThrows", out _, out string? problem)).IsFalse();
        await Assert.That(Expect.NotNull(problem)).StartsWith("Unknown source kind 'TestConfigureThrows'.");
    }

    [Test]
    public async Task AFailingStackContributionOrSettingsRegistrationFailsAtCommit()
    {
        using LoaderHarness harness = new("isolation-stack");

        PluginLoadReport report = _LoadAll(harness);

        PluginRecord stack = report.Plugins.Single(static record => record.Name == "Test.StackThrows");
        PluginRecord settings = report.Plugins.Single(static record => record.Name == "Test.SettingsThrows");
        await Assert.That(stack.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(stack.Reason)).StartsWith("Registration failed:");
        await Assert.That(Expect.NotNull(stack.Reason)).Contains("stack boom");
        await Assert.That(settings.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(settings.Reason)).StartsWith("Registration failed:");
        await Assert.That(Expect.NotNull(settings.Reason)).Contains("settings boom");

        // A real stack never runs the failed contributions; the healthy plugin's contribution ran.
        using SettingsManager manager = new();
        StackBuilder builder = new(manager, new FrameInterfaceRegistry());
        IReadOnlyList<PluginContributionFailure> failures = harness.Registry.ApplyStackContributions(builder);
        await Assert.That(failures).IsEmpty();
        await Assert.That(builder.GetProtocolId("test.healthy")).IsNotNull();
    }

    [Test]
    public async Task MisdeclaredEntryTypesGetClearReasons()
    {
        using LoaderHarness harness = new("isolation-entry");

        PluginLoadReport report = _LoadAll(harness);

        PluginRecord internalType = report.Plugins.Single(static record => record.Name == _Namespace + "InternalPlugin");
        PluginRecord generic = report.Plugins.Single(static record => record.Name.StartsWith(_Namespace + "GenericPlugin", StringComparison.Ordinal));
        PluginRecord noConstructor = report.Plugins.Single(static record => record.Name == _Namespace + "NoDefaultCtorPlugin");
        PluginRecord infoThrows = report.Plugins.Single(static record => record.Name == _Namespace + "InfoThrowsPlugin");
        await Assert.That(internalType.Reason).IsEqualTo($"Entry type '{_Namespace}InternalPlugin' must be public.");
        await Assert.That(Expect.NotNull(generic.Reason)).EndsWith("must not be generic.");
        await Assert.That(noConstructor.Reason).IsEqualTo($"Entry type '{_Namespace}NoDefaultCtorPlugin' needs a public parameterless constructor.");
        await Assert.That(Expect.NotNull(infoThrows.Reason)).StartsWith("reading Info threw:");
        await Assert.That(new[] { internalType, generic, noConstructor, infoThrows }.All(static record => record.State == PluginLoadState.Failed)).IsTrue();

        // An abstract base class is a base, not an entry type: no record at all.
        await Assert.That(report.Plugins.Any(static record => record.Name.Contains("AbstractPluginBase", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ASecondPluginWithTheSameNameFailsAndTheFirstStaysLoaded()
    {
        using LoaderHarness harness = new("isolation-duplicate");

        PluginLoadReport report = _LoadAll(harness);

        PluginRecord duplicate = report.Plugins.Single(static record => record.Name == _Namespace + "LateDuplicateNamePlugin");
        await Assert.That(duplicate.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(duplicate.Reason).IsEqualTo("A plugin named 'Test.Healthy' is already loaded.");
        await Assert.That(report.Plugins.Single(static record => record.Name == "Test.Healthy").State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task ABuiltInWinsTheNameAgainstAnExternalPlugin()
    {
        using LoaderHarness harness = new("isolation-builtin-name");
        List<string> calls = [];
        LifePlugin builtIn = new("Test.Healthy", calls);
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folder);

        PluginLoadReport report = harness.Load([folder], [builtIn]);

        PluginRecord[] named = [.. report.Plugins.Where(static record => record.Name == "Test.Healthy")];
        await Assert.That(named.Length).IsEqualTo(1);
        await Assert.That(named[0].BuiltIn).IsTrue();
        await Assert.That(report.Plugins.Any(static record => record.Reason == "A plugin named 'Test.Healthy' is already loaded.")).IsTrue();
    }

    [Test]
    public async Task NamesCompareIgnoringCase()
    {
        using LoaderHarness harness = new("isolation-case");
        List<string> calls = [];

        PluginLoadReport report = harness.Load([], [new LifePlugin("Same.Name", calls), new LifePlugin("same.name", calls)]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(report.Plugins[1].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(report.Plugins[1].Reason).IsEqualTo("A plugin named 'same.name' is already loaded.");
    }

    #endregion
}
