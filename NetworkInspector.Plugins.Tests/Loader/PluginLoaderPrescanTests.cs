// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The metadata pre-scan seen through the loader: only plugin assemblies are loaded, junk files are skipped quietly, and entry
/// types are found by the plugin interfaces in their own interface list.
/// </summary>
internal sealed class PluginLoaderPrescanTests
{
    #region Constants

    private static readonly string[] _TwoNames = ["Two.A", "Two.B"];

    private static readonly int[] _CoreAndHostTiers = [1, 2];

    #endregion

    #region Only plugins load

    [Test]
    [ClassDataSource<RuntimeDllFolder>(Shared = SharedType.PerTestSession)]
    [Category("Slow")]
    public async Task OnlyThePluginAssemblyIsLoadedFromAFolderWithTwoHundredOtherDlls(RuntimeDllFolder runtime)
    {
        using LoaderHarness harness = new("prescan");
        string folder = harness.Folder.FilePath("plugins");
        runtime.CopyTo(folder);
        string sample = TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Prescan.CandidateFiles).IsEqualTo(RuntimeDllFolder.FileCount + 1);
        await Assert.That(report.Prescan.InspectedFiles).IsEqualTo(RuntimeDllFolder.FileCount + 1);
        await Assert.That(report.Prescan.CachedFiles).IsEqualTo(0);
        await Assert.That(report.Prescan.PluginAssemblies).IsEqualTo(1);
        await Assert.That(report.Plugins.Count).IsEqualTo(1);
        await Assert.That(report.Plugins[0].Name).IsEqualTo("Sample.Protocol");
        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);

        // Nothing but the plugin was loaded from the folder: candidates were inspected through metadata only.
        string[] loaded =
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .Select(static assembly => assembly.IsDynamic ? string.Empty : assembly.Location)
                .Where(location => location.StartsWith(folder, StringComparison.OrdinalIgnoreCase)),
        ];
        await Assert.That(loaded).IsEquivalentTo([sample]);
    }

    [Test]
    [Category("Slow")]
    public async Task JunkFilesAreSkippedQuietly()
    {
        using LoaderHarness harness = new("junk");
        string folder = harness.Folder.FilePath("plugins");
        Directory.CreateDirectory(folder);
        NativePeWriter.Write(Path.Combine(folder, "native.dll"));
        File.WriteAllBytes(Path.Combine(folder, "garbage.dll"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(folder, "empty.dll"), []);
        TestAssemblyCompiler.Compile(Path.Combine(folder, "module.dll"), "module", "public class Unused { }", null, OutputKind.NetModule);
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, folder);

        PluginLoadReport first = harness.Load([folder]);
        PluginLoadReport second = harness.Load([folder]);

        await Assert.That(first.Prescan.CandidateFiles).IsEqualTo(5);
        await Assert.That(first.Prescan.InspectedFiles).IsEqualTo(5);
        await Assert.That(first.Prescan.PluginAssemblies).IsEqualTo(1);
        await Assert.That(first.Plugins.Count).IsEqualTo(1);
        await Assert.That(first.Plugins[0].Name).IsEqualTo("Sample.Protocol");

        // The junk files are cached like any other file: the second scan reads none.
        await Assert.That(second.Prescan.CachedFiles).IsEqualTo(5);
        await Assert.That(second.Prescan.InspectedFiles).IsEqualTo(0);
    }

    #endregion

    #region Entry types by interface

    [Test]
    [Category("Slow")]
    public async Task TwoEntryTypesOfOneAssemblyLoadIntoOneContext()
    {
        using LoaderHarness harness = new("two-entries");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "TwoEntries.dll"), "TwoEntries", """
            using NetworkInspector.Plugins;
            public sealed class A : IPlugin { public PluginInfo Info => new("Two.A", "1", "MIT", "a", []); public void Configure(IPluginBuilder b) { } }
            public sealed class B : IPlugin { public PluginInfo Info => new("Two.B", "1", "MIT", "b", []); public void Configure(IPluginBuilder b) { } }
            """);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins.Select(static record => record.Name).ToArray()).IsEquivalentTo(_TwoNames);
        await Assert.That(report.Plugins.All(static record => record.State == PluginLoadState.Loaded)).IsTrue();
        AssemblyLoadContext? first = AssemblyLoadContext.GetLoadContext(Expect.NotNull(report.Plugins[0].Instance).GetType().Assembly);
        AssemblyLoadContext? second = AssemblyLoadContext.GetLoadContext(Expect.NotNull(report.Plugins[1].Instance).GetType().Assembly);
        await Assert.That(first).IsSameReferenceAs(second);
    }

    [Test]
    public async Task InterfacesAndAbstractBasesAreNotEntryTypes()
    {
        using LoaderHarness harness = new("interfaces");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "Interfaces.dll"), "Interfaces", """
            using NetworkInspector.Plugins;
            public interface IMyPlugin : IPlugin { }
            public abstract class PluginBase : IPlugin
            {
                public abstract PluginInfo Info { get; }
                public virtual void Configure(IPluginBuilder b) { }
            }
            """);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins).IsEmpty();
        await Assert.That(report.Prescan.PluginAssemblies).IsEqualTo(0);
    }

    [Test]
    public async Task AClassThatOnlyInheritsTheInterfaceIsNotAnEntryType()
    {
        // The documented pitfall: the pre-scan reads the class's own interface list, so an inherited interface is invisible.
        using LoaderHarness harness = new("derived-only");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "DerivedOnly.dll"), "DerivedOnly", """
            using NetworkInspector.Plugins;
            public abstract class PluginBase : IPlugin
            {
                public abstract PluginInfo Info { get; }
                public virtual void Configure(IPluginBuilder b) { }
            }
            public sealed class Derived : PluginBase
            {
                public override PluginInfo Info => new("Derived.Plugin", "1", "MIT", "d", []);
            }
            """);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins).IsEmpty();
    }

    [Test]
    public async Task AnInterfaceDefinedInThePluginAssemblyDoesNotCount()
    {
        // A plugin cannot define its own IPlugin: the interface must be a type reference into the contract assembly.
        using LoaderHarness harness = new("own-iplugin");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "OwnIPlugin.dll"), "OwnIPlugin", """
            namespace NetworkInspector.Plugins
            {
                public interface IPlugin { }
            }
            public sealed class Own : NetworkInspector.Plugins.IPlugin { }
            public static class Touch
            {
                // Forces a reference to the real contract assembly, so the cheap reject does not decide this case.
                public static object Dependency() => new NetworkInspector.Plugins.PluginDependency("a", "b", "c", "d", "e", "f");
            }
            """);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins).IsEmpty();
    }

    [Test]
    public async Task AnAssemblyThatReferencesTheContractWithoutAnEntryTypeIsNotAPlugin()
    {
        using LoaderHarness harness = new("uses-contract");
        string folder = harness.Folder.FilePath("plugins");
        TestAssemblyCompiler.Compile(Path.Combine(folder, "UsesContractOnly.dll"), "UsesContractOnly", """
            public static class Uses
            {
                public static object Info() => new NetworkInspector.Plugins.PluginInfo("a", "b", "c", "d", []);
            }
            """);

        PluginLoadReport report = harness.Load([folder]);

        await Assert.That(report.Plugins).IsEmpty();
        await Assert.That(report.Prescan.PluginAssemblies).IsEqualTo(0);
    }

    [Test]
    [ClassDataSource<DummyContracts>(Shared = SharedType.PerTestSession)]
    public async Task MixedTiersOfOneAssemblyAreRecordedInOneCacheEntry(DummyContracts dummy)
    {
        using LoaderHarness harness = new("mixed");
        string folder = harness.Folder.FilePath("plugins");
        string mixed = TestAssemblyCompiler.Compile(Path.Combine(folder, "Mixed.dll"), "Mixed", """
            using NetworkInspector.Plugins;
            public sealed class MixedCore : IPlugin
            {
                public PluginInfo Info => new("Mixed.Core", "1", "MIT", "c", []);
                public void Configure(IPluginBuilder b) { }
            }
            public sealed class MixedHost : NetworkInspector.Host.IHostPlugin
            {
                public PluginInfo Info => new("Mixed.Host", "1", "MIT", "h", []);
                public void Configure(IPluginBuilder b) { }
                public void ConfigureHost(IPluginBuilder b) { }
            }
            """, [dummy.HostContractPath]);

        PluginLoadReport report = harness.Load([folder]);

        // Under the core tier the core class loads and the host class is skipped without loading anything for it.
        await Assert.That(report.Plugins.Single(static record => record.Tier == PluginTier.Core).State).IsEqualTo(PluginLoadState.Loaded);
        PluginRecord skipped = report.Plugins.Single(static record => record.Tier == PluginTier.Host);
        await Assert.That(skipped.State).IsEqualTo(PluginLoadState.Skipped);
        await Assert.That(skipped.Reason).IsEqualTo("needs the host");

        // Both entry types are in one cache entry with their tiers.
        using JsonDocument cache = JsonDocument.Parse(await File.ReadAllTextAsync(harness.CacheFile));
        JsonElement entry = cache.RootElement.GetProperty("entries").EnumerateArray()
            .Single(file => string.Equals(file.GetProperty("path").GetString(), mixed, StringComparison.OrdinalIgnoreCase));
        int[] tiers = [.. entry.GetProperty("entries").EnumerateArray().Select(static type => type.GetProperty("tier").GetInt32())];
        await Assert.That(tiers).IsEquivalentTo(_CoreAndHostTiers);
    }

    #endregion
}
