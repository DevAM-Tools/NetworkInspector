// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Load contexts: one collectible context per plugin assembly, shared contracts, and private dependencies.</summary>
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
}
