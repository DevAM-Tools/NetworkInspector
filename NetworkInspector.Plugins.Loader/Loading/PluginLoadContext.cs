// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// One collectible load context per plugin assembly. An assembly that the default context already has — loaded there, or
/// listed in <c>TRUSTED_PLATFORM_ASSEMBLIES</c> (the shared framework and the application's dependency closure, which includes
/// every contract and Core) — always comes from the default context, so contract types keep one identity across the
/// application and its plugins; a plugin that ships its own copy of such an assembly gets the application's copy. Other
/// dependencies resolve from the plugin folder.
/// <para>Thread safety: <see cref="AssemblyLoadContext"/> is thread-safe; the trusted-name set is computed once and immutable.</para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    #region Fields

    private static readonly FrozenSet<string> _TrustedPlatformNames = _ReadTrustedPlatformNames();

    private readonly AssemblyDependencyResolver? _Resolver;

    private readonly string _Directory;

    #endregion

    #region Lifecycle

    /// <summary>Creates a collectible context for the plugin assembly at <paramref name="mainAssemblyPath"/>.</summary>
    /// <param name="mainAssemblyPath">Full path of the plugin assembly; its folder is the probe folder for dependencies.</param>
    internal PluginLoadContext(string mainAssemblyPath)
        : base(Invariant($"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}"), isCollectible: true)
    {
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
        string? name = assemblyName.Name;
        if (name is null || _IsDefaultAssembly(name))
        {
            // Null hands the request to the default context.
            return null;
        }

        // deps.json first; without one, the plugin folder itself.
        string? path = _Resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string candidate = Path.Combine(_Directory, name + ".dll");
            path = File.Exists(candidate)
                ? candidate
                : null;
        }

        return path is null
            ? null
            : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _Resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null
            ? IntPtr.Zero
            : LoadUnmanagedDllFromPath(path);
    }

    #endregion

    #region Private helpers

    // Loaded-in-default is checked per call (single-file hosts and test fixtures load contracts at run time); the trusted
    // platform set is computed once.
    private static bool _IsDefaultAssembly(string name) =>
        _TrustedPlatformNames.Contains(name) || _IsLoadedInDefault(name);

    private static bool _IsLoadedInDefault(string name)
    {
        foreach (Assembly assembly in Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<string> _ReadTrustedPlatformNames()
    {
        // The host writes the application's assembly closure here (names of the shared framework and the dependency file).
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
