// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// One collectible load context per plugin assembly. An assembly that the default context already has — loaded there, or
/// listed in <c>TRUSTED_PLATFORM_ASSEMBLIES</c> (the shared framework and the application's dependency closure, which includes
/// every contract and Core) — always comes from the default context, so contract types keep one identity across the
/// application and its plugins; a plugin that ships its own copy of such an assembly gets the application's copy. Other
/// dependencies resolve from the plugin folder.
/// <para>
/// Decisions. The match is by assembly name: the application's copy of a shared assembly is the only one, so a plugin cannot
/// bring its own copy (side-by-side loading of shared assemblies is not supported). A plugin built against the same or an
/// older version runs with the application's copy. A plugin built against a <b>newer</b> version cannot run, because the
/// runtime never binds a reference to a lower version; <see cref="TryFindNewerSharedReference"/> finds that case before any
/// plugin code runs, so the plugin fails with a reason that names both versions instead of a misleading "type not found".
/// The context is collectible so that an assembly that turns out to hold no loadable plugin can be released; a context with a
/// loaded plugin stays alive for the process, because the plugin instance and its registered delegates keep it referenced.
/// </para>
/// <para>Thread safety: <see cref="AssemblyLoadContext"/> is thread-safe; the trusted-name map is computed once and immutable.</para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    #region Fields

    // Simple names of the application's assemblies with their file paths, computed once per process: the list is fixed at
    // startup and the lookup runs for every dependency a plugin assembly asks for. The path is kept so that the version check
    // can read the application's version from metadata without loading the assembly. Frozen for the cheapest read; compared
    // ignoring case, like assembly names.
    private static readonly FrozenDictionary<string, string> _TrustedPlatformAssemblies = _ReadTrustedPlatformAssemblies();

    // Reads the optional deps.json next to the plugin to find its dependencies; null when the resolver could not be created.
    private readonly AssemblyDependencyResolver? _Resolver;

    // Folder of the plugin assembly: the fallback probe folder for dependencies without a deps.json entry.
    private readonly string _Directory;

    #endregion

    #region Lifecycle

    /// <summary>Creates a collectible context for the plugin assembly at <paramref name="mainAssemblyPath"/>.</summary>
    /// <param name="mainAssemblyPath">Full path of the plugin assembly; its folder is the probe folder for dependencies.</param>
    internal PluginLoadContext(string mainAssemblyPath)
        : base(Invariant($"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}"), isCollectible: true)
    {
        // The context name only helps diagnostics (it appears in debuggers and exception texts).
        _Directory = Path.GetDirectoryName(mainAssemblyPath) ?? string.Empty;

        // The resolver reads the optional deps.json next to the plugin. It throws for some unusual folders, in which case the
        // plain folder probe below still works, so the failure is not fatal.
        try
        {
            _Resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or BadImageFormatException)
        {
            _Resolver = null;
        }
    }

    #endregion

    #region AssemblyLoadContext

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The runtime asks this context first for every assembly that code in it references, so the default-context check has to
        // come first: contract and Core types must keep one identity, or a cast from the plugin's IPlugin to the application's
        // IPlugin would fail.
        string? name = assemblyName.Name;
        if (name is null || _IsDefaultAssembly(name))
        {
            // Null hands the request to the default context.
            return null;
        }

        // deps.json first; without one, the plugin folder itself. A reference name is plugin-controlled text; it is not
        // sanitized because a plugin is trusted in-process code that could load any file itself, so a name that leaves the
        // folder grants nothing new.
        string? path = _Resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string candidate = Path.Combine(_Directory, name + ".dll");
            path = File.Exists(candidate)
                ? candidate
                : null;
        }

        // Null (not found) lets the runtime continue with the default context, which reports the missing dependency itself.
        return path is null
            ? null
            : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Native libraries are found through deps.json only; Zero lets the runtime use its normal native probing.
        string? path = _Resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null
            ? IntPtr.Zero
            : LoadUnmanagedDllFromPath(path);
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Finds the first reference of a plugin assembly to an assembly that the application supplies (see the class remarks) in a
    /// lower version than the plugin was built against. Such a plugin cannot run here: the runtime does not bind a reference to a
    /// lower version, and without this check the failure shows up later as an entry type that is "not found" or a member that
    /// is missing. Only metadata is read (the references of the plugin and, for an application assembly that is not loaded yet,
    /// its assembly name); no plugin code runs.
    /// </summary>
    /// <param name="assembly">The loaded plugin assembly.</param>
    /// <param name="problem">One line that names the assembly, both versions, and the fix; <see langword="null"/> when every shared
    /// reference is satisfied.</param>
    /// <returns><see langword="true"/> when a reference needs a newer version than the application has.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is <see langword="null"/>.</exception>
    internal static bool TryFindNewerSharedReference(Assembly assembly, [NotNullWhen(true)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        problem = null;

        // The reference list comes from a file somebody else wrote; a damaged table is not this check's business (loading the
        // types reports it with its own reason), so an unreadable list means "nothing to report" rather than a failure here.
        AssemblyName[] references;
        try
        {
            references = assembly.GetReferencedAssemblies();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return false;
        }

        foreach (AssemblyName reference in references)
        {
            // Only assemblies that come from the application matter: a private dependency resolves from the plugin folder in
            // whatever version the plugin ships.
            string? name = reference.Name;
            Version? wanted = reference.Version;
            if (name is null || wanted is null || !_IsDefaultAssembly(name))
            {
                continue;
            }

            Version? available = _ApplicationVersion(name);
            if (available is not null && wanted > available)
            {
                problem = Invariant($"The plugin was built against {name} {wanted}, but this application provides {available}; ")
                    + Invariant($"build the plugin against {available} or older, or update the application.");
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Whether the default context supplies the assembly. Loaded-in-default is checked per call (single-file hosts and test
    /// fixtures load contracts at run time); the trusted platform map is computed once.
    /// </summary>
    /// <param name="name">Simple name of the requested assembly.</param>
    private static bool _IsDefaultAssembly(string name) =>
        _TrustedPlatformAssemblies.ContainsKey(name) || _FindLoadedInDefault(name) is not null;

    /// <summary>The assembly of that simple name that is already loaded in the default context, if any.</summary>
    /// <param name="name">Simple name of the requested assembly.</param>
    /// <returns>The loaded assembly, or <see langword="null"/>.</returns>
    private static Assembly? _FindLoadedInDefault(string name)
    {
        // Default.Assemblies allocates a snapshot per call. This runs once per dependency of a plugin at load time, never on a
        // per-item path, so the cost is not significant.
        foreach (Assembly assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        return null;
    }

    /// <summary>
    /// The version of an application assembly: the loaded copy first (that is the one a reference would bind to), otherwise the
    /// assembly name read from the file of the trusted platform list, which reads metadata and does not load the assembly.
    /// </summary>
    /// <param name="name">Simple name of an assembly the application supplies.</param>
    /// <returns>The version, or <see langword="null"/> when it cannot be determined (then no version problem is reported).</returns>
    private static Version? _ApplicationVersion(string name)
    {
        if (_FindLoadedInDefault(name) is Assembly loaded)
        {
            return loaded.GetName().Version;
        }

        if (!_TrustedPlatformAssemblies.TryGetValue(name, out string? path))
        {
            return null;
        }

        // A file of the list that cannot be read leaves the decision to the runtime, which reports its own error when the plugin
        // really needs the assembly; the check must never fail a plugin on a guess.
        try
        {
            return AssemblyName.GetAssemblyName(path).Version;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads the simple assembly names and paths from the <c>TRUSTED_PLATFORM_ASSEMBLIES</c> list of the running host.</summary>
    /// <returns>Name to path; empty when the host did not provide the list (then only the loaded-in-default check applies).</returns>
    private static FrozenDictionary<string, string> _ReadTrustedPlatformAssemblies()
    {
        // The host writes the application's assembly closure here (names of the shared framework and the dependency file): a
        // path list separated by Path.PathSeparator. The simple name is the key, because that is what a reference carries; the
        // first path of a name wins, like the order of the list.
        string? list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(list))
        {
            return FrozenDictionary<string, string>.Empty;
        }

        Dictionary<string, string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            assemblies.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        return assemblies.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    #endregion
}
