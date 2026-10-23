// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>
/// Adds a source kind and then throws in <c>Configure</c>: nothing of this plugin may be published. The loader tests check
/// the reason text and that the source kind is unknown afterwards.
/// </summary>
public sealed class ConfigureThrowsPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.ConfigureThrows", "1.0.0", "MIT", "Throws in Configure.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        builder.AddSource(new SourceRegistration("TestConfigureThrows", "Test", "Never published.", [], false, static _ => { },
            TestFailures.CannotCreateSource));
        throw new InvalidOperationException("configure boom");
    }
}
