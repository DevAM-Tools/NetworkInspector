// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Inspects candidate files with <c>System.Reflection.Metadata</c> and never loads an assembly: the loader later loads only
/// the files that hold at least one plugin entry type. Candidates are every <c>*.dll</c> below each configured folder
/// (recursively) and every configured file; each file is looked up in the scan cache by full path, length, and last write
/// time, and only misses are read.
/// <para>
/// Why metadata only: a configured folder mostly holds dependencies of plugins, not plugins. Loading every file to ask it
/// whether it is a plugin would run foreign code for all of them, lock the files, and cost more than reading the metadata
/// tables of the type definitions. The cache makes an unchanged start read no assembly content at all (see
/// <see cref="PrescanSummary"/>). Cache hits assume that equal length and last write time mean equal content; a file rewritten
/// with the same length within the timestamp resolution is not noticed.
/// </para>
/// <para>
/// Thread safety: stateless apart from the cache file; safe for concurrent use. Cache misses are inspected in parallel, each
/// worker writing its own slot of a pre-sized array.
/// </para>
/// </summary>
internal sealed class AssemblyPrescanner
{
    #region Constants

    // Simple name of the contract assembly that every plugin references. Used only as a cheap reject before the type walk;
    // the exact entry interfaces (and their assemblies) are matched by TierDetection.
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

        // Only the misses are read. Each worker writes its own slot of the pre-sized array (the indexes are distinct), so no
        // lock is needed, and the join at the end of Parallel.For publishes the slots to this thread. The degree is capped at
        // the core count so that a huge folder does not start more concurrent file reads than cores. Inspect turns a bad file into
        // a result instead of throwing, so one hostile candidate cannot fail the loop for the others; cancellation (and an
        // out-of-memory condition) are the only ways out.
        ParallelOptions parallel = new() { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, misses.Count, parallel, miss => results[misses[miss]] = Inspect(candidates[misses[miss]]));

        // 3. Write the cache only when something changed: new or changed files, or entries of files that vanished. The cache
        //    holds exactly the current candidates: an application that scans other paths replaces it, and the other one
        //    re-inspects at its next start (a cost, never a wrong answer). Results of unreadable files carry zeros and
        //    never match a real file, so they are re-inspected next time (see Inspect).
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
    internal static PrescanResult Inspect(string path) => Inspect(path, null);

