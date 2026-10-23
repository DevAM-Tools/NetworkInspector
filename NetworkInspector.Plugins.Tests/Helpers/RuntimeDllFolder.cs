// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// A session-wide fixture with realistic non-plugin DLLs: the 100 smallest managed assemblies of the runtime directory, copied
/// into the subfolders <c>a/</c> and <c>b/</c> of one temporary folder (200 files). Tests copy the whole set into their own
/// folder, so they never touch the shared copy. Read-only after initialization; thread-safe.
/// </summary>
internal sealed class RuntimeDllFolder : IAsyncInitializer, IAsyncDisposable
{
    #region Constants

    private const int _FilesPerFolder = 100;

    #endregion

    #region Fields

    private string _Root = string.Empty;

    #endregion

    #region Lifecycle

    /// <summary>Selects and copies the files; runs once per test session.</summary>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync()
    {
        _Root = Directory.CreateTempSubdirectory("ni-runtime-dlls-").FullName;
        List<(string Path, long Length)> managed = [];
        foreach (string path in Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        {
            if (_IsManagedAssembly(path))
            {
                managed.Add((path, new FileInfo(path).Length));
            }
        }

        foreach ((string path, _) in managed.OrderBy(static file => file.Length).ThenBy(static file => file.Path, StringComparer.Ordinal).Take(_FilesPerFolder))
        {
            foreach (string folder in (string[])["a", "b"])
            {
                Directory.CreateDirectory(Path.Combine(_Root, folder));
                File.Copy(path, Path.Combine(_Root, folder, Path.GetFileName(path)));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Deletes the shared folder.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The operating system cleans the temp area; a held file must not fail the session.
        }

        return ValueTask.CompletedTask;
    }

    #endregion

    #region Public API

    /// <summary>Number of files a copy contains (two folders of 100).</summary>
    internal static int FileCount => _FilesPerFolder * 2;

    /// <summary>Copies the 200 files (folders <c>a/</c> and <c>b/</c>) into <paramref name="target"/>.</summary>
    /// <param name="target">Per-test folder.</param>
    internal void CopyTo(string target)
    {
        foreach (string file in Directory.EnumerateFiles(_Root, "*.dll", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_Root, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
            File.Copy(file, destination);
        }
    }

    #endregion

    #region Private helpers

    // A managed assembly has a CLI header and an assembly manifest; native libraries and reference-only files do not qualify.
    private static bool _IsManagedAssembly(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    #endregion
}
