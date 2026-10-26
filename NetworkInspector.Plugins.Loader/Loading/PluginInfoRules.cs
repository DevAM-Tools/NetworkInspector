// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The rules a plugin's info and its registration names must satisfy. The contract types are plain data; the loader checks
/// them when a plugin is configured, so a violation fails that plugin and not the application. The only limit is the name
/// length: names become file and folder names. Texts such as version, license, and description may have any length: a
/// plugin is trusted in-process code, so a length limit on the texts it hands over would not stop anything it could do
/// anyway. Stateless; thread-safe.
/// </summary>
internal static class PluginInfoRules
{
    #region Constants

    /// <summary>Longest plugin, source kind, and format name, in characters.</summary>
    /// <remarks>
    /// Limit: 1 to 256 UTF-16 characters. Threat: an overlong name used as a file or folder name (the plugin data folder is
    /// named after the plugin) or repeated in logs and settings. Constant, not configurable: it is the one length that every
    /// storage name shares, and storage names beyond a shorter direct length are mapped to a hashed form so that a valid name
    /// always fits a file system name. Posture: secure by default; the value is agreed once for all names.
    /// </remarks>
    internal const int MaxNameLength = StorageNames.MaxNameLength;

    #endregion

    #region Internal API

    /// <summary>
    /// Checks plugin info: a valid name (that it is unique among the loaded plugins is checked by the loader), texts that are
    /// not <see langword="null"/>, and complete dependencies.
    /// </summary>
    /// <param name="info">The info a plugin returned.</param>
    /// <param name="problem">What is wrong, as text for the plugin's failure reason; <see langword="null"/> when the info is valid.</param>
    /// <returns><see langword="true"/> when the info is valid.</returns>
    internal static bool TryValidate([NotNullWhen(true)] PluginInfo? info, [NotNullWhen(false)] out string? problem)
    {
        // The contract types are plain records with non-nullable members, but nullable annotations are not enforced at run
        // time: a plugin compiled without nullable checks can hand over null anywhere. Every field that the loader or a later
        // consumer (plugin list, plugin page) dereferences is checked here, once, so that a violation fails the plugin and not
        // the consumer.
        if (info is null)
        {
            problem = "the plugin returned no info";
            return false;
        }

        if (string.IsNullOrEmpty(info.Name))
        {
            problem = "the name is empty";
            return false;
        }

        if (info.Name.Length > MaxNameLength)
        {
            problem = Invariant($"the name has more than {MaxNameLength} characters");
            return false;
        }

        // The plugin name follows the Core identifier rules (dot-separated ASCII identifiers); source kinds and format names use
        // the looser command-line form of IsValidRegistrationName.
        if (!NameValidation.IsValidName(info.Name))
        {
            problem = Invariant($"the name '{info.Name}' is not a dot-separated identifier of letters, digits, and underscores");
            return false;
        }

        if (info.Version is null || info.License is null || info.Description is null)
        {
            problem = "version, license, and description must not be null";
            return false;
        }

        if (info.Dependencies is null)
        {
            problem = "the dependency list must not be null";
            return false;
        }

        foreach (PluginDependency? dependency in info.Dependencies)
        {
            if (dependency is null
                || dependency.Name is null
                || dependency.Version is null
                || dependency.License is null
                || dependency.CopyrightHolder is null
                || dependency.Link is null
                || dependency.LicenseText is null)
            {
                problem = "a dependency or one of its fields is null";
                return false;
            }
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Checks a source kind or export format name: 1 to <see cref="MaxNameLength"/> characters, an ASCII letter first, then ASCII
    /// letters, digits, <c>_</c>, <c>.</c>, or <c>-</c> (the names appear in command lines and settings, so no spaces). The
    /// comparison of such names elsewhere is case-insensitive, so two names that differ only in case are the same name.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <returns><see langword="true"/> when the name is valid.</returns>
    internal static bool IsValidRegistrationName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
