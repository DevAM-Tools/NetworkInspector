// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>CSV</c> packet exporter. The registration returns the Core <see cref="CsvExporter"/> itself. Columns are named
/// by protocol field names, resolved against the stack that parsed the packets. Stateless; the factory may run concurrently.
/// </summary>
internal static class CsvExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "CSV";

    /// <summary>Option name: field separator.</summary>
    private const string _DelimiterOption = "delimiter";

    /// <summary>Option name: write a header row.</summary>
    private const string _HeaderOption = "header";

    /// <summary>Option name: write a UTF-8 byte order mark.</summary>
    private const string _BomOption = "bom";

    /// <summary>Option name: list of field columns.</summary>
    private const string _ColumnsOption = "columns";

    /// <summary>Option name: prefix text cells that a spreadsheet would run as a formula.</summary>
    private const string _NeutralizeFormulasOption = "neutralizeFormulas";

    /// <summary>Separator of the field names in the <c>columns</c> option.</summary>
    private const char _ColumnSeparator = ';';

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "CSV",
        Description: "Writes one row per packet; the default columns are number, time, info, and length.",
        FileExtension: ".csv",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterStringSetting(_DelimiterOption, "Delimiter", "", ",",
            description: "Field separator: exactly one ASCII character other than NUL, CR, LF, and the double quote (a tab is allowed).");
        options.RegisterBoolSetting(_HeaderOption, "Header row", "", true, description: "Write a header row with the column titles.");
        options.RegisterBoolSetting(_BomOption, "Byte order mark", "", true,
            description: "Write a UTF-8 byte order mark so that spreadsheet programs detect the encoding.");
        options.RegisterStringSetting(_ColumnsOption, "Columns", "", "",
            description: "Protocol field names separated by ';' (for example ip.src;udp.dstport); empty writes number, time, info, and length.");

        // Threat: CSV injection. Text in a capture (a DNS name, an HTTP line, the info column) is controlled by whoever sent the
        // traffic, and a spreadsheet runs a cell that starts with =, +, -, @, tab, or CR as a formula when the file is opened.
        // Posture: secure by default (on); the user may turn it off, because machine processing needs the exact text and the
        // added apostrophe would corrupt it. The Core exporter owns the rule; this option only passes the choice through.
        // Rejected alternative: no option (a fixed behavior would break every consumer that reads the text back).
        // Residual risk when off: any text cell may run as a formula in a spreadsheet.
        options.RegisterBoolSetting(_NeutralizeFormulasOption, "Neutralize spreadsheet formulas", "", true,
            description: "Protects against CSV injection: a text cell (the info column and text fields) that starts with =, +, -, @, tab, " +
                "or CR gets a leading apostrophe, so a spreadsheet shows it as text instead of running it as a formula. Numbers, times, " +
                "addresses, and the header row are never changed. Turn it off for machine processing, where the apostrophe would " +
                "change the data; the file is then unsafe to open in a spreadsheet.");
    }

    /// <summary>Creates the Core CSV exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The delimiter and the columns are the options that Core settings cannot check on their own.
        listener = null;
        ReadOnlySettingsManagerView values = request.Options;
        if (!_TryGetDelimiter(OptionValues.GetString(values, _DelimiterOption), out char delimiter, out error)
            || !_TryGetColumns(OptionValues.GetString(values, _ColumnsOption), request.Stack, out List<(string Name, FieldId Id)>? columns,
                out error))
        {
            return false;
        }

        bool header = OptionValues.GetBool(values, _HeaderOption);
        bool bom = OptionValues.GetBool(values, _BomOption);
        bool neutralizeFormulas = OptionValues.GetBool(values, _NeutralizeFormulasOption);
        return ExporterBuild.TryBuild<IPacketListener>(
            () =>
            {
                CsvExporter.Builder builder = CsvExporter.CreateBuilder()
                    .ToFile(request.TargetPath)
                    .WithDelimiter(delimiter)
                    .WithHeader(header)
                    .WithBom(bom)
                    .WithFormulaNeutralization(neutralizeFormulas)
                    .WithCancellationToken(request.CancellationToken);

                // No field names means the default columns; otherwise exactly the listed fields, titled by their names.
                if (columns.Count == 0)
                {
                    builder.WithDefaultColumns();
                }
                else
                {
                    foreach ((string name, FieldId id) in columns)
                    {
                        builder.WithFieldColumn(name, id, name);
                    }
                }

                return builder.Build();
            },
            request.TargetPath, out listener, out error);
    }

    /// <summary>Checks the delimiter against the rules of the Core builder, so that the error names the option.</summary>
    private static bool _TryGetDelimiter(string text, out char delimiter, [NotNullWhen(false)] out PluginError? error)
    {
        // Exactly one ASCII character that cannot break the row structure or the quoting.
        delimiter = ',';
        if (text.Length != 1)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "The delimiter must be exactly one character.", _DelimiterOption);
            return false;
        }

        char candidate = text[0];
        if (candidate > '\u007F')
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "The delimiter must be an ASCII character.", _DelimiterOption);
            return false;
        }

        if (candidate is '\0' or '\r' or '\n' or '"')
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                "The delimiter must not be a NUL, CR, or LF character or a double quote.", _DelimiterOption);
            return false;
        }

        delimiter = candidate;
        error = null;
        return true;
    }

    /// <summary>Resolves the listed field names with the stack that parsed the packets.</summary>
    private static bool _TryGetColumns(string text, IStack stack, [NotNullWhen(true)] out List<(string Name, FieldId Id)>? columns,
        [NotNullWhen(false)] out PluginError? error)
    {
        // Blank entries are skipped so that a trailing separator does not matter; an unknown name is the user's mistake.
        columns = [];
        foreach (string entry in text.Split(_ColumnSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            FieldId? id = stack.GetFieldId(entry);
            if (id is null)
            {
                columns = null;
                error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"Unknown field '{entry}' in the columns."),
                    _ColumnsOption);
                return false;
            }

            columns.Add((entry, id.Value));
        }

        error = null;
        return true;
    }

    #endregion
}
