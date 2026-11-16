// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Load contexts: one collectible context per plugin assembly, shared contracts, and private dependencies.</summary>
[Category("Slow")]
internal sealed class PluginLoadContextTests
{
    #region Contexts

    [Test]
    public async Task EachPluginAssemblyGetsItsOwnCollectibleContextAndSharesTheContract()
    {
        using LoaderHarness harness = new("context");
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);
        TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folder);

        // A copy of Core next to the plugins must be ignored: the application's Core is the one in the default context.
        string coreCopy = TestPluginFiles.CopyInto(typeof(StackBuilder).Assembly.Location, folder);
        PluginLoadReport report = harness.Load([folder]);

        PluginRecord sample = report.Plugins.Single(static record => record.Name == "Sample.Protocol");
        PluginRecord healthy = report.Plugins.Single(static record => record.Name == "Test.Healthy");
        AssemblyLoadContext? sampleContext = AssemblyLoadContext.GetLoadContext(Expect.NotNull(sample.Instance).GetType().Assembly);
        AssemblyLoadContext? healthyContext = AssemblyLoadContext.GetLoadContext(Expect.NotNull(healthy.Instance).GetType().Assembly);

        await Assert.That(sampleContext).IsTypeOf<PluginLoadContext>();
        await Assert.That(Expect.NotNull(sampleContext).IsCollectible).IsTrue();
        await Assert.That(healthyContext).IsNotSameReferenceAs(sampleContext);
        await Assert.That(sampleContext).IsNotSameReferenceAs(AssemblyLoadContext.Default);

        // One type identity for the contract: the plugin implements the very IPlugin of this test process.
        await Assert.That(Expect.NotNull(sample.Instance).GetType().GetInterfaces()).Contains(typeof(IPlugin));
        await Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic
            && string.Equals(assembly.Location, coreCopy, StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Test]
    public async Task APluginRegistersASettingAndALimitThroughTheSettingsLibraryOfTheApplicationEvenWhenItsFolderShipsOwnCopies()
    {
        using LoaderHarness harness = new("settings-identity");
        string folder = harness.Folder.FilePath("plugins");

        // The plugin folder ships its own copies of both libraries. The loader must ignore them: a copy would be a second
        // SettingsRegistrar and a second LimitDefinition, and the cast of the application's registrar to the plugin's would fail.
        string settingsCopy = TestPluginFiles.CopyInto(typeof(SettingsRegistrar).Assembly.Location, folder);
        string limitsCopy = TestPluginFiles.CopyInto(typeof(LimitValue).Assembly.Location, folder);
        TestAssemblyCompiler.Compile(Path.Combine(folder, "IdentityPlugin.dll"), "IdentityPlugin", """
            using NetworkInspector.Core.Settings;
            using NetworkInspector.Limits;
            using NetworkInspector.Limits.Utility;
            using NetworkInspector.Plugins;
            using NetworkInspector.Settings;
            public sealed class IdentityPlugin : IPlugin
            {
                private static readonly LimitDefinition _MaxItems = new("test.identity.maxItems", LimitUnits.Count,
                    100, 1, 1000, new LimitTexts("Largest list", "The largest list.", "A hostile list.",
                        "A clear refusal.", "Smaller values refuse sooner: 10 refuses 11.", "Larger values allow more: 1000 allows 999.",
                        "Every list passes."));
                public PluginInfo Info => new("Test.Identity", "1", "MIT", "Registers a setting and a limit.", []);
                public void Configure(IPluginBuilder builder) => builder.AddSettings(SettingScope.Application, static registrar =>
                {
                    registrar.RegisterI64Setting("test.identity.level", "Level", "test.identity", 7);
                    registrar.RegisterLimit(_MaxItems);
                });
            }
            """);

        PluginLoadReport report = harness.Load([folder]);
        using SettingsManager application = new();
        IReadOnlyList<PluginContributionFailure> failures = harness.Registry.ApplySettings(SettingScope.Application, application);

        PluginRecord identity = report.Plugins.Single(static record => record.Name == "Test.Identity");
        await Assert.That(identity.State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(failures).IsEmpty();
        await Assert.That(application.GetI64Setting("test.identity.level")).IsEqualTo(7L);
        await Assert.That(application.GetLimit("test.identity.maxItems")).IsEqualTo((LimitValue?)LimitValue.Of(100));
        await Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic
            && (string.Equals(assembly.Location, settingsCopy, StringComparison.OrdinalIgnoreCase)
                || string.Equals(assembly.Location, limitsCopy, StringComparison.OrdinalIgnoreCase)))).IsFalse();
    }

    [Test]
    public async Task APrivateDependencyResolvesFromThePluginFolder()
    {
        using LoaderHarness harness = new("private-dep");
        string folder = harness.Folder.FilePath("plugins");
        string dependency = TestAssemblyCompiler.Compile(Path.Combine(folder, "PrivateDep.dll"), "PrivateDep", """
            namespace PrivateDep
            {
                public static class Helper { public static string Value => "private-42"; }
            }
            """);
        TestAssemblyCompiler.Compile(Path.Combine(folder, "PrivateDepPlugin.dll"), "PrivateDepPlugin", """
            using NetworkInspector.Plugins;
            public sealed class PrivateDepPlugin : IPlugin
            {
                public PluginInfo Info => new("Private.Dep", "1", "MIT", PrivateDep.Helper.Value, []);
                public void Configure(IPluginBuilder builder) { }
            }
            """, [dependency]);

        PluginLoadReport report = harness.Load([folder]);

        PluginRecord plugin = report.Plugins.Single();
        await Assert.That(plugin.State).IsEqualTo(PluginLoadState.Loaded);
        IPlugin instance = Expect.NotNull(plugin.Instance);
        await Assert.That(instance.Info.Description).IsEqualTo("private-42");

        // The dependency lives in the plugin's context, loaded from the plugin folder.
        Assembly loadedDependency = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => !assembly.IsDynamic && string.Equals(assembly.Location, dependency, StringComparison.OrdinalIgnoreCase));
        AssemblyLoadContext? pluginContext = AssemblyLoadContext.GetLoadContext(instance.GetType().Assembly);
        await Assert.That(AssemblyLoadContext.GetLoadContext(loadedDependency)).IsSameReferenceAs(pluginContext);
    }

    #endregion

    #region Versions of shared assemblies

    [Test]
    public async Task APluginBuiltAgainstANewerSharedAssemblyFailsWithBothVersionsBeforeItsCodeRuns()
    {
        using LoaderHarness harness = new("newer-shared");
        string available = string.Create(CultureInfo.InvariantCulture, $"{Expect.NotNull(typeof(NetworkInspectorBuiltIns).Assembly.GetName().Version)}");

        // NetworkInspector.Protocols is an assembly of the application (it comes from the default context); the plugin is
        // compiled against a stand-in of version 99 that is not shipped, exactly like a plugin built for a newer release.
        PluginRecord record = _LoadAgainstSharedVersion(harness, "99.0.0.0");

        await Assert.That(record.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(record.Reason)).Contains("NetworkInspector.Protocols 99.0.0.0");
        await Assert.That(Expect.NotNull(record.Reason)).Contains("this application provides " + available);
        await Assert.That(Expect.NotNull(record.Reason)).Contains("update the application");

        // No plugin code ran: the instance was never created and the record still carries the entry type name, because the
        // info (which would rename it) was never read.
        await Assert.That(record.Instance).IsNull();
        await Assert.That(record.Name).IsEqualTo("VersionedPlugin");
    }

    [Test]
    public async Task APluginBuiltAgainstAnOlderSharedAssemblyLoadsWithTheApplicationsCopy()
    {
        using LoaderHarness harness = new("older-shared");

        PluginRecord record = _LoadAgainstSharedVersion(harness, "0.0.1.0");

        await Assert.That(record.State).IsEqualTo(PluginLoadState.Loaded);
        await Assert.That(record.Name).IsEqualTo("Versioned.Plugin");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Compiles a stand-in of <c>NetworkInspector.Protocols</c> with <paramref name="version"/> outside the plugin folder, then a
    /// plugin that references it (from a method that never runs, so only the reference matters), and loads the plugin folder.
    /// </summary>
    /// <param name="harness">The loader harness of the test.</param>
    /// <param name="version">The assembly version of the stand-in the plugin is built against.</param>
    /// <returns>The record of the one plugin.</returns>
    private static PluginRecord _LoadAgainstSharedVersion(LoaderHarness harness, string version)
    {
        string standIn = TestAssemblyCompiler.Compile(harness.Folder.FilePath(Path.Combine("build", "NetworkInspector.Protocols.dll")),
            "NetworkInspector.Protocols", "[assembly: System.Reflection.AssemblyVersion(\"" + version + "\")]\n"
            + "namespace VersionProbe { public static class Marker { public static string Text => \"stand-in\"; } }");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "VersionedPlugin.dll"), "VersionedPlugin", """
            using NetworkInspector.Plugins;
            public sealed class VersionedPlugin : IPlugin
            {
                public PluginInfo Info => new("Versioned.Plugin", "1", "MIT", "Built against a stand-in.", []);
                public void Configure(IPluginBuilder builder) { }
                public static string NeverCalled() => VersionProbe.Marker.Text;
            }
            """, [standIn]);

        return harness.Load([folder]).Plugins.Single();
    }

    #endregion
}
