// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>
/// A plugin that reports the name of the healthy test plugin, to test the duplicate-name rule. Its type name sorts after the
/// healthy plugin's, so the healthy plugin is admitted first and keeps the name (the entry types of one assembly load in
/// ordinal order of their full names).
/// </summary>
public sealed class LateDuplicateNamePlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.Healthy", "9.9.9", "MIT", "Same name as the healthy plugin.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }
}
