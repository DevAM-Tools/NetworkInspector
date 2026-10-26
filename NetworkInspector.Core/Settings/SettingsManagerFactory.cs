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
    /// <param name="maxConfigFileBytes">
    /// Maximum accepted size in bytes for settings and referenced protocol config files.
    /// Omit for <see cref="SettingsManager.DefaultMaxConfigFileBytes"/> (1 GiB).
    /// Values <c>&lt;= 0</c> disable the size check.
    /// </param>
    /// <param name="maxJsonDepth">
    /// Maximum JSON nesting depth for those same files.
    /// Omit for <see cref="SettingsManager.DefaultMaxJsonDepth"/> (1024).
    /// Values <c>&lt;= 0</c> disable the depth check.
    /// </param>
    /// <returns>
    /// A new <see cref="SettingsManager"/> configured with the resolved storage path,
    /// <see cref="SettingsManager.MaxConfigFileBytes"/>, and <see cref="SettingsManager.MaxJsonDepth"/>.
    /// </returns>
    public static SettingsManager Create(
        string? settingsPath = null,
        string? profileName = null,
        long maxConfigFileBytes = SettingsManager.DefaultMaxConfigFileBytes,
        int maxJsonDepth = SettingsManager.DefaultMaxJsonDepth)
    {
        string storagePath = ResolvePath(settingsPath, profileName);
        return new SettingsManager(storagePath, maxConfigFileBytes, maxJsonDepth);
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
    /// Optional profile name that becomes a sub-directory of the base directory (its storage name, see <see cref="StorageNames"/>).
    /// The syntax rules of <see cref="ProfileNames.TryValidateSyntax"/> apply: 1 to 256 ASCII letters, digits, spaces, dots,
    /// dashes, and underscores, no leading or trailing space or dot, and no device name.
    /// </param>
    /// <returns>
    /// The storage path: <paramref name="settingsPath"/> (as given, not made absolute) or the platform default, followed by
    /// the storage name of the profile when one is given. Absolute whenever <paramref name="settingsPath"/> is absolute or null.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="profileName"/> breaks the syntax rules of <see cref="ProfileNames.TryValidateSyntax"/>
    /// (path separators, traversal sequences, and every other character outside the allowed set included).
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

        // Security: the profile name becomes a folder name under the base directory, so it is a path-traversal vector. The
        // syntax rule is an allow-list (ASCII letters, digits, space, dot, dash, underscore; no separators and no leading dot,
        // so no ".." segment; no device names), and a name that breaks it is refused with an exception rather than altered.
        // One rule for every caller; reserved names stay allowed here because the hosts open "application" through
        // this method. User-given names are checked with ProfileNames.TryValidate before they get here. The folder is the
        // storage name, so long names stay valid folder names.
        if (!ProfileNames.TryValidateSyntax(profileName, out string? error))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Profile name '{profileName}': {error}."), nameof(profileName));
        }

        return Path.Combine(basePath, StorageNames.ToStorageName(profileName));
    }

    #endregion
}
