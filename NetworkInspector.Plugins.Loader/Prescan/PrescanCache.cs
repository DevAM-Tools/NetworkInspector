// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The per-file pre-scan cache: one JSON file keyed by full path, length, and last write time (UTC). A cache that is missing,
/// unreadable, from another format version, or damaged is treated as empty and rebuilt by the next write — the cache is an
/// optimization, never a source of truth, so it has no size limit and never blocks plugin loading.
/// <para>
/// Security and limits. Threat: the file lies in the user's settings folder and could be replaced by garbage or by a very
/// large file. Mitigation: any parse or format problem yields an empty cache (and a rebuild), the JSON reader has its default
/// depth limit, and the cache decides nothing that is not re-verified: a hit only skips reading a file whose path, length, and
/// last write time are unchanged, and the loader still checks every entry type when it creates it. Size: no limit, by the
/// owner's decision (limits exist only at network boundaries); the accepted residual risk is that a huge cache file is read
/// into memory once per start.
/// </para>
/// <para>
/// Writes go to a unique temporary file in the cache folder and replace the cache with <see cref="File.Move(string, string, bool)"/>,
/// so a reader never sees a torn file and several processes can write at once (the last complete write wins).
/// </para>
/// <para>Thread safety: stateless apart from the file name; safe for concurrent use within one process and across processes.</para>
/// </summary>
internal sealed class PrescanCache
{
    #region Constants

    /// <summary>
    /// Format version stored in the file; a file with another version is ignored. Raise it whenever the layout of the file
    /// changes, including the numeric <see cref="PluginTier"/> values that are stored as numbers.
    /// </summary>
    internal const int FormatVersion = 1;

    #endregion

    #region Fields

    private readonly string _File;

    #endregion

    #region Lifecycle

