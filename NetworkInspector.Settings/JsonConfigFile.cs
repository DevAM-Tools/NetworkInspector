// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Internal pure-function helper that loads and deserializes a JSON file into a typed
/// configuration object. Used by <see cref="SettingsManagerExtensions"/> and directly
/// testable in isolation. File loads and stream loads both take explicit size and JSON-depth limits.
/// <para>
/// Handles path resolution, existence checks, and all I/O and deserialization exceptions,
/// mapping each failure mode to a human-readable error message. Expected failures are returned as an error string, never
/// thrown, because a bad or missing configuration file is normal input, not a bug.
/// </para>
/// <para>
/// Security decisions for file loads. Threat 1, path traversal: the path names a file that a settings value refers to (so
/// it is not fully trusted); it is resolved with <see cref="Path.GetFullPath(string)"/> and must stay under a mandatory
/// base directory, and a missing base denies the load. Accepted residual risk: a symbolic link or junction inside the base
/// that points outside is followed, because the check is on the normalized path text and does not resolve links. Threat 2, resource
/// exhaustion: the size and depth limits (see <see cref="SettingsLimits.MaxFileSize"/> and
/// <see cref="SettingsLimits.MaxJsonDepth"/>) bound memory, parse time, and recursion. Threat 3, information
/// disclosure: error text uses only the file name, never the resolved path.
/// </para>
/// </summary>
internal static class JsonConfigFile
{
    #region Loading a file or a stream

    /// <summary>
    /// Attempts to load and deserialize a JSON file at <paramref name="filePath"/> with explicit size and depth limits.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="filePath">Absolute or relative path to the JSON file.</param>
    /// <param name="baseDirectory">
    /// Required directory that confines <paramref name="filePath"/>.
    /// Paths that resolve outside the base after normalization (including through <c>..</c> segments) are rejected.
    /// When <see langword="null"/> or whitespace, the load fails (default-deny).
    /// </param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="maxFileSize">
    /// A value of <see cref="SettingsLimits.MaxFileSize"/>; <see cref="LimitValue.Off"/> removes the size check.
    /// </param>
    /// <param name="maxJsonDepth">
    /// A value of <see cref="SettingsLimits.MaxJsonDepth"/>; <see cref="LimitValue.Off"/> removes the depth check
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
        LimitValue maxFileSize,
        LimitValue maxJsonDepth,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        // Confine the path first; nothing touches the file system before this check passes.
        if (!_TryResolvePath(filePath, baseDirectory, out string resolvedPath, out error))
        {
            value = null;
            return false;
        }

