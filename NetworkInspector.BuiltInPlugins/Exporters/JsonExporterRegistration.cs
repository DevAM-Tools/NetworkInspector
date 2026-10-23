// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>JSON</c> packet exporter. The registration returns the Core <see cref="JsonExporter"/> itself. Stateless; the
/// factory may run concurrently.
/// </summary>
internal static class JsonExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "JSON";

    /// <summary>Option name: layout of the output.</summary>
    private const string _StyleOption = "style";

    /// <summary>Option name: flush after every packet.</summary>
    private const string _FlushOption = "flushPerPacket";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "JSON",
        Description: "Writes parsed packets as JSON: one object per line, indented objects, or one array.",
        FileExtension: ".json",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the command line (compact lines).</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(_StyleOption, "Style", "", 0,
            EnumSettingMetadata.FromPairs([("compact", 0), ("pretty", 1), ("array", 2)]),
            description: "compact: one object per line; pretty: indented objects; array: one JSON array.");
        options.RegisterBoolSetting(_FlushOption, "Flush per packet", "", false,
            description: "Flush the file after every packet so that a reader can follow the export; slower.");
    }

    /// <summary>Creates the Core JSON exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReadOnlySettingsManagerView values = request.Options;
        JsonExportFormat format = OptionValues.GetEnumName(values, _StyleOption) switch
        {
            "pretty" => JsonExportFormat.Pretty,
            "array" => JsonExportFormat.Array,
            _ => JsonExportFormat.Compact,
        };

        bool flush = OptionValues.GetBool(values, _FlushOption);
        return ExporterBuild.TryBuild<IPacketListener>(
            () => JsonExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithFormat(format)
                .WithFlushPerPacket(flush)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}
