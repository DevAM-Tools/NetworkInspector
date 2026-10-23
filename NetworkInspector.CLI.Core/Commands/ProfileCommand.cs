// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// <c>ni profile list | create &lt;name&gt; [--copy-from &lt;profile&gt;] | rename &lt;old&gt; &lt;new&gt; | delete &lt;name&gt;</c>: manages
/// the profile folders under the settings base. No plugins are loaded; only folders change. A profile that a running app
/// uses can still be renamed or deleted here (the command cannot see the app); the app re-reads the list.
/// </summary>
internal static class ProfileCommand
{
    #region Public API

    /// <summary>Runs the profile command.</summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    internal static int Run(string[] args, CliIo io)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        return CliArgumentParsing.RunWithArgumentGuard(() => _RunCore(args, io), io.Error);
    }

    #endregion

    #region Private Helpers

    /// <summary>Parses the arguments and runs the sub-command.</summary>
    private static int _RunCore(string[] args, CliIo io)
    {
        if (args.Length == 0)
        {
            _PrintUsage(io.Error);
            return (int)ExitCode.ArgumentError;
        }

        if (CliArgumentParsing.IsHelpFlag(args[0]))
        {
            _PrintUsage(io.Error);
            return (int)ExitCode.Success;
        }

        // Positional arguments and the two options of this command; the settings options are accepted so scripts can share them.
        string command = args[0].ToUpperInvariant();
        List<string> positional = [];
        string? copyFrom = null;
        CliSettingsOptionsBuilder settings = new();
        for (int i = 1; i < args.Length; i++)
        {
            if (CliArgumentParsing.ParseSettingsOption(args, ref i, settings))
            {
                continue;
            }

            if (CliArgumentParsing.IsHelpFlag(args[i]))
            {
                _PrintUsage(io.Error);
                return (int)ExitCode.Success;
            }

            if (string.Equals(args[i], "--copy-from", StringComparison.OrdinalIgnoreCase))
            {
                copyFrom = CliArgumentParsing.GetNextArg(args, ref i, "--copy-from");
                continue;
            }

            positional.Add(args[i]);
        }

        string settingsBase = Path.GetFullPath(SettingsManagerFactory.ResolvePath(settings.Build().SettingsPath, null));
        return command switch
        {
            "LIST" => _List(settingsBase, positional, io),
            "CREATE" => _Create(settingsBase, positional, copyFrom, io),
            "RENAME" => _Rename(settingsBase, positional, io),
            "DELETE" => _Delete(settingsBase, positional, io),
            _ => _Unknown(args[0], io),
        };
    }

    /// <summary>Prints the profile names, one per line.</summary>
    private static int _List(string settingsBase, List<string> positional, CliIo io)
    {
        if (positional.Count != 0)
        {
            return _UsageError("'profile list' takes no arguments.", io);
        }

        foreach (string name in SettingsProfiles.List(settingsBase))
        {
            io.Out.WriteLine(name);
        }

        return (int)ExitCode.Success;
    }

    /// <summary>Creates a profile, empty or as a copy.</summary>
    private static int _Create(string settingsBase, List<string> positional, string? copyFrom, CliIo io)
    {
        if (positional.Count != 1)
        {
            return _UsageError("'profile create' needs exactly one profile name.", io);
        }

        if (!SettingsProfiles.TryCreate(settingsBase, positional[0], copyFrom, out string? error))
        {
            return _Refuse(error, io);
        }

        io.Out.WriteLine(Invariant($"Profile '{positional[0]}' created."));
        return (int)ExitCode.Success;
    }

    /// <summary>Renames a profile.</summary>
    private static int _Rename(string settingsBase, List<string> positional, CliIo io)
    {
        if (positional.Count != 2)
        {
            return _UsageError("'profile rename' needs the old and the new profile name.", io);
        }

        if (!SettingsProfiles.TryRename(settingsBase, positional[0], positional[1], out string? error))
        {
            return _Refuse(error, io);
        }

        io.Out.WriteLine(Invariant($"Profile '{positional[0]}' renamed to '{positional[1]}'."));
        return (int)ExitCode.Success;
    }

    /// <summary>Deletes a profile.</summary>
    private static int _Delete(string settingsBase, List<string> positional, CliIo io)
    {
        if (positional.Count != 1)
        {
            return _UsageError("'profile delete' needs exactly one profile name.", io);
        }

        if (!SettingsProfiles.TryDelete(settingsBase, positional[0], out string? error))
        {
            return _Refuse(error, io);
        }

        io.Out.WriteLine(Invariant($"Profile '{positional[0]}' deleted."));
        return (int)ExitCode.Success;
    }

    /// <summary>Reports an unknown sub-command with the usage.</summary>
    private static int _Unknown(string command, CliIo io)
    {
        io.Error.WriteLine($"Unknown profile command: '{command}'");
        _PrintUsage(io.Error);
        return (int)ExitCode.ArgumentError;
    }

    /// <summary>Reports wrong arguments with one error line and the usage.</summary>
    private static int _UsageError(string message, CliIo io)
    {
        io.Error.WriteLine("Error: " + message);
        _PrintUsage(io.Error);
        return (int)ExitCode.ArgumentError;
    }

    /// <summary>Reports a refusal of the profile operation as one error line.</summary>
    private static int _Refuse(string message, CliIo io)
    {
        io.Error.WriteLine("Error: " + message);
        return (int)ExitCode.ArgumentError;
    }

    /// <summary>Prints usage information for the profile command.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: ni profile <command> [options]");
        error.WriteLine();
        error.WriteLine("Manage settings profiles (folders under the settings base). Plugins are not loaded.");
        error.WriteLine();
        error.WriteLine("Commands:");
        error.WriteLine("  list                          Print the profile names, one per line");
        error.WriteLine("  create <name> [--copy-from <profile>]");
        error.WriteLine("                                Create an empty profile or a copy of another one");
        error.WriteLine("  rename <old> <new>            Rename a profile");
        error.WriteLine("  delete <name>                 Delete a profile (the last profile cannot be deleted)");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --settings-path <dir>         Base directory for settings storage");
        error.WriteLine("  -h, --help                    Show this help message");
        error.WriteLine();
        error.WriteLine("Names: 1 to 256 letters, digits, spaces, dots, dashes, and underscores; not application, logs, plugins,");
        error.WriteLine("plugins-data, plugin-scan-cache, host, app, or presets.");
    }

    #endregion
}
