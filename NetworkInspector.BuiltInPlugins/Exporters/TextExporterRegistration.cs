// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>Text</c> packet exporter: the human-readable protocol tree. The registration returns the Core
/// <see cref="TextExporter"/> itself. Stateless; the factory may run concurrently.
/// </summary>
internal static class TextExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "Text";

    /// <summary>Option name: detail level of the field tree.</summary>
    private const string _LevelOption = "level";

    /// <summary>Option name: maximum characters of string and byte values.</summary>
    private const string _TruncateOption = "truncate";

    /// <summary>
    /// The content members of the Text options: the frame bytes and the additional buffers (a hex dump of each after the packet). The
    /// text format shows the packet info as a field line, so it has no <c>info</c> option.
    /// </summary>
    private const ExportContentOptions.ContentMembers _Members =
        ExportContentOptions.ContentMembers.FrameBytes | ExportContentOptions.ContentMembers.Buffers;

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "Text",
        Description: "Writes each packet as a human-readable protocol tree.",
        FileExtension: ".txt",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the command line (standard detail, 256 characters).</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        ExportContentOptions.RegisterContent(options, TextExporter.DefaultContent, _Members);
        options.RegisterEnumSetting(_LevelOption, "Detail level", "", 1,
            EnumSettingMetadata.FromPairs([("summary", 0), ("standard", 1), ("full", 2)]),
            description: "summary: protocol containers only; standard: all fields except raw bytes; full: all fields including raw bytes.");
        // Limit: characters per string or byte value, 0 to int.MaxValue (the builder takes an int). Threat: with 0 (unlimited) one
        // very large value is formatted completely and the exporter's scratch buffer grows to its size. Secure by default:
        // 256 truncates; unlimited is opt-in and only advisable for trusted input.
        options.RegisterI64Setting(_TruncateOption, "Value length limit", "", 256, min: 0, max: int.MaxValue,
            description: "Maximum characters of a string or byte value; 0 writes values in full.");
    }

    /// <summary>Creates the Core text exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The bound of the setting makes the cast safe.
        ReadOnlySettingsManagerView values = request.Options;
        TextDetailLevel level = OptionValues.GetEnumName(values, _LevelOption) switch
        {
            "summary" => TextDetailLevel.Summary,
            "full" => TextDetailLevel.Full,
            _ => TextDetailLevel.Standard,
        };

        int truncate = (int)OptionValues.GetI64(values, _TruncateOption);

        // The content names fields of the stack that parses the packets, so a typo is reported here, before a file is created.
        if (!ExportContentOptions.TryReadContent(values, request.Stack, TextExporter.DefaultContent,
                _Members, out ExportContent? content, out error))
        {
            listener = null;
            return false;
        }

        return ExporterBuild.TryBuild<IPacketListener>(
            () => TextExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithContent(content!) // Invariant: only the CSV "packets" shape has no content.
                .WithDetailLevel(level)
                .WithMaxTextLength(truncate)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}
