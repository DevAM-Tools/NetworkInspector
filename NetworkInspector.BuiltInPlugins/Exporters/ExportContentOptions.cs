// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Shared option names, registration, and parsing of the export content (shape, fields, occurrences, texts) and of the batch
/// writing settings for the built-in exporter registrations. One place spells the options, so <c>duckdb:fields=…</c> and
/// <c>csv:fields=…</c> behave the same and the export dialogs of the apps show the same texts for every format.
/// <para>
/// <b>Field list syntax:</b> <c>name[:occurrence]</c> entries joined by <c>;</c>, because the CLI format spec splits options at
/// commas and has no escaping; <c>:</c> after a name selects <c>all</c>, <c>first</c>, or <c>last</c> for that field. Names are
/// resolved against the stack of the export request (field ids belong to one stack), so a typo is reported with its name before
/// any file is created.
/// </para>
/// <para><b>Thread safety:</b> stateless; the registrations may call it concurrently.</para>
/// </summary>
internal static class ExportContentOptions
{
    #region Constants

    /// <summary>Option name: the export shape.</summary>
    internal const string ShapeOption = "shape";

    /// <summary>Option name: the selected fields.</summary>
    internal const string FieldsOption = "fields";

    /// <summary>Option name: the default occurrence choice of the selection.</summary>
    internal const string OccurrenceOption = "occurrence";

    /// <summary>Option name: write the packet info.</summary>
    internal const string InfoOption = "info";

    /// <summary>Option name: write the frame bytes.</summary>
    internal const string FramesOption = "frames";

    /// <summary>Option name: write the additional packet buffers.</summary>
    internal const string BuffersOption = "buffers";

    /// <summary>Option name: write the custom texts.</summary>
    internal const string TextOption = "text";

    /// <summary>Option name: write the custom representations.</summary>
    internal const string ReprOption = "repr";

    /// <summary>Option name: write batches on a writer thread or inline.</summary>
    internal const string WriteModeOption = "writeMode";

    /// <summary>Option name: packets per batch.</summary>
    internal const string BatchPacketsOption = "batchPackets";

    /// <summary>Option name: estimated bytes per batch.</summary>
    internal const string BatchBytesOption = "batchBytes";

    /// <summary>
    /// Default of the <c>batchPackets</c> option: 50 000 packets per batch. Spelled out here because the limits type is internal to
    /// the exporters assembly; a test compares it with the builder default. Limit record: <see cref="RegisterBatching"/>.
    /// </summary>
    private const long _DefaultPacketsPerBatch = 50_000;

    /// <summary>
    /// Default of the <c>batchBytes</c> option: 16 MiB of estimated batch size, in bytes. Spelled out here because the limits type
    /// is internal to the exporters assembly; a test compares it with the builder default. Limit record:
    /// <see cref="RegisterBatching"/>.
    /// </summary>
    private const long _DefaultBytesPerBatch = 16L * 1024 * 1024;

    /// <summary>Separator of the entries of the field list.</summary>
    private const char _FieldSeparator = ';';

    /// <summary>
    /// Enum value of the <c>auto</c> shape text: selected fields when a field list is given, else the format default. The shape
    /// numbers are stable identifiers of the registered setting, not <see cref="ExportShape"/> values.
    /// </summary>
    private const int _ShapeAuto = 0;

    /// <summary>
    /// Enum value of the <c>fields</c> shape text (selected fields); a stable identifier of the registered setting, not an
    /// <see cref="ExportShape"/> value.
    /// </summary>
    private const int _ShapeFields = 1;

    /// <summary>
    /// Enum value of the <c>all</c> shape text (every field); a stable identifier of the registered setting, not an
    /// <see cref="ExportShape"/> value.
    /// </summary>
    private const int _ShapeAll = 2;

    /// <summary>
    /// Enum value of the <c>hierarchy</c> shape text (the field tree); a stable identifier of the registered setting, not an
    /// <see cref="ExportShape"/> value.
    /// </summary>
    private const int _ShapeHierarchy = 3;

