// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
        if (args.Length == 0 || IsHelpFlag(args[0]))
        {
            PrintUsage();
            return args.Length > 0 && IsHelpFlag(args[0]) ? 0 : 1;
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
                    outputPath = GetNextArg(args, ref i, "--output");
                    break;
                case "--OUTPUT-FORMAT" or "--FORMAT" or "-F":
                    outputFormatSpec = GetNextArg(args, ref i, "--output-format");
                    break;
                case "--PROFILE":
                    profileName = GetNextArg(args, ref i, "--profile");
                    break;
                case "--SETTINGS-PATH":
                    settingsPath = GetNextArg(args, ref i, "--settings-path");
                    break;
                case "-N" or "--MAX-FRAMES":
                    maxFrames = ParseLong(GetNextArg(args, ref i, "--max-frames"));
                    break;
                case "--SPLIT-SIZE":
                    splitSize = ParseLong(GetNextArg(args, ref i, "--split-size"));
                    break;
                case "--SPLIT-COUNT":
                    splitCount = ParseLong(GetNextArg(args, ref i, "--split-count"));
                    break;
                case "--PROGRESS":
                    progressInterval = ParseLong(GetNextArg(args, ref i, "--progress"));
                    break;
                case "--TOLERANT":
                    tolerant = true;
                    break;
                case "--BLF-CACHE-SIZE":
                    blfCacheSize = ParseLong(GetNextArg(args, ref i, "--blf-cache-size"));
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

        return Execute(
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

    /// <summary>Executes the conversion pipeline.</summary>
    private static int Execute(
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
                IFrameSource source = config.CreateSource();
                FrameSourceId sourceId = registry.RegisterSource(source);
                source.Start(sourceId, registry);
                sources.Add(source);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error opening source: {ex.Message}");
            DisposeSources(sources);
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
            // Round-robin across sources: track which sources are still active
            bool[] activeSources = new bool[sources.Count];
            Array.Fill(activeSources, true);
            int activeSourceCount = sources.Count;
            int currentSource = 0;

            while (activeSourceCount > 0 && (maxFrames == 0 || totalFrames < maxFrames))
            {
                cts.Token.ThrowIfCancellationRequested();
                // Advance to the next active source
                if (!activeSources[currentSource])
                {
                    currentSource = (currentSource + 1) % sources.Count;
                    continue;
                }

                IFrameSource source = sources[currentSource];
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
                        currentSource = (currentSource + 1) % sources.Count;
                        continue;
                    }

                    throw;
                }

                if (frame is null)
                {
                    // This source is exhausted
                    activeSources[currentSource] = false;
                    activeSourceCount--;
                    currentSource = (currentSource + 1) % sources.Count;
                    continue;
                }

                // Check if we need to open or rotate the output file
                if (exporter is null || (splitManager.IsSplitting &&
                    splitManager.NeedsSplit(GetFileSize(currentPath), fileFrames)))
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
            DisposeSources(sources);
        }
    }

    /// <summary>Gets the size of a file, returning 0 if not found or for stdout.</summary>
    private static long GetFileSize(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "-")
        {
            return 0;
        }

        FileInfo fi = new(path);
        return fi.Exists ? fi.Length : 0;
    }

    /// <summary>Disposes all sources safely, ignoring individual cleanup errors.</summary>
    private static void DisposeSources(List<IFrameSource> sources)
    {
        foreach (IFrameSource source in sources)
        {
            try
            {
                source.Dispose();
            }
            catch
            {
                // Best-effort disposal
            }
        }
    }

    /// <summary>Checks whether a string is a help flag.</summary>
    private static bool IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "--HELP" or "-H";

    /// <summary>Gets the next argument value, throwing if missing.</summary>
    private static string GetNextArg(string[] args, ref int index, string name)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        return args[index];
    }

    /// <summary>Parses a long value, throwing a user-friendly message on failure.</summary>
    private static long ParseLong(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < 0)
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
    }

    /// <summary>Prints usage information for the convert command.</summary>
    private static void PrintUsage()
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
        Console.Error.WriteLine();
        Console.Error.WriteLine("Output format specifications (--output-format):");
        Console.Error.WriteLine("  pcapng                PCAPNG format");
        Console.Error.WriteLine("  blf                   BLF, default compression");
        Console.Error.WriteLine("  blf:compression=off   BLF, no compression");
        Console.Error.WriteLine("  blf:compression=fast  BLF, fast compression");
        Console.Error.WriteLine("  blf:compression=best  BLF, best compression ratio");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Source specifications:");
        Console.Error.WriteLine("  capture.pcap[ng]      Auto-detected PCAP/PCAPNG");
        Console.Error.WriteLine("  data.blf              Auto-detected BLF");
        Console.Error.WriteLine("  pcap:path=<file>      Explicit PCAP/PCAPNG spec");
        Console.Error.WriteLine("  blf:path=<file>       Explicit BLF spec");
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