        // The label (file name only) is what error text may show; the resolved path stays out of messages.
        string label = SettingsFileAccess.SafeFileLabel(filePath);
        try
        {
            // Cheap early reject by the length the file system reports, before any byte is parsed.
            using FileStream stream = SettingsFileAccess.OpenSharedRead(resolvedPath);

            // The ceiling is long.MaxValue when the limit is off, so the same code runs with and without a cap.
            long maxBytes = maxFileSize.Ceiling;
            if (stream.Length > maxBytes)
            {
                error = _ExceedsMessage(label, maxFileSize);
                value = null;
                return false;
            }

            // The cap also holds for the bytes actually read: the file can grow between the length check and the
            // parse. A parse cut at the cap reports the size, not the syntax error the cut produced.
            using BoundedReadStream bounded = new(stream, maxBytes);
            bool loaded = _TryDeserialize(bounded, typeInfo, label, maxJsonDepth, out value, out error);
            if (bounded.Exceeded)
            {
                error = _ExceedsMessage(label, maxFileSize);
                value = null;
                return false;
            }

            // The parser reports a too deep file as a plain failure that names neither the cause nor the limit; the probe counts the
            // nesting so that the error names the limit, like the size limit does. It runs only after a failure.
            if (!loaded && JsonDepthProbe.FileExceeds(resolvedPath, maxBytes, maxJsonDepth))
            {
                error = _TooDeepMessage(label, maxJsonDepth);
                value = null;
                return false;
            }

            return loaded;
        }
        catch (FileNotFoundException)
        {
            error = Invariant($"Configuration file not found: {label}");
            value = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = Invariant($"Configuration file not found: {label}");
            value = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = Invariant($"Access denied reading '{label}'.");
            value = null;
            return false;
        }
        catch (IOException)
        {
            error = Invariant($"Failed to read '{label}'.");
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to deserialize JSON from <paramref name="stream"/> without closing it. The serializer reads forward only, so
    /// one bounded path serves seekable and non-seekable streams alike: the stream is read through a size cap and parsed with the
    /// depth limit.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">Readable stream positioned at the JSON payload. Not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="label">Safe display label used in error text (not a filesystem path).</param>
    /// <param name="limits">The size and depth limits; validated by the caller.</param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="error">On failure a human-readable description; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> on any failure.</returns>
    /// <remarks>
    /// Security: a stream is as untrusted as a file. Posture: secure by default; the caller passes other limits when it needs
    /// them. Rejected: "the caller bounds the stream", because the first caller that forgets has no protection, and a copy of a
    /// non-seekable stream into memory without end. Residual risk: with the limits off the stream is read without a bound.
    /// A too deep stream cannot be read a second time, so its error is the parser's, without the key of the limit.
    /// </remarks>
    internal static bool TryLoadFromStream<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        SettingsLimitValues limits,
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

        using BoundedReadStream bounded = new(stream, limits.MaxFileSize.Ceiling);
        bool loaded = _TryDeserialize(bounded, typeInfo, label, limits.MaxJsonDepth, out value, out error);
        if (bounded.Exceeded)
        {
            error = _ExceedsMessage(label, limits.MaxFileSize);
            value = null;
            return false;
        }

        return loaded;
    }

    #endregion

    #region Deserialization and messages

    /// <summary>The hit sentence for a file above the size limit (reported length or bytes read).</summary>
    /// <param name="label">The file name without folder.</param>
    /// <param name="maxFileSize">The limit that was in effect.</param>
    /// <returns>The event, the limit in its unit, the key, and the hint of the library.</returns>
    private static string _ExceedsMessage(string label, LimitValue maxFileSize) =>
        SettingsLimits.MaxFileSize.HitMessage(
            Invariant($"Configuration file '{SettingValidation.Quote(label)}' is larger than allowed"),
            maxFileSize,
            SettingsLimits.ChangeHint(SettingsLimits.MaxFileSize));

    /// <summary>The hit sentence for a file that nests deeper than the depth limit.</summary>
    /// <param name="label">The file name without folder.</param>
    /// <param name="maxJsonDepth">The limit that was in effect.</param>
    /// <returns>The event, the limit in its unit, the key, and the hint of the library.</returns>
    private static string _TooDeepMessage(string label, LimitValue maxJsonDepth) =>
        SettingsLimits.MaxJsonDepth.HitMessage(
            Invariant($"Configuration file '{SettingValidation.Quote(label)}' nests deeper than allowed"),
            maxJsonDepth,
            SettingsLimits.ChangeHint(SettingsLimits.MaxJsonDepth));

