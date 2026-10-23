// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>An internal class: the pre-scan finds it and reports that it must be public.</summary>
internal sealed class InternalPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.Internal", "1.0.0", "MIT", "Not public.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }
}
