// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// A unique temporary directory per test, deleted on dispose. Tests run in parallel, so no test shares a folder.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    #region Lifecycle

    /// <summary>Creates the folder.</summary>
    /// <param name="topic">Short topic used in the folder name, for example <c>sample</c>.</param>
    internal TempFolder(string topic)
    {
        Path = Directory.CreateTempSubdirectory("ni-" + topic + "-").FullName;
    }

    /// <summary>Deletes the folder with everything in it; failures are ignored because the operating system cleans the temp area.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scanner or an antivirus may hold a file briefly; the leftover folder is harmless.
        }
    }

    #endregion

    #region Public API

    /// <summary>Full path of the folder.</summary>
    internal string Path { get; }

    /// <summary>Combines the folder path with a file or folder name.</summary>
    /// <param name="name">File or folder name.</param>
    /// <returns>The full path inside the folder.</returns>
    internal string FilePath(string name) => System.IO.Path.Combine(Path, name);

    #endregion
}
