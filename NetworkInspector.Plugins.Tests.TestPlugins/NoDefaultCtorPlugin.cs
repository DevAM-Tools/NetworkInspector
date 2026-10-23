// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A public class without a public parameterless constructor.</summary>
public sealed class NoDefaultCtorPlugin : IPlugin
{
    /// <summary>Creates the plugin; the required argument is what makes the class unusable as an entry type.</summary>
    /// <param name="name">Plugin name.</param>
    public NoDefaultCtorPlugin(string name)
    {
        Info = new PluginInfo(name, "1.0.0", "MIT", "Needs a constructor argument.", []);
    }

    /// <inheritdoc/>
    public PluginInfo Info { get; }

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }
}
