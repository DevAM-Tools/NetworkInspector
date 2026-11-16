// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The contract itself: default lifecycle members, registrations that keep their delegates, plain record behavior, and the
/// exact set of exported types.
/// </summary>
internal sealed class ContractDefaultsTests
{
    #region Test doubles

    /// <summary>A plugin that implements only the two required members; the lifecycle defaults must be enough.</summary>
    private sealed class MinimalPlugin : IPlugin
    {
        public PluginInfo Info { get; } = new("Test.Minimal", "1.0.0", "MIT", "Minimal plugin.", []);

        public void Configure(IPluginBuilder builder)
        {
        }
    }

    /// <summary>A context that is never used; the default members must not touch it.</summary>
    private sealed class UnusedContext : IPluginContext
    {
        public string PluginName => throw new InvalidOperationException("The default lifecycle must not read the context.");

        public IPluginLog Log => throw new InvalidOperationException("The default lifecycle must not read the context.");

        public string DataDirectory => throw new InvalidOperationException("The default lifecycle must not read the context.");

        public ReadOnlySettingsManagerView ApplicationSettings => throw new InvalidOperationException("Not used.");

        public ReadOnlySettingsManagerView ProfileSettings => throw new InvalidOperationException("Not used.");

        public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied
        {
            add { }
            remove { }
        }

        public CancellationToken Stopping => throw new InvalidOperationException("Not used.");
    }

    #endregion

    #region Helpers

    // Named methods instead of lambdas: the delegate parameters carry nullability attributes that a lambda cannot repeat.
    private static bool _RefuseSource(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        source = null;
        error = new PluginError(PluginErrorCode.Failed, "not created");
        return false;
    }

    private static bool _RefuseExporter(StreamExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = null;
        error = new PluginError(PluginErrorCode.TargetExists, "exists");
        return false;
    }

    #endregion

    #region Lifecycle defaults

    [Test]
    public async Task DefaultStartReturnsCompletedValueTask()
    {
        IPlugin plugin = new MinimalPlugin();

        ValueTask start = plugin.StartAsync(new UnusedContext(), CancellationToken.None);

        await Assert.That(start.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task DefaultStopReturnsCompletedValueTask()
    {
        IPlugin plugin = new MinimalPlugin();

        ValueTask stop = plugin.StopAsync(CancellationToken.None);

        await Assert.That(stop.IsCompletedSuccessfully).IsTrue();
    }

    #endregion

    #region Registrations

    [Test]
    public async Task SourceRegistrationKeepsGivenDelegatesAndValues()
    {
        Action<SettingsRegistrar> options = static registrar => registrar.RegisterBoolSetting("flag", "Flag", "", true);
        SourceCreate create = _RefuseSource;

        SourceRegistration registration = new("Kind", "Name", "Description", [".x"], AllowsMultipleFiles: true, options, create);

        await Assert.That(registration.RegisterOptions).IsSameReferenceAs(options);
        await Assert.That(registration.Create).IsSameReferenceAs(create);
        await Assert.That(registration.FileExtensions.Count).IsEqualTo(1);
        await Assert.That(registration.AllowsMultipleFiles).IsTrue();
    }

    [Test]
    public async Task ExporterRegistrationKeepsGivenCreationAndValues()
    {
        Action<SettingsRegistrar> options = static registrar => registrar.RegisterBoolSetting("flag", "Flag", "", true);
        ExporterCreation<IPacketListener> create = ExporterCreation.ForStreams<IPacketListener>(_RefuseExporter);

        ExporterRegistration<IPacketListener> registration = new("Fmt", "Name", "Description", ".fmt", options, create);

        await Assert.That(registration.RegisterOptions).IsSameReferenceAs(options);
        await Assert.That(registration.Creation).IsSameReferenceAs(create);
        await Assert.That(registration.FileExtension).IsEqualTo(".fmt");
    }

    [Test]
    public async Task PluginErrorDefaultsOptionToNull()
    {
        PluginError error = new(PluginErrorCode.InvalidOption, "bad");

        await Assert.That(error.Option).IsNull();
        await Assert.That(error.Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task RecordsWithEqualMembersAreEqual()
    {
        PluginInfo first = new("A.B", "1", "MIT", "d", []);
        PluginInfo second = new("A.B", "1", "MIT", "d", first.Dependencies);
        PluginError errorA = new(PluginErrorCode.Failed, "x", "opt");
        PluginError errorB = new(PluginErrorCode.Failed, "x", "opt");
        PluginDependency dependencyA = new("D", "1", "MIT", "Holder", "https://example.invalid", "text");
        PluginDependency dependencyB = new("D", "1", "MIT", "Holder", "https://example.invalid", "text");

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(errorA).IsEqualTo(errorB);
        await Assert.That(dependencyA).IsEqualTo(dependencyB);
    }

    #endregion

    #region Exported surface

    [Test]
    public async Task AssemblyExportsExactlyTheContractTypes()
    {
        string[] expected =
        [
            "ExporterCreation", "ExporterCreation`1", "ExporterRegistration`1", "FileExporterCreate`1", "FileExportRequest",
            "IPlugin", "IPluginBuilder", "IPluginContext", "IPluginLog", "PluginDependency", "PluginError", "PluginErrorCode",
            "PluginInfo", "SourceCreate", "SourceRegistration", "SourceRequest", "StreamExporterCreate`1", "StreamExportRequest",
        ];

        string[] exported = [.. typeof(IPlugin).Assembly.GetExportedTypes().Select(static type => type.Name).Order(StringComparer.Ordinal)];

        await Assert.That(exported).IsEquivalentTo(expected);
    }

    [Test]
    public async Task AssemblyHasNoAttributeOptionFactoryOrTierType()
    {
        string[] forbiddenFragments = ["Attribute", "Option", "Factory", "Tier", "Sink", "Runtime", "Storage"];

        string[] offending =
        [
            .. typeof(IPlugin).Assembly.GetExportedTypes()
                .Select(static type => type.Name)
                .Where(name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.Ordinal))),
        ];

        await Assert.That(offending).IsEmpty();
    }

    [Test]
    public async Task ContractAssemblyReferencesOnlyTheLibrariesOfItsTypesAndTheFramework()
    {
        string[] references =
        [
            .. typeof(IPlugin).Assembly.GetReferencedAssemblies()
                .Select(static name => name.Name ?? string.Empty)
                .Where(static name => name.StartsWith("NetworkInspector", StringComparison.Ordinal)),
        ];

        // The contract exposes settings types (the builder registers settings, the context reads them), so it references the settings
        // library and, through it, the limits library; nothing else of NetworkInspector.
        await Assert.That(references.All(static name => name is "NetworkInspector.Core" or "NetworkInspector.Values" or "NetworkInspector.Settings"
            or "NetworkInspector.Limits")).IsTrue();
    }

    #endregion
}
