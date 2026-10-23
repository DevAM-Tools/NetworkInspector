// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A plugin whose <c>Info</c> getter throws.</summary>
public sealed class InfoThrowsPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info => throw new InvalidOperationException("info boom");

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }
}
