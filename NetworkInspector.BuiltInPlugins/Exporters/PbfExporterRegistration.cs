// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>PBF</c> packet exporter (NetworkInspector's block format). The registration returns the Core
/// <see cref="PbfExporter"/> itself. Stateless; the factory may run concurrently.
/// </summary>
internal static class PbfExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "PBF";

    /// <summary>Option name: row or column layout.</summary>
    private const string _FormatOption = "format";

    /// <summary>Option name: LZ4 compression of the blocks.</summary>
    private const string _CompressedOption = "compressed";

    /// <summary>Option name: write the block index at the end of the file.</summary>
    private const string _TrailerOption = "trailerIndex";

    /// <summary>Option name: declares that the packets arrive in non-decreasing timestamp order (metadata only; nothing is sorted).</summary>
    private const string _SortedOption = "timestampSorted";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "PBF",
        Description: "Writes parsed packets in the PBF block format (row or column layout, optionally LZ4-compressed).",
        FileExtension: ".pbf",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the command line (standard layout, compressed).</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(_FormatOption, "Layout", "", 0,
            EnumSettingMetadata.FromPairs([("standard", 0), ("columnar", 1)]),
            description: "standard: row-oriented blocks; columnar: column-oriented blocks.");
        options.RegisterBoolSetting(_CompressedOption, "Compressed", "", true, description: "Compress the blocks with LZ4.");
        options.RegisterBoolSetting(_TrailerOption, "Trailer index", "", true,
            description: "Write the block index at the end of the file for random access.");
        options.RegisterBoolSetting(_SortedOption, "Packets arrive sorted", "", false,
            description: "Declares that the packets arrive in non-decreasing timestamp order. Metadata only: the exporter sorts nothing " +
                "and the written bytes do not change.");
    }

    /// <summary>Creates the Core PBF exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReadOnlySettingsManagerView values = request.Options;
        PbfExportFormat format = OptionValues.GetEnumName(values, _FormatOption) == "columnar"
            ? PbfExportFormat.Columnar
            : PbfExportFormat.Standard;
        bool compressed = OptionValues.GetBool(values, _CompressedOption);
        bool trailer = OptionValues.GetBool(values, _TrailerOption);

        // The flag is passed through as the Core builder defines it: a declaration that the packets arrive in timestamp
        // order; the current PBF writer emits the same bytes either way. It does not sort the packets.
        bool sorted = OptionValues.GetBool(values, _SortedOption);
        return ExporterBuild.TryBuild<IPacketListener>(
            () => PbfExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithFormat(format)
                .WithCompressed(compressed)
                .WithTrailerIndex(trailer)
                .WithTimestampSorted(sorted)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}
