// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Shared persistence I/O helpers for settings JSON files.
/// Caps size at the trust boundary and opens files so other processes can still read them.
/// </summary>
internal static class SettingsFileAccess
{
    #region Constants

    /// <summary>Maximum accepted settings or referenced-config file size (1 MiB).</summary>
    internal const long MaxFileBytes = 1_048_576;

    /// <summary>Maximum JSON nesting depth when parsing persisted settings.</summary>
    internal const int JsonMaxDepth = 64;

    /// <summary>
    /// Manifest of group JSON file names last written by <see cref="SettingsManager.Save"/>.
    /// Not a <c>.json</c> file so <see cref="SettingsManager.Load"/> will not treat it as a group.
    /// </summary>
    internal const string OwnedGroupManifestFileName = ".ni-settings-owned";

    #endregion

    #region Public API

    /// <summary>
    /// Opens <paramref name="path"/> for sequential read while allowing other processes
    /// to read the same file (<see cref="FileShare.Read"/>).
    /// </summary>
    internal static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
    }

    /// <summary>
    /// Returns a display label that does not echo a fully resolved filesystem path
    /// (avoids leaking sensitive locations into UI/log strings).
    /// </summary>
    internal static string SafeFileLabel(string path)
    {
        string fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        return "configuration file";
    }

    #endregion
}
