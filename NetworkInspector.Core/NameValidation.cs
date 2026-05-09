// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core;

/// <summary>
/// Validates protocol and field names. Names must be C-style identifiers
/// optionally separated by dots (e.g., "ip.src", "tcp.flags.syn").
/// </summary>
internal static class NameValidation
{
    #region Public Validation API

    /// <summary>
    /// Checks whether the given name is a valid protocol/field identifier.
    /// </summary>
    /// <param name="name">The name to validate.</param>
    /// <returns>True if the name is valid.</returns>
    internal static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        bool expectIdentStart = true;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (expectIdentStart)
            {
                // First character of segment: letter or underscore
                if (!char.IsLetter(c) && c != '_')
                {
                    return false;
                }
                expectIdentStart = false;
            }
            else if (c == '.')
            {
                // Dot separator — next must be ident start
                expectIdentStart = true;
            }
            else if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        // Must not end with a dot
        return !expectIdentStart;
    }

    /// <summary>
    /// Checks whether the given name is a valid setting group name.
    /// Group names follow the same dot-separated C-style identifier rules as
    /// protocol/field names, but are additionally restricted to lowercase letters,
    /// digits, underscores, and dots — no uppercase letters allowed.
    /// The empty string is a valid group name (the default/root group).
    /// </summary>
    internal static bool IsValidGroupName(ReadOnlySpan<char> name)
    {
        // Empty string is the default group — always valid.
        if (name.IsEmpty)
        {
            return true;
        }

        // Must satisfy the general name rules first.
        if (!IsValidName(name))
        {
            return false;
        }

        // Additionally: no uppercase letters allowed in group names.
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the given UI name is valid (no control characters or line breaks).
    /// </summary>
    internal static bool IsValidUiName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
