// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// A plugin registry that holds the built-in plugins only, for the tests of the spec parsers. Disposing releases the registry
/// and the scan cache folder. Not thread-safe; one test uses it.
/// </summary>
internal sealed class BuiltInRegistry : IDisposable
{
    private readonly string _Folder;

    /// <summary>Loads the built-in plugins into a new registry.</summary>
    internal BuiltInRegistry()
    {
        _Folder = Directory.CreateTempSubdirectory("ni-registry-").FullName;
        Registry = new();
        new PluginLoader(new PluginLoaderOptions(Path.Combine(_Folder, "cache.json"), NetworkInspectorBuiltIns.CreateAll()))
            .Load(PluginTier.Core, [], Registry, CancellationToken.None);
    }

    /// <summary>The registry with the built-in sources and exporters.</summary>
    internal PluginRegistry Registry { get; }

    /// <summary>Disposes the registry and deletes the cache folder.</summary>
    public void Dispose()
    {
        Registry.Dispose();
        try
        {
            Directory.Delete(_Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The operating system cleans the temp area; a leftover folder is harmless.
        }
    }
}
