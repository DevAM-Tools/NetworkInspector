// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Frame-level format conversion command.
/// Reads frames from one or more sources and writes them to a target format
/// (PCAPNG, BLF, or ASC) without protocol parsing.
/// </summary>
internal static class ConvertCommand
{
    #region Public API

    /// <summary>Runs the convert command.</summary>
    internal static int Run(string[] args) =>
        CliArgumentParsing.RunWithArgumentGuard(() => _RunCore(args));

    /// <summary>
    /// Reads frames round-robin from <paramref name="sources"/> and hands the accepted ones to
    /// exporters created on demand by <paramref name="createExporter"/>. Returns the number of
    /// frames written and reports how many outputs were finalized in <paramref name="filesWritten"/>.
    /// </summary>
    /// <remarks>
    /// A frame is judged before an output is opened, so a filter that matches nothing leaves no
    /// empty file behind. When <paramref name="filter"/> is <see langword="null"/> the loop stays
    /// frame-level and never parses; <paramref name="stack"/> is then unused and may be
    /// <see langword="null"/>. A filter that cannot produce a verdict aborts the loop rather than
    /// writing an output whose contents silently depend on where filtering stopped.
    /// </remarks>
    internal static int RunConvertLoop(
        List<IFrameSource> sources,
        Func<string, IFrameListener> createExporter,
        SplitOutputManager splitManager,
        Stack? stack,
        IFilter? filter,
        int maxFrames,
        int progressInterval,
        bool tolerant,
        CancellationToken cancellationToken,
        out int filesWritten)
    {
        int totalFrames = 0;
        int fileFrames = 0;
        int filterPacketId = 0;
        filesWritten = 0;
        IFrameListener? exporter = null;
        IExportByteProgress? byteProgress = null;
        PacketIndex? filterIndex = filter is not null && stack is not null ? new PacketIndex(stack) : null;

        RoundRobinSourceIterator iterator = new(sources);

        try
        {
            while (iterator.HasActive && (maxFrames == 0 || totalFrames < maxFrames))
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

                // Judged before an output is opened, so a filter that matches nothing leaves no
                // empty file behind.
                if (filter is not null && stack is not null && filterIndex is not null)
                {
                    ArrayIndexIdRange.ThrowIfInvalidNextIndex(filterPacketId, "packet");
                    Packet packet = Packet.ParseFrameIndexed(
                        new PacketId(filterPacketId++),
                        stack,
                        frame.Value,
                        filterIndex);
                    if (!CliFilter.TryMatch(filter, packet, filterIndex, out bool matched))
                    {
                        throw new InvalidOperationException(
                            "Filter evaluation failed; the conversion was aborted to avoid writing a partially filtered output.");
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
                        && splitManager.NeedsSplit(estimatedBytes, fileFrames)))
                {
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        filesWritten++;
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

                    fileFrames = 0;
                }

                exporter.OnFrame(frame.Value);
                totalFrames++;
                fileFrames++;

                if (progressInterval > 0 && totalFrames % progressInterval == 0)
                {
                    Console.Error.WriteLine($"Progress: {totalFrames} frames written");
                }

                if (maxFrames > 0 && totalFrames >= maxFrames)
                {
                    break;
                }

                iterator.Advance();
            }

            if (exporter is not null)
            {
                exporter.OnFinish();
                filesWritten++;
            }

            return totalFrames;
        }
        catch (OperationCanceledException)
        {
            // Finalize what has been produced so far so the partial output stays readable.
            try
            {
                exporter?.OnFinish();
            }
            catch (Exception finalizeEx)
            {
                Console.Error.WriteLine(
                    $"Warning: finalize failed during cancellation: {finalizeEx.Message}");
            }

            throw;
        }
        finally
        {
            (exporter as IDisposable)?.Dispose();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Parses arguments and executes conversion; throws <see cref="ArgumentException"/> on bad args.</summary>
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
        string? outputFormatSpec = null;
        string? filterExpression = null;
        string? profileName = null;
        string? settingsPath = null;
        int maxFrames = 0;
        long splitSize = 0;          // MiB; converted to bytes before use
        int splitCount = 0;          // frames
        int progressInterval = 0;
        long blfCacheSize = 0;        // MiB; for BLF sources
        bool tolerant = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "-O" or "--OUTPUT":
                    outputPath = CliArgumentParsing.GetNextArg(args, ref i, "--output");
                    break;
                case "--OUTPUT-FORMAT" or "--FORMAT" or "-F":
                    outputFormatSpec = CliArgumentParsing.GetNextArg(args, ref i, "--output-format");
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
                case "-N" or "--MAX-FRAMES":
                    maxFrames = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--max-frames"),
                        "--max-frames");
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
            outputFormatSpec,
            filterExpression,
            profileName,
            settingsPath,
            maxFrames,
            splitSizeBytes,
            splitCount,
            progressInterval,
            blfCacheBudgetBytes,
            tolerant);
    }

    /// <summary>Executes the conversion pipeline.</summary>
    private static int _Execute(
        List<string> sourceSpecs,
        string outputPath,
        string? outputFormatSpec,
        string? filterExpression,
        string? profileName,
        string? settingsPath,
        int maxFrames,
        long splitSizeBytes,  // bytes; 0 = no limit
        int splitCount,      // frames
        int progressInterval,
        int blfCacheBudgetBytes,    // bytes; 0 = default
        bool tolerant)
    {
        ConvertOutputConfig outputConfig;
        try
        {
            outputConfig = !string.IsNullOrEmpty(outputFormatSpec)
                ? ConvertOutputConfig.Parse(outputFormatSpec)
                : ConvertOutputConfig.FromExtension(Path.GetExtension(outputPath));
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

        // Conversion is frame-level and needs no protocol stack. A filter changes that: every
        // frame has to be parsed before it can be judged, so the stack is built only in that case
        // and then takes ownership of the settings manager.
        Stack? stack = null;
        IFilter? filter = null;
        if (CliFilter.IsActive(filterExpression))
        {
            try
            {
                StackBuilder stackBuilder = new(settingsManager, registry);
                stackBuilder.RegisterStandardProtocols();
                stack = stackBuilder.Build();
                settingsManager = null; // ownership transferred to the stack
            }
            finally
            {
                settingsManager?.Dispose();
            }

            if (!CliFilter.TryCompile(filterExpression, stack, out filter))
            {
                stack.Dispose();
                return (int)ExitCode.ArgumentError;
            }
        }

        ReadOnlySettingsManagerView settings = stack?.Settings ?? settingsManager!.ReadOnly;

        List<IFrameSource> sources = [];
        try
        {
            foreach (SourceConfig config in configs)
            {
                config.ValidateBeforeStart();
                IFrameSource source = config.CreateSource(settings);
                FrameSourceId sourceId = registry.RegisterSource(source);
                source.Start(sourceId, registry);
                sources.Add(source);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error opening source: {ex.Message}");
            CliSourceLifetime.DisposeSources(sources);
            settingsManager?.Dispose();
            stack?.Dispose();
            return (int)ExitCode.SourceOpenError;
        }

        SplitOutputManager splitManager = new(outputPath, splitSizeBytes, splitCount);

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
            int totalFrames = RunConvertLoop(
                sources,
                path => outputConfig.CreateExporter(path, settings),
                splitManager,
                stack,
                filter,
                maxFrames,
                progressInterval,
                tolerant,
                cts.Token,
                out int filesWritten);

            Console.Error.WriteLine(
                $"Conversion complete: {totalFrames} frames written to {filesWritten} file(s).");

            return (int)ExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Conversion cancelled.");
            return (int)ExitCode.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during conversion: {ex.Message}");
            return (int)ExitCode.RuntimeError;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            CliSourceLifetime.DisposeSources(sources);
            settingsManager?.Dispose();
            stack?.Dispose();
        }
    }

    /// <summary>Prints usage information for the convert command.</summary>
    private static void _PrintUsage()
    {
        Console.Error.WriteLine("Usage: ni convert <sources...> -o <output> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Convert capture files between formats (frame-level, no protocol parsing).");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Arguments:");
        Console.Error.WriteLine("  <sources...>          One or more source files or specs");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -o, --output          Output file path (required)");
        Console.Error.WriteLine("  --output-format, -f   Output format spec (see below; overrides extension)");
        Console.Error.WriteLine("  -n, --max-frames      Maximum number of frames to convert");
        Console.Error.WriteLine("  --split-size <MB>     Split when exporter EstimatedOutputBytes reaches this size (MiB; live, no FS probe)");
        Console.Error.WriteLine("  --split-count <N>     Split output every N frames");
        CliFilter.PrintUsageLines();
        Console.Error.WriteLine("                        (frames are parsed only when a filter is set)");
        Console.Error.WriteLine("  --profile <name>      Settings profile (available to sources/exporters)");
        Console.Error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        Console.Error.WriteLine("  --blf-cache-size <MB> Container cache budget for BLF sources (MiB)");
        Console.Error.WriteLine("  --progress <N>        Report progress every N frames");
        Console.Error.WriteLine("  --tolerant            Skip malformed frames instead of aborting");
        Console.Error.WriteLine("  -h, --help            Show this help message");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Output format is auto-detected from the output extension:");
        Console.Error.WriteLine("  .pcapng / other       PCAPNG (default)");
        Console.Error.WriteLine("  .blf                  BLF with default compression");
        Console.Error.WriteLine("  .asc                  CANalyzer ASCII log (CAN, CAN FD, LIN, FlexRay)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Output format specifications (--output-format):");
        Console.Error.WriteLine("  pcapng                PCAPNG format");
        Console.Error.WriteLine("  blf                   BLF, default compression");
        Console.Error.WriteLine("  blf:compression=off   BLF, no compression");
        Console.Error.WriteLine("  blf:compression=fast  BLF, fast compression");
        Console.Error.WriteLine("  blf:compression=best  BLF, best compression ratio");
        Console.Error.WriteLine("  asc                   CANalyzer ASCII log (CAN, CAN FD, LIN, FlexRay)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Source specifications:");
        Console.Error.WriteLine("  capture.pcap[ng]      Auto-detected PCAP/PCAPNG");
        Console.Error.WriteLine("  data.blf              Auto-detected BLF");
        Console.Error.WriteLine("  log.asc               Auto-detected CANalyzer ASC");
        Console.Error.WriteLine("  pcap:path=<file>      Explicit PCAP/PCAPNG spec");
        Console.Error.WriteLine("  blf:path=<file>       Explicit BLF spec");
        Console.Error.WriteLine("  asc:path=<file>       Explicit ASC spec");
        Console.Error.WriteLine("  random:count=N,seed=S,mode=udp4|udp6|random  Synthetic frames");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  ni convert capture.pcap -o output.pcapng");
        Console.Error.WriteLine("  ni convert input.blf -o output.pcapng --progress 1000");
        Console.Error.WriteLine("  ni convert input.pcap -o output.blf --output-format blf:compression=best");
        Console.Error.WriteLine("  ni convert big.pcapng -o split.pcapng --split-count 10000");
        Console.Error.WriteLine("  ni convert big.blf -o out.pcapng --blf-cache-size 256 --split-size 512");
        Console.Error.WriteLine("  ni convert input.pcap -o filtered.pcapng --filter \"udp.dstport == 53\"");
    }

    #endregion
}
