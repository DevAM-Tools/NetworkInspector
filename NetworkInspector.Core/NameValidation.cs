// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Validates protocol and field names. Names must be ASCII C-style identifiers
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>) optionally separated by dots (e.g., "ip.src", "tcp.flags.syn").
/// Unicode letters and digits are rejected so filters, generated identifiers, and config keys stay ASCII.
/// </summary>
public static class NameValidation
{
    #region Name grammar

    /// <summary>
    /// Checks whether the given name is a valid protocol/field identifier.
    /// </summary>
    /// <param name="name">The name to validate.</param>
    /// <returns><see langword="true"/> if the name is a non-empty ASCII C-style identifier, optionally dot-separated.</returns>
    /// <remarks>Sync: <see cref="SettingNames.IsValidName"/> holds the same grammar for setting names; a change is made in both.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        // Single pass over the span with a two-state machine (no allocation, no regex, no culture): either the next
        // character must start a segment (letter or underscore) or it continues one (letter, digit, underscore) or
        // separates segments ('.'). Starting in "expect segment start" rejects a leading dot and a leading digit,
        // and a dot switches back to it, which rejects ".." and a segment that starts with a digit.
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

        // Must not end with a dot: after a trailing '.' the state is still "expect segment start".
        return !expectIdentStart;
    }

    /// <summary>
    /// Checks whether the given UI name is valid: non-empty and free of control characters (<see cref="char.IsControl(char)"/>,
    /// which includes line breaks and tabs). Unlike <see cref="IsValidName"/>, any other Unicode text is allowed because
    /// a UI name is human-readable display text, not an identifier.
    /// </summary>
    /// <param name="name">The display name to validate.</param>
    /// <returns><see langword="true"/> when the name is non-empty and contains no control character.</returns>
    /// <remarks>Sync: <see cref="SettingNames.IsValidUiName"/> holds the same rule for the UI names of settings; a change is made in both.</remarks>
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

    #region ASCII character classes

    /// <summary>
    /// ASCII <c>A-Z</c> / <c>a-z</c> only. <see cref="char.IsLetter(char)"/> accepts Unicode letters
    /// and would disagree with the documented C-style alphabet.
    /// </summary>
    /// <param name="c">The character to test.</param>
    /// <returns><see langword="true"/> for <c>A-Z</c> and <c>a-z</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(char c) =>
        (uint)(c - 'A') <= 'Z' - 'A' || (uint)(c - 'a') <= 'z' - 'a';

    /// <summary>
    /// ASCII letter or <c>0-9</c>. Full-width and other Unicode digits are rejected.
    /// </summary>
    /// <param name="c">The character to test.</param>
    /// <returns><see langword="true"/> for an ASCII letter or <c>0-9</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetterOrDigit(char c) =>
        _IsAsciiLetter(c) || (uint)(c - '0') <= 9;

    #endregion
}
