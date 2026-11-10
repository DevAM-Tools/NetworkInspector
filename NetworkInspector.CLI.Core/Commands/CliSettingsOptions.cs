// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// The settings options every command accepts: <c>--profile</c>, <c>--settings-path</c>, and <c>--set &lt;key&gt;=&lt;value&gt;</c>
/// (repeatable). Immutable.
/// <para>
/// <c>--set</c> overrides one setting for the current run only; nothing is written. The value uses the text form of the setting
/// (a limit takes a number with a unit word such as <c>64MiB</c>, or <c>off</c>). The loader limits of the settings library
/// (<c>settings.maxFileSize</c>, <c>settings.maxJsonDepth</c>, <c>settings.folderLockTimeout</c>, <c>settings.maxGroupFiles</c>,
/// <c>settings.maxTextLength</c>) are in effect before the first settings file is read, so an override can let a run load a file
/// that the stored limit would skip. A repeated key keeps its first position and takes the last value.
/// </para>
/// </summary>
/// <param name="SettingsPath">Base folder of the settings, or <see langword="null"/> for the default of the platform.</param>
/// <param name="Profile">Profile name, or <see langword="null"/> for <see cref="SettingsProfiles.DefaultProfile"/>.</param>
/// <param name="Overrides">The <c>--set</c> pairs (key, text) in the order given, one per key; applied for this run only.</param>
internal sealed record CliSettingsOptions(string? SettingsPath, string? Profile, IReadOnlyList<KeyValuePair<string, string>> Overrides)
{
    /// <summary>The options when none is given: the platform default folder, the profile <c>default</c>, and no override.</summary>
    internal static CliSettingsOptions Defaults { get; } = new(null, null, []);
}

/// <summary>
/// Collects the settings options while a command walks its arguments; every command calls <see cref="TryConsume"/> first for
/// each argument, so the options behave the same everywhere. Not thread-safe; used by one parser.
/// </summary>
internal sealed class CliSettingsOptionsBuilder
{
    #region Collected options

    /// <summary>The settings base given with <c>--settings-path</c>, or <see langword="null"/>.</summary>
    private string? _SettingsPath;

    /// <summary>The profile given with <c>--profile</c>, or <see langword="null"/>.</summary>
    private string? _Profile;

    /// <summary>The <c>--set</c> pairs, one per key.</summary>
    private readonly List<KeyValuePair<string, string>> _Overrides = [];

    #endregion

    #region Consuming an argument

    /// <summary>
    /// Consumes the option at <paramref name="index"/> when it is one of the settings options (and its value).
    /// </summary>
    /// <param name="args">All arguments of the command.</param>
    /// <param name="index">Position of the current argument; advanced to the last consumed argument.</param>
    /// <returns><see langword="true"/> when the argument was a settings option.</returns>
    /// <exception cref="ArgumentException">The option has no value.</exception>
    /// <exception cref="CliUsageException">
    /// A <c>--set</c> value is not <c>key=value</c>, or the option is one of the two flags that <c>--set</c> replaced.
    /// </exception>
    internal bool TryConsume(string[] args, ref int index)
    {
        // Case-insensitive like every option of the CLI. A repeated option overwrites the earlier one (last one wins).
        switch (args[index].ToUpperInvariant())
        {
            case "--PROFILE":
                _Profile = CliArgumentParsing.GetNextArg(args, ref index, "--profile");
                return true;
            case "--SETTINGS-PATH":
                _SettingsPath = CliArgumentParsing.GetNextArg(args, ref index, "--settings-path");
                return true;
            case "--SET":
                _AddOverride(CliArgumentParsing.GetNextArg(args, ref index, "--set"));
                return true;
            case "--SETTINGS-MAX-CONFIG-FILE-BYTES":
                throw _Replaced(args, index, "--settings-max-config-file-bytes", "settings.maxFileSize");
            case "--SETTINGS-MAX-JSON-DEPTH":
                throw _Replaced(args, index, "--settings-max-json-depth", "settings.maxJsonDepth");
            default:
                return false;
        }
    }

    /// <summary>Creates the options from what was consumed.</summary>
    /// <returns>The options record.</returns>
    internal CliSettingsOptions Build() => new(_SettingsPath, _Profile, [.. _Overrides]);

    #endregion

    #region Override text

    /// <summary>Adds one <c>--set</c> pair; a key that was given before keeps its position and takes the new value.</summary>
    /// <param name="text">The argument after <c>--set</c>: <c>key=value</c>.</param>
    /// <exception cref="CliUsageException">The text has no <c>=</c> or an empty key.</exception>
    private void _AddOverride(string text)
    {
        int separator = text.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new CliUsageException(Invariant($"--set expects key=value, got '{_Shown(text)}'."));
        }

        string key = text[..separator];
        string value = text[(separator + 1)..];
        for (int index = 0; index < _Overrides.Count; index++)
        {
            if (string.Equals(_Overrides[index].Key, key, StringComparison.Ordinal))
            {
                _Overrides[index] = new KeyValuePair<string, string>(key, value);
                return;
            }
        }

        _Overrides.Add(new KeyValuePair<string, string>(key, value));
    }

    /// <summary>Shortens an argument that an error message repeats and replaces its control characters.</summary>
    /// <param name="text">The argument as typed.</param>
    /// <returns>At most 64 characters on one line, followed by an ellipsis when shortened.</returns>
    private static string _Shown(string text)
    {
        string line = CliLogFile.SingleLine(text);
        return line.Length <= 64
            ? line
            : line[..64] + "…";
    }

    /// <summary>The usage error for a flag that <c>--set</c> replaced; it names the replacement with the value the user gave.</summary>
    /// <param name="args">All arguments of the command.</param>
    /// <param name="index">Position of the removed flag.</param>
    /// <param name="flag">The removed flag as the user types it.</param>
    /// <param name="key">The key of the limit that <c>--set</c> takes now.</param>
    /// <returns>The exception to throw.</returns>
    private static CliUsageException _Replaced(string[] args, int index, string flag, string key)
    {
        string value = index + 1 < args.Length
            ? _Shown(args[index + 1])
            : "<value>";
        return new CliUsageException(Invariant($"{flag} was replaced: use --set {key}={value}."));
    }

    #endregion
}
