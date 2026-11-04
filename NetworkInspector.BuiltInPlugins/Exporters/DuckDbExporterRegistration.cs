// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>DuckDB</c> packet exporter. The registration returns the Core <see cref="DuckDbExporter"/> itself. Stateless;
/// the factory may run concurrently.
/// </summary>
internal static class DuckDbExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "DuckDB";

    /// <summary>Option name: memory limit of the database engine.</summary>
    private const string _MemoryLimitOption = "memoryLimitBytes";

    /// <summary>Option name: worker threads of the database engine.</summary>
    private const string _ThreadsOption = "threads";

    /// <summary>Option name: the DuckDB storage version of the file.</summary>
    private const string _StorageVersionOption = "storageVersion";

    // Limit: worker threads, 0 to 1024. Threat: a mistyped huge value asking the engine for an absurd number of threads.
    // Constant, not configurable: it is not a host-derived protection, only a plausibility bound, and 0 (engine default)
    // stays the default, so the posture is open by default. The memory limit has no upper bound here: the value is
    // forwarded to the engine, which owns what it means; 0 leaves the engine default.
    /// <summary>Largest accepted thread count; far beyond any machine, it only stops typing mistakes.</summary>
    private const long _MaxThreads = 1024;

    /// <summary>The content members of the database tables: the packet info, the frame bytes, and the additional buffers.</summary>
    private const ExportContentOptions.ContentMembers _Members =
        ExportContentOptions.ContentMembers.Info | ExportContentOptions.ContentMembers.FrameBytes | ExportContentOptions.ContentMembers.Buffers;

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "DuckDB",
        Description: "Writes parsed packets into a DuckDB database file.",
        FileExtension: ".duckdb",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>
    /// Registers the options; 0 leaves the choice to the database engine.
    /// </summary>
    /// <remarks>
    /// The <c>storageVersion</c> enum lists exactly the six <see cref="DuckDbStorageVersion"/> members under their setting texts, in
    /// enum order, so the numeric value of a pair is the enum value and the setting refuses every other text with the allowed
    /// values. Posture: open by default (the newest format; the size effect depends on the data); an older DuckDB reader needs an older value. There is no
    /// compaction option on purpose: the export only appends, so a compaction would gain little and add a failure point at the end.
    /// Sync: <see cref="DuckDbStorageVersion"/>, the DuckDB README § Storage Version table, the CLI README duckdb row.
    /// </remarks>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        ExportContentOptions.RegisterContent(options, DuckDbExporter.DefaultContent, _Members);
        ExportContentOptions.RegisterBatching(options);
        options.RegisterEnumSetting(_StorageVersionOption, "Storage version", "", (ulong)DuckDbExporter.DefaultStorageVersion,
            EnumSettingMetadata.FromPairs(
            [
                ("v1.0.0", (ulong)DuckDbStorageVersion.V1Dot0),
                ("v1.1.0", (ulong)DuckDbStorageVersion.V1Dot1),
                ("v1.2.0", (ulong)DuckDbStorageVersion.V1Dot2),
                ("v1.3.0", (ulong)DuckDbStorageVersion.V1Dot3),
                ("v1.4.0", (ulong)DuckDbStorageVersion.V1Dot4),
                ("v1.5.0", (ulong)DuckDbStorageVersion.V1Dot5),
            ]),
            description: "DuckDB file format version. Older versions open in older DuckDB releases; newer versions allow newer "
                + "encodings (the effect on the file size depends on the data). Default v1.5.0 (DuckDB 1.5 or later).");
        options.RegisterI64Setting(_MemoryLimitOption, "Memory limit", "", 0, min: 0,
            description: "Memory limit of the database engine in bytes; 0 uses the engine default.");
        options.RegisterI64Setting(_ThreadsOption, "Threads", "", 0, min: 0, max: _MaxThreads,
            description: "Worker threads of the database engine; 0 uses the engine default.");
    }

    /// <summary>Creates the Core DuckDB exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReadOnlySettingsManagerView values = request.Options;
        long memoryLimit = OptionValues.GetI64(values, _MemoryLimitOption);

        // The setting bounds (0 to 1024) make the narrowing cast safe.
        int threads = (int)OptionValues.GetI64(values, _ThreadsOption);

        // The pairs of the setting carry the numbers of DuckDbStorageVersion, and the setting refuses every other text before this
        // point (the allowed values are named in its message), so the cast cannot meet a stranger; the builder would refuse one anyway.
        // Invariant: the numbers are 0 to 5, so the checked cast cannot overflow; a pair that broke it would throw here instead of
        // choosing another storage format silently.
        DuckDbStorageVersion storageVersion = checked((DuckDbStorageVersion)OptionValues.GetEnumNumber(values, _StorageVersionOption));

        // The content names fields of the stack that parses the packets, so a typo is reported here, before a file is created.
        if (!ExportContentOptions.TryReadContent(values, request.Stack, DuckDbExporter.DefaultContent, _Members,
                out ExportContent? content, out error))
        {
            listener = null;
            return false;
        }

        BatchingOptions batching = ExportContentOptions.ReadBatching(values);
        return ExporterBuild.TryBuild<IPacketListener>(
            () =>
            {
                DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder()
                    .ToFile(request.TargetPath)
                    .WithContent(content!) // Invariant: only the CSV "packets" shape has no content.
                    .WithWriteMode(batching.WriteMode)
                    .WithMaxPacketsPerBlock(batching.MaxPacketsPerBatch)
                    .WithMaxBlockSize(batching.MaxBatchBytes)
                    .WithStorageVersion(storageVersion)
                    .WithCancellationToken(request.CancellationToken);

                // Zero means "not set": the builder refuses zero, so the call is skipped and the engine decides.
                if (memoryLimit != 0)
                {
                    builder.WithMemoryLimit(memoryLimit);
                }

                if (threads != 0)
                {
                    builder.WithThreads(threads);
                }

                return builder.Build();
            },
            request.TargetPath, out listener, out error);
    }

    #endregion
}
