// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Inspects candidate files with <c>System.Reflection.Metadata</c> and never loads an assembly: the loader later loads only
/// the files that hold at least one plugin entry type. Candidates are every <c>*.dll</c> below each configured folder
/// (recursively) and every configured file; each file is looked up in the scan cache by full path, length, and last write
/// time, and only misses are read.
/// <para>
/// Thread safety: stateless apart from the cache file; safe for concurrent use. Cache misses are inspected in parallel, each
/// worker writing its own slot of a pre-sized array.
/// </para>
/// </summary>
internal sealed class AssemblyPrescanner
{
    #region Constants

    private const string _ContractAssemblyName = "NetworkInspector.Plugins";

    #endregion

    #region Fields

    private readonly PrescanCache _Cache;

    #endregion

    #region Lifecycle

    /// <summary>Creates a pre-scanner over a cache.</summary>
    /// <param name="cache">The scan cache.</param>
    internal AssemblyPrescanner(PrescanCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _Cache = cache;
    }

    #endregion

    #region Internal API

    /// <summary>Scans the configured paths.</summary>
    /// <param name="paths">Configured folders and files; relative entries are resolved against the current directory.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Summary, plugin assemblies in path order, and configured paths that do not exist.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal PrescanOutcome Scan(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        long start = Stopwatch.GetTimestamp();

        // 1. Candidates: de-duplicated by full path, so overlapping configuration inspects and loads a file once.
        List<string> missing = [];
        List<string> candidates = _EnumerateCandidates(paths, missing, cancellationToken);

        // 2. Cache lookup by full path, length, and last write ticks; misses are inspected in parallel into a pre-sized array.
        IReadOnlyDictionary<string, PrescanResult> cached = _Cache.Read();
        PrescanResult?[] results = new PrescanResult?[candidates.Count];
        List<int> misses = [];
        for (int index = 0; index < candidates.Count; index++)
        {
            // A file that vanished since the enumeration (or cannot be read) is a miss: Inspect turns it into a non-plugin
            // result. Reading Length of a missing file would throw, so existence is checked first.
            FileInfo info = new(candidates[index]);
            if (info.Exists
                && cached.TryGetValue(info.FullName, out PrescanResult? hit)
                && hit.Length == info.Length
                && hit.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                results[index] = hit;
            }
            else
            {
                misses.Add(index);
            }
        }

        ParallelOptions parallel = new() { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, misses.Count, parallel, miss => results[misses[miss]] = Inspect(candidates[misses[miss]]));

        // 3. Write the cache only when something changed: new or changed files, or entries of files that vanished.
        List<PrescanResult> all = new(results.Length);
        List<PrescanResult> plugins = [];
        foreach (PrescanResult? result in results)
        {
            // Every slot was filled by the lookup or by the parallel loop above.
            PrescanResult filled = result ?? throw new InvalidOperationException("A candidate was neither cached nor inspected.");
            all.Add(filled);
            if (filled.IsPluginAssembly)
            {
                plugins.Add(filled);
            }
        }

        if (misses.Count > 0 || cached.Count != candidates.Count)
        {
            _Cache.Write(all);
        }

