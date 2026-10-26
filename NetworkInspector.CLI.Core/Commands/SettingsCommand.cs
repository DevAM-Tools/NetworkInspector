// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// <c>ni settings list | get | set | reset</c>: reads and changes the application settings and the settings of a profile,
/// plugin settings included (plugins are loaded to register them). Values use the Core text form, errors use the Core
/// problem messages, and every check happens before a file is touched, so a refused command changes nothing. A value is written
/// with a merge save of its own key only, so other keys of the files (and other processes' changes) stay. Exit codes: a refused
/// command (unknown name, bad value, wrong arguments) is 1; a settings folder that cannot be written (locked by another
/// process, I/O error) is 3. Results go to the output writer, errors to the error writer. Stateless; thread-safe.
/// </summary>
internal static class SettingsCommand
{
    #region Types

    /// <summary>The parsed arguments of one run.</summary>
    private sealed record SettingsArguments(
        string Command,
        List<string> Positional,
        SettingScope? Scope,
        string? Group,
        bool Json,
        CliSettingsOptions Options);

    #endregion

    #region Public API

    /// <summary>Runs the settings command.</summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsync(string[] args, CliIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        return CliArgumentParsing.RunWithArgumentGuardAsync(() => _RunCoreAsync(args, io, cancellationToken), io.Error);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Parses the arguments, opens the session, and runs the sub-command. The syntax (sub-command, number of arguments, scope
    /// text) is checked before the session opens, so a usage error never loads a plugin or touches a file.
    /// </summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">Cancels the plugin load and start.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentException">An option has no value or an invalid value; the caller maps it to exit code 1.</exception>
    private static async Task<int> _RunCoreAsync(string[] args, CliIo io, CancellationToken cancellationToken)
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

        string command = args[0].ToUpperInvariant();
        if (command is not ("LIST" or "GET" or "SET" or "RESET"))
        {
            io.Error.WriteLine($"Unknown settings command: '{args[0]}'");
            _PrintUsage(io.Error);
            return (int)ExitCode.ArgumentError;
        }

