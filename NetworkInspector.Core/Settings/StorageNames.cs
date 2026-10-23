// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Maps names of 1–256 characters to portable file and folder names (profiles, presets, plugin data folders). A name of at
/// most <see cref="MaxDirectLength"/> characters that is not a device name is its own storage name. Any other name becomes
/// its first 100 characters (lower case, trailing spaces and dots removed), <c>~</c>, and 16 lower-case hex digits of the
/// 64-bit FNV-1a hash of its upper-invariant UTF-16 form; <c>~</c> is not a name character, so a mapped and a direct name
/// never collide, and names that differ only in case map to the same storage name. A mapped entry keeps its full name in the
/// sidecar file <c>&lt;storage name&gt;.name</c> next to it. Stateless; thread-safe; sidecar writes are atomic (temporary
/// file, then move).
/// </summary>
public static class StorageNames
{
    #region Constants

    /// <summary>Most characters of a name (profiles, presets, sessions, plugins, source kinds, formats, labels).</summary>
    public const int MaxNameLength = 256;

    /// <summary>Longest name used as storage name unchanged; leaves room for suffixes such as <c>.json</c> and <c>.creating</c>.</summary>
    public const int MaxDirectLength = 120;

    /// <summary>Extension of the sidecar file that holds the full name of a mapped entry.</summary>
    public const string NameFileExtension = ".name";

    /// <summary>Characters of the name kept in front of the hash of a mapped storage name.</summary>
    private const int _MappedPrefixLength = 100;

    /// <summary>Number of hex digits of the hash.</summary>
    private const int _HashDigits = 16;

    /// <summary>Most bytes read from a sidecar; a larger file is not a name file.</summary>
    private const int _MaxSidecarBytes = 1024;

    /// <summary>FNV-1a 64-bit offset basis.</summary>
    private const ulong _FnvOffsetBasis = 14695981039346656037UL;

    /// <summary>FNV-1a 64-bit prime.</summary>
    private const ulong _FnvPrime = 1099511628211UL;

    #endregion

    #region Public API

