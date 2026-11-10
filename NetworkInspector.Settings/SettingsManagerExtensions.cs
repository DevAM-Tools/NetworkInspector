// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Extension methods on <see cref="IReadOnlySettingsManager"/> for loading external
/// JSON configuration files whose paths are stored as string settings.
/// <para>
/// A component registers a <c>string</c> setting (e.g. <c>"reader.config_file"</c>) that
/// holds a user-supplied file path. During its registration phase it calls
/// <see cref="TryLoadReferencedJsonConfig"/> to resolve the path, load the file,
/// and deserialize it into a typed configuration object — without duplicating the I/O
/// and error-handling boilerplate in every component.
/// </para>
/// <para>
/// Available to every project that references this library: components that load
/// configuration files at registration time.
/// </para>
/// </summary>
public static class SettingsManagerExtensions
{
    #region Referenced configuration files

    /// <summary>
    /// Reads the string setting identified by <paramref name="stringSettingName"/>,
    /// treats its value as a file path, and attempts to deserialize the referenced JSON
    /// file into <typeparamref name="T"/> using the AOT-safe <paramref name="typeInfo"/>.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <typeparam name="TSettings">
    /// Reader type. Constrained so <see cref="ReadOnlySettingsManagerView"/> is not boxed
    /// when the compile-time type is the struct.
    /// </typeparam>
    /// <param name="settings">The settings manager to look up the path setting in.</param>
    /// <param name="stringSettingName">
    /// Name of the registered <c>string</c> setting that holds the file path
    /// (e.g. <c>"reader.config_file"</c>).
    /// </param>
    /// <param name="typeInfo">AOT-compatible type info for JSON deserialization.</param>
    /// <param name="value">
    /// On success contains the deserialized object; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="warning">
    /// When the method returns <see langword="false"/> due to a load failure, contains a
    /// <see cref="SettingsLoadWarning"/> with <see cref="SettingsLoadWarningKind.ExternalConfigUnavailable"/>.
    /// <see langword="null"/> when the method returns <see langword="false"/> only because
    /// no path is configured (empty or whitespace setting value) — this is not an error.
    /// <see langword="null"/> on success.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the file was found, parsed, and deserialized successfully.
    /// <see langword="false"/> when no path is configured (<paramref name="warning"/> will be
    /// <see langword="null"/>) or when loading failed (<paramref name="warning"/> will be set).
    /// </returns>
    public static bool TryLoadReferencedJsonConfig<T, TSettings>(
        this TSettings settings,
        string stringSettingName,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out SettingsLoadWarning? warning)
        where T : class
        where TSettings : IReadOnlySettingsManager
    {
        string? filePath = settings.GetStringSetting(stringSettingName);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            // No path configured — not an error, caller should use defaults
            value = null;
            warning = null;
            return false;
        }

        // Relative paths are resolved against the settings folder, or the application folder when the manager does not persist.
        // Security: the path comes from a user-editable setting and is untrusted. JsonConfigFile.TryLoad confines it to this base
        // directory (traversal and escapes are refused) instead of this method sanitizing the string; the size and depth caps of
        // the manager bound the file. Failures come back as a warning, never as an exception, so a bad path cannot stop startup.
        string? baseDirectory = settings.StoragePath ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            value = null;
            warning = new SettingsLoadWarning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                _DeriveGroupName(stringSettingName),
                stringSettingName,
                "Cannot load an external configuration file because no storage path or application base directory is available.");
            return false;
        }

        if (!JsonConfigFile.TryLoad(
                filePath,
                baseDirectory,
                typeInfo,
                settings.Limits.MaxFileSize,
                settings.Limits.MaxJsonDepth,
                out value,
                out string? error))
        {
            // Derive the group name from the setting name (part before the last dot)
            string groupName = _DeriveGroupName(stringSettingName);

            warning = new SettingsLoadWarning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                groupName,
                stringSettingName,
                error ?? "Unknown error loading external configuration file.");
            return false;
        }

        warning = null;
        return true;
    }

    #endregion

    #region Group name derivation

    /// <summary>
    /// Derives a group name from a setting name by taking the portion before the last dot.
    /// For example, <c>"reader.config_file"</c> yields <c>"reader"</c>.
    /// Falls back to the full name when no dot is present.
    /// </summary>
    /// <param name="settingName">The key of the setting that holds the path.</param>
    /// <returns>The text before the last dot; the whole key when it has no dot or starts with one.</returns>
    private static string _DeriveGroupName(string settingName)
    {
        int dot = settingName.LastIndexOf('.');
        if (dot > 0)
        {
            return settingName[..dot];
        }
        return settingName;
    }

    #endregion
}
