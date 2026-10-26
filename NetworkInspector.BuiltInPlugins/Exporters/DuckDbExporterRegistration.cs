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

    // Limit: worker threads, 0 to 1024. Threat: a mistyped huge value asking the engine for an absurd number of threads.
    // Constant, not configurable: it is not a host-derived protection, only a plausibility bound, and 0 (engine default)
    // stays the default, so the posture is open by default. The memory limit has no upper bound here: the value is
    // forwarded to the engine, which owns what it means; 0 leaves the engine default.
    /// <summary>Largest accepted thread count; far beyond any machine, it only stops typing mistakes.</summary>
    private const long _MaxThreads = 1024;

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

    /// <summary>Registers the options; 0 leaves the choice to the database engine.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
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
        return ExporterBuild.TryBuild<IPacketListener>(
            () =>
            {
                DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder()
                    .ToFile(request.TargetPath)
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