        // Options are shared by all sub-commands except --json, which only "list" knows; for the others "--json" is not an
        // option and ends up in the positional arguments, where the count check refuses it. For "list" a null scope means
        // both scopes ("all").
        List<string> positional = [];
        SettingScope? scope = null;
        string? group = null;
        bool json = false;
        CliSettingsOptionsBuilder settings = new();
        for (int i = 1; i < args.Length; i++)
        {
            if (CliArgumentParsing.ParseSettingsOption(args, ref i, settings))
            {
                continue;
            }

            switch (args[i].ToUpperInvariant())
            {
                case "-H" or "--HELP":
                    _PrintUsage(io.Error);
                    return (int)ExitCode.Success;
                case "--SCOPE":
                    scope = _ParseScope(CliArgumentParsing.GetNextArg(args, ref i, "--scope"), command == "LIST");
                    break;
                case "--GROUP":
                    group = CliArgumentParsing.GetNextArg(args, ref i, "--group");
                    break;
                case "--JSON" when command == "LIST":
                    json = true;
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        // The number of positional arguments is part of the syntax: a wrong count is usage, not a settings problem.
        int expected = command switch
        {
            "LIST" => 0,
            "SET" => 2,
            _ => 1,
        };
        if (positional.Count != expected)
        {
            io.Error.WriteLine("Error: " + _Syntax(command));
            _PrintUsage(io.Error);
            return (int)ExitCode.ArgumentError;
        }

        // Plugins are loaded on purpose: their settings only exist (with type, range, and description) while the plugin is
        // loaded. A stored value of a plugin that is not loaded is still reachable as an orphan.
        SettingsArguments arguments = new(command, positional, scope, group, json, settings.Build());
        (CliPluginSession? session, int openExit) = await CliPluginSession.OpenAsync(arguments.Options, io, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return openExit;
        }

        await using (session.ConfigureAwait(false))
        {
            // Only the write paths can fail with a persistence error (locked folder, I/O); it is a runtime failure (3), not a
            // usage error. All other failures were refused by the lookup and the Core codec before anything was written.
            try
            {
                return arguments.Command switch
                {
                    "LIST" => _List(session, arguments, io),
                    "GET" => _Get(session, arguments, io),
                    "SET" => _Set(session, arguments, io),
                    _ => _Reset(session, arguments, io),
                };
            }
            catch (PersistenceSettingsException ex)
            {
                io.Error.WriteLine("Error: " + ex.Message);
                return (int)ExitCode.RuntimeError;
            }
        }
    }

    /// <summary>Parses <c>application</c>, <c>profile</c>, or (for list) <c>all</c>, ignoring case.</summary>
    /// <param name="text">The value of <c>--scope</c>.</param>
    /// <param name="allowAll">Whether <c>all</c> is accepted (only the list command).</param>
    /// <returns>The scope, or <see langword="null"/> for <c>all</c> (both scopes).</returns>
    /// <exception cref="ArgumentException">The text names no scope.</exception>
    private static SettingScope? _ParseScope(string text, bool allowAll)
    {
        if (string.Equals(text, "application", StringComparison.OrdinalIgnoreCase))
        {
            return SettingScope.Application;
        }

        if (string.Equals(text, "profile", StringComparison.OrdinalIgnoreCase))
        {
            return SettingScope.Profile;
        }

        if (allowAll && string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new ArgumentException(allowAll
            ? $"Unknown scope '{text}'; use application, profile, or all."
            : $"Unknown scope '{text}'; use application or profile.");
    }

    /// <summary>The syntax line of a sub-command for the error about a wrong number of arguments.</summary>
    /// <param name="command">The upper-case sub-command.</param>
    /// <returns>One line of text.</returns>
    private static string _Syntax(string command) => command switch
    {
        "LIST" => "'settings list' takes no arguments.",
        "GET" => "usage: ni settings get <name> [--scope application|profile] [--group <group>]",
        "SET" => "usage: ni settings set <name> <value> [--scope application|profile] [--group <group>]",
        _ => "usage: ni settings reset <name> [--scope application|profile]",
    };

    #endregion

    #region List

    /// <summary>
    /// Lists the settings of the chosen scopes: per scope the registered settings and the stored values of plugins that are not
    /// loaded together, sorted by name (ordinal, so the order does not depend on the culture); application scope first.
    /// </summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    private static int _List(CliPluginSession session, SettingsArguments arguments, CliIo io)
    {
        // An unknown group is refused like in the lookup, with the list of groups.
        if (arguments.Group is not null && !SettingsLookup.Groups(session, arguments.Scope).Contains(arguments.Group, StringComparer.Ordinal))
        {
            io.Error.WriteLine(Invariant(
                $"Error: Unknown group '{arguments.Group}'; groups: {string.Join(", ", SettingsLookup.Groups(session, arguments.Scope).Order(StringComparer.Ordinal))}."));
            return (int)ExitCode.ArgumentError;
        }

        // Collect per scope. "Stored" tells whether a value is present in a file (as opposed to the default applying); it is
        // found by reading the key names of the group files.
        List<Entry> entries = [];
        foreach ((SettingScope scope, SettingsManager manager) in SettingsLookup.Scopes(session, arguments.Scope))
        {
            HashSet<string> stored = _StoredNames(manager);
            List<Entry> scopeEntries = [];
            foreach (Setting setting in manager.AllSettings)
            {
                if (arguments.Group is null || string.Equals(setting.GroupName, arguments.Group, StringComparison.Ordinal))
                {
                    scopeEntries.Add(new Entry(scope, setting.Name, setting, null, stored.Contains(setting.Name)));
                }
            }

            foreach (OrphanedSettingValue orphan in manager.Orphans)
            {
                if (arguments.Group is null || string.Equals(orphan.GroupName, arguments.Group, StringComparison.Ordinal))
                {
                    scopeEntries.Add(new Entry(scope, orphan.Name, null, orphan, true));
                }
            }

            entries.AddRange(scopeEntries.OrderBy(static entry => entry.Name, StringComparer.Ordinal));
        }

        // JSON is the complete form; the text form is one line per setting: scope, name, type, value.
        if (arguments.Json)
        {
            io.Out.WriteLine(_ToJson(entries));
            return (int)ExitCode.Success;
        }

        foreach (Entry entry in entries)
        {
            string type = entry.Setting is null
                ? "(not loaded)"
                : entry.Setting.Type.ToString();
            string value = entry.Setting is null
                ? entry.Orphan?.Value.GetRawText() ?? string.Empty
                : SettingValueText.Format(entry.Setting, entry.Setting.Value);
            // A string value can hold line breaks or terminal sequences (from a settings file or another process); the list is one
            // line per setting, so the text is cleaned here. "get" prints the value as it is, for scripts.
            io.Out.WriteLine($"{ScopeName(entry.Scope)}  {entry.Name}  {type}  {CliLogFile.SingleLine(value)}");
        }

        return (int)ExitCode.Success;
    }

    /// <summary>One line of the list.</summary>
    private sealed record Entry(SettingScope Scope, string Name, Setting? Setting, OrphanedSettingValue? Orphan, bool Stored);

    /// <summary>
    /// The names of the keys present in the group files of a manager's folder. A file that cannot be read or parsed contributes
    /// no names (the manager's own load already reported it).
    /// </summary>
    /// <param name="manager">The settings manager of a scope.</param>
    /// <returns>The key names, compared ordinally.</returns>
    private static HashSet<string> _StoredNames(SettingsManager manager)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        if (manager.StoragePath is null || !Directory.Exists(manager.StoragePath))
        {
            return names;
        }

        // The files are read with the limits the manager loaded them with (--settings-max-*): a file that Load skipped as too
        // large or too deep is not read here either, and nothing is loaded into one string (the parser reads the stream).
        // Residual risk: the size is checked before the read, so a file that grows in between is bounded only by the depth
        // limit; the files belong to the user who runs the command.
        JsonDocumentOptions documentOptions = new()
        {
            MaxDepth = manager.MaxJsonDepth > 0
                ? manager.MaxJsonDepth
                : int.MaxValue,
        };
        foreach (string file in Directory.EnumerateFiles(manager.StoragePath, "*.json"))
        {
            try
            {
                if (manager.MaxConfigFileBytes > 0 && new FileInfo(file).Length > manager.MaxConfigFileBytes)
                {
                    continue;
                }

                // Shared read/write/delete access, so that another process that holds or replaces the file at the same time does
                // not make this read fail.
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (JsonNode.Parse(stream, nodeOptions: null, documentOptions) is JsonObject obj)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in obj)
                    {
                        names.Add(pair.Key);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read is not a source of stored values; Load already reported it.
            }
        }

        return names;
    }

    /// <summary>
    /// Writes the list as a JSON array of setting objects. Every object has the same keys: a stored value of a plugin that is not
    /// loaded (<c>orphan: true</c>) has no type information, so its <c>uiName</c>, <c>type</c>, <c>default</c>,
    /// <c>description</c>, and <c>activation</c> are <c>null</c>, and its value is the stored JSON as it is.
    /// </summary>
    /// <param name="entries">The entries in output order.</param>
    /// <returns>The JSON text.</returns>
    private static string _ToJson(List<Entry> entries)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (Entry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("scope", ScopeName(entry.Scope));
                writer.WriteString("name", entry.Name);
                if (entry.Setting is { } setting)
                {
                    writer.WriteString("uiName", setting.UiName);
                    writer.WriteString("group", setting.GroupName);
                    writer.WriteString("type", setting.Type.ToString());
                    writer.WritePropertyName("value");
                    SettingValueJson.Write(writer, setting, setting.Value);
                    writer.WritePropertyName("default");
                    SettingValueJson.Write(writer, setting, setting.DefaultValue);
                    writer.WriteString("description", setting.Description);
                    writer.WriteString("activation", setting.Activation.ToString());
                    writer.WriteBoolean("stored", entry.Stored);
                    writer.WriteBoolean("orphan", false);
                }
                else if (entry.Orphan is { } orphan)
                {
                    writer.WriteNull("uiName");
                    writer.WriteString("group", orphan.GroupName);
                    writer.WriteNull("type");
                    writer.WritePropertyName("value");
                    orphan.Value.WriteTo(writer);
                    writer.WriteNull("default");
                    writer.WriteNull("description");
                    writer.WriteNull("activation");
                    writer.WriteBoolean("stored", true);
                    writer.WriteBoolean("orphan", true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The lower-case name of a scope in the output (the same words <c>--scope</c> accepts).</summary>
    /// <param name="scope">The scope.</param>
    /// <returns><c>application</c> or <c>profile</c>.</returns>
    internal static string ScopeName(SettingScope scope) => scope == SettingScope.Application
        ? "application"
        : "profile";

    #endregion

    #region Get, set, reset

    /// <summary>
    /// Prints the current value of one setting in the Core text form, without any decoration, so a script can capture it. Unlike
    /// the list, the value is printed as it is (no control-character cleaning).
    /// </summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    private static int _Get(CliPluginSession session, SettingsArguments arguments, CliIo io)
    {
        if (!SettingsLookup.TryFind(session, arguments.Positional[0], arguments.Scope, arguments.Group, out SettingsLookup.Match? match,
            out string? error))
        {
            io.Error.WriteLine("Error: " + error);
            return (int)ExitCode.ArgumentError;
        }

        io.Out.WriteLine(match.Setting is { } setting
            ? SettingValueText.Format(setting, setting.Value)
            : match.Orphan?.Value.GetRawText() ?? string.Empty);
        return (int)ExitCode.Success;
    }

    /// <summary>
    /// Sets one setting: find, parse, validate, apply, and merge-save its key. Everything that can be refused is refused before
    /// the first write, so a refused command changes nothing.
    /// </summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="PersistenceSettingsException">The settings folder cannot be written (the caller maps it to exit code 3).</exception>
    private static int _Set(CliPluginSession session, SettingsArguments arguments, CliIo io)
    {
        string name = arguments.Positional[0];
        string text = arguments.Positional[1];
        if (!SettingsLookup.TryFind(session, name, arguments.Scope, arguments.Group, out SettingsLookup.Match? match, out string? error))
        {
            io.Error.WriteLine("Error: " + error);
            return (int)ExitCode.ArgumentError;
        }

        // A stored value of a plugin that is not loaded has no type: it takes JSON and nothing else.
        if (match.Setting is null)
        {
            return _SetOrphan(match, name, text, io);
        }

        Setting setting = match.Setting;
        if (!SettingValueText.TryParse(setting, text, out SettingValue value, out SettingProblem? problem))
        {
            io.Error.WriteLine("Error: " + problem);
            return (int)ExitCode.ArgumentError;
        }

        // Relative paths are resolved against the base directory of the run (the current directory) before they are stored,
        // because a stored path must mean one place: a relative path in a settings file would point somewhere else for every
        // later run from another directory. Empty entries are left as they are.
        if (string.Equals(name, PluginPathsSetting.Name, StringComparison.Ordinal) && value.TryGetAsStringArray(out string[] paths))
        {
            string baseDirectory = io.BaseDirectory;
            value = SettingValue.StringArray([.. paths.Select(path => path.Length == 0
                ? path
                : Path.GetFullPath(path, baseDirectory))]);
        }

        // The setting validates the value (range, shape) and keeps it as pending; a refusal comes back as a problem, not as an
        // exception. Applying makes the pending value the current one, which is what SaveChanged writes; it then merges only this
        // key into the group file under the folder lock, so other keys and other processes' changes survive.
        if (!setting.TrySetPendingValue(value, out SettingProblem? setProblem))
        {
            io.Error.WriteLine("Error: " + setProblem);
            return (int)ExitCode.ArgumentError;
        }

        match.Manager.ApplyChanges([name]);
        match.Manager.SaveChanged([name]);
        io.Out.WriteLine($"{name} = {SettingValueText.Format(setting, setting.Value)}");
        return (int)ExitCode.Success;
    }

    /// <summary>
    /// Sets the stored value of a setting whose plugin is not loaded; the text must be JSON, because without the plugin there is
    /// no type to parse a plain text against. The text is one command-line argument (bounded by the operating system) and is
    /// parsed with the default depth limit of <see cref="JsonDocument"/>.
    /// </summary>
    /// <param name="match">The orphan found by the lookup.</param>
    /// <param name="name">The setting name.</param>
    /// <param name="text">The value as typed.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="PersistenceSettingsException">The settings folder cannot be written (the caller maps it to exit code 3).</exception>
    private static int _SetOrphan(SettingsLookup.Match match, string name, string text, CliIo io)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            io.Error.WriteLine(Invariant($"Error: {name}: '{text}' is not valid JSON; the plugin that registers this setting is not loaded."));
            return (int)ExitCode.ArgumentError;
        }

        using (document)
        {
            // The manager refuses a JSON null and a name that is not an orphan; nothing is written then.
            if (!match.Manager.TrySetOrphanValue(name, document.RootElement))
            {
                io.Error.WriteLine(Invariant($"Error: {name}: the value cannot be stored."));
                return (int)ExitCode.ArgumentError;
            }

            match.Manager.SaveChanged([name]);
            io.Out.WriteLine($"{name} = {document.RootElement.GetRawText()}");
        }

        return (int)ExitCode.Success;
    }

    /// <summary>
    /// Removes the stored value of a setting; the default applies again. A stored value of a plugin that is not loaded is removed
    /// from the file. Only the one key is removed (merge write), other keys of the file stay.
    /// </summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="PersistenceSettingsException">The settings folder cannot be written (the caller maps it to exit code 3).</exception>
    private static int _Reset(CliPluginSession session, SettingsArguments arguments, CliIo io)
    {
        string name = arguments.Positional[0];
        if (!SettingsLookup.TryFind(session, name, arguments.Scope, arguments.Group, out SettingsLookup.Match? match, out string? error))
        {
            io.Error.WriteLine("Error: " + error);
            return (int)ExitCode.ArgumentError;
        }

        // Unset changes the file first and the in-memory value second, and needs no separate save; false means the name is no
        // longer known to the manager (the lookup just found it, so this only guards against a change in between).
        if (!match.Manager.Unset(name))
        {
            io.Error.WriteLine($"Error: Unknown setting '{name}'; see ni settings list.");
            return (int)ExitCode.ArgumentError;
        }

        io.Out.WriteLine(match.Setting is { } setting
            ? $"{name} = {SettingValueText.Format(setting, setting.DefaultValue)} (default)"
            : $"{name} removed");
        return (int)ExitCode.Success;
    }

    /// <summary>Prints usage information for the settings command.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: ni settings <command> [options]");
        error.WriteLine();
        error.WriteLine("Read and change the application settings and the settings of a profile (plugin settings included).");
        error.WriteLine();
        error.WriteLine("Commands:");
        error.WriteLine("  list  [--scope application|profile|all] [--group <group>] [--json]");
        error.WriteLine("  get   <name> [--scope application|profile] [--group <group>]");
        error.WriteLine("  set   <name> <value> [--scope application|profile] [--group <group>]");
        error.WriteLine("  reset <name> [--scope application|profile]    Remove the stored value; the default applies");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --profile <name>      Settings profile (must exist; 'default' is created on first use)");
        error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        error.WriteLine("  --settings-max-config-file-bytes <bytes>");
        error.WriteLine("                        Max settings JSON size in bytes (default 1073741824). <=0 disables.");
        error.WriteLine("  --settings-max-json-depth <n>");
        error.WriteLine("                        Max JSON nesting depth for settings (default 1024). <=0 disables.");
        error.WriteLine("  -h, --help            Show this help message");
        error.WriteLine();
        error.WriteLine("Values: true/false, numbers, hex bytes (0A1B), enumeration names, text, and arrays as JSON ([1,2]) or comma lists.");
        error.WriteLine("Relative paths in plugins.paths are stored as full paths. A stored value of a plugin that is not loaded is JSON.");
    }

    #endregion
}
