// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>
/// A plugin that hangs in <c>StartAsync</c> or <c>StopAsync</c> until its token is cancelled, but only when its own application
/// setting asks for it (<c>test.hanging.start</c>, <c>test.hanging.stop</c>, both false by default). Tests that load the whole
/// folder therefore stay fast, and the timeout tests switch the setting on. Thread safety: sequential lifecycle calls only.
/// </summary>
public sealed class HangingPlugin : IPlugin
{
    #region Fields

    private IPluginContext? _Context;

    #endregion

    #region IPlugin

    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.Hanging", "1.0.0", "MIT", "Hangs when its settings say so.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        builder.AddSettings(SettingScope.Application, static registrar =>
        {
            registrar.RegisterGroup("test.hanging", "Test hanging", "Settings of the hanging test plugin.");
            registrar.RegisterBoolSetting("test.hanging.start", "Hang on start", "test.hanging", false, "Hang in StartAsync until cancelled.");
            registrar.RegisterBoolSetting("test.hanging.stop", "Hang on stop", "test.hanging", false, "Hang in StopAsync until cancelled.");
        });
    }

    /// <inheritdoc/>
    public async ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _Context, context);
        context.Log.Info("started");
        if (context.ApplicationSettings.GetBoolSetting("test.hanging.start") == true)
        {
            // Waits for the token only: the lifecycle cancels it at the start timeout.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IPluginContext? context = Volatile.Read(ref _Context);
        context?.Log.Info("stopped");
        if (context?.ApplicationSettings.GetBoolSetting("test.hanging.stop") == true)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion
}
