// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A plugin whose settings registration throws when it is tried.</summary>
public sealed class SettingsThrowsPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.SettingsThrows", "1.0.0", "MIT", "Settings registration throws.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder) =>
        builder.AddSettings(SettingScope.Application, static _ => throw new InvalidOperationException("settings boom"));
}
