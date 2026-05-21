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
    /// <param name="filePath">
    /// Absolute or relative path to the JSON file. Resolved to an absolute path via
    /// <see cref="Path.GetFullPath(string)"/> before opening.
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
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        // Resolve relative paths to absolute so error messages show the full path
        string resolvedPath = Path.GetFullPath(filePath);

        if (!File.Exists(resolvedPath))
        {
            error = $"Configuration file not found: {resolvedPath}";
            value = null;
            return false;
        }

        try
        {
            // Use a stream to avoid loading the entire file into a string first
            using FileStream stream = File.OpenRead(resolvedPath);
            value = JsonSerializer.Deserialize(stream, typeInfo);

            // A JSON null literal deserializes to null — treat it as a malformed config
            if (value is null)
            {
                error = $"Deserializing '{resolvedPath}' produced a null result. Expected a JSON object.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Failed to parse JSON in '{resolvedPath}': {ex.Message}";
            value = null;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Access denied reading '{resolvedPath}': {ex.Message}";
            value = null;
            return false;
        }
        catch (IOException ex)
        {
            error = $"Failed to read '{resolvedPath}': {ex.Message}";
            value = null;
            return false;
        }
    }

    #endregion
}
