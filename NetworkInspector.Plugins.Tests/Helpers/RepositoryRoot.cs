// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Finds the repository root (the folder with <c>NetworkInspector.slnx</c>) by walking up from the test assembly's folder, so
/// that the documentation tests read the files of the checkout they were built from. Stateless; thread-safe.
/// </summary>
internal static class RepositoryRoot
{
    #region Constants

    private const string _SolutionFile = "NetworkInspector.slnx";

    #endregion

    #region Public API

    /// <summary>Full path of the repository root.</summary>
    /// <exception cref="DirectoryNotFoundException">No folder above the test assembly contains the solution file.</exception>
    internal static string Path { get; } = _Find();

    /// <summary>Combines the repository root with a relative path (forward slashes are accepted).</summary>
    /// <param name="relativePath">Path below the repository root.</param>
    /// <returns>The full path.</returns>
    internal static string Combine(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    #endregion

    #region Private Helpers

    /// <summary>Walks up from the assembly folder until the solution file is found.</summary>
    private static string _Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, _SolutionFile)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No folder above '{AppContext.BaseDirectory}' contains {_SolutionFile}.");
    }

    #endregion
}
