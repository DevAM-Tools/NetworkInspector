// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The rules a plugin's info and its registration names must satisfy. The contract types are plain data; the loader checks
/// them when a plugin is configured, so a violation fails that plugin and not the application. The only limit is the name
/// length: names become file and folder names. Texts such as version, license, and description may have any length.
/// Stateless; thread-safe.
/// </summary>
internal static class PluginInfoRules
{
    #region Constants

    /// <summary>Longest plugin, source kind, and format name, in characters.</summary>
    internal const int MaxNameLength = StorageNames.MaxNameLength;

    #endregion

    #region Internal API

    /// <summary>Checks plugin info: a valid unique-able name, texts that are not <see langword="null"/>, and complete dependencies.</summary>
    /// <param name="info">The info a plugin returned.</param>
    /// <param name="problem">What is wrong; <see langword="null"/> when the info is valid.</param>
    /// <returns><see langword="true"/> when the info is valid.</returns>
    internal static bool TryValidate([NotNullWhen(true)] PluginInfo? info, [NotNullWhen(false)] out string? problem)
    {
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
    /// Checks a source kind or export format name: 1–256 characters, a letter first, then letters, digits, <c>_</c>, <c>.</c>,
    /// or <c>-</c> (the names appear in command lines and settings, so no spaces).
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
