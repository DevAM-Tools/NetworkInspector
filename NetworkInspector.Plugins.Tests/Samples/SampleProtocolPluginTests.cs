// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The sample plugin as an entry type: shape, info, contributions, references, and its application setting.</summary>
internal sealed class SampleProtocolPluginTests
{
    #region Constants

    private static readonly string[] _SourceKinds = ["SampleGen"];

    private static readonly string[] _PacketFormats = ["SampleText"];

    #endregion

    #region Entry type

    [Test]
    public async Task SampleIsAPublicSealedClassWithAPublicParameterlessConstructor()
    {
        Type type = typeof(SampleProtocolPlugin);

        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.IsSealed).IsTrue();
        await Assert.That(type.IsAbstract).IsFalse();
        await Assert.That(type.GetConstructor(Type.EmptyTypes)).IsNotNull();
    }

    [Test]
    public async Task SampleListsThePluginInterfaceInItsOwnInterfaceList()
    {
        // The pre-scan reads the class's own interface list, so the class must name the interface itself.
        await Assert.That(typeof(SampleProtocolPlugin).GetInterfaces()).Contains(typeof(IPlugin));
    }

    [Test]
    public async Task SampleAssemblyCarriesNoPluginAttribute()
    {
        string[] attributeNames =
        [
            .. typeof(SampleProtocolPlugin).Assembly.GetCustomAttributes().Select(static attribute => attribute.GetType().Name),
        ];

        await Assert.That(attributeNames.Any(static name => name.Contains("Plugin", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task InfoDescribesTheSample()
    {
        PluginInfo info = new SampleProtocolPlugin().Info;

        await Assert.That(info.Name).IsEqualTo("Sample.Protocol");
        await Assert.That(info.Version).IsEqualTo("0.1.0");
        await Assert.That(info.License).IsEqualTo("MIT");
        await Assert.That(info.Description).IsNotEmpty();
        await Assert.That(info.Dependencies).IsEmpty();
    }

    #endregion

    #region Contributions

    [Test]
    public async Task ConfigureDeclaresExactlyTheDocumentedContributions()
    {
        RecordingPluginBuilder builder = new();

        new SampleProtocolPlugin().Configure(builder);

        await Assert.That(builder.StackContributions.Count).IsEqualTo(1);
        await Assert.That(builder.Sources.Select(static source => source.Kind).ToArray()).IsEquivalentTo(_SourceKinds);
        await Assert.That(builder.PacketExporters.Select(static exporter => exporter.Format).ToArray()).IsEquivalentTo(_PacketFormats);
        await Assert.That(builder.FrameExporters.Count).IsEqualTo(0);
        await Assert.That(builder.Settings.Count).IsEqualTo(1);
        await Assert.That(builder.Settings[0].Scope).IsEqualTo(SettingScope.Application);
        await Assert.That(builder.NameReads).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task ConfigureRefusesANullBuilder()
    {
        await Assert.That(() =>
        {
            SampleProtocolPlugin plugin = new();
            plugin.Configure(null!); // deliberate null: proves the argument guard
        }).Throws<ArgumentNullException>();
    }

    #endregion

    #region References

    [Test]
    public async Task SampleReferencesOnlyTheContractAndCore()
    {
        string[] references =
        [
            .. typeof(SampleProtocolPlugin).Assembly.GetReferencedAssemblies()
                .Select(static name => name.Name ?? string.Empty)
                .Where(static name => name.StartsWith("NetworkInspector", StringComparison.Ordinal)),
        ];

        await Assert.That(references).Contains("NetworkInspector.Plugins");
        await Assert.That(references).Contains("NetworkInspector.Core");
        await Assert.That(references.Any(static name => name.Contains("Loader", StringComparison.Ordinal)
            || name.Contains("BuiltInPlugins", StringComparison.Ordinal)
            || name.Contains("CLI", StringComparison.Ordinal))).IsFalse();
    }

    #endregion

    #region Setting

    [Test]
    public async Task GreetingSettingRegistersGroupAndDefault()
    {
        RecordingPluginBuilder builder = new();
        new SampleProtocolPlugin().Configure(builder);
        using SettingsManager manager = new();

        builder.Settings[0].Register(new SettingsRegistrar(manager));

        Setting greeting = Expect.NotNull(manager.GetSetting("sample.greeting"));
        await Assert.That(greeting.Type).IsEqualTo(SettingType.String);
        await Assert.That(greeting.GroupName).IsEqualTo("sample");
        await Assert.That(greeting.Activation).IsEqualTo(SettingActivation.Immediate);
        await Assert.That(greeting.DefaultValue).IsEqualTo(SettingValue.String("Hello from Sample.Protocol"));
        await Assert.That(Expect.NotNull(manager.GetGroup("sample")).UiName).IsEqualTo("Sample plugin");
    }

    [Test]
    public async Task RegistrationActionCreatesNewSettingsForEveryManager()
    {
        RecordingPluginBuilder builder = new();
        new SampleProtocolPlugin().Configure(builder);
        using SettingsManager first = new();
        using SettingsManager second = new();

        builder.Settings[0].Register(new SettingsRegistrar(first));
        builder.Settings[0].Register(new SettingsRegistrar(second));

        // A reused Setting instance would be bound to the first manager and the second registration would fail.
        await Assert.That(second.GetSetting("sample.greeting")).IsNotNull();
        await Assert.That(second.GetSetting("sample.greeting")).IsNotSameReferenceAs(first.GetSetting("sample.greeting"));
    }

    #endregion
}
