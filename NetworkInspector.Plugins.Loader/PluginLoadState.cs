// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>State of a plugin record.</summary>
public enum PluginLoadState : byte
{
    /// <summary>The plugin was configured and committed; it is started by the lifecycle.</summary>
    Loaded,

    /// <summary>Something went wrong; the record's reason says what. The application continues without the plugin.</summary>
    Failed,

    /// <summary>The plugin needs a higher tier than the application offers and was not loaded.</summary>
    Skipped,
}
