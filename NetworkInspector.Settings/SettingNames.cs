// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The name rules of the settings: the grammar of a setting name, of a group name, and of the text a user interface shows.
/// Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Setting names are dot-separated ASCII C-style identifiers on purpose: such a name is a safe key in a file, on a command
/// line, and in a filter, and an application that uses the same grammar for other names (fields, columns) can use one name in
/// all of them. The rules live here so that the settings need nothing of any other layer of the application.
/// <para>Sync: an application that keeps a second copy of this grammar for its own names keeps it in step with
/// <see cref="IsValidName"/> and <see cref="IsValidUiName"/> and proves it with a test over a fixed corpus.</para>
/// </remarks>
/// <seealso cref="Setting"/>
/// <seealso cref="SettingGroup"/>
public static class SettingNames
{
    #region Name grammar

    /// <summary>
    /// The file stem of the default group (<c>default.json</c>); also the one group name that is refused, so no group shares the
    /// file of the default group.
    /// </summary>
    /// <remarks>
    /// Sync: <c>SettingsManager.Load</c> maps this stem to the group <c>""</c>, and <c>SettingsManager.Merge</c> (<c>_StemOf</c>)
    /// maps the group <c>""</c> to this stem; the three places must agree or a load would not find what a save wrote.
    /// </remarks>
    internal const string DefaultGroupStem = "default";

    /// <summary>Checks whether a name is a valid setting name.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns><see langword="true"/> for a non-empty ASCII C-style identifier, optionally dot-separated (<c>ip.src</c>, <c>a.b2</c>).</returns>
    /// <remarks>Sync: <c>NetworkInspector.Core.NameValidation</c> holds the same grammar for protocol and field names; a change is made in both.</remarks>
    public static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        // Single pass with a two-state machine (no allocation, no regex, no culture): either the next character must start a
        // segment (letter or underscore) or it continues one (letter, digit, underscore) or separates segments ('.'). Starting
        // in "expect segment start" rejects a leading dot and a leading digit, and a dot switches back to it, which rejects
        // ".." and a segment that starts with a digit. char.IsAsciiLetter and not char.IsLetter: Unicode letters would
        // disagree with the documented alphabet.
        bool expectSegmentStart = true;
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (expectSegmentStart)
            {
                if (!char.IsAsciiLetter(character) && character != '_')
                {
                    return false;
                }

                expectSegmentStart = false;
            }
            else if (character == '.')
            {
                expectSegmentStart = true;
            }
            else if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        // After a trailing dot the state is still "expect segment start", so a name must not end in a dot.
        return !expectSegmentStart;
    }

    /// <summary>Checks whether a name is a valid setting group name.</summary>
    /// <param name="name">The group name to check.</param>
    /// <returns>
    /// <see langword="true"/> for the empty string (the default group) or a lower-case, dot-separated ASCII identifier other than
    /// <c>default</c>.
    /// </returns>
    public static bool IsValidGroupName(ReadOnlySpan<char> name)
    {
        // The empty string is the default group and always valid.
        if (name.IsEmpty)
        {
            return true;
        }

        if (!IsValidName(name))
        {
            return false;
        }

        // Reserved for the default group's file (default.json).
        if (name.SequenceEqual(DefaultGroupStem))
        {
            return false;
        }

        // Group names are file names, and two names that differ only in case would be one file on some systems.
        for (int index = 0; index < name.Length; index++)
        {
            if (char.IsAsciiLetterUpper(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether a user-interface name is valid: non-empty and free of control characters (line breaks and tabs included).
    /// Any other Unicode text is allowed because it is display text and not an identifier.
    /// </summary>
    /// <param name="name">The display name to check.</param>
    /// <returns><see langword="true"/> when the name is non-empty and contains no control character.</returns>
    /// <remarks>Sync: <c>NetworkInspector.Core.NameValidation</c> holds the same grammar for protocol and field names; a change is made in both.</remarks>
    public static bool IsValidUiName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        for (int index = 0; index < name.Length; index++)
        {
            if (char.IsControl(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
