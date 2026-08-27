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

            return _TryDeserialize(stream, typeInfo, label, out value, out error);
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
    /// Seekable streams are size-checked in place; non-seekable streams are copied up to
    /// <see cref="SettingsFileAccess.MaxFileBytes"/>.
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
                if (remaining > SettingsFileAccess.MaxFileBytes)
                {
                    value = null;
                    error = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Configuration stream '{label}' exceeds {SettingsFileAccess.MaxFileBytes} bytes.");
                    return false;
                }

                return _TryDeserialize(stream, typeInfo, label, out value, out error);
            }
        }

        if (!_TryCopyBounded(stream, out MemoryStream? copy, out error))
        {
            value = null;
            return false;
        }

        using (copy)
        {
            return _TryDeserialize(copy, typeInfo, label, out value, out error);
        }
    }

    #endregion

    #region Deserialize and bounded copy

    /// <summary>
    /// Deserializes <paramref name="stream"/> with AOT-safe <paramref name="typeInfo"/>.
    /// Maps JSON/I/O failures to <paramref name="error"/>; does not close the stream.
    /// </summary>
    private static bool _TryDeserialize<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        try
        {
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
    /// Copies <paramref name="source"/> into a rewindable buffer, failing when the payload
    /// would exceed <see cref="SettingsFileAccess.MaxFileBytes"/>. Used for non-seekable streams.
    /// </summary>
    private static bool _TryCopyBounded(Stream source, [NotNullWhen(true)] out MemoryStream? copy, out string? error)
    {
        MemoryStream bufferStream = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            long total = 0;
            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (read > SettingsFileAccess.MaxFileBytes - total)
                {
                    error = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Configuration stream exceeds {SettingsFileAccess.MaxFileBytes} bytes.");
                    bufferStream.Dispose();
                    copy = null;
                    return false;
                }

                bufferStream.Write(buffer, 0, read);
                total += read;
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
