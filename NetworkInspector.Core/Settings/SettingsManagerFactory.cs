// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Factory helpers for creating <see cref="SettingsManager"/> instances
/// with the standard profile-path resolution used across all NetworkInspector
/// applications (CLI, MCP host, GUI, …).
///
/// <para>
/// Path resolution rules:
/// <list type="number">
///   <item>
///     <b>Base directory</b> — if <c>settingsPath</c> is provided it is
///     used as-is; otherwise the default is
///     <c>%AppData%\NetworkInspector</c> on Windows and
///     <c>~/.config/NetworkInspector</c> (via <see cref="Environment.SpecialFolder.ApplicationData"/>)
///     on other platforms.
///   </item>
///   <item>
///     <b>Profile sub-directory</b> — if <c>profileName</c> is provided
///     the effective storage path becomes <c>&lt;base&gt;\&lt;profile&gt;</c>.
///     Otherwise the base directory itself is used.
///   </item>
/// </list>
/// </para>
/// </summary>
public static class SettingsManagerFactory
{
    #region Public API

    /// <summary>
    /// Creates a <see cref="SettingsManager"/> whose storage path is resolved from
    /// an optional explicit base directory and an optional profile name.
    /// </summary>
    /// <param name="settingsPath">
    /// Explicit base directory for settings storage.
    /// Pass <see langword="null"/> to use the platform default
    /// (<c>%AppData%\NetworkInspector</c>).
    /// </param>
    /// <param name="profileName">
    /// Optional profile name. When provided, settings are stored in a sub-directory
    /// named after the profile within the base directory.
    /// Pass <see langword="null"/> or an empty string to use the base directory directly.
    /// </param>
    /// <returns>
    /// A new <see cref="SettingsManager"/> configured with the resolved storage path.
    /// </returns>
    public static SettingsManager Create(string? settingsPath = null, string? profileName = null)
    {
        string storagePath = ResolvePath(settingsPath, profileName);
        return new SettingsManager(storagePath);
    }

    /// <summary>
    /// Resolves the effective storage path from an optional explicit base directory
    /// and an optional profile name, without creating a <see cref="SettingsManager"/>.
    /// Useful when the path itself is needed before constructing the manager.
    /// </summary>
    /// <param name="settingsPath">
    /// Explicit base directory. Pass <see langword="null"/> to use the platform default.
    /// </param>
    /// <param name="profileName">
    /// Optional profile name that becomes a sub-directory of the base directory.
    /// Must contain only alphanumeric characters, hyphens, and underscores.
    /// </param>
    /// <returns>The resolved absolute storage path.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="profileName"/> contains path separators or
    /// traversal sequences.
    /// </exception>
    public static string ResolvePath(string? settingsPath = null, string? profileName = null)
    {
        // Determine the base directory: explicit override or platform default.
        string basePath = settingsPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NetworkInspector");

        if (string.IsNullOrEmpty(profileName))
        {
            return basePath;
        }

        // Guard against path traversal: reject names with separators or ".."
        if (profileName.AsSpan().ContainsAny('/', '\\')
            || profileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Profile name must not contain path separators or '..' sequences.",
                nameof(profileName));
        }

        // Apply the optional profile as a sub-directory of the base path.
        return Path.Combine(basePath, profileName);
    }

    #endregion
}