    /// <summary>
    /// Deserializes <paramref name="stream"/> with AOT-safe <paramref name="typeInfo"/>.
    /// Maps JSON/I/O failures to <paramref name="error"/>; does not close the stream.
    /// <see cref="LimitValue.Off"/> for <paramref name="maxJsonDepth"/> raises System.Text.Json to its library ceiling.
    /// </summary>
    /// <typeparam name="T">Target configuration model type.</typeparam>
    /// <param name="stream">The readable stream; not closed.</param>
    /// <param name="typeInfo">AOT-compatible type info for deserialization.</param>
    /// <param name="label">Safe display label used in error text.</param>
    /// <param name="maxJsonDepth">A value of <see cref="SettingsLimits.MaxJsonDepth"/>.</param>
    /// <param name="value">On success the deserialized object; otherwise <see langword="null"/>.</param>
    /// <param name="error">On failure a human-readable description; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> on any failure.</returns>
    private static bool _TryDeserialize<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        LimitValue maxJsonDepth,
        [NotNullWhen(true)] out T? value,
        out string? error)
        where T : class
    {
        try
        {
            // The caller's options are read-only once they have been used, so the depth cap goes onto a copy and the type
            // info is re-resolved from it. This allocates per load, which is acceptable: configuration loads are cold.
            // Off maps to a very large ceiling, because System.Text.Json reads 0 as "default of 64".
            JsonSerializerOptions options = new(typeInfo.Options)
            {
                MaxDepth = SettingsManager.EffectiveJsonMaxDepth(maxJsonDepth)
            };
            JsonTypeInfo<T> boundInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            value = JsonSerializer.Deserialize(stream, boundInfo);

            // A JSON null literal deserializes to null — treat it as a malformed config
            if (value is null)
            {
                error = Invariant($"Deserializing '{label}' produced a null result. Expected a JSON object.");
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = Invariant($"Failed to parse JSON in '{SettingValidation.Quote(label)}': {SettingValidation.QuoteMessage(ex.Message)}");
            value = null;
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            // The serializer reports an unknown type discriminator, and a converter reports a token of the wrong type, this way.
            // Content of the file is a refused input here, not a bug of the caller.
            error = Invariant($"Failed to parse JSON in '{SettingValidation.Quote(label)}': {SettingValidation.QuoteMessage(ex.Message)}");
            value = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = Invariant($"Access denied reading '{label}'.");
            value = null;
            return false;
        }
        catch (IOException)
        {
            error = Invariant($"Failed to read '{label}'.");
            value = null;
            return false;
        }
        catch (ObjectDisposedException)
        {
            error = Invariant($"Failed to read '{label}'.");
            value = null;
            return false;
        }
    }

    #endregion

    #region Path resolution

    /// <summary>
    /// Resolves <paramref name="filePath"/> under <paramref name="baseDirectory"/>.
    /// Rejects a missing base and paths that resolve outside the base after
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    /// <param name="filePath">The path from the setting; absolute or relative to the base.</param>
    /// <param name="baseDirectory">The directory that confines the path; null or white space denies the load.</param>
    /// <param name="resolvedPath">The normalized full path when the method returns <see langword="true"/>.</param>
    /// <param name="error">The reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the path is usable and stays under the base.</returns>
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

        // Normalize both sides with GetFullPath (collapses ".." and "."), then compare as text. A rooted file path is
        // taken as is and must still land under the base, so an absolute path elsewhere is rejected, not trusted.
        string baseFullPath;
        try
        {
            baseFullPath = Path.GetFullPath(baseDirectory);
            string candidatePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(baseDirectory, filePath);
            resolvedPath = Path.GetFullPath(candidatePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Security: the path comes from a user-editable setting; a form the file system cannot use (a NUL character, a name
            // that is too long) is a refused input, not a bug of the caller, and must not stop a start.
            resolvedPath = string.Empty;
            error = "Configuration file path is not a valid path.";
            return false;
        }

        if (!_IsPathUnderBase(resolvedPath, baseFullPath))
        {
            resolvedPath = string.Empty;
            error = Invariant($"Configuration file path '{SettingsFileAccess.SafeFileLabel(filePath)}' resolves outside the allowed base directory.");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Returns true when <paramref name="path"/> is equal to or nested under <paramref name="baseFullPath"/>.</summary>
    /// <param name="path">The normalized full path to check.</param>
    /// <param name="baseFullPath">The normalized full path of the confining directory.</param>
    /// <returns><see langword="true"/> when the path is the base or lies below it.</returns>
    private static bool _IsPathUnderBase(string path, string baseFullPath)
    {
        // File names are case-insensitive on Windows and case-sensitive elsewhere; a wrong choice would either reject valid
        // paths or accept a sibling folder that differs only by case. The trailing separator on the prefix below prevents
        // a sibling such as "<base>-other" from matching "<base>".
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
