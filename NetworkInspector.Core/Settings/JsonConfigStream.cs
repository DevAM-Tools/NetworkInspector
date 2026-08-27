// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Loads typed JSON configuration from a caller-owned <see cref="Stream"/>
/// (memory, files already opened by the caller, or other readable sources).
/// Does not close the stream. Size-capped at <c>1 MiB</c> like file-based config load.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> stateless; all methods are safe for concurrent callers
/// provided each call uses its own stream. The stream itself is not synchronized.</para>
/// </remarks>
public static class JsonConfigStream
{
    #region Public API

    /// <summary>
    /// Deserializes JSON from <paramref name="stream"/> using AOT-safe <paramref name="typeInfo"/>.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the start of the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for JSON deserialization.</param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="error">On failure a human-readable description; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when deserialization succeeded.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/> or <paramref name="typeInfo"/> is <see langword="null"/>.
    /// </exception>
    public static bool TryLoad<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonConfigFile.TryLoadFromStream(stream, typeInfo, "configuration stream", out value, out error);
    }

    /// <summary>
    /// Deserializes JSON from <paramref name="stream"/> and maps failure to a
    /// <see cref="SettingsLoadWarning"/> so callers can decide whether to continue.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the start of the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for JSON deserialization.</param>
    /// <param name="groupName">
    /// Warning group identifier (e.g. <c>signal_message</c>). Not format-validated; caller supplies
    /// the identifier that should appear on the warning.
    /// </param>
    /// <param name="settingName">
    /// Warning setting identifier (e.g. <c>signal_message.config_file</c>). Not format-validated.
    /// </param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="warning">
    /// Set when the method returns <see langword="false"/>; <see langword="null"/> on success.
    /// </param>
    /// <returns><see langword="true"/> when deserialization succeeded.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/>, <paramref name="typeInfo"/>,
    /// <paramref name="groupName"/>, or <paramref name="settingName"/> is <see langword="null"/>.
    /// </exception>
    public static bool TryLoad<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string groupName,
        string settingName,
        [NotNullWhen(true)] out T? value,
        out SettingsLoadWarning? warning)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(groupName);
        ArgumentNullException.ThrowIfNull(settingName);

        if (!JsonConfigFile.TryLoadFromStream(stream, typeInfo, "configuration stream", out value, out string? error))
        {
            warning = new SettingsLoadWarning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                groupName,
                settingName,
                error ?? "Unknown error loading configuration stream.");
            return false;
        }

        warning = null;
        return true;
    }

    #endregion
}
