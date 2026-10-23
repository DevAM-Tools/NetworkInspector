// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>
/// A plugin that does everything right: a source kind and a packet format that create nothing useful, a stack contribution
/// with one protocol, and the setting <c>test.healthy.level</c> in both the application and the profile scope. It logs
/// <c>started</c> and <c>stopped</c> so tests can see the lifecycle. Thread safety: sequential lifecycle calls only.
/// </summary>
public sealed class HealthyTestPlugin : IPlugin
{
    #region Fields

    private IPluginContext? _Context;

    #endregion

    #region IPlugin

    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.Healthy", "1.0.0", "MIT", "Healthy test plugin.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        builder.AddSource(new SourceRegistration("TestHealthy", "Test healthy", "A source that creates nothing.", [], false,
            static _ => { }, _TryCreateSource));
        builder.AddPacketExporter(new ExporterRegistration<IPacketListener>("TestHealthy", "Test healthy", "Writes nothing.", ".testhealthy",
            static _ => { }, _TryCreateExporter));
        builder.AddStackContribution(static stack => stack.RegisterProtocol(new HealthyProtocol()));
        builder.AddSettings(SettingScope.Application, static registrar => _RegisterLevel(registrar));
        builder.AddSettings(SettingScope.Profile, static registrar => _RegisterLevel(registrar));
    }

    /// <inheritdoc/>
    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _Context, context);
        context.Log.Info("started");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref _Context)?.Log.Info("stopped");
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Private helpers

    private static void _RegisterLevel(SettingsRegistrar registrar) =>
        registrar.RegisterI64Setting("test.healthy.level", "Level", "test.healthy", 1, description: "Level of the healthy test plugin.");

    private static bool _TryCreateSource(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        source = null;
        error = new PluginError(PluginErrorCode.Failed, "The healthy test source creates nothing.");
        return false;
    }

    private static bool _TryCreateExporter(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = new NullPacketListener();
        error = null;
        return true;
    }

    #endregion

    #region Nested types

    // A protocol that parses nothing; it exists so that the stack contribution has something to register.
    private sealed class HealthyProtocol : IProtocol
    {
        public string Name => "test.healthy";

        public string UiName => "Test healthy";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    // A listener that accepts every packet and writes nothing.
    private sealed class NullPacketListener : IPacketListener
    {
        public string UiName => "Test healthy";

        public string? Description => "Writes nothing.";

        public bool OnPacket(Packet packet) => true;

        public void OnFinish()
        {
        }
    }

    #endregion
}
