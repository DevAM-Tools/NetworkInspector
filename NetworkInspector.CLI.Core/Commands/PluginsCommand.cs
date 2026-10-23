// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// <c>ni plugins list [--json]</c>: the plugins of this run (built-in and from <c>plugins.paths</c>) with their state, path,
/// license, load time, and what they contribute, preceded by the pre-scan summary. Text goes to the output writer as lines;
/// <c>--json</c> writes one indented JSON document.
/// </summary>
internal static class PluginsCommand
{
    #region Public API

    /// <summary>Runs the plugins command.</summary>
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

    /// <summary>Parses the arguments and runs the sub-command.</summary>
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

        if (!string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            io.Error.WriteLine($"Unknown plugins command: '{args[0]}'");
            _PrintUsage(io.Error);
            return (int)ExitCode.ArgumentError;
        }

        bool json = false;
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

            if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            throw new ArgumentException($"Unknown option '{args[i]}' for 'plugins list'.");
        }

        (CliPluginSession? session, int openExit) = await CliPluginSession.OpenAsync(settings.Build(), io, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return openExit;
        }

        await using (session.ConfigureAwait(false))
        {
            if (json)
            {
                io.Out.WriteLine(_ToJson(session.Report));
            }
            else
            {
                _WriteText(session.Report, io.Out);
            }
        }

        return (int)ExitCode.Success;
    }

    /// <summary>Writes the summary line and one line per plugin.</summary>
    private static void _WriteText(PluginLoadReport report, TextWriter output)
    {
        PrescanSummary prescan = report.Prescan;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Pre-scan: {prescan.CandidateFiles} candidate files, {prescan.InspectedFiles} inspected, {prescan.CachedFiles} from cache, "
            + $"{prescan.PluginAssemblies} plugin assemblies, {prescan.InspectionTime.TotalMilliseconds:0.0} ms"));
        foreach (PluginRecord record in report.Plugins)
        {
            string contributions = string.Join(", ", Enum.GetValues<ContributionKind>()
                .Where(kind => record.Contributions.TryGetValue(kind, out IReadOnlyList<ContributionEntry>? entries) && entries.Count > 0)
                .Select(kind => string.Create(CultureInfo.InvariantCulture, $"{kind} {record.Contributions[kind].Count}")));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Cell(record.Name)}  {Cell(record.Version)}  {_TierName(record.Tier)}  {record.State}  {Cell(record.Reason)}  "
                + $"{Cell(record.Path)}  {Cell(record.License)}  load {record.LoadTime.TotalMilliseconds:0.0} ms  {Cell(contributions)}"));
        }
    }

    /// <summary>Writes the report as one indented JSON document.</summary>
    private static string _ToJson(PluginLoadReport report)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("prescan");
            writer.WriteNumber("candidateFiles", report.Prescan.CandidateFiles);
            writer.WriteNumber("inspectedFiles", report.Prescan.InspectedFiles);
            writer.WriteNumber("cachedFiles", report.Prescan.CachedFiles);
            writer.WriteNumber("pluginAssemblies", report.Prescan.PluginAssemblies);
            writer.WriteNumber("inspectionMs", Math.Round(report.Prescan.InspectionTime.TotalMilliseconds, 1));
            writer.WriteEndObject();
            writer.WriteStartArray("plugins");
            foreach (PluginRecord record in report.Plugins)
            {
                _WriteRecord(writer, record);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes one plugin object.</summary>
    private static void _WriteRecord(Utf8JsonWriter writer, PluginRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("name", record.Name);
        writer.WriteString("version", record.Version);
        writer.WriteString("tier", _TierName(record.Tier));
        writer.WriteString("state", record.State.ToString());
        writer.WriteString("reason", record.Reason);
        writer.WriteString("path", record.Path);
        writer.WriteBoolean("builtIn", record.BuiltIn);
        writer.WriteString("license", record.License);
        writer.WriteNumber("loadMs", Math.Round(record.LoadTime.TotalMilliseconds, 1));
        writer.WriteStartArray("dependencies");
        foreach (PluginDependency dependency in record.Dependencies)
        {
            writer.WriteStartObject();
            writer.WriteString("name", dependency.Name);
            writer.WriteString("version", dependency.Version);
            writer.WriteString("license", dependency.License);
            writer.WriteString("copyrightHolder", dependency.CopyrightHolder);
            writer.WriteString("link", dependency.Link);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("contributions");
        foreach (ContributionKind kind in Enum.GetValues<ContributionKind>())
        {
            if (!record.Contributions.TryGetValue(kind, out IReadOnlyList<ContributionEntry>? entries) || entries.Count == 0)
            {
                continue;
            }

            writer.WriteStartArray(kind.ToString());
            foreach (ContributionEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteString("description", entry.Description);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The lower-case tier name used in the output.</summary>
    private static string _TierName(PluginTier tier) => tier switch
    {
        PluginTier.Host => "host",
        PluginTier.App => "app",
        _ => "core",
    };

    /// <summary>
    /// The text of one column of a plugin line: a dash for an empty or missing text, so the columns never collapse, and no
    /// control characters, because the reason of a failed plugin can be a multi-line exception message and the row must stay
    /// one line (the JSON form escapes them itself).
    /// </summary>
    internal static string Cell(string? text) =>
        string.IsNullOrEmpty(text)
            ? "-"
            : CliLogFile.SingleLine(text);

    /// <summary>Prints usage information for the plugins command.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: ni plugins list [--json] [options]");
        error.WriteLine();
        error.WriteLine("List the plugins of this run: built-in plugins and those found through the setting plugins.paths.");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --json                Write one JSON document instead of text lines");
        error.WriteLine("  --profile <name>      Settings profile (must exist; 'default' is created on first use)");
        error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        error.WriteLine("  --settings-max-config-file-bytes <bytes>");
        error.WriteLine("                        Max settings JSON size in bytes (default 1073741824). <=0 disables.");
        error.WriteLine("  --settings-max-json-depth <n>");
        error.WriteLine("                        Max JSON nesting depth for settings (default 1024). <=0 disables.");
        error.WriteLine("  -h, --help            Show this help message");
        error.WriteLine();
        error.WriteLine("Each line: name, version, tier, state, reason, path, license, load time, and what the plugin contributes.");
        error.WriteLine("Plugin folders are set with: ni settings set plugins.paths <folder> --scope application");
    }

    #endregion
}
