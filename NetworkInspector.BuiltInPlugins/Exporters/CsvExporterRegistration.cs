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

    /// <summary>Option name: prefix text cells that a spreadsheet would run as a formula.</summary>
    private const string _NeutralizeFormulasOption = "neutralizeFormulas";

    /// <summary>Option name: text between the occurrences of one field in a joined cell.</summary>
    private const string _SeparatorOption = "separator";

    /// <summary>
    /// The content members of the CSV options: the packet rows shape and the <c>frames</c> and <c>buffers</c> switches. CSV keeps
    /// packet-level data in columns, so the switches are not part of the content it passes on (see <see cref="_TryCreate"/>).
    /// </summary>
    private const ExportContentOptions.ContentMembers _Members =
        ExportContentOptions.ContentMembers.PacketRows
        | ExportContentOptions.ContentMembers.FrameBytes
        | ExportContentOptions.ContentMembers.Buffers;

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
        Creation: ExporterCreation.ForStreams<IPacketListener>(_TryCreate));

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterStringSetting(_DelimiterOption, "Delimiter", "", ",",
            description: "Field separator: exactly one ASCII character other than NUL, CR, LF, the double quote, an ASCII letter or digit, " +
                "and - + . : (a tab is allowed, and so is ;).");
        options.RegisterBoolSetting(_HeaderOption, "Header row", "", true, description: "Write a header row with the column titles.");
        options.RegisterBoolSetting(_BomOption, "Byte order mark", "", true,
            description: "Write a UTF-8 byte order mark so that spreadsheet programs detect the encoding.");
        ExportContentOptions.RegisterContent(options, CsvExporter.ContentTemplate, _Members);
        options.RegisterStringSetting(_SeparatorOption, "Occurrence separator", "", "|",
            description: "Text between the occurrences of one field in a cell when a field occurs more than once in a packet (the "
                + "selected-fields layout). Must not contain a line break. Default '|'.");

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
    private static bool _TryCreate(StreamExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The delimiter, the separator, and the field list are the options that Core settings cannot check on their own.
        listener = null;
        ReadOnlySettingsManagerView values = request.Options;
        string separator = OptionValues.GetString(values, _SeparatorOption);
        if (!_TryGetDelimiter(OptionValues.GetString(values, _DelimiterOption), out char delimiter, out error)
            || !_TryGetSeparator(separator, out error)
            || !ExportContentOptions.TryReadContent(
                values, request.Stack, CsvExporter.ContentTemplate, _Members, out ExportContent? content, out error))
        {
            return false;
        }

        // The frame bytes and the additional buffers are columns in CSV, not content members: the switches are read here (the
        // packet-rows shape has no content to carry them), the content passed on keeps both off, and the builder gets the columns.
        // The long layouts repeat the packet columns on every field row, so the combination is refused here with a PluginError, before
        // a file is created (the builder refuses it with an ArgumentException for library callers). Sync: the message at
        // CsvExporter.Builder.Build.
        bool frames = OptionValues.GetBool(values, ExportContentOptions.FramesOption);
        bool buffers = OptionValues.GetBool(values, ExportContentOptions.BuffersOption);
        if (content is { Shape: ExportShape.AllFields or ExportShape.Hierarchy } && (frames || buffers))
        {
            // Sync: the wording of CsvExporter.Builder.Build (the same refusal for the library API) and the CSV README § Frame bytes
            // and § Buffers; the message names the column kind and the shape.
            CsvColumnKind refusedKind = frames
                ? CsvColumnKind.FrameBytes
                : CsvColumnKind.Buffers;
            error = new PluginError(
                PluginErrorCode.InvalidOption,
                Invariant($"The {refusedKind} column is only valid in packet rows and the selected-fields layout, not with the shape ") +
                Invariant($"{content.Shape}."),
                frames
                    ? ExportContentOptions.FramesOption
                    : ExportContentOptions.BuffersOption);
            return false;
        }

        if (content is not null)
        {
            content = content with { FrameBytes = false, Buffers = false };
        }

        bool header = OptionValues.GetBool(values, _HeaderOption);
        bool bom = OptionValues.GetBool(values, _BomOption);
        bool neutralizeFormulas = OptionValues.GetBool(values, _NeutralizeFormulasOption);
        return ExporterBuild.TryBuildOnTarget<IPacketListener>(
            () =>
            {
                CsvExporter.Builder builder = CsvExporter.CreateBuilder()
                    .ToTarget(request.Target)
                    .WithDelimiter(delimiter)
                    .WithHeader(header)
                    .WithBom(bom)
                    .WithFormulaNeutralization(neutralizeFormulas)
                    .WithOccurrenceSeparator(separator)
                    .WithCancellationToken(request.CancellationToken);

                // No content means the packet rows with the default columns (the format default shape "packets"); a content
                // writes the fields in the chosen layout after the packet number and the time.
                if (content is not null)
                {
                    builder.WithContent(content);
                }

                // A configured column replaces the default columns of the layout, so with frames or buffers on the layout's default
                // prefix is added first: No., Time, Info, Length for packet rows; No. and Time for the wide layout. The frame column
                // comes next and the buffers column last.
                if (frames || buffers)
                {
                    if (content is null)
                    {
                        builder.WithDefaultColumns();
                    }
                    else
                    {
                        builder.WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Timestamp);
                    }

                    if (frames)
                    {
                        builder.WithColumn(CsvColumnKind.FrameBytes);
                    }

                    if (buffers)
                    {
                        builder.WithColumn(CsvColumnKind.Buffers);
                    }
                }

                return builder.Build();
            },
            out listener, out error);
    }

    /// <summary>Checks the occurrence separator against the rule of the Core builder, so that the error names the option.</summary>
    private static bool _TryGetSeparator(string text, [NotNullWhen(false)] out PluginError? error)
    {
        if (text.Length == 0 || text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                "Option 'separator' must not be empty and must not contain CR or LF.", _SeparatorOption);
            return false;
        }

        error = null;
        return true;
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

        // Sync: CsvExporter.Builder.WithDelimiter (the same set); such a delimiter occurs in the unquoted number, timestamp, and hex cells.
        if (char.IsAsciiLetterOrDigit(candidate) || candidate is '-' or '+' or '.' or ':')
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                "The delimiter must not be an ASCII letter, a digit, or one of - + . : (they occur in unquoted cells).", _DelimiterOption);
            return false;
        }

        delimiter = candidate;
        error = null;
        return true;
    }
    #endregion
}