    /// <summary>Creates a cache over a file.</summary>
    /// <param name="file">Full path of the cache file.</param>
    internal PrescanCache(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _File = file;
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Path comparison for cache keys and de-duplication: the file systems of Windows and macOS ignore case, Linux does not.
    /// </summary>
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsLinux()
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    /// <summary>Reads the cache; any problem with the file yields an empty result.</summary>
    /// <returns>The cached results by full path.</returns>
    internal IReadOnlyDictionary<string, PrescanResult> Read()
    {
        Dictionary<string, PrescanResult> results = new(PathComparer);
        PrescanCacheFile? document;
        try
        {
            // A missing file is the normal first start, not an error.
            if (!File.Exists(_File))
            {
                return results;
            }

            // FileShare.ReadWrite | Delete is what lets another process replace the cache (Write moves a new file over it)
            // while this one reads; without Delete the replace would fail on Windows. The stream is deserialized directly, so
            // the file is never held as one string or byte array.
            using FileStream stream = new(_File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
            document = JsonSerializer.Deserialize(stream, PrescanCacheJsonContext.Default.PrescanCacheFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Garbage, a directory in place of the file, a locked file: all mean "no usable cache".
            return results;
        }

        // Deserialization ignores nullable annotations: a damaged or foreign file can leave Entries null, and a file of another
        // format version must not be interpreted at all.
        if (document is null || document.Version != FormatVersion || document.Entries is null)
        {
            return results;
        }

        // Entries are converted one by one with a format check; the result is keyed by full path with the platform's path
        // comparison, so a lookup finds the entry whatever the case of the configured path on a case-insensitive system.
        foreach (PrescanCacheFileEntry entry in document.Entries)
        {
            if (!_TryConvert(entry, out PrescanResult? result))
            {
                // One damaged entry makes the whole file untrustworthy: rebuild instead of mixing.
                results.Clear();
                return results;
            }

            results[result.Path] = result;
        }

        return results;
    }

    /// <summary>
    /// Replaces the cache with the given results (only the current ones: entries of vanished files are dropped). Failures are
    /// swallowed — the next start inspects again — and a leftover temporary file is removed.
    /// </summary>
    /// <param name="results">Results of the scan that just finished.</param>
    internal void Write(IReadOnlyList<PrescanResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        // Map the in-memory results to the file DTOs. The tier is stored as its number (see FormatVersion).
        List<PrescanCacheFileEntry> entries = new(results.Count);
        foreach (PrescanResult result in results)
        {
            List<PrescanCacheTypeEntry> types = new(result.Entries.Count);
            foreach (PrescanEntry entry in result.Entries)
            {
                types.Add(new PrescanCacheTypeEntry(entry.TypeName, (int)entry.Tier, entry.Problem));
            }

            entries.Add(new PrescanCacheFileEntry(result.Path, result.Length, result.LastWriteUtcTicks, result.AssemblyName,
                result.AssemblyVersion, types));
        }

        // Write to a unique temporary file in the same folder, then move it over the cache. The same folder keeps the move on one
        // volume, where it replaces the target in one step, so a reader sees the old or the new file and never a torn one;
        // a unique name lets two processes write at once (the last complete move wins). Rejected: writing the cache file
        // in place, which a crash or a second process could leave half written.
        PrescanCacheFile document = new(FormatVersion, entries);
        string? folder = Path.GetDirectoryName(_File);
        string temp = Path.Combine(folder ?? string.Empty, Invariant($"{Path.GetFileName(_File)}.{Guid.NewGuid():N}.tmp"));
        try
        {
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // CreateNew fails instead of reusing a leftover; the stream is closed (flushed) before the move.
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, PrescanCacheJsonContext.Default.PrescanCacheFile);
            }

            File.Move(temp, _File, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No cache this time. A stale temporary file would only waste space, so remove it best effort.
            _DeleteQuietly(temp);
        }
    }

    #endregion

    #region Private helpers

    /// <summary>Format check of one entry: a damaged value (empty path, negative length, tier outside 1..3) rejects the file.</summary>
    /// <param name="entry">The entry as deserialized.</param>
    /// <param name="result">The converted result; <see langword="null"/> when the entry is damaged.</param>
    /// <returns><see langword="true"/> when the entry is well formed.</returns>
    private static bool _TryConvert(PrescanCacheFileEntry entry, [NotNullWhen(true)] out PrescanResult? result)
    {
        result = null;
        if (string.IsNullOrEmpty(entry.Path) || entry.Length < 0 || entry.LastWriteUtcTicks < 0 || entry.Entries is null)
        {
            return false;
        }

        List<PrescanEntry> types = new(entry.Entries.Count);
        foreach (PrescanCacheTypeEntry type in entry.Entries)
        {
            if (string.IsNullOrEmpty(type.TypeName) || type.Tier < (int)PluginTier.Core || type.Tier > (int)PluginTier.App)
            {
                return false;
            }

            types.Add(new PrescanEntry(type.TypeName, (PluginTier)type.Tier, type.Problem));
        }

        result = new PrescanResult(entry.Path, entry.Length, entry.LastWriteUtcTicks, entry.AssemblyName, entry.AssemblyVersion, types);
        return true;
    }

    /// <summary>Removes a leftover temporary file; a failure is ignored because the file only wastes space.</summary>
    /// <param name="path">The temporary file.</param>
    private static void _DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only: the temporary file has a unique name and nothing reads it.
        }
    }

    #endregion
}

/// <summary>The cache file as JSON.</summary>
/// <param name="Version">Format version, <see cref="PrescanCache.FormatVersion"/>.</param>
/// <param name="Entries">One entry per inspected file.</param>
internal sealed record PrescanCacheFile(int Version, List<PrescanCacheFileEntry> Entries);

/// <summary>One inspected file in the cache file.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Length">Length in bytes when it was inspected; 0 together with a 0 time marks an untrusted result.</param>
/// <param name="LastWriteUtcTicks">Last write time (UTC ticks) when it was inspected.</param>
/// <param name="AssemblyName">Assembly name; <see langword="null"/> for files that are not managed assemblies.</param>
/// <param name="AssemblyVersion">Assembly version; <see langword="null"/> for files that are not managed assemblies.</param>
/// <param name="Entries">The entry types found.</param>
internal sealed record PrescanCacheFileEntry(string Path, long Length, long LastWriteUtcTicks, string? AssemblyName,
    string? AssemblyVersion, List<PrescanCacheTypeEntry> Entries);

/// <summary>One entry type in the cache file; <paramref name="Tier"/> is the numeric <see cref="PluginTier"/>.</summary>
/// <param name="TypeName">Full name of the entry type.</param>
/// <param name="Tier">The numeric tier; the reader accepts 1 to 3 only.</param>
/// <param name="Problem">A problem decided from metadata, or <see langword="null"/>.</param>
internal sealed record PrescanCacheTypeEntry(string TypeName, int Tier, string? Problem);

/// <summary>Source-generated serializer metadata for the cache file (no reflection, camel-case names).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PrescanCacheFile))]
internal sealed partial class PrescanCacheJsonContext : JsonSerializerContext
{
}
