// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parse and export command.
/// Reads frames from sources, parses them into packets with the full protocol stack,
/// and exports to an analysis format (JSON, PBF, text, Parquet, or DuckDB).
/// </summary>
internal static class ExportCommand
{
    #region Public API

    /// <summary>Runs the export command.</summary>
    internal static int Run(string[] args) =>
        CliArgumentParsing.RunWithArgumentGuard(() => _RunCore(args));

    /// <summary>
    /// Drives the frame-read → parse → export loop across all active sources,
    /// rotating outputs when <paramref name="splitManager"/> requests a split.
    /// </summary>
    /// <param name="sources">Active, already-started frame sources in round-robin order.</param>
    /// <param name="createExporter">Factory that opens a new file/directory exporter for a path.</param>
    /// <param name="splitManager">Split policy (size and/or packet count); may disable splitting.</param>
    /// <param name="stack">Protocol stack that owns parsed packets.</param>
    /// <param name="filter">
    /// Optional packet filter. When set, packets it rejects are counted as read but never handed
    /// to the exporter; an evaluation failure aborts the loop with an
    /// <see cref="InvalidOperationException"/> because a filter that cannot decide must not
    /// silently keep or drop data.
    /// </param>
    /// <param name="maxPackets">Stop after this many packets (0 = unlimited).</param>
    /// <param name="progressInterval">Print progress every N packets to stderr (0 = silent).</param>
    /// <param name="tolerant">When <see langword="true"/>, log and skip frames that throw.</param>
    /// <param name="packetIdCounter">Monotonically increasing packet ID counter.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <param name="outputsWritten">Number of output files or dataset directories finalized.</param>
    /// <returns>Number of packets exported successfully.</returns>
    internal static int RunExportLoop(
        List<IFrameSource> sources,
        Func<string, IPacketListener> createExporter,
        SplitOutputManager splitManager,
        Stack stack,
        IFilter? filter,
        int maxPackets,
        int progressInterval,
        bool tolerant,
        ref int packetIdCounter,
        CancellationToken cancellationToken,
        out int outputsWritten)
    {
        int packetCount = 0;
        int filePackets = 0;
        outputsWritten = 0;
        Packet? recyclePacket = null;
        IPacketListener? exporter = null;
        IExportByteProgress? byteProgress = null;
        PacketIndex? filterIndex = filter is not null ? new PacketIndex(stack) : null;

        RoundRobinSourceIterator iterator = new(sources);

        try
        {
            while (iterator.HasActive
                && !cancellationToken.IsCancellationRequested
                && (maxPackets == 0 || packetCount < maxPackets))
            {
                IFrameSource source = iterator.Current;
                Frame? frame;
                try
                {
                    frame = source.NextFrame(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (tolerant)
                    {
                        Console.Error.WriteLine(
                            $"Warning: Skipping frame from {source.UiName}: {ex.Message}");
                        iterator.Advance();
                        continue;
                    }

                    throw;
                }

                if (frame is null)
                {
                    iterator.MarkCurrentExhaustedAndAdvance();
                    continue;
                }

                int idValue = ++packetIdCounter;
                ArrayIndexIdRange.ThrowIfInvalidNextIndex(idValue, "packet");

                PacketId pid = new(idValue);
                Packet packet;
                if (filterIndex is not null)
                {
                    if (recyclePacket is null
                        || Packet.TryParseFrameIndexed(recyclePacket, pid, stack, frame.Value, filterIndex) is not null)
                    {
                        packet = Packet.ParseFrameIndexed(pid, stack, frame.Value, filterIndex);
                    }
                    else
                    {
                        packet = recyclePacket;
                    }
                }
                else if (recyclePacket is null || Packet.TryParseFrame(recyclePacket, pid, stack, frame.Value) is not null)
                {
                    packet = Packet.ParseFrame(pid, stack, frame.Value);
                }
                else
                {
                    packet = recyclePacket;
                }

                recyclePacket = packet;

                // Judged before an output is opened, so a filter that matches nothing leaves no
                // empty file or dataset directory behind.
                if (filter is not null)
                {
                    if (!CliFilter.TryMatch(filter, packet, filterIndex, out bool matched))
                    {
                        throw new InvalidOperationException(
                            "Filter evaluation failed; the export was aborted to avoid writing a partially filtered output.");
                    }

                    if (!matched)
                    {
                        iterator.Advance();
                        continue;
                    }
                }

                long estimatedBytes = byteProgress?.EstimatedOutputBytes ?? 0;
                if (exporter is null
                    || (splitManager.IsSplitting
                        && splitManager.NeedsSplit(estimatedBytes, filePackets)))
                {
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        outputsWritten++;
                    }

                    string currentPath = splitManager.NextPath();
                    exporter = createExporter(currentPath);
                    byteProgress = exporter as IExportByteProgress;
                    if (splitManager.IsSizeSplitting && byteProgress is null)
                    {
                        throw new InvalidOperationException(
                            "Size-based splitting (--split-size) requires an exporter that reports " +
                            "IExportByteProgress.EstimatedOutputBytes.");
                    }

                    filePackets = 0;
                }

                bool continueExport = exporter.OnPacket(packet);
                packetCount++;
                filePackets++;

                if (progressInterval > 0 && packetCount % progressInterval == 0)
                {
                    Console.Error.WriteLine($"Progress: {packetCount} packets exported");
                }

                iterator.Advance();

                if (!continueExport)
                {
                    break;
                }

                if (maxPackets > 0 && packetCount >= maxPackets)
                {
                    break;
                }
            }

            if (exporter is not null)
            {
                exporter.OnFinish();
                (exporter as IDisposable)?.Dispose();
                outputsWritten++;
                exporter = null;
            }

            return packetCount;
        }
        finally
        {
            if (exporter is not null)
            {
                (exporter as IDisposable)?.Dispose();
            }
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Parses arguments and executes export; throws <see cref="ArgumentException"/> on bad args.</summary>
    private static int _RunCore(string[] args)
    {
        if (args.Length == 0 || CliArgumentParsing.IsHelpFlag(args[0]))
        {
            _PrintUsage();
            if (args.Length > 0 && CliArgumentParsing.IsHelpFlag(args[0]))
            {
                return (int)ExitCode.Success;
            }

            return (int)ExitCode.ArgumentError;
        }

        List<string> sourceSpecs = [];
        string? outputPath = null;
        string? formatSpec = null;
        string? filterExpression = null;
        string? profileName = null;
        string? settingsPath = null;
        int maxPackets = 0;
        long splitSize = 0;          // MiB
        int splitCount = 0;         // packets
        int progressInterval = 0;
        long blfCacheSize = 0;  // MiB
        bool tolerant = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "-O" or "--OUTPUT":
                    outputPath = CliArgumentParsing.GetNextArg(args, ref i, "--output");
                    break;
                case "-F" or "--FORMAT":
                    formatSpec = CliArgumentParsing.GetNextArg(args, ref i, "--format");
                    break;
                case "-N" or "--MAX-PACKETS":
                    maxPackets = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--max-packets"),
                        "--max-packets");
                    break;
                case "--SPLIT-SIZE":
                    splitSize = CliArgumentParsing.ParseNonNegativeLong(
                        CliArgumentParsing.GetNextArg(args, ref i, "--split-size"));
                    break;
                case "--SPLIT-COUNT":
                    splitCount = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--split-count"),
                        "--split-count");
                    break;
                case "--PROGRESS":
                    progressInterval = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--progress"),
                        "--progress");
                    break;
                case "--TOLERANT":
                    tolerant = true;
                    break;
                case "--FILTER":
                    filterExpression = CliArgumentParsing.GetNextArg(args, ref i, "--filter");
                    break;
                case "--PROFILE":
                    profileName = CliArgumentParsing.GetNextArg(args, ref i, "--profile");
                    break;
                case "--SETTINGS-PATH":
                    settingsPath = CliArgumentParsing.GetNextArg(args, ref i, "--settings-path");
                    break;
                case "--BLF-CACHE-SIZE":
                    blfCacheSize = CliArgumentParsing.ParseNonNegativeLong(
                        CliArgumentParsing.GetNextArg(args, ref i, "--blf-cache-size"));
                    break;
                default:
                    sourceSpecs.Add(args[i]);
                    break;
            }
        }

        if (sourceSpecs.Count == 0)
        {
            Console.Error.WriteLine("Error: No source files specified.");
            return (int)ExitCode.ArgumentError;
        }

        if (string.IsNullOrEmpty(outputPath) || outputPath == "-")
        {
            Console.Error.WriteLine(
                "Error: Output file path required (-o / --output). Stdout ('-') is not supported.");
            return (int)ExitCode.ArgumentError;
        }

        int blfCacheBudgetBytes = 0;
        if (blfCacheSize > 0)
        {
            blfCacheBudgetBytes = CliArgumentParsing.MiBToCacheBudgetBytes(
                blfCacheSize, "--blf-cache-size");
        }

        long splitSizeBytes = CliArgumentParsing.MiBToSplitSizeBytes(splitSize, "--split-size");

        return _Execute(
            sourceSpecs,
            outputPath,
            formatSpec,
            filterExpression,
            profileName,
            settingsPath,
            maxPackets,
            splitSizeBytes,
            splitCount,
            progressInterval,
            blfCacheBudgetBytes,
            tolerant);
    }

    /// <summary>Executes the export pipeline.</summary>
    private static int _Execute(
        List<string> sourceSpecs,
        string outputPath,
        string? formatSpec,
        string? filterExpression,
        string? profileName,
        string? settingsPath,
        int maxPackets,
        long splitSizeBytes,
        int splitCount,
        int progressInterval,
        int blfCacheBudgetBytes,   // bytes; 0 = default
        bool tolerant)
    {
        ExportFormatConfig formatConfig;
        try
        {
            formatConfig = !string.IsNullOrEmpty(formatSpec)
                ? ExportFormatConfig.Parse(formatSpec)
                : ExportFormatConfig.FromExtension(Path.GetExtension(outputPath));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return (int)ExitCode.ArgumentError;
        }

        List<SourceConfig> configs;
        try
        {
            configs = sourceSpecs.ConvertAll(spec =>
            {
                SourceConfig config = SourceConfig.Parse(spec);
                if (config is BlfSourceConfig blfConfig && blfCacheBudgetBytes > 0)
                {
                    return new BlfSourceConfig(blfConfig.Path)
                    {
                        CacheBudget = blfCacheBudgetBytes,
                    };
                }

                return config;
            });
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return (int)ExitCode.ArgumentError;
        }

        SettingsManager? settingsManager = null;
        Stack? stack = null;
        List<IFrameSource> sources = [];

        using CancellationTokenSource cts = new();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancellation requested...");
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            try
            {
                settingsManager = SettingsManagerFactory.Create(settingsPath, profileName);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return (int)ExitCode.ArgumentError;
            }

            FrameInterfaceRegistry registry = new();
            try
            {
                StackBuilder stackBuilder = new(settingsManager, registry);
                stackBuilder.RegisterStandardProtocols();
                stack = stackBuilder.Build();
                settingsManager = null; // ownership transferred to stack
            }
            finally
            {
                settingsManager?.Dispose();
            }

            IFilter? filter = null;
            if (CliFilter.IsActive(filterExpression) && !CliFilter.TryCompile(filterExpression, stack!, out filter))
            {
                return (int)ExitCode.ArgumentError;
            }

            try
            {
                foreach (SourceConfig config in configs)
                {
                    config.ValidateBeforeStart();
                    IFrameSource source = config.CreateSource(stack!.Settings);
                    FrameSourceId sourceId = registry.RegisterSource(source);
                    source.Start(sourceId, registry);
                    sources.Add(source);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error opening source: {ex.Message}");
                return (int)ExitCode.SourceOpenError;
            }

            try
            {
                SplitOutputManager splitManager = new(
                    outputPath,
                    splitSizeBytes,
                    splitCount,
                    formatConfig.IsDirectoryOutput);
                CancellationToken token = cts.Token;
                int packetIdCounter = 0;
                int packetCount = RunExportLoop(
                    sources,
                    path => formatConfig.CreateExporter(path, token),
                    splitManager,
                    stack!,
                    filter,
                    maxPackets,
                    progressInterval,
                    tolerant,
                    ref packetIdCounter,
                    token,
                    out int outputsWritten);

                string outputUnit = formatConfig.IsDirectoryOutput ? "dataset(s)" : "file(s)";
                Console.Error.WriteLine(
                    $"Export complete: {packetCount} packets written to {outputsWritten} {outputUnit}.");

                return (int)ExitCode.Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error during export: {ex.Message}");
                return (int)ExitCode.RuntimeError;
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            CliSourceLifetime.DisposeSources(sources);
            stack?.Dispose();
        }
    }

    /// <summary>Prints usage information for the export command.</summary>
    private static void _PrintUsage()
    {
        Console.Error.WriteLine("Usage: ni export <sources...> -o <output> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Parse and export packets to analysis formats.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Arguments:");
        Console.Error.WriteLine("  <sources...>          One or more source files or specs");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -o, --output          Output file path (required)");
        Console.Error.WriteLine("  -f, --format          Export format spec (see below; overrides extension)");
        Console.Error.WriteLine("  -n, --max-packets     Maximum number of packets to export");
        Console.Error.WriteLine("  --split-size <MB>     Split when exporter EstimatedOutputBytes reaches this size (MiB; live, no FS probe)");
        Console.Error.WriteLine("  --split-count <N>     Split output every N packets (files, or sibling Parquet directories)");
        CliFilter.PrintUsageLines();
        Console.Error.WriteLine("  --profile <name>      Settings profile name");
        Console.Error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        Console.Error.WriteLine("  --blf-cache-size <MB> Container cache budget for BLF sources (MiB)");
        Console.Error.WriteLine("  --progress <N>        Report progress every N packets");
        Console.Error.WriteLine("  --tolerant            Skip malformed frames instead of aborting");
        Console.Error.WriteLine("  -h, --help            Show this help message");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Source specifications:");
        Console.Error.WriteLine("  capture.pcap[ng]      Auto-detected PCAP/PCAPNG file");
        Console.Error.WriteLine("  data.blf              Auto-detected BLF file");
        Console.Error.WriteLine("  log.asc               Auto-detected CANalyzer ASC file");
        Console.Error.WriteLine("  pcap:path=<file>      Explicit PCAP/PCAPNG spec");
        Console.Error.WriteLine("  blf:path=<file>       Explicit BLF spec");
        Console.Error.WriteLine("  asc:path=<file>       Explicit ASC spec");
        Console.Error.WriteLine("  random:count=N,seed=S,mode=udp4|udp6|random  Synthetic frames");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Format specifications:");
        Console.Error.WriteLine("  json                  Compact JSON (default for .json)");
        Console.Error.WriteLine("  json:style=compact    Compact JSON");
        Console.Error.WriteLine("  json:style=pretty     Pretty-printed JSON");
        Console.Error.WriteLine("  json:style=array      JSON array format");
        Console.Error.WriteLine("  pbf                   Standard PBF");
        Console.Error.WriteLine("  pbf:format=standard   Standard (row-oriented) PBF");
        Console.Error.WriteLine("  pbf:format=columnar   Columnar PBF");
        Console.Error.WriteLine("  pbf:format=columnar,compressed  Columnar PBF with LZ4");
        Console.Error.WriteLine("  text                  Human-readable protocol tree (default for .txt)");
        Console.Error.WriteLine("  text:level=summary    Protocol containers only (no field values)");
        Console.Error.WriteLine("  text:level=standard   All fields except raw bytes (default)");
        Console.Error.WriteLine("  text:level=full       All fields including raw bytes");
        Console.Error.WriteLine("  text:truncate=N       Truncate values at N characters (0 = unlimited, default: 256)");
        Console.Error.WriteLine("  text:level=full,truncate=0  Full detail without any truncation");
        Console.Error.WriteLine("  parquet               Columnar Parquet directory (requires -o <dir>)");
        Console.Error.WriteLine("  duckdb                DuckDB database file (requires -o <file>.duckdb)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  ni export capture.pcap -o output.json -f json:style=pretty");
        Console.Error.WriteLine("  ni export input.pcap -o data.pbf -f pbf:format=columnar,compressed");
        Console.Error.WriteLine("  ni export input.pcap -o output.json -n 100 --progress 10");
        Console.Error.WriteLine("  ni export random:count=1000,mode=udp4 -o out.json -f json");
        Console.Error.WriteLine("  ni export capture.pcap -o out.txt -f text");
        Console.Error.WriteLine("  ni export capture.pcap -o split.json --split-count 10000");
        Console.Error.WriteLine("  ni export capture.pcap -o out_parquet -f parquet");
        Console.Error.WriteLine("  ni export capture.pcap -o out_parquet -f parquet --split-count 50000");
        Console.Error.WriteLine("  ni export capture.pcap -o out.duckdb -f duckdb");
        Console.Error.WriteLine("  ni export capture.pcap -o out.duckdb -f duckdb --split-count 50000");
        Console.Error.WriteLine("  ni export capture.pcap -o dns.json --filter \"udp.dstport == 53\"");
    }

    #endregion
}