    /// <summary>
    /// Enum value of the <c>packets</c> shape text (packet rows only, CSV only); a stable identifier of the registered setting, not an
    /// <see cref="ExportShape"/> value.
    /// </summary>
    private const int _ShapePackets = 4;

    #endregion

    #region Types

    /// <summary>Which content members a format can write; members it cannot write are not registered.</summary>
    [Flags]
    internal enum ContentMembers
    {
        /// <summary>No optional member.</summary>
        None = 0,

        /// <summary>The packet info string.</summary>
        Info = 1,

        /// <summary>The raw frame bytes (every packet exporter registers the <c>frames</c> option; CSV turns it into a column).</summary>
        FrameBytes = 2,

        /// <summary>CSV: the <c>packets</c> shape (the packet rows) is the format default.</summary>
        PacketRows = 4,

        /// <summary>
        /// The additional packet buffers (every packet exporter registers the <c>buffers</c> option, CSV turns it into a column; the
        /// frame exporters PCAPNG, BLF, and ASC never register it, because they write frames, not packet content).
        /// </summary>
        Buffers = 8,
    }

    #endregion

    #region Registration

    /// <summary>Registers shape, fields, occurrence, and the text, info, frames, and buffers switches with the format's defaults.</summary>
    /// <param name="options">The registrar of the format.</param>
    /// <param name="defaults">The default content of the format; CSV passes its content template, since it writes packet rows by default.</param>
    /// <param name="members">The members the format can write.</param>
    internal static void RegisterContent(SettingsRegistrar options, ExportContent defaults, ContentMembers members)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        bool packetRows = (members & ContentMembers.PacketRows) != 0;
        (string Name, ulong Value)[] shapes = packetRows
            ? [("auto", _ShapeAuto), ("fields", _ShapeFields), ("all", _ShapeAll), ("hierarchy", _ShapeHierarchy), ("packets", _ShapePackets)]
            : [("auto", _ShapeAuto), ("fields", _ShapeFields), ("all", _ShapeAll), ("hierarchy", _ShapeHierarchy)];
        string formatDefault = packetRows
            ? "packets"
            : "hierarchy";
        options.RegisterEnumSetting(ShapeOption, "Shape", "", _ShapeAuto, EnumSettingMetadata.FromPairs(shapes),
            description: "What is exported per packet. auto: the listed fields when the option 'fields' is set, otherwise " + formatDefault
                + "; fields: only the listed fields; all: every value field; hierarchy: every field with its place in the field tree. "
                + "Only 'hierarchy' needs a field tree; 'fields' and 'all' parse only what they write, which is much faster.");
        options.RegisterStringSetting(FieldsOption, "Fields", "", "",
            description: "Fields to export, separated by ';' (for example ip.src;udp.dstport). Append ':all', ':first', or ':last' to a "
                + "name to choose which occurrence is written when the field occurs more than once in a packet (for example "
                + "ip.src:last). Empty exports the shape's default.");
        options.RegisterEnumSetting(OccurrenceOption, "Default occurrence", "", 0,
            EnumSettingMetadata.FromPairs([("all", 0), ("first", 1), ("last", 2)]),
            description: "Which occurrence of a field is written when a field is listed without ':all', ':first', or ':last'. A tunnel "
                + "packet holds ip.src twice: all writes both, first the outer, last the inner.");
        if ((members & ContentMembers.Info) != 0)
        {
            options.RegisterBoolSetting(InfoOption, "Packet info", "", defaults.Info, description: "Write the packet summary line.");
        }

        if ((members & ContentMembers.FrameBytes) != 0)
        {
            // Off by default in every packet format: the field tree already holds the parsed bytes, so the frame copy is additional
            // and only written on request. Sync: ExportContent.FrameBytes (Exporters) and the README option tables.
            options.RegisterBoolSetting(FramesOption, "Frame bytes", "", defaults.FrameBytes,
                description: "Write the raw frame bytes of every packet (off by default; the field tree already holds the parsed bytes).");
        }

