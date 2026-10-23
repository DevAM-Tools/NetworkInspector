// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Shared metadata of the built-in plugins. The built-ins ship with the application, so they carry the application
/// version and the license of the repository, and declare no private dependencies: the libraries they use are
/// application dependencies, listed by the application's own license page.
/// </summary>
internal static class BuiltInPluginInfo
{
    #region Constants

    /// <summary>License name of every built-in plugin.</summary>
    internal const string License = "MIT";

    #endregion

    #region Properties

    /// <summary>
    /// Version of this assembly without build metadata (<c>0.18.0</c> instead of <c>0.18.0+abc123</c>), so that the
    /// plugin list shows a clean version number. Empty when the assembly carries no informational version.
    /// </summary>
    internal static string Version { get; } = _ReadVersion();

    #endregion

    #region Internal API

    /// <summary>Creates the <see cref="PluginInfo"/> of a built-in plugin.</summary>
    /// <param name="name">Unique plugin name.</param>
    /// <param name="description">One or two sentences about what the plugin contributes.</param>
    /// <returns>The info record with the shared version and license and no dependencies.</returns>
    internal static PluginInfo Create(string name, string description) =>
        new(name, Version, License, description, []);

    #endregion

    #region Private Helpers

    /// <summary>Reads the informational version and removes the build metadata that starts at the first <c>+</c>.</summary>
    private static string _ReadVersion()
    {
        string? informational = typeof(BuiltInPluginInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is null)
        {
            return string.Empty;
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? informational
            : informational[..plus];
    }

    #endregion
}
