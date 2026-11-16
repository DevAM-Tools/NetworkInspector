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

    /// <summary>Option name: uncompressed bytes per log container.</summary>
    private const string _ContainerSizeOption = "containerSize";

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
        Creation: ExporterCreation.ForStreams<IFrameListener>(_TryCreate));

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(_CompressionOption, "Compression", "", 2,
            EnumSettingMetadata.FromPairs([("none", 0), ("fast", 1), ("default", 2), ("best", 3)]),
            description: "Compression of the log containers: none, fast, default (zlib level 6), or best (level 9).");
        ExportContentOptions.RegisterWriteMode(options);
        // Limit: container size 65 536 to 67 108 864 bytes, default 10 485 760 (10 MiB). Threat: memory exhaustion — a container is
        // held uncompressed until it is compressed, twice with the writer thread, and a compressed-output buffer of the same size is
        // rented (none without compression). Configurable by the CLI user and the library caller. Posture: secure by default (the
        // default bounds memory on any host; raising it compresses slightly better).
        options.RegisterI64Setting(_ContainerSizeOption, "Container size", "", 10L * 1024 * 1024, min: 64L * 1024, max: 64L * 1024 * 1024,
            description: "Uncompressed size in bytes at which a log container is compressed and written (65536 to 67108864, default "
                + "10485760 = 10 MiB). A container is held in memory until it is written, and with background writing two exist; unless "
                + "compression is none, a buffer of the same size holds the compressed output. So this limits memory. Larger containers "
                + "compress slightly better; lower it when memory is tight.");
        options.RegisterStringSetting(FileSourceOptions.TimeZoneOption, "Timestamp time zone", "", "UTC",
            description: "Time zone of the written absolute timestamps: UTC, Local, or a system time zone id.");
    }

    /// <summary>Creates the Core BLF exporter.</summary>
    private static bool _TryCreate(StreamExportRequest request, [NotNullWhen(true)] out IFrameListener? listener,
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
        return ExporterBuild.TryBuildOnTarget<IFrameListener>(
            () => BlfExporter.CreateBuilder()
                .ToTarget(request.Target)
                .WithCompressionLevel(level)
                .WithWriteMode(ExportContentOptions.ReadWriteMode(values))
                .WithMaxContainerSize(OptionValues.GetI64(values, _ContainerSizeOption))
                .WithTimestampTimeZone(zone)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            out listener, out error);
    }

    #endregion
}
