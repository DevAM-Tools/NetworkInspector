// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Internal pure-function helper that loads and deserializes a JSON file into a typed
/// configuration object. Used by <see cref="SettingsManagerExtensions"/> and directly
/// testable in isolation. File loads take explicit size and JSON-depth caps; stream loads do not.
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
        return TryLoad(
            filePath,
            baseDirectory,
            typeInfo,
            SettingsManager.DefaultMaxConfigFileBytes,
            SettingsManager.DefaultMaxJsonDepth,
            out value,
            out error);
    }

    /// <summary>
    /// Attempts to load and deserialize a JSON file at <paramref name="filePath"/> with an explicit size cap
    /// and the default JSON depth cap.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="filePath">Absolute or relative path to the JSON file.</param>
    /// <param name="baseDirectory">
    /// Required directory that confines <paramref name="filePath"/>.
    /// Paths containing <c>..</c> segments or resolving outside the base are rejected.
    /// When <see langword="null"/> or whitespace, the load fails (default-deny).
    /// </param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="maxFileBytes">
    /// Maximum accepted file size in bytes. Values <c>&lt;= 0</c> disable the size check.
    /// </param>
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
        long maxFileBytes,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        return TryLoad(
            filePath,
            baseDirectory,
            typeInfo,
            maxFileBytes,
            SettingsManager.DefaultMaxJsonDepth,
            out value,
            out error);
    }

    /// <summary>
    /// Attempts to load and deserialize a JSON file at <paramref name="filePath"/> with explicit size and depth caps.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="filePath">Absolute or relative path to the JSON file.</param>
    /// <param name="baseDirectory">
    /// Required directory that confines <paramref name="filePath"/>.
    /// Paths containing <c>..</c> segments or resolving outside the base are rejected.
    /// When <see langword="null"/> or whitespace, the load fails (default-deny).
    /// </param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="maxFileBytes">
    /// Maximum accepted file size in bytes. Values <c>&lt;= 0</c> disable the size check.
    /// </param>
    /// <param name="maxJsonDepth">
    /// Maximum JSON nesting depth. Values <c>&lt;= 0</c> disable the depth check
    /// (System.Text.Json still has a library ceiling).
    /// </param>
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
        long maxFileBytes,
        int maxJsonDepth,
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
            if (maxFileBytes > 0 && stream.Length > maxFileBytes)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Configuration file '{label}' exceeds {maxFileBytes} bytes.");
                value = null;
                return false;
            }

            return _TryDeserialize(stream, typeInfo, label, maxJsonDepth, out value, out error);
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

    /// <summary>
    /// Attempts to deserialize JSON from <paramref name="stream"/> without closing it.
    /// Seekable streams are deserialized in place. Non-seekable streams are copied into a
    /// rewindable buffer. This method does not enforce a size or JSON-depth limit.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="label">Safe display label used in error text (not a filesystem path).</param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="error">On failure a human-readable description; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> on any failure.</returns>
    internal static bool TryLoadFromStream<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        if (!stream.CanRead)
        {
            value = null;
            error = "Configuration stream is not readable.";
            return false;
        }

        if (stream.CanSeek)
        {
            long remaining;
            try
            {
                remaining = stream.Length - stream.Position;
            }
            catch (NotSupportedException)
            {
                remaining = -1;
            }

            if (remaining >= 0)
            {
                return _TryDeserialize(stream, typeInfo, label, maxJsonDepth: 0, out value, out error);
            }
        }

        if (!_TryCopy(stream, out MemoryStream? copy, out error))
        {
            value = null;
            return false;
        }

        using (copy)
        {
            return _TryDeserialize(copy, typeInfo, label, maxJsonDepth: 0, out value, out error);
        }
    }

    #endregion

    #region Deserialize and stream copy

    /// <summary>
    /// Deserializes <paramref name="stream"/> with AOT-safe <paramref name="typeInfo"/>.
    /// Maps JSON/I/O failures to <paramref name="error"/>; does not close the stream.
    /// <paramref name="maxJsonDepth"/> values <c>&lt;= 0</c> raise System.Text.Json to its library ceiling.
    /// </summary>
    private static bool _TryDeserialize<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        int maxJsonDepth,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        try
        {
            JsonSerializerOptions options = new(typeInfo.Options)
            {
                MaxDepth = SettingsManager.EffectiveJsonMaxDepth(maxJsonDepth)
            };
            JsonTypeInfo<T> boundInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            value = JsonSerializer.Deserialize(stream, boundInfo);

            // A JSON null literal deserializes to null — treat it as a malformed config
            if (value is null)
            {
                error = $"Deserializing '{label}' produced a null result. Expected a JSON object.";
                return false;
            }

            error = null;
            return true;
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
        catch (ObjectDisposedException)
        {
            error = $"Failed to read '{label}'.";
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a rewindable buffer. Used for non-seekable streams.
    /// Does not enforce a size limit; the caller must bound untrusted streams.
    /// </summary>
    private static bool _TryCopy(Stream source, [NotNullWhen(true)] out MemoryStream? copy, out string? error)
    {
        MemoryStream bufferStream = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                bufferStream.Write(buffer, 0, read);
            }

            bufferStream.Position = 0;
            copy = bufferStream;
            error = null;
            return true;
        }
        catch (IOException)
        {
            bufferStream.Dispose();
            copy = null;
            error = "Failed to read configuration stream.";
            return false;
        }
        catch (ObjectDisposedException)
        {
            bufferStream.Dispose();
            copy = null;
            error = "Failed to read configuration stream.";
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Path resolution

    /// <summary>
    /// Resolves <paramref name="filePath"/> under <paramref name="baseDirectory"/>.
    /// Rejects a missing base and paths that resolve outside the base after
    /// <see cref="Path.GetFullPath(string)"/>.
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
