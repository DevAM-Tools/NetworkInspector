// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Temporary directory for test output files. Automatically cleaned up on <see cref="Dispose"/>.
/// </summary>
internal sealed class TestDir : IDisposable
{
    /// <summary>Absolute path to the temporary directory.</summary>
    private readonly string _Path;

    /// <summary>
    /// Creates a new temporary directory with a unique name based on the given prefix.
    /// </summary>
    /// <param name="prefix">Prefix for the directory name.</param>
    internal TestDir(string prefix)
    {
        _Path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_Path);
    }

    /// <summary>The absolute path to the temporary directory.</summary>
    internal string DirectoryPath => _Path;

    /// <summary>
    /// Returns the absolute path to a file inside this temporary directory.
    /// </summary>
    /// <param name="name">File name (not a path).</param>
    /// <returns>Absolute path to the file.</returns>
    internal string FilePath(string name) => Path.Combine(_Path, name);

    /// <summary>
    /// Deletes the temporary directory and all its contents.
    /// Silently ignores errors (e.g., file locks).
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — test temp dirs are in the OS temp folder
        }
    }
}
