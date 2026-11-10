// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Loads typed JSON configuration from a caller-owned <see cref="Stream"/>
/// (memory, files already opened by the caller, or other readable sources).
/// Does not close the stream. The load is bounded by default: it reads at most <see cref="SettingsLimits.MaxFileSize"/> bytes and
/// accepts at most <see cref="SettingsLimits.MaxJsonDepth"/> levels of nesting. A caller that holds a <see cref="SettingsManager"/> passes
/// <see cref="SettingsManager.Limits"/> so that the stream follows the same limits as the files of the manager.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> stateless; all methods are safe for concurrent callers
/// provided each call uses its own stream. The stream itself is not synchronized.</para>
/// <para>Security: a stream is as untrusted as a file, so the default is secure. Rejected: unbounded by default with a note that the
/// caller must bound it, because the first caller to forget would have no protection.</para>
/// </remarks>
public static class JsonConfigStream
{
    #region Loading a stream

    /// <summary>
    /// Deserializes JSON from <paramref name="stream"/> using AOT-safe <paramref name="typeInfo"/>.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the start of the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for JSON deserialization.</param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="error">On failure a human-readable description; <see langword="null"/> on success.</param>
    /// <param name="limits">
    /// The size and depth limits, or <see langword="null"/> for <see cref="SettingsLimitValues.Default"/>. A value outside the range of
    /// its definition is refused with an exception, like in the constructor of <see cref="SettingsManager"/>.
    /// </param>
    /// <returns><see langword="true"/> when deserialization succeeded.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/> or <paramref name="typeInfo"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="limits"/> is outside the range of its definition.</exception>
    public static bool TryLoad<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out string? error,
        SettingsLimitValues? limits = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(typeInfo);
        limits?.Validate();
        return JsonConfigFile.TryLoadFromStream(
            stream, typeInfo, "configuration stream", limits ?? SettingsLimitValues.Default, out value, out error);
    }

    /// <summary>
    /// Deserializes JSON from <paramref name="stream"/> and maps failure to a
    /// <see cref="SettingsLoadWarning"/> so callers can decide whether to continue.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the start of the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for JSON deserialization.</param>
    /// <param name="groupName">
    /// Warning group identifier (e.g. <c>reader</c>). Not format-validated; caller supplies
    /// the identifier that should appear on the warning.
    /// </param>
    /// <param name="settingName">
    /// Warning setting identifier (e.g. <c>reader.config_file</c>). Not format-validated.
    /// </param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="warning">
    /// Set when the method returns <see langword="false"/>; <see langword="null"/> on success.
    /// </param>
    /// <param name="limits">
    /// The size and depth limits, or <see langword="null"/> for <see cref="SettingsLimitValues.Default"/>.
    /// </param>
    /// <returns><see langword="true"/> when deserialization succeeded.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/>, <paramref name="typeInfo"/>,
    /// <paramref name="groupName"/>, or <paramref name="settingName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="limits"/> is outside the range of its definition.</exception>
    public static bool TryLoad<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string groupName,
        string settingName,
        [NotNullWhen(true)] out T? value,
        out SettingsLoadWarning? warning,
        SettingsLimitValues? limits = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(groupName);
        ArgumentNullException.ThrowIfNull(settingName);
        limits?.Validate();

        if (!JsonConfigFile.TryLoadFromStream(
                stream, typeInfo, "configuration stream", limits ?? SettingsLimitValues.Default, out value, out string? error))
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
