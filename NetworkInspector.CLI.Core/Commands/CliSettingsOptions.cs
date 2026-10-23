// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// The settings options every command accepts: <c>--profile</c>, <c>--settings-path</c>,
/// <c>--settings-max-config-file-bytes</c>, and <c>--settings-max-json-depth</c>.
/// </summary>
/// <param name="SettingsPath">Base folder of the settings, or <see langword="null"/> for the default of the platform.</param>
/// <param name="Profile">Profile name, or <see langword="null"/> for <see cref="SettingsProfiles.DefaultProfile"/>.</param>
/// <param name="MaxConfigFileBytes">Largest accepted settings JSON in bytes; 0 or less disables the check.</param>
/// <param name="MaxJsonDepth">Deepest accepted JSON nesting; 0 or less disables the check.</param>
internal sealed record CliSettingsOptions(string? SettingsPath, string? Profile, long MaxConfigFileBytes, int MaxJsonDepth)
{
    /// <summary>The options when none is given.</summary>
    internal static CliSettingsOptions Defaults { get; } =
        new(null, null, SettingsManager.DefaultMaxConfigFileBytes, SettingsManager.DefaultMaxJsonDepth);
}

/// <summary>
/// Collects the settings options while a command walks its arguments; every command calls <see cref="TryConsume"/> first for
/// each argument, so the four options behave the same everywhere. Not thread-safe; used by one parser.
/// </summary>
internal sealed class CliSettingsOptionsBuilder
{
    #region Fields

    private string? _SettingsPath;

    private string? _Profile;

    private long _MaxConfigFileBytes = SettingsManager.DefaultMaxConfigFileBytes;

    private int _MaxJsonDepth = SettingsManager.DefaultMaxJsonDepth;

    #endregion

    #region Public API

    /// <summary>
    /// Consumes the option at <paramref name="index"/> when it is one of the four settings options (and its value).
    /// </summary>
    /// <param name="args">All arguments of the command.</param>
    /// <param name="index">Position of the current argument; advanced to the last consumed argument.</param>
    /// <returns><see langword="true"/> when the argument was a settings option.</returns>
    /// <exception cref="ArgumentException">The option has no value or the value is not a number.</exception>
    internal bool TryConsume(string[] args, ref int index)
    {
        switch (args[index].ToUpperInvariant())
        {
            case "--PROFILE":
                _Profile = CliArgumentParsing.GetNextArg(args, ref index, "--profile");
                return true;
            case "--SETTINGS-PATH":
                _SettingsPath = CliArgumentParsing.GetNextArg(args, ref index, "--settings-path");
                return true;
            case "--SETTINGS-MAX-CONFIG-FILE-BYTES":
                _MaxConfigFileBytes = CliArgumentParsing.ParseInt64(
                    CliArgumentParsing.GetNextArg(args, ref index, "--settings-max-config-file-bytes"));
                return true;
            case "--SETTINGS-MAX-JSON-DEPTH":
                _MaxJsonDepth = CliArgumentParsing.ParseInt32(
                    CliArgumentParsing.GetNextArg(args, ref index, "--settings-max-json-depth"));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Creates the options from what was consumed.</summary>
    /// <returns>The options record.</returns>
    internal CliSettingsOptions Build() => new(_SettingsPath, _Profile, _MaxConfigFileBytes, _MaxJsonDepth);

    #endregion
}