        if ((members & ContentMembers.Buffers) != 0)
        {
            // Off by default and only in the packet formats: reassembled and decoded bytes are packet content that the field tree does
            // not hold as one block. Security: buffers can hold decoded payloads that the frame does not show in clear (an inflated
            // body, an unmasked WebSocket payload) and can be far larger than the frames, so the description says so.
            // Sync: ExportContent.Buffers (Exporters) and the README option tables.
            options.RegisterBoolSetting(BuffersOption, "Additional buffers", "", defaults.Buffers,
                description: "Write the additional packet buffers (reassembled datagrams, TCP PDUs, decoded HTTP bodies, unmasked "
                    + "WebSocket payloads) of every packet, off by default. Can hold decoded content that the frame does not show in "
                    + "clear and can be much larger than the frames.");
        }

        options.RegisterBoolSetting(TextOption, "Custom text", "", defaults.CustomText,
            description: "Write the display text of each field. Off saves the work of building the texts.");
        options.RegisterBoolSetting(ReprOption, "Custom representation", "", defaults.CustomRepresentation,
            description: "Write the custom representation of each value. Off saves the work of building the texts.");
    }

    /// <summary>Registers writeMode, batchPackets, and batchBytes.</summary>
    /// <param name="options">The registrar of the format.</param>
    /// <remarks>
    /// Limits (§4.2), shown to the end user in the descriptions: <c>batchPackets</c> 1 to 2 147 483 647, default 50 000;
    /// <c>batchBytes</c> 1 to <see cref="long.MaxValue"/> bytes, default 16 MiB. Threat: memory exhaustion — a batch stays in
    /// memory until it is written, and with a writer thread two batches exist. Configurable by the CLI user and the library
    /// caller. Posture: secure by default (small batches fit any host; a user with memory to spare raises them for fewer
    /// transactions, row groups, or blocks). The settings validate the range, and the message of a refused value names it.
    /// </remarks>
    internal static void RegisterBatching(SettingsRegistrar options)
    {
        RegisterWriteMode(options);
        options.RegisterI64Setting(BatchPacketsOption, "Packets per batch", "", _DefaultPacketsPerBatch, min: 1, max: int.MaxValue,
            description: "How many packets are collected before they are written (1 to 2147483647, default 50000). A batch stays in "
                + "memory until it is written, and with background writing two batches exist, so this limits memory. Raise it on hosts "
                + "with memory to spare for fewer, larger writes; lower it when memory is tight.");
        options.RegisterI64Setting(BatchBytesOption, "Bytes per batch", "", _DefaultBytesPerBatch, min: 1,
            description: "Estimated size in bytes at which a batch is written (1 to 9223372036854775807, default 16777216 = 16 MiB). "
                + "The estimate is below the real memory use, and with background writing two batches exist, so this limits memory. Raise "
                + "it on hosts with memory to spare for fewer, larger writes; lower it when memory is tight.");
    }

    /// <summary>Registers only the write mode (the BLF exporter has no batch size, its container size is its own option).</summary>
    /// <param name="options">The registrar of the format.</param>
    internal static void RegisterWriteMode(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(WriteModeOption, "Write mode", "", 0,
            EnumSettingMetadata.FromPairs([("background", 0), ("sync", 1)]),
            description: "background: full batches are written by a writer thread while the next one fills (two batches in memory); "
                + "sync: each batch is written by the call that completed it (one batch in memory, no extra thread). With a stream "
                + "target the stream must not be used by anyone else until the export has finished.");
    }

    #endregion

    #region Reading

    /// <summary>
    /// Reads the content options. Returns <see langword="false"/> with a <see cref="PluginError"/> of code
    /// <see cref="PluginErrorCode.InvalidOption"/> that names the option (and the field for an unknown field).
    /// </summary>
    /// <param name="values">The option values of the request.</param>
    /// <param name="stack">The stack that parses the packets; field names are resolved against it.</param>
    /// <param name="defaults">The default content of the format; CSV passes its content template, since it writes packet rows by default.</param>
    /// <param name="members">The members the format can write.</param>
    /// <param name="content">The content; <see langword="null"/> for the CSV <c>packets</c> shape (today's packet rows).</param>
    /// <param name="error">The reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the options are valid.</returns>
    internal static bool TryReadContent(
        ReadOnlySettingsManagerView values,
        IStack stack,
        ExportContent defaults,
        ContentMembers members,
        out ExportContent? content,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(defaults);
        content = null;
        error = null;

        // 1. The field list: "name" or "name:choice" entries.
        Occurrences defaultOccurrence = _ParseOccurrence(OptionValues.GetEnumName(values, OccurrenceOption));
        if (!_TryParseFields(OptionValues.GetString(values, FieldsOption), defaultOccurrence, out List<FieldRequest>? requests, out error))
        {
            return false;
        }

        // 2. The shape: auto picks the listed fields, else the format default; an explicit shape must agree with the list.
        string shapeName = OptionValues.GetEnumName(values, ShapeOption);
        bool hasFields = requests.Count > 0;
        if (shapeName == "auto")
        {
            shapeName = hasFields
                ? "fields"
                : _FormatDefaultShape(defaults, members);
        }

        if (hasFields && shapeName != "fields")
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"Option 'fields' needs shape=fields (or auto), not '{shapeName}'."), FieldsOption);
            return false;
        }

        if (shapeName == "fields" && !hasFields)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                "Option 'shape=fields' needs at least one field in option 'fields'.", ShapeOption);
            return false;
        }

        if (shapeName == "packets")
        {
            return true; // content stays null: today's packet rows
        }

        // 3. The selection, resolved against the stack of the export (an unknown name is reported before a file is created).
        FieldSelection? selection = null;
        if (shapeName == "fields"
            && !_TryCreateSelection(stack, requests, defaultOccurrence, out selection, out error))
        {
            return false;
        }

        // 4. The content: the defaults with the shape, the selection, and the switches the format registered.
        ExportShape shape = shapeName switch
        {
            "fields" => ExportShape.SelectedFields,
            "all" => ExportShape.AllFields,
            _ => ExportShape.Hierarchy,
        };
        content = defaults with
        {
            Shape = shape,
            Fields = selection,
            Info = _ReadSwitch(values, InfoOption, members, ContentMembers.Info, defaults.Info),
            FrameBytes = _ReadSwitch(values, FramesOption, members, ContentMembers.FrameBytes, defaults.FrameBytes),
            Buffers = _ReadSwitch(values, BuffersOption, members, ContentMembers.Buffers, defaults.Buffers),
            CustomText = OptionValues.GetBool(values, TextOption),
            CustomRepresentation = OptionValues.GetBool(values, ReprOption),
        };
        return true;
    }

    /// <summary>Reads the batching options (already range-checked by the settings).</summary>
    /// <param name="values">The option values of the request.</param>
    /// <returns>The write mode and the two batch limits.</returns>
    internal static BatchingOptions ReadBatching(ReadOnlySettingsManagerView values)
    {
        ExportWriteMode mode = ReadWriteMode(values);

        // The registered range (1 to int.MaxValue) makes the narrowing cast safe.
        int packets = (int)OptionValues.GetI64(values, BatchPacketsOption);
        long bytes = OptionValues.GetI64(values, BatchBytesOption);
        return new BatchingOptions(mode, packets, bytes);
    }

    /// <summary>Reads the write mode.</summary>
    /// <param name="values">The option values of the request.</param>
    /// <returns>The mode.</returns>
    internal static ExportWriteMode ReadWriteMode(ReadOnlySettingsManagerView values) =>
        OptionValues.GetEnumName(values, WriteModeOption) == "sync"
            ? ExportWriteMode.Synchronous
            : ExportWriteMode.Background;

    #endregion

    #region Private helpers

    /// <summary>The shape name of <c>auto</c> without fields: the format default.</summary>
    private static string _FormatDefaultShape(ExportContent defaults, ContentMembers members)
    {
        if ((members & ContentMembers.PacketRows) != 0)
        {
            return "packets";
        }

        return defaults.Shape switch
        {
            ExportShape.SelectedFields => "fields",
            ExportShape.AllFields => "all",
            _ => "hierarchy",
        };
    }

    /// <summary>A switch the format registered, or the default for one it did not.</summary>
    private static bool _ReadSwitch(ReadOnlySettingsManagerView values, string name, ContentMembers members, ContentMembers member, bool fallback)
    {
        if ((members & member) == 0)
        {
            return fallback;
        }

        return OptionValues.GetBool(values, name);
    }

    /// <summary>
    /// Maps the text of the <c>occurrence</c> option to <see cref="Occurrences"/>; any text other than <c>first</c> or <c>last</c> is
    /// <see cref="Occurrences.All"/>.
    /// </summary>
    /// <param name="name">The option text; the setting restricts it to <c>all</c>, <c>first</c>, and <c>last</c>.</param>
    /// <returns>The occurrence choice.</returns>
    private static Occurrences _ParseOccurrence(string name) =>
        name switch
        {
            "first" => Occurrences.First,
            "last" => Occurrences.Last,
            _ => Occurrences.All,
        };

    /// <summary>
    /// Parses the field list: <c>name[:choice]</c> entries joined by <c>;</c>. Each failure names the option and, for a bad
    /// choice, the field and the allowed values.
    /// </summary>
    private static bool _TryParseFields(
        string text, Occurrences defaultOccurrence, [NotNullWhen(true)] out List<FieldRequest>? requests, [NotNullWhen(false)] out PluginError? error)
    {
        requests = [];
        error = null;
        foreach (string raw in text.Split(_FieldSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = raw.LastIndexOf(':');
            if (colon < 0)
            {
                requests.Add(new FieldRequest(raw, defaultOccurrence));
                continue;
            }

            string name = raw[..colon].Trim();
            string choice = raw[(colon + 1)..].Trim();
            Occurrences? occurrence = choice.ToLowerInvariant() switch
            {
                "all" => Occurrences.All,
                "first" => Occurrences.First,
                "last" => Occurrences.Last,
                _ => null,
            };
            if (name.Length == 0 || occurrence is null)
            {
                error = new PluginError(PluginErrorCode.InvalidOption,
                    Invariant($"Unknown occurrence '{choice}' for field '{name}' in option 'fields'; use all, first, or last."), FieldsOption);
                requests = null;
                return false;
            }

            requests.Add(new FieldRequest(name, occurrence));
        }

        return true;
    }

    /// <summary>Resolves the requests against the stack; the message of a failure names the option and the field.</summary>
    private static bool _TryCreateSelection(
        IStack stack,
        List<FieldRequest> requests,
        Occurrences defaultOccurrence,
        [NotNullWhen(true)] out FieldSelection? selection,
        [NotNullWhen(false)] out PluginError? error)
    {
        FieldSelection? created;
        if (FieldSelection.TryCreate(stack, requests.ToArray(), defaultOccurrence, out created, out FieldSelectionError problem))
        {
            selection = created;
            error = null;
            return true;
        }

        selection = null;

        string message = problem.Code switch
        {
            FieldSelectionErrorCode.UnknownField => Invariant($"Unknown field '{problem.FieldName}' in option 'fields'."),
            FieldSelectionErrorCode.NoValue =>
                Invariant($"Field '{problem.FieldName}' has no value (a protocol or group); name one of its value fields in option 'fields'."),
            FieldSelectionErrorCode.Duplicate => Invariant($"Field '{problem.FieldName}' is listed twice in option 'fields'."),
            _ => "Option 'fields' lists no field.",
        };
        error = new PluginError(PluginErrorCode.InvalidOption, message, FieldsOption);
        return false;
    }

    #endregion
}

/// <summary>Batch writing settings of one export request.</summary>
/// <param name="WriteMode">Whether full batches are written on a writer thread or by the call that completed them.</param>
/// <param name="MaxPacketsPerBatch">Packets per batch, 1 to <see cref="int.MaxValue"/>.</param>
/// <param name="MaxBatchBytes">Estimated bytes per batch, 1 to <see cref="long.MaxValue"/>.</param>
internal readonly record struct BatchingOptions(ExportWriteMode WriteMode, int MaxPacketsPerBatch, long MaxBatchBytes);