        // 4. Summary and the plugin list in a stable order, so load order does not depend on directory enumeration order.
        plugins.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        PrescanSummary summary = new(candidates.Count, misses.Count, candidates.Count - misses.Count, plugins.Count,
            Stopwatch.GetElapsedTime(start));
        return new PrescanOutcome(summary, plugins, missing);
    }

    /// <summary>
    /// Reads the metadata of one file. Every read problem — a native binary, garbage, an empty or truncated file, a net
    /// module, a locked file — yields a result without entry types; nothing escapes, because candidates come from a folder
    /// the user pointed at and most of them are not plugins.
    /// </summary>
    /// <param name="path">Full path of the candidate.</param>
    /// <returns>The result, with entry types when the file is a plugin assembly.</returns>
    internal static PrescanResult Inspect(string path)
    {
        FileInfo info = new(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
            using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata)
            {
                // A native binary has no CLI header.
                return _NotPlugin(info);
            }

            MetadataReader reader = pe.GetMetadataReader();
            if (!reader.IsAssembly || !_ReferencesContract(reader))
            {
                // A net module has no assembly manifest; an assembly that does not reference the contract cannot implement
                // an entry interface. The second check is a cheap reject before the type walk.
                return _NotPlugin(info);
            }

            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            return new PrescanResult(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, reader.GetString(assembly.Name),
                assembly.Version.ToString(), _ReadEntries(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may be locked for a moment (installer, scanner). Zeros never match a real file, so it is inspected again
            // on the next scan instead of being remembered as "not a plugin".
            return new PrescanResult(info.FullName, 0, 0, null, null, []);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException
            or OverflowException or NotSupportedException)
        {
            // A damaged or foreign image does not change by itself: remembered for the current length and time.
            return _NotPlugin(info);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // The bytes of a candidate are untrusted input and the metadata reader may reject them in ways the documented set does
            // not list. One bad file must not end the scan for every other plugin (Parallel.For would rethrow it for the whole
            // load), and it is not remembered, because the cause is unknown: it is inspected again next time.
            return new PrescanResult(info.FullName, 0, 0, null, null, []);
        }
    }

    #endregion

    #region Private helpers

    // Result for a file without entry types. A file that vanished meanwhile gets zeros, so it is inspected again next time.
    private static PrescanResult _NotPlugin(FileInfo info)
    {
        bool exists = info.Exists;
        return new PrescanResult(info.FullName, exists ? info.Length : 0, exists ? info.LastWriteTimeUtc.Ticks : 0, null, null, []);
    }

    // True when the assembly references the contract assembly; every plugin tier's interface closure contains IPlugin.
    private static bool _ReferencesContract(MetadataReader reader)
    {
        foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
        {
            if (reader.StringComparer.Equals(reader.GetAssemblyReference(handle).Name, _ContractAssemblyName))
            {
                return true;
            }
        }

        return false;
    }

    // Entry types: classes (not interfaces, not abstract) that list a plugin entry interface in their own interface table.
    // Abstract classes and interfaces are bases, not entry points, and are skipped without a record.
    private static List<PrescanEntry> _ReadEntries(MetadataReader reader)
    {
        List<PrescanEntry> entries = [];
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0)
            {
                continue;
            }

            if (TierDetection.FromInterfaces(reader, type) is not PluginTier tier)
            {
                continue;
            }

            string name = _FullName(reader, type);
            string? problem = null;
            if (!_IsPublic(reader, type))
            {
                problem = Invariant($"Entry type '{name}' must be public.");
            }
            else if (type.GetGenericParameters().Count > 0)
            {
                problem = Invariant($"Entry type '{name}' must not be generic.");
            }

            entries.Add(new PrescanEntry(name, tier, problem));
        }

        // Ordinal order by full name: the load order of the entry types of one assembly must not depend on the order in which
        // a compiler happened to emit them (that follows the order of the source files).
        entries.Sort(static (left, right) => string.CompareOrdinal(left.TypeName, right.TypeName));
        return entries;
    }

    // Full name with the namespace of the outermost type and '+' between nested types, like Type.FullName.
    private static string _FullName(MetadataReader reader, TypeDefinition type)
    {
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return _FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        }

        string ns = reader.GetString(type.Namespace);
        return ns.Length == 0
            ? name
            : ns + "." + name;
    }

    // Public means visible outside the assembly: a public top-level type, or a nested public type of public declaring types.
    private static bool _IsPublic(MetadataReader reader, TypeDefinition type)
    {
        TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        if (visibility != TypeAttributes.NestedPublic)
        {
            return false;
        }

        return _IsPublic(reader, reader.GetTypeDefinition(type.GetDeclaringType()));
    }

    // Folders are searched recursively for *.dll (any case); a configured file is taken as named, whatever its extension.
    // Reparse points (symbolic links, junctions) are not followed into, so link loops end; files that are reparse points
    // (cloud placeholders) are still candidates.
    private static List<string> _EnumerateCandidates(IReadOnlyList<string> paths, List<string> missing,
        CancellationToken cancellationToken)
    {
        List<string> candidates = [];
        HashSet<string> seen = new(PrescanCache.PathComparer);
        HashSet<string> seenMissing = new(PrescanCache.PathComparer);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (string entry in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full;
            try
            {
                full = Path.GetFullPath(entry);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                if (seenMissing.Add(entry))
                {
                    missing.Add(entry);
                }

                continue;
            }

            if (Directory.Exists(full))
            {
                FileSystemEnumerable<string> files = new(full, static (ref FileSystemEntry file) => file.ToFullPath(), options)
                {
                    ShouldIncludePredicate = static (ref FileSystemEntry file) =>
                        !file.IsDirectory && file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
                    ShouldRecursePredicate = static (ref FileSystemEntry directory) =>
                        (directory.Attributes & FileAttributes.ReparsePoint) == 0,
                };
                foreach (string file in files)
                {
                    if (seen.Add(file))
                    {
                        candidates.Add(file);
                    }
                }
            }
            else if (File.Exists(full))
            {
                if (seen.Add(full))
                {
                    candidates.Add(full);
                }
            }
            else if (seenMissing.Add(full))
            {
                missing.Add(full);
            }
        }

        return candidates;
    }

    #endregion
}
