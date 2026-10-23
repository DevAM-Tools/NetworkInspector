// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A plugin whose name is not an identifier.</summary>
public sealed class InvalidInfoPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("bad name!", "1.0.0", "MIT", "Invalid name.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }
}