    /// <summary>Maps a name to its storage name (see the type description).</summary>
    /// <param name="name">The name, 1–256 characters.</param>
    /// <returns>The file or folder name to use under the parent folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or longer than <see cref="MaxNameLength"/>.</exception>
    public static string ToStorageName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A name has 1 to {MaxNameLength} characters, got {name.Length}."), nameof(name));
        }

        if (name.Length <= MaxDirectLength && !IsDeviceName(name))
        {
            return name;
        }

        // Prefix: readable start of the name in lower case, so that names differing only in case share one storage name.
        string prefix = name[..Math.Min(name.Length, _MappedPrefixLength)].ToLowerInvariant().TrimEnd(' ', '.');
        ulong hash = _FnvOffsetBasis;
        foreach (char c in name.ToUpperInvariant())
        {
            hash = (hash ^ (byte)(c & 0xFF)) * _FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * _FnvPrime;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{prefix}~{hash:x16}");
    }

    /// <summary>Whether a storage name is a mapped one (its full name lives in a sidecar file).</summary>
    /// <param name="storageName">A storage name.</param>
    /// <returns><see langword="true"/> for the form <c>&lt;prefix&gt;~&lt;16 hex digits&gt;</c>.</returns>
    public static bool IsMapped(ReadOnlySpan<char> storageName)
    {
        // The tilde sits in front of exactly 16 lower-case hex digits at the end.
        if (storageName.Length < _HashDigits + 1 || storageName[^(_HashDigits + 1)] != '~')
        {
            return false;
        }

        foreach (char c in storageName[^_HashDigits..])
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the part of the name before the first dot is a reserved device name of Windows: <c>CON</c>, <c>PRN</c>,
    /// <c>AUX</c>, <c>NUL</c>, <c>COM1</c>–<c>COM9</c>, or <c>LPT1</c>–<c>LPT9</c>, in any case.
    /// </summary>
    /// <param name="name">A name or file name.</param>
    /// <returns><see langword="true"/> for a device name.</returns>
    public static bool IsDeviceName(ReadOnlySpan<char> name)
    {
        int dot = name.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0
            ? name
            : name[..dot];
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }

    /// <summary>
    /// Writes the sidecar of a mapped name when it is missing or does not hold a name that maps to the same storage name. A
    /// direct name needs no sidecar and succeeds without writing.
    /// </summary>
    /// <param name="parentDirectory">The folder that holds the entry and its sidecar; it must exist.</param>
    /// <param name="name">The full name.</param>
    /// <param name="problem">Why the sidecar was not written; <see langword="null"/> on success.</param>
    /// <returns><see langword="false"/> when the sidecar names a different name with the same storage name (a collision) or
    /// the file cannot be written.</returns>
    /// <exception cref="ArgumentException"><paramref name="parentDirectory"/> is empty, or <paramref name="name"/> is not 1–256 characters.</exception>
    public static bool TryWriteNameFile(string parentDirectory, string name, [NotNullWhen(false)] out string? problem) =>
        TryWriteNameFile(parentDirectory, name, ToStorageName, out problem);

    /// <summary>The mapping is a parameter so that tests can force two names onto one storage name.</summary>
    internal static bool TryWriteNameFile(string parentDirectory, string name, Func<string, string> toStorageName,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        string storageName = toStorageName(name);
        problem = null;
        if (!IsMapped(storageName))
        {
            return true;
        }

        // A sidecar that holds another name for the same storage name is a hash collision; refusing is the only safe answer.
        if (TryReadName(parentDirectory, storageName, toStorageName, out string? existing)
            && !string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
        {
            problem = string.Create(CultureInfo.InvariantCulture,
                $"The name '{name}' collides with the existing name '{existing}' (same storage name '{storageName}').");
            return false;
        }

        string path = Path.Combine(parentDirectory, storageName + NameFileExtension);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _DeleteQuietly(temporary);
            problem = string.Create(CultureInfo.InvariantCulture, $"The name file '{path}' cannot be written: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads the full name of a mapped entry from its sidecar: at most 1,024 bytes of UTF-8, accepted only when the content is a
    /// valid name that maps back to <paramref name="storageName"/>.
    /// </summary>
    /// <param name="parentDirectory">The folder that holds the entry and its sidecar.</param>
    /// <param name="storageName">The storage name of the entry.</param>
    /// <param name="name">The full name; <see langword="null"/> when there is no usable sidecar.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is set.</returns>
    public static bool TryReadName(string parentDirectory, string storageName, [NotNullWhen(true)] out string? name) =>
        TryReadName(parentDirectory, storageName, ToStorageName, out name);

    /// <summary>The mapping is a parameter so that tests can force two names onto one storage name.</summary>
    internal static bool TryReadName(string parentDirectory, string storageName, Func<string, string> toStorageName,
        [NotNullWhen(true)] out string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        name = null;
        string path = Path.Combine(parentDirectory, storageName + NameFileExtension);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[_MaxSidecarBytes + 1];
            int length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += read;
            }

            if (length is 0 or > _MaxSidecarBytes)
            {
                return false;
            }

            // Strict decoding: invalid UTF-8 is not a name.
            string content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
            if (content.Length > MaxNameLength || !string.Equals(toStorageName(content), storageName, StringComparison.Ordinal))
            {
                return false;
            }

            name = content;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Deletes the sidecar of a mapped entry; a missing sidecar or a failed delete is ignored (a leftover sidecar is harmless).</summary>
    /// <param name="parentDirectory">The folder that holds the entry and its sidecar.</param>
    /// <param name="storageName">The storage name of the entry.</param>
    public static void DeleteNameFile(string parentDirectory, string storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        _DeleteQuietly(Path.Combine(parentDirectory, storageName + NameFileExtension));
    }

    #endregion

    #region Private Helpers

    /// <summary>Deletes a file and ignores I/O and access errors (cleanup paths).</summary>
    private static void _DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup only: the file is a leftover and the next write replaces it.
        }
    }

    #endregion
}
