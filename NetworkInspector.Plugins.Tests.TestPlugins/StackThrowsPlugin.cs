// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A plugin whose stack contribution throws when it is tried.</summary>
public sealed class StackThrowsPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.StackThrows", "1.0.0", "MIT", "Stack contribution throws.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder) =>
        builder.AddStackContribution(static _ => throw new InvalidOperationException("stack boom"));
}