    /// <summary>
    /// Reads the metadata of one file, like <see cref="Inspect(string)"/>, with a hook for tests that need a change of the file
    /// at the one moment that matters: after its content was read and before the result is built.
    /// </summary>
    /// <param name="path">Full path of the candidate.</param>
    /// <param name="afterContentRead">Called once when the content was read successfully; <see langword="null"/> in production.</param>
    /// <returns>The result, with entry types when the file is a plugin assembly.</returns>
    internal static PrescanResult Inspect(string path, Action? afterContentRead)
    {
        FileInfo info = new(path);
        long length = 0;
        long ticks = 0;
        try
        {
            // The stamps are taken before the content is read, not after. FileInfo reads length and time on first use, and a stamp
            // taken after the read could belong to a newer file than the content that was read: a file replaced during the read
            // would be remembered as "unchanged" with the old content and re-inspected only when it changed once more. A stamp
            // from before the read can only be older than the content, so a file that changed meanwhile differs from its stamp
            // at the next scan and is read again. Rejected: reading the stamps again after the read and comparing them; the
            // early stamp is enough for the cache to stay correct and costs no second file system call.
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;

            // FileShare.ReadWrite | Delete: the scan must not block an installer or a build that writes, replaces, or deletes the
            // file at the same moment. The file is read once and not kept open (SequentialScan, the usual 4 KiB buffer).
            // PrefetchMetadata copies the metadata block into memory in one read, so the reader does not issue many small
            // random reads against the file while it walks the type definitions.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
            using PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata)
            {
                // A native binary has no CLI header.
                return _NotPlugin(info.FullName, length, ticks);
            }

            MetadataReader reader = pe.GetMetadataReader();
            if (!reader.IsAssembly || !_ReferencesContract(reader))
            {
                // A net module has no assembly manifest; an assembly that does not reference the contract cannot implement
                // an entry interface. The second check is a cheap reject before the type walk.
                return _NotPlugin(info.FullName, length, ticks);
            }

            // The entry types are read inside the using scope: the reader works on the prefetched block, which is released with
            // the PEReader. The result carries the stamps taken before the read (see above), never a fresh look at the file.
            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            List<PrescanEntry> entries = _ReadEntries(reader);
            afterContentRead?.Invoke();
            return new PrescanResult(info.FullName, length, ticks, reader.GetString(assembly.Name),
                string.Create(CultureInfo.InvariantCulture, $"{assembly.Version}"), entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may be locked for a moment (installer, scanner). Zeros never match a real file, so it is inspected again
            // on the next scan instead of being remembered as "not a plugin". The result is still written to the cache file;
            // the zeros are what keep it from ever being a hit.
            return new PrescanResult(info.FullName, 0, 0, null, null, []);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException
            or OverflowException or NotSupportedException)
        {
            // A damaged or foreign image does not change by itself: remembered for the current length and time.
            return _NotPlugin(info.FullName, length, ticks);
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

    /// <summary>
    /// Result for a file without entry types, remembered for the stamps that were taken before the file was read.
    /// </summary>
    /// <param name="fullPath">Full path of the candidate.</param>
    /// <param name="length">Length taken before the read.</param>
    /// <param name="lastWriteUtcTicks">Last write time (UTC ticks) taken before the read.</param>
    private static PrescanResult _NotPlugin(string fullPath, long length, long lastWriteUtcTicks) =>
        new(fullPath, length, lastWriteUtcTicks, null, null, []);

    /// <summary>
    /// True when the assembly references the contract assembly; every plugin tier's interface closure contains IPlugin, so a
    /// plugin of any tier must reference it. The check compares names without allocating strings.
    /// </summary>
    /// <param name="reader">Metadata of the candidate.</param>
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

    /// <summary>
    /// Entry types: classes (not interfaces, not abstract) that list a plugin entry interface in their own interface table.
    /// Abstract classes and interfaces are bases, not entry points, and are skipped without a record.
    /// </summary>
    /// <param name="reader">Metadata of the plugin candidate.</param>
    /// <returns>The entry types in ordinal name order, including those with a declaration problem.</returns>
    private static List<PrescanEntry> _ReadEntries(MetadataReader reader)
    {
        List<PrescanEntry> entries = [];
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            // Cheap flag test first: the interface walk below is the expensive part and runs for every remaining type.
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0)
            {
                continue;
            }

            if (TierDetection.FromInterfaces(reader, type) is not PluginTier tier)
            {
                continue;
            }

            // An entry type that is found but cannot be used becomes an entry with a problem text (reported as a Failed record)
            // instead of being dropped, so the author learns why the plugin is missing. Public and non-generic can be decided
            // from metadata alone, which keeps the assembly unloaded.
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

    /// <summary>
    /// Full name with the namespace of the outermost type and '+' between nested types, like <see cref="Type.FullName"/>: the
    /// name <see cref="Assembly.GetType(string, bool)"/> accepts later when the entry type is created.
    /// </summary>
    /// <param name="reader">Metadata of the candidate.</param>
    /// <param name="type">The type definition.</param>
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

    /// <summary>
    /// Public means visible outside the assembly: a public top-level type, or a nested public type of public declaring types.
    /// </summary>
    /// <param name="reader">Metadata of the candidate.</param>
    /// <param name="type">The type definition.</param>
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

    /// <summary>
    /// Folders are searched recursively for *.dll (any case); a configured file is taken as named, whatever its extension.
    /// Reparse points (symbolic links, junctions) are not followed into, so link loops end; files that are reparse points
    /// (cloud placeholders) are still candidates. There is no cap on the number of candidates: a huge folder is read lazily,
    /// and the scan summary shows what it cost.
    /// </summary>
    /// <param name="paths">Configured folders and files.</param>
    /// <param name="missing">Receives the full paths (or the raw text) of entries that are neither a folder nor a file.</param>
    /// <param name="cancellationToken">Cancels the enumeration between configured entries.</param>
    /// <returns>The candidate files, each full path once, in configuration order.</returns>
    private static List<string> _EnumerateCandidates(IReadOnlyList<string> paths, List<string> missing,
        CancellationToken cancellationToken)
    {
        // Two de-duplication sets with the platform's path comparison: overlapping configured entries (a folder and a file in
        // it, or the same folder twice) must inspect and load a file once, and report a missing entry once.
        List<string> candidates = [];
        HashSet<string> seen = new(PrescanCache.PathComparer);
        HashSet<string> seenMissing = new(PrescanCache.PathComparer);

        // AttributesToSkip = 0: the default skips hidden and system files and folders, which would hide a plugin folder that is
        // marked hidden; every file that is not a folder is a candidate. Inaccessible folders are skipped instead of failing
        // the whole scan.
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

            // A custom enumerable instead of Directory.EnumerateFiles: the recursion predicate skips only directories that are
            // reparse points. The option AttributesToSkip = ReparsePoint would also drop files that are reparse points (cloud
            // placeholders), which must stay candidates. The predicates are static and work on the raw entry, so no per-entry
            // FileInfo or closure is allocated; only a matching file becomes a path string.
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
