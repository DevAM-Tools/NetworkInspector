// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>An abstract base class: a base, not an entry type; the pre-scan skips it without a record.</summary>
public abstract class AbstractPluginBase : IPlugin
{
    /// <inheritdoc/>
    public abstract PluginInfo Info { get; }

    /// <inheritdoc/>
    public virtual void Configure(IPluginBuilder builder)
    {
    }
}
