// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Profile name rules, the single rule for every front end of an application. A profile name is a trust boundary: it
/// becomes a folder name under the settings base (through <see cref="StorageNames"/>), so the syntax keeps it to a portable
/// subset. Stateless; thread-safe.
/// </summary>
public static class ProfileNames
{
    #region Profile name rules

    /// <summary>
    /// Checks the syntax only: 1–256 characters of ASCII letters, digits, space, <c>.</c>, <c>-</c>, <c>_</c>; no leading or
    /// trailing space or dot; not a device name of the operating system. The reserved names pass here, because the front ends open
    /// the <c>application</c> folder through the same path rules.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="error">
    /// The broken rule, without the name and without a final period: <c>a profile name is needed</c>,
    /// <c>at most 256 characters</c>, <c>letters, digits, space, dot, dash, and underscore only</c>,
    /// <c>must not start or end with a space or a dot</c>, or <c>{name} is reserved by the operating system</c>.
    /// </param>
    /// <returns><see langword="true"/> when the name is acceptable.</returns>
    public static bool TryValidateSyntax(string? name, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(name))
        {
            error = "a profile name is needed";
            return false;
        }

        // Length cap shared with every other storage name (StorageNames.MaxNameLength); longer names are not mapped to a folder.
        if (name.Length > StorageNames.MaxNameLength)
        {
            error = "at most 256 characters";
            return false;
        }

        // Allow-list instead of a deny-list of dangerous characters: the name becomes a folder name, so separators, "..",
        // control characters, and characters that some file systems treat specially must never get through. ASCII only,
        // because the same name has to map to the same folder on Windows, Linux, and macOS (case folding and Unicode
        // normalization differ between them).
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not (' ' or '.' or '-' or '_'))
            {
                error = "letters, digits, space, dot, dash, and underscore only";
                return false;
            }
        }

        // Windows drops a trailing space or dot from a folder name, so "a" and "a." would be the same folder; a leading dot
        // would also hide the folder on Linux and macOS. Rejecting both ends keeps name and folder one-to-one. The
        // allow-list above guarantees the name is not empty here.
        if (name[0] is ' ' or '.' || name[^1] is ' ' or '.')
        {
            error = "must not start or end with a space or a dot";
            return false;
        }

        if (StorageNames.IsDeviceName(name))
        {
            error = name + " is reserved by the operating system";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Checks the syntax and the reserved names of the layout: the rule for every name a user or an agent gives. A reserved name
    /// fails with <c>{name} is reserved</c>.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="layout">The layout whose <see cref="SettingsStorageLayout.ReservedFolderNames"/> are no profiles.</param>
    /// <param name="error">The broken rule (see <see cref="TryValidateSyntax"/>); <see langword="null"/> when acceptable.</param>
    /// <returns><see langword="true"/> when the name is acceptable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(string? name, SettingsStorageLayout layout, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!TryValidateSyntax(name, out error))
        {
            return false;
        }

        // Compared ignoring case because the reserved names are folders next to the profiles, and Windows and default macOS
        // volumes treat "Logs" and "logs" as the same folder. Linear scan: the list is tiny and this is not a hot path.
        foreach (string reserved in layout.ReservedFolderNames)
        {
            if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
            {
                error = name + " is reserved";
                return false;
            }
        }

        error = null;
        return true;
    }

    #endregion
}
