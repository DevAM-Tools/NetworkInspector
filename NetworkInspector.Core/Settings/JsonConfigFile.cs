// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Internal pure-function helper that loads and deserializes a JSON file into a typed
/// configuration object. Contains no dependency on <see cref="SettingsManager"/>; used by
/// <see cref="SettingsManagerExtensions"/> and directly testable in isolation.
/// <para>
/// Handles path resolution, existence checks, and all I/O and deserialization exceptions,
/// mapping each failure mode to a human-readable error message.
/// </para>
/// </summary>
internal static class JsonConfigFile
{
    #region Internal API

    /// <summary>
    /// Attempts to load and deserialize a JSON file at <paramref name="filePath"/>.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="filePath">Absolute or relative path to the JSON file.</param>
    /// <param name="baseDirectory">
    /// Required directory that confines <paramref name="filePath"/>.
    /// Paths containing <c>..</c> segments or resolving outside the base are rejected.
    /// When <see langword="null"/> or whitespace, the load fails (default-deny).
    /// </param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="value">
    /// On success contains the deserialized object; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="error">
    /// On failure contains a human-readable description of the problem;
    /// <see langword="null"/> on success.
    /// </param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> on any failure.</returns>
    internal static bool TryLoad<T>(
        string filePath,
        string? baseDirectory,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        if (!_TryResolvePath(filePath, baseDirectory, out string resolvedPath, out error))
        {
            value = null;
            return false;
        }

        string label = SettingsFileAccess.SafeFileLabel(filePath);
        try
        {
            using FileStream stream = SettingsFileAccess.OpenSharedRead(resolvedPath);
            if (stream.Length > SettingsFileAccess.MaxFileBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Configuration file '{label}' exceeds {SettingsFileAccess.MaxFileBytes} bytes.");
                value = null;
                return false;
            }

            value = JsonSerializer.Deserialize(stream, typeInfo);

            // A JSON null literal deserializes to null — treat it as a malformed config
            if (value is null)
            {
                error = $"Deserializing '{label}' produced a null result. Expected a JSON object.";
                return false;
            }

            error = null;
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"Configuration file not found: {label}";
            value = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"Configuration file not found: {label}";
            value = null;
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Failed to parse JSON in '{label}': {ex.Message}";
            value = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = $"Access denied reading '{label}'.";
            value = null;
            return false;
        }
        catch (IOException)
        {
            error = $"Failed to read '{label}'.";
            value = null;
            return false;
        }
    }

    #endregion

    #region Path resolution

    /// <summary>
    /// Resolves <paramref name="filePath"/> under <paramref name="baseDirectory"/>.
    /// Rejects a missing base, <c>..</c> segments, and paths that resolve outside the base.
    /// </summary>
    private static bool _TryResolvePath(
        string filePath,
        string? baseDirectory,
        out string resolvedPath,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            resolvedPath = string.Empty;
            error = "Configuration file path is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            resolvedPath = string.Empty;
            error = "A base directory is required to load configuration files.";
            return false;
        }

        if (filePath.Contains("..", StringComparison.Ordinal))
        {
            resolvedPath = string.Empty;
            error = "Configuration file path must not contain '..' segments.";
            return false;
        }

        string baseFullPath = Path.GetFullPath(baseDirectory);
        string candidatePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(baseDirectory, filePath);
        resolvedPath = Path.GetFullPath(candidatePath);

        if (!_IsPathUnderBase(resolvedPath, baseFullPath))
        {
            resolvedPath = string.Empty;
            error = $"Configuration file path '{SettingsFileAccess.SafeFileLabel(filePath)}' resolves outside the allowed base directory.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Returns true when <paramref name="path"/> is equal to or nested under <paramref name="baseFullPath"/>.</summary>
    private static bool _IsPathUnderBase(string path, string baseFullPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (path.Equals(baseFullPath, comparison))
        {
            return true;
        }

        string prefix = baseFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    #endregion
}
