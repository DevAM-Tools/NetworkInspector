// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Paths of the plugin assemblies that the test project copies next to itself (<c>TestPlugins/&lt;name&gt;/&lt;name&gt;.dll</c>) and
/// a helper that copies one of them into a per-test folder, so tests never load the shared copy. Stateless; thread-safe.
/// </summary>
internal static class TestPluginFiles
{
    #region Constants

    /// <summary>File name of the sample plugin assembly.</summary>
    internal const string SampleName = "Sample.Protocol";

    /// <summary>File name (without extension) of the test plugin assembly.</summary>
    internal const string TestPluginsName = "NetworkInspector.Plugins.Tests.TestPlugins";

    #endregion

    #region Public API

    /// <summary>Path of the shared copy of the sample plugin assembly.</summary>
    internal static string Sample => _PathOf(SampleName);

    /// <summary>Path of the shared copy of the test plugin assembly.</summary>
    internal static string TestPlugins => _PathOf(TestPluginsName);

    /// <summary>Copies <paramref name="source"/> into <paramref name="folder"/> (created when missing) and returns the new path.</summary>
    /// <param name="source">File to copy.</param>
    /// <param name="folder">Target folder.</param>
    /// <returns>Full path of the copy.</returns>
    internal static string CopyInto(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, target, overwrite: true);
        return target;
    }

    #endregion

    #region Private helpers

    private static string _PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "TestPlugins", name, name + ".dll");

    #endregion
}
