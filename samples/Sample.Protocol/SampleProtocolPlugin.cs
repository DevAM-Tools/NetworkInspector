// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Tier 1 sample plugin; found by the pre-scan because it implements <see cref="IPlugin"/> itself (no attribute). It shows
/// every core-tier extension point: a protocol with a port registration, a heuristic parser, a post-parser, a source, an
/// exporter, and an application setting.
/// <para>
/// Thread safety: <see cref="StartAsync"/> and <see cref="StopAsync"/> run sequentially; the context reference is published
/// with <see cref="Volatile"/> so that a plugin which logs from a stack contribution (any thread) would see it. This sample logs
/// only in <see cref="StartAsync"/> and <see cref="StopAsync"/>.
/// </para>
/// </summary>
public sealed class SampleProtocolPlugin : IPlugin
{
    #region Constants

    /// <summary>Application setting with the text the plugin logs when it starts.</summary>
    public const string GreetingSetting = "sample.greeting";

    #endregion

    #region Fields

    private IPluginContext? _Context;

    #endregion

    #region IPlugin

    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Sample.Protocol", "0.1.0", "MIT",
        "Sample protocol on UDP 40000 with a NIS1 TCP heuristic, a post-parser, a generator source, a text exporter, and a setting.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Protocols are registered on the Core stack builder exactly like the built-in protocols; the callback runs for every
        // stack the application builds.
        builder.AddStackContribution(static stack => SampleRegistration.Register(stack));

        // A source is a Core IFrameSource, an exporter a Core IPacketListener; their options are Core settings.
        builder.AddSource(SampleSource.Registration);
        builder.AddPacketExporter(SampleTextExporter.Registration);

        // Application settings use the Core registrar. The action creates a new Setting per call because one Setting binds to
        // exactly one manager, and the application runs the action once per manager.
        builder.AddSettings(SettingScope.Application, static registrar =>
        {
            registrar.RegisterGroup("sample", "Sample plugin", "Settings of the Sample.Protocol plugin.");
            registrar.RegisterStringSetting(GreetingSetting, "Greeting", "sample", "Hello from Sample.Protocol",
                "Text the sample plugin logs when it starts.");
        });
    }

    /// <inheritdoc/>
    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Volatile.Write(ref _Context, context);

        // The application settings view also holds this plugin's own settings, so the greeting set with "ni settings set"
        // reaches the plugin without any plugin-specific plumbing.
        string greeting = context.ApplicationSettings.GetStringSetting(GreetingSetting) ?? string.Empty;
        context.Log.Info(Invariant($"started; greeting={greeting}"));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref _Context)?.Log.Info("stopped");
        Volatile.Write(ref _Context, null);
        return ValueTask.CompletedTask;
    }

    #endregion
}
