// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>BLF</c> frame exporter. The registration returns the Core <see cref="BlfExporter"/> itself. Stateless; the
/// factory may run concurrently.
/// </summary>
internal static class BlfExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "BLF";

    /// <summary>Option name: compression of the log containers.</summary>
    private const string _CompressionOption = "compression";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddFrameExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IFrameListener> Create() => new(
        Format: Format,
        UiName: "Vector BLF",
        Description: "Writes frames as a Vector Binary Logging Format file.",
        FileExtension: ".blf",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(_CompressionOption, "Compression", "", 2,
            EnumSettingMetadata.FromPairs([("none", 0), ("fast", 1), ("default", 2), ("best", 3)]),
            description: "Compression of the log containers: none, fast, default (zlib level 6), or best (level 9).");
        options.RegisterStringSetting(FileSourceOptions.TimeZoneOption, "Timestamp time zone", "", "UTC",
            description: "Time zone of the written absolute timestamps: UTC, Local, or a system time zone id.");
    }

    /// <summary>Creates the Core BLF exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IFrameListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The zone is the one option that Core settings cannot check.
        listener = null;
        ReadOnlySettingsManagerView values = request.Options;
        if (!FileSourceOptions.TryGetTimeZone(values, out TimeZoneInfo? zone, out error))
        {
            return false;
        }

        // The setting's own numbers (0 to 3, default 2 = "default") are not the zlib levels of BlfCompressionLevel (0, 1, 6, 9),
        // so the enumeration is mapped by its registered name; an unlisted name cannot occur because Core settings reject it.
        BlfCompressionLevel level = OptionValues.GetEnumName(values, _CompressionOption) switch
        {
            "none" => BlfCompressionLevel.None,
            "fast" => BlfCompressionLevel.Fast,
            "best" => BlfCompressionLevel.Best,
            _ => BlfCompressionLevel.Default,
        };

        // The expected item count is progress information; it is never passed as a target count, so the exporter
        // cannot stop early on its own.
        return ExporterBuild.TryBuild<IFrameListener>(
            () => BlfExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithCompressionLevel(level)
                .WithTimestampTimeZone(zone)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}
