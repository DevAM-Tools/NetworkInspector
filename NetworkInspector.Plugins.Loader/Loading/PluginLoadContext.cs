// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// One collectible load context per plugin assembly. An assembly that the default context already has — loaded there, or
/// listed in <c>TRUSTED_PLATFORM_ASSEMBLIES</c> (the shared framework and the application's dependency closure, which includes
/// every contract and Core) — always comes from the default context, so contract types keep one identity across the
/// application and its plugins; a plugin that ships its own copy of such an assembly gets the application's copy. Other
/// dependencies resolve from the plugin folder.
/// <para>
/// Decisions. The match is by assembly name only, never by version: the application's version always wins, so a plugin cannot
/// bring a newer or older copy of a shared assembly (side-by-side loading of shared assemblies is not supported). The context
/// is collectible so that an assembly that turns out to hold no loadable plugin can be released; a context with a loaded
/// plugin stays alive for the process, because the plugin instance and its registered delegates keep it referenced.
/// </para>
/// <para>Thread safety: <see cref="AssemblyLoadContext"/> is thread-safe; the trusted-name set is computed once and immutable.</para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    #region Fields

    // Simple names of the application's assemblies, computed once per process: the list is fixed at startup and the lookup runs
    // for every dependency a plugin assembly asks for. Frozen for the cheapest read; compared ignoring case, like assembly names.
    private static readonly FrozenSet<string> _TrustedPlatformNames = _ReadTrustedPlatformNames();

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

    #region Private helpers

    /// <summary>
    /// Whether the default context supplies the assembly. Loaded-in-default is checked per call (single-file hosts and test
    /// fixtures load contracts at run time); the trusted platform set is computed once.
    /// </summary>
    /// <param name="name">Simple name of the requested assembly.</param>
    private static bool _IsDefaultAssembly(string name) =>
        _TrustedPlatformNames.Contains(name) || _IsLoadedInDefault(name);

    /// <summary>Whether an assembly of that simple name is already loaded in the default context.</summary>
    /// <param name="name">Simple name of the requested assembly.</param>
    private static bool _IsLoadedInDefault(string name)
    {
        // Default.Assemblies allocates a snapshot per call. This runs once per dependency of a plugin at load time, never on a
        // per-item path, so the cost is not significant.
        foreach (Assembly assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the simple assembly names from the <c>TRUSTED_PLATFORM_ASSEMBLIES</c> list of the running host.</summary>
    /// <returns>The names; empty when the host did not provide the list (then only the loaded-in-default check applies).</returns>
    private static FrozenSet<string> _ReadTrustedPlatformNames()
    {
        // The host writes the application's assembly closure here (names of the shared framework and the dependency file): a
        // path list separated by Path.PathSeparator. Only the simple name matters, because that is what a reference carries.
        string? list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(list))
        {
            return FrozenSet<string>.Empty;
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            names.Add(Path.GetFileNameWithoutExtension(path));
        }

        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    #endregion
}
