// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Shared utility for loading JSON configuration files used by
/// config-driven protocols (CAN, Signal PDU, PDU Transport, SOME/IP).
/// <para>
/// Protocols store a file path in a string setting, then call
/// <see cref="Load{T}"/> during <c>OnStartCustom</c> to deserialize the JSON.
/// Uses <see cref="JsonTypeInfo{T}"/> for AOT-compatible deserialization.
/// </para>
/// </summary>
internal static class ConfigLoader
{
    #region Internal API

    /// <summary>
    /// Loads and deserializes a JSON configuration file using AOT-safe <see cref="JsonTypeInfo{T}"/>.
    /// </summary>
    /// <typeparam name="T">The configuration model type to deserialize into.</typeparam>
    /// <param name="filePath">Absolute or relative path to the JSON file.</param>
    /// <param name="typeInfo">The AOT-compatible type info for deserialization.</param>
    /// <param name="error">
    /// When the method returns <see langword="null"/> and the path was non-empty,
    /// contains a human-readable description of the failure; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>The deserialized config, or <see langword="null"/> on failure.</returns>
    internal static T? Load<T>(string? filePath, JsonTypeInfo<T> typeInfo, out string? error) where T : class
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            // No path configured — this is an expected "no config" scenario, not an error.
            error = null;
            return null;
        }

        // Resolve to absolute path
        string resolvedPath = Path.GetFullPath(filePath);

        if (!File.Exists(resolvedPath))
        {
            error = $"Configuration file not found: {resolvedPath}";
            return null;
        }

        try
        {
            // Read and deserialize — use stream for efficiency on large configs
            using FileStream stream = File.OpenRead(resolvedPath);
            T? result = JsonSerializer.Deserialize(stream, typeInfo);
            error = null;
            return result;
        }
        catch (JsonException ex)
        {
            error = $"Failed to parse JSON in '{resolvedPath}': {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            error = $"Failed to read '{resolvedPath}': {ex.Message}";
            return null;
        }
    }

    #endregion
}
