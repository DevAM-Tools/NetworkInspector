// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>Fixed inputs of a <see cref="PluginLoader"/>.</summary>
/// <param name="ScanCacheFile">
/// Full path of the pre-scan cache file (<c>&lt;settings base&gt;/plugin-scan-cache.json</c>). The folder is created on the
/// first write. A missing, unreadable, or foreign file is treated as an empty cache.
/// </param>
/// <param name="BuiltInPlugins">
/// Plugin instances that are part of the application. They are loaded before any configured plugin, run through the same
/// configure path, and win name conflicts against configured plugins. The list and its entries must not be
/// <see langword="null"/> (the <see cref="PluginLoader"/> constructor checks). Their <c>Info</c>, <c>Configure</c>, and
/// commit run inside the same failure boundary as an external plugin's.
/// </param>
public sealed record PluginLoaderOptions(string ScanCacheFile, IReadOnlyList<IPlugin> BuiltInPlugins);
