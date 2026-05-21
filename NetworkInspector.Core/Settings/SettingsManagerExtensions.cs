// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Extension methods on <see cref="IReadOnlySettingsManager"/> for loading external
/// JSON configuration files whose paths are stored as string settings.
/// <para>
/// Protocols register a <c>string</c> setting (e.g. <c>"can.config_file"</c>) that
/// holds a user-supplied file path. During the registration phase they call
/// <see cref="TryLoadReferencedJsonConfig{T}"/> to resolve the path, load the file,
/// and deserialize it into a typed configuration object — without duplicating the I/O
/// and error-handling boilerplate in every protocol.
/// </para>
/// <para>
/// Available to all projects that reference <c>NetworkInspector.Core</c>: protocol
/// assemblies, sources, exporters, and filters.
/// </para>
/// </summary>
public static class SettingsManagerExtensions
{
    #region Public API

    /// <summary>
    /// Reads the string setting identified by <paramref name="stringSettingName"/>,
    /// treats its value as a file path, and attempts to deserialize the referenced JSON
    /// file into <typeparamref name="T"/> using the AOT-safe <paramref name="typeInfo"/>.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="settings">The settings manager to look up the path setting in.</param>
    /// <param name="stringSettingName">
    /// Name of the registered <c>string</c> setting that holds the file path
    /// (e.g. <c>"can.config_file"</c>).
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
    public static bool TryLoadReferencedJsonConfig<T>(
        this IReadOnlySettingsManager settings,
        string stringSettingName,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out SettingsLoadWarning? warning)
        where T : class
    {
        string? filePath = settings.GetStringSetting(stringSettingName);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            // No path configured — not an error, caller should use defaults
            value = null;
            warning = null;
            return false;
        }

        if (!JsonConfigFile.TryLoad(filePath, typeInfo, out value, out string? error))
        {
            // Derive the group name from the setting name (part before the last dot)
            string groupName = DeriveGroupName(stringSettingName);

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

    #region Private helpers

    /// <summary>
    /// Derives a group name from a setting name by taking the portion before the last dot.
    /// For example, <c>"can.config_file"</c> yields <c>"can"</c>.
    /// Falls back to the full name when no dot is present.
    /// </summary>
    private static string DeriveGroupName(string settingName)
    {
        int dot = settingName.LastIndexOf('.');
        return dot > 0 ? settingName[..dot] : settingName;
    }

    #endregion
}
