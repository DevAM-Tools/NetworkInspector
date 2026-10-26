// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The core-tier plugins that ship with the application, in their fixed load order. The application passes them as
/// instances to the plugin loader (<c>PluginLoaderOptions.BuiltInPlugins</c>); they are never found by folder scan, so a
/// copy of this assembly in a plugin folder is reported as a duplicate name instead of being loaded twice. Stateless;
/// thread-safe.
/// </summary>
public static class NetworkInspectorBuiltIns
{
    /// <summary>
    /// Creates new instances of the four built-in plugins: StandardProtocols, FileSources, RandomSource, Exporters. The
    /// order is the load order: stack contributions run and plugins start in this order and stop in reverse, and the
    /// registrations keep it in the lists the application shows. The standard protocols come first, before the stack
    /// contribution of any other plugin.
    /// </summary>
    /// <returns>A new list with new plugin instances on every call, so that two loaders never share plugin state.</returns>
    public static IReadOnlyList<IPlugin> CreateAll() =>
        [new StandardProtocolsPlugin(), new FileSourcesPlugin(), new RandomSourcePlugin(), new ExportersPlugin()];
}
