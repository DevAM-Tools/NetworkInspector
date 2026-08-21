// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Validates protocol and field names. Names must be ASCII C-style identifiers
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>) optionally separated by dots (e.g., "ip.src", "tcp.flags.syn").
/// Unicode letters and digits are rejected so filters, generated identifiers, and config keys stay ASCII.
/// </summary>
public static class NameValidation
{
    #region Public Validation API

    /// <summary>
    /// Checks whether the given name is a valid protocol/field identifier.
    /// </summary>
    /// <param name="name">The name to validate.</param>
    /// <returns>True if the name is a non-empty ASCII C-style identifier, optionally dot-separated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidName(ReadOnlySpan<char> name)
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
                // First character of segment: ASCII letter or underscore (classic C identifier).
                if (!_IsAsciiLetter(c) && c != '_')
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
            else if (!_IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        // Must not end with a dot
        return !expectIdentStart;
    }

    /// <summary>
    /// Checks whether the given name is a valid setting group name.
    /// Group names follow the same dot-separated ASCII C-style identifier rules as
    /// protocol/field names, but are additionally restricted to lowercase letters,
    /// digits, underscores, and dots — no uppercase letters allowed.
    /// The empty string is a valid group name (the default/root group).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidGroupName(ReadOnlySpan<char> name)
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidUiName(ReadOnlySpan<char> name)
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

    #region Private helpers

    /// <summary>
    /// ASCII <c>A-Z</c> / <c>a-z</c> only. <see cref="char.IsLetter(char)"/> accepts Unicode letters
    /// and would disagree with the documented C-style alphabet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(char c) =>
        (uint)(c - 'A') <= 'Z' - 'A' || (uint)(c - 'a') <= 'z' - 'a';

    /// <summary>
    /// ASCII letter or <c>0-9</c>. Full-width and other Unicode digits are rejected.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetterOrDigit(char c) =>
        _IsAsciiLetter(c) || (uint)(c - '0') <= 9;

    #endregion
}
