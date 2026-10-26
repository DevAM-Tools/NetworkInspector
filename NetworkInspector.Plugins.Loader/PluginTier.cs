// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Informational tier of a plugin and of an application. The plugin contracts carry no tier value: the tier of an entry type
/// follows from the most derived plugin interface it lists, and the loader only uses it to decide which plugins an
/// application can load and which configure methods apply.
/// </summary>
/// <remarks>
/// The numeric values are relied upon: the loader derives the number and the order of the tier steps from them (Core = 1, so
/// an App application needs <c>App - Core</c> = 2 steps, for Host and App), and the pre-scan cache stores the number and
/// accepts only 1 to 3. Keep them consecutive and change the cache format version when a value changes.
/// </remarks>
public enum PluginTier : byte
{
    /// <summary>Core tier: implements <c>IPlugin</c>; sources, exporters, protocols, settings. Every application loads these.</summary>
    Core = 1,

    /// <summary>Host tier: implements <c>IHostPlugin</c>; adds commands and host services. Needs a host.</summary>
    Host = 2,

    /// <summary>App tier: implements <c>IAppPlugin</c>; adds pages, widgets, popups. Needs the app.</summary>
    App = 3,
}
