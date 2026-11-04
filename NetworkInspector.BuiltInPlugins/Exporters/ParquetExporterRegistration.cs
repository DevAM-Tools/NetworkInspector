// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>Parquet</c> packet exporter. The output is a dataset directory, never a single file, so the registration has no
/// file extension. The registration returns the Core <see cref="ParquetExporter"/> itself. Stateless; the factory may run
/// concurrently.
/// </summary>
internal static class ParquetExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "Parquet";

    /// <summary>Option name: declares that the packets arrive in non-decreasing timestamp order (metadata only; nothing is sorted).</summary>
    private const string _SortedOption = "timestampSorted";

    /// <summary>Option name: the compression codec of every file of the dataset.</summary>
    private const string _CompressionOption = "compression";

    /// <summary>Option name: the compression level of every file of the dataset.</summary>
    private const string _CompressionLevelOption = "compressionLevel";

    /// <summary>The content members of the dataset: the packet info, the frame bytes, and the additional buffers.</summary>
    private const ExportContentOptions.ContentMembers _Members =
        ExportContentOptions.ContentMembers.Info | ExportContentOptions.ContentMembers.FrameBytes | ExportContentOptions.ContentMembers.Buffers;

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "Parquet",
        Description: "Writes parsed packets as an Apache Parquet dataset in a directory.",
        FileExtension: "",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    /// <remarks>
    /// The <c>compression</c> and <c>compressionLevel</c> enums list exactly the members of <see cref="ParquetCompression"/> and
    /// <see cref="ParquetCompressionLevel"/> under their setting texts, so the number of a pair is the enum value and the setting
    /// refuses every other text with the allowed values. Posture: open by default for current readers (zstd at the optimal level:
    /// small files); a reader that does not know a codec needs another one. Sync: <see cref="ParquetCompression"/>,
    /// <see cref="ParquetCompressionLevel"/>, the Parquet README § Compression table, the CLI README parquet row.
    /// </remarks>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        ExportContentOptions.RegisterContent(options, ParquetExporter.DefaultContent, _Members);
        ExportContentOptions.RegisterBatching(options);
        options.RegisterEnumSetting(_CompressionOption, "Compression", "", (ulong)ParquetExporter.DefaultCompression,
            EnumSettingMetadata.FromPairs(
            [
                ("none", (ulong)ParquetCompression.None),
                ("snappy", (ulong)ParquetCompression.Snappy),
                ("gzip", (ulong)ParquetCompression.Gzip),
                ("brotli", (ulong)ParquetCompression.Brotli),
                ("lz4", (ulong)ParquetCompression.Lz4),
                ("zstd", (ulong)ParquetCompression.Zstd),
            ]),
            description: "Compression codec of every file of the dataset. zstd (default) gives small files and is read by current "
                + "tools; snappy is fast and supported by the common tools; gzip is smaller than snappy but slower; brotli is small and slow, "
                + "and not every tool reads it; lz4 is written as LZ4_RAW (fast, light compression like snappy; older tools may not "
                + "read it); none writes no compression (largest, fastest).");
        options.RegisterEnumSetting(_CompressionLevelOption, "Compression level", "", (ulong)ParquetExporter.DefaultCompressionLevel,
            EnumSettingMetadata.FromPairs(
            [
                ("fastest", (ulong)ParquetCompressionLevel.Fastest),
                ("optimal", (ulong)ParquetCompressionLevel.Optimal),
                ("smallest", (ulong)ParquetCompressionLevel.Smallest),
            ]),
            description: "How hard the codec works: fastest writes quickest and gives the largest files, optimal (default) balances "
                + "both, smallest gives the smallest files and writes slowest. Codecs without levels (none, snappy) ignore it.");
        options.RegisterBoolSetting(_SortedOption, "Packets arrive sorted", "", false,
            description: "Declares that the packets arrive in non-decreasing timestamp order. Metadata only: the exporter sorts nothing " +
                "and writes the packets in the order they arrive.");
    }

    /// <summary>Creates the Core Parquet exporter for a directory target.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The flag is passed through as the Core builder defines it: a declaration that the packets arrive in timestamp
        // order (metadata for the shared columnar batch). It does not sort the packets.
        bool sorted = OptionValues.GetBool(request.Options, _SortedOption);

        // The content names fields of the stack that parses the packets, so a typo is reported here, before a directory is created.
        if (!ExportContentOptions.TryReadContent(request.Options, request.Stack, ParquetExporter.DefaultContent, _Members,
                out ExportContent? content, out error))
        {
            listener = null;
            return false;
        }

        // The pairs of both settings carry the numbers of the library enums, and the settings refuse every other text before this
        // point (the allowed values are named in their message), so the casts cannot meet a stranger; the builder would refuse one
        // anyway. Invariant: the numbers are 0 to 5 and 0 to 2, so the checked casts cannot overflow; a pair that broke it would
        // throw here instead of choosing another codec silently.
        ParquetCompression compression = checked((ParquetCompression)OptionValues.GetEnumNumber(request.Options, _CompressionOption));
        ParquetCompressionLevel level = checked((ParquetCompressionLevel)OptionValues.GetEnumNumber(request.Options, _CompressionLevelOption));

        BatchingOptions batching = ExportContentOptions.ReadBatching(request.Options);
        return ExporterBuild.TryBuild<IPacketListener>(
            () => ParquetExporter.CreateBuilder()
                .ToDirectory(request.TargetPath)
                .WithContent(content!) // Invariant: only the CSV "packets" shape has no content.
                .WithWriteMode(batching.WriteMode)
                .WithMaxPacketsPerBlock(batching.MaxPacketsPerBatch)
                .WithMaxBlockSize(batching.MaxBatchBytes)
                .WithTimestampSorted(sorted)
                .WithCompression(compression, level)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}
