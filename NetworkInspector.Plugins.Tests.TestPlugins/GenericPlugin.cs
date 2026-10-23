// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A generic class: the pre-scan finds it and reports that it must not be generic.</summary>
/// <typeparam name="T">Makes the class generic.</typeparam>
public sealed class GenericPlugin<T> : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.Generic", "1.0.0", "MIT", "Generic.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        _ = typeof(T);
    }
}
