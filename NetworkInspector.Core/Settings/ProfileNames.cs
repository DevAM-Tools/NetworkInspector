// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Profile name rules, the single rule of the command line, the host, and the app. A profile name is a trust boundary: it
/// becomes a folder name under the settings base (through <see cref="StorageNames"/>), so the syntax keeps it to a portable
/// subset. Stateless; thread-safe.
/// </summary>
public static class ProfileNames
{
    #region Fields

    /// <summary>Folder names under the settings base that are not profiles (compared ignoring case).</summary>
    private static readonly IReadOnlyList<string> _Reserved = Array.AsReadOnly(
    [
        "application", "logs", "plugins", "plugins-data", "plugin-scan-cache", "host", "app", "presets",
    ]);

    #endregion

    #region Public API

    /// <summary>Folder names under the settings base that are not profiles (case-insensitive).</summary>
    public static IReadOnlyList<string> Reserved => _Reserved;

    /// <summary>
    /// Checks the syntax only: 1–256 characters of ASCII letters, digits, space, <c>.</c>, <c>-</c>, <c>_</c>; no leading or
    /// trailing space or dot; not a device name of the operating system. The reserved names pass here, because the hosts open
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

        if (name.Length > StorageNames.MaxNameLength)
        {
            error = "at most 256 characters";
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not (' ' or '.' or '-' or '_'))
            {
                error = "letters, digits, space, dot, dash, and underscore only";
                return false;
            }
        }

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
    /// Checks the syntax and the reserved names: the rule for every name a user or an agent gives. A reserved name fails with
    /// <c>{name} is reserved</c>.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="error">The broken rule (see <see cref="TryValidateSyntax"/>); <see langword="null"/> when acceptable.</param>
    /// <returns><see langword="true"/> when the name is acceptable.</returns>
    public static bool TryValidate(string? name, [NotNullWhen(false)] out string? error)
    {
        if (!TryValidateSyntax(name, out error))
        {
            return false;
        }

        foreach (string reserved in _Reserved)
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
