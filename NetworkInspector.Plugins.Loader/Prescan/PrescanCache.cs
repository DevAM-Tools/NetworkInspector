// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The per-file pre-scan cache: one JSON file keyed by full path, length, and last write time (UTC). A cache that is missing,
/// unreadable, from another format version, or damaged is treated as empty and rebuilt by the next write — the cache is an
/// optimization, never a source of truth, so it has no size limit and never blocks plugin loading.
/// <para>
/// Writes go to a unique temporary file in the cache folder and replace the cache with <see cref="File.Move(string, string, bool)"/>,
/// so a reader never sees a torn file and several processes can write at once (the last complete write wins).
/// </para>
/// <para>Thread safety: stateless apart from the file name; safe for concurrent use within one process and across processes.</para>
/// </summary>
internal sealed class PrescanCache
{
    #region Constants

    /// <summary>Format version stored in the file; a file with another version is ignored.</summary>
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
            if (!File.Exists(_File))
            {
                return results;
            }

            using FileStream stream = new(_File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
            document = JsonSerializer.Deserialize(stream, PrescanCacheJsonContext.Default.PrescanCacheFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Garbage, a directory in place of the file, a locked file: all mean "no usable cache".
            return results;
        }

        if (document is null || document.Version != FormatVersion || document.Entries is null)
        {
            return results;
        }

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

        PrescanCacheFile document = new(FormatVersion, entries);
        string? folder = Path.GetDirectoryName(_File);
        string temp = Path.Combine(folder ?? string.Empty, Invariant($"{Path.GetFileName(_File)}.{Guid.NewGuid():N}.tmp"));
        try
        {
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

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

    // Format check of one entry: a damaged value (empty path, negative length, tier outside 1..3) rejects the file.
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
internal sealed record PrescanCacheFileEntry(string Path, long Length, long LastWriteUtcTicks, string? AssemblyName,
    string? AssemblyVersion, List<PrescanCacheTypeEntry> Entries);

/// <summary>One entry type in the cache file; <paramref name="Tier"/> is the numeric <see cref="PluginTier"/>.</summary>
internal sealed record PrescanCacheTypeEntry(string TypeName, int Tier, string? Problem);

/// <summary>Source-generated serializer metadata for the cache file (no reflection, camel-case names).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PrescanCacheFile))]
internal sealed partial class PrescanCacheJsonContext : JsonSerializerContext
{
}
