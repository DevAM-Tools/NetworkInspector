// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Frame-level format conversion command.
/// Reads frames from one or more sources and writes them to a target format
/// (PCAPNG or BLF) without protocol parsing.
/// </summary>
internal static class ConvertCommand
{
    /// <summary>Runs the convert command.</summary>
    internal static int Run(string[] args)
    {
        if (args.Length == 0 || _IsHelpFlag(args[0]))
        {
            _PrintUsage();
            if (args.Length > 0 && _IsHelpFlag(args[0]))
            {
                return 0;
            }

            return 1;
        }

        // Parse arguments
        List<string> sourceSpecs = [];
        string? outputPath = null;
        string? outputFormatSpec = null;
        string? profileName = null;
        string? settingsPath = null;
        long maxFrames = 0;
        long splitSize = 0;          // MiB; converted to bytes before use
        long splitCount = 0;          // frames
        long progressInterval = 0;
        long blfCacheSize = 0;        // MiB; for BLF sources
        bool tolerant = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "-O" or "--OUTPUT":
                    outputPath = _GetNextArg(args, ref i, "--output");
                    break;
                case "--OUTPUT-FORMAT" or "--FORMAT" or "-F":
                    outputFormatSpec = _GetNextArg(args, ref i, "--output-format");
                    break;
                case "--PROFILE":
                    profileName = _GetNextArg(args, ref i, "--profile");
                    break;
                case "--SETTINGS-PATH":
                    settingsPath = _GetNextArg(args, ref i, "--settings-path");
                    break;
                case "-N" or "--MAX-FRAMES":
                    maxFrames = _ParseLong(_GetNextArg(args, ref i, "--max-frames"));
                    break;
                case "--SPLIT-SIZE":
                    splitSize = _ParseLong(_GetNextArg(args, ref i, "--split-size"));
                    break;
                case "--SPLIT-COUNT":
                    splitCount = _ParseLong(_GetNextArg(args, ref i, "--split-count"));
                    break;
                case "--PROGRESS":
                    progressInterval = _ParseLong(_GetNextArg(args, ref i, "--progress"));
                    break;
                case "--TOLERANT":
                    tolerant = true;
                    break;
                case "--BLF-CACHE-SIZE":
                    blfCacheSize = _ParseLong(_GetNextArg(args, ref i, "--blf-cache-size"));
                    break;
                default:
                    sourceSpecs.Add(args[i]);
                    break;
            }
        }

        if (sourceSpecs.Count == 0)
        {
            Console.Error.WriteLine("Error: No source files specified.");
            return 1;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            Console.Error.WriteLine("Error: Output path required (-o / --output).");
            return 1;
        }

        return _Execute(
            sourceSpecs,
            outputPath,
            outputFormatSpec,
            profileName,
            settingsPath,
            maxFrames,
            splitSize,
            splitCount,
            progressInterval,
            blfCacheSize,
            tolerant);
    }

    /// <summary>_Executes the conversion pipeline.</summary>
    private static int _Execute(
        List<string> sourceSpecs,
        string outputPath,
        string? outputFormatSpec,
        string? profileName,
        string? settingsPath,
        long maxFrames,
        long splitSize,       // MiB
        long splitCount,      // frames
        long progressInterval,
        long blfCacheSize,    // MiB
        bool tolerant)
    {
        bool isStdout = outputPath == "-";
        string extension = isStdout ? ".pcapng" : Path.GetExtension(outputPath);

        // Determine output format config (explicit spec takes priority over extension)
        ConvertOutputConfig outputConfig;
        try
        {
            outputConfig = !string.IsNullOrEmpty(outputFormatSpec)
                ? ConvertOutputConfig.Parse(outputFormatSpec)
                : ConvertOutputConfig.FromExtension(extension);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        // Parse source configs, applying BLF cache budget where applicable
        List<SourceConfig> configs;
        try
        {
            configs = sourceSpecs.ConvertAll(spec =>
            {
                SourceConfig config = SourceConfig.Parse(spec);
                if (config is BlfSourceConfig blfConfig && blfCacheSize > 0)
                {
                    // Convert MiB to bytes; cap at int.MaxValue
                    return new BlfSourceConfig(blfConfig.Path)
                    {
                        CacheBudget = (int)Math.Min(blfCacheSize * 1024 * 1024, int.MaxValue)
                    };
                }

                return config;
            });
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        // Create frame interface registry and source pool
        FrameInterfaceRegistry registry = new();

        // Profile and settings are accepted for forward compatibility and script consistency
        // with the 'export' command; no stack is built during frame-level conversion unless
        // filter support is added in a future version.
        _ = profileName;
        _ = settingsPath;

        // Create and start sources — a failure here is a source open / parse failure (exit code 2).
        List<IFrameSource> sources = [];
        try
        {
            foreach (SourceConfig config in configs)
            {
                config.ValidateBeforeStart();
                IFrameSource source = config.CreateSource();
                FrameSourceId sourceId = registry.RegisterSource(source);
                source.Start(sourceId, registry);
                sources.Add(source);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error opening source: {ex.Message}");
            _DisposeSources(sources);
            return 2;
        }

        // Convert split size from MiB to bytes for the split manager.
        // 0 means no limit.
        long splitSizeBytes = splitSize > 0 ? splitSize * 1024 * 1024 : 0; // bytes

        // Set up file splitting
        SplitOutputManager splitManager = new(
            isStdout ? "stdout.pcapng" : outputPath,
            splitSizeBytes,
            splitCount);

        long totalFrames = 0;
        long fileFrames = 0;
        int filesWritten = 0;
        IFrameListener? exporter = null;
        string currentPath = "";

        // Cached FileInfo for split-size checks; avoids re-allocating on every frame.
        // Reset whenever the output path is rotated.
        FileInfo? splitSizeFileInfo = null;

        // Cooperative cancellation: Ctrl+C trips the token, which the loop checks each
        // iteration. The conversion currently in flight is finalised cleanly in the
        // OperationCanceledException catch block below.
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
            // Round-robin across sources via RoundRobinSourceIterator.
            // Sources are removed from rotation as they are exhausted.
            RoundRobinSourceIterator iterator = new(sources);

            while (iterator.HasActive && (maxFrames == 0 || totalFrames < maxFrames))
            {
                cts.Token.ThrowIfCancellationRequested();

                IFrameSource source = iterator.Current;
                Frame? frame;
                try
                {
                    frame = source.NextFrame();
                }
                catch (Exception ex)
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
                    // This source is exhausted
                    iterator.MarkCurrentExhaustedAndAdvance();
                    continue;
                }

                // Check if we need to open or rotate the output file
                if (exporter is null || (splitManager.IsSplitting && _NeedsSplit(splitSizeFileInfo, fileFrames, splitManager)))
                {
                    // Finalize the previous exporter before rotating
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        filesWritten++;
                    }

                    currentPath = isStdout ? "-" : splitManager.NextPath();
                    exporter = outputConfig.CreateExporter(currentPath, isStdout);
                    fileFrames = 0;

                    // Reset the cached FileInfo for the new output file.
                    // Only useful when splitting is active and not writing to stdout.
                    splitSizeFileInfo = (splitManager.IsSplitting && currentPath != "-")
                        ? new FileInfo(currentPath)
                        : null;
                }

                exporter.OnFrame(frame.Value);
                totalFrames++;
                fileFrames++;

                // Progress reporting
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

            // Finalize the last output file
            if (exporter is not null)
            {
                exporter.OnFinish();
                (exporter as IDisposable)?.Dispose();
                filesWritten++;
            }

            if (!isStdout)
            {
                Console.Error.WriteLine(
                    $"Conversion complete: {totalFrames} frames written to {filesWritten} file(s).");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation — finalize what we wrote so far is handled by the
            // surrounding logic; treat as a successful early exit.
            if (exporter is not null)
            {
                try
                {
                    exporter.OnFinish();
                }
                catch (Exception finalizeEx) { Console.Error.WriteLine($"Warning: finalize failed during cancellation: {finalizeEx.Message}"); }
                (exporter as IDisposable)?.Dispose();
            }
            Console.Error.WriteLine("Conversion cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            // Failures during the conversion loop (writer, IO, parse) abort the run
            // and surface as exit code 3 per README §Exit Codes.
            Console.Error.WriteLine($"Error during conversion: {ex.Message}");
            return 3;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            _DisposeSources(sources);
        }
    }

    /// <summary>
    /// Determines whether the current output file needs to be rotated.
    /// Refreshes the cached <see cref="FileInfo"/> to get the current on-disk size
    /// instead of allocating a new instance on every frame.
    /// </summary>
    private static bool _NeedsSplit(FileInfo? fileInfo, long fileFrames, SplitOutputManager splitManager)
    {
        long currentSize = 0;
        if (fileInfo is not null)
        {
            fileInfo.Refresh();
            currentSize = fileInfo.Exists ? fileInfo.Length : 0;
        }

        return splitManager.NeedsSplit(currentSize, fileFrames);
    }

    /// <summary>
    /// Disposes all sources, writing a warning to stderr for each failure.
    /// If every disposal fails the aggregate is re-thrown so callers are not
    /// silently left with unreleased resources.
    /// </summary>
    private static void _DisposeSources(List<IFrameSource> sources)
    {
        List<Exception>? errors = null;
        foreach (IFrameSource source in sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Warning: failed to dispose source '{source.GetType().Name}': {ex.Message}");
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null && errors.Count == sources.Count && sources.Count > 0)
        {
            throw new AggregateException("All source disposals failed.", errors);
        }
    }

    /// <summary>Checks whether a string is a help flag.</summary>
    private static bool _IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "--HELP" or "-H";

    /// <summary>Gets the next argument value, throwing if missing or null.</summary>
    private static string _GetNextArg(string[] args, ref int index, string name)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        string? value = args[index];
        if (value is null)
        {
            throw new ArgumentException($"Option '{name}' received a null argument (internal error).");
        }

        return value;
    }

    /// <summary>Parses a long value, throwing a user-friendly message on failure.</summary>
    private static long _ParseLong(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < 0)
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
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
        Console.Error.WriteLine("  -o, --output          Output file path (required; '-' for stdout)");
        Console.Error.WriteLine("  --output-format, -f   Output format spec (see below; overrides extension)");
        Console.Error.WriteLine("  -n, --max-frames      Maximum number of frames to convert");
        Console.Error.WriteLine("  --split-size <MB>     Split output at this size in MiB");
        Console.Error.WriteLine("  --split-count <N>     Split output every N frames");
        Console.Error.WriteLine("  --profile <name>      Settings profile name");
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
    }
}
