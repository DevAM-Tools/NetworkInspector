// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Shared utility for loading JSON configuration files used by
/// config-driven protocols (CAN, Signal Message, PDU Transport, SOME/IP).
/// <para>
/// Protocols store a file path in a string setting and deserialize the JSON during registration.
/// Uses <see cref="JsonTypeInfo{T}"/> (source-generated metadata) instead of reflection so that loading stays
/// trim- and AOT-compatible.
/// </para>
/// <para>
/// Note: the protocols in this assembly currently load their files through
/// <c>SettingsManagerExtensions.TryLoadReferencedJsonConfig</c> in <c>NetworkInspector.Core</c>, which also applies
/// the configured size cap and path rules. <see cref="Load{T}"/> has neither: it reads any readable file of any size,
/// which is acceptable only because the path comes from an operator-controlled setting and not from packet data.
/// </para>
/// <para><b>Thread safety:</b> stateless; safe for concurrent use.</para>
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
    /// A file whose JSON content is the literal <c>null</c> also returns <see langword="null"/> with no error.
    /// </param>
    /// <returns>The deserialized config, or <see langword="null"/> on failure or when no path is configured.</returns>
    internal static T? Load<T>(string? filePath, JsonTypeInfo<T> typeInfo, out string? error) where T : class
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            // No path configured — this is an expected "no config" scenario, not an error.
            error = null;
            return null;
        }

        // Resolve to absolute path. A path the OS cannot represent (for example one with a NUL character) is a
        // configuration error like a missing file, not an exception for the caller.
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid configuration path: {ex.Message}";
            return null;
        }

        // Existence is checked first only to give a precise "not found" message. The file can still vanish before
        // it is opened; that race surfaces as an IOException below and is reported as a read failure, not thrown.
        if (!File.Exists(resolvedPath))
        {
            error = $"Configuration file not found: {resolvedPath}";
            return null;
        }

        // Every expected failure (malformed JSON, access denied, I/O) becomes an error string so that a bad
        // configuration file never aborts stack construction.
        try
        {
            // Deserialize straight from the stream instead of reading the file into a string or byte array first,
            // so a large config does not allocate a second copy of itself.
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
        catch (UnauthorizedAccessException ex)
        {
            error = $"Access to '{resolvedPath}' is denied: {ex.Message}";
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
