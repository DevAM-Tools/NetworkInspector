// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces.Tests;

/// <summary>A unique temporary folder per test, deleted on dispose; tests run in parallel, so none shares a folder.</summary>
internal sealed class TempFolder : IDisposable
{
    #region Lifecycle

    /// <summary>Creates the folder.</summary>
    /// <param name="topic">Short topic used in the folder name.</param>
    internal TempFolder(string topic)
    {
        Path = Directory.CreateTempSubdirectory("ni-" + topic + "-").FullName;
    }

    /// <summary>Deletes the folder with everything in it; a scanner may hold a file briefly, so a failure is ignored.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The operating system cleans the temp area; a leftover folder is harmless.
        }
    }

    #endregion

    #region Public API

    /// <summary>Full path of the folder.</summary>
    internal string Path { get; }

    #endregion
}
