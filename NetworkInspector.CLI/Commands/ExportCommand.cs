// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parse and export command.
/// Reads frames from sources, parses them into packets with the full protocol stack,
/// and exports to an analysis format (JSON, PBF, or text).
/// </summary>
internal static class ExportCommand
{
    /// <summary>Runs the export command.</summary>
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
        string? formatSpec = null;
        string? profileName = null;
        string? settingsPath = null;
        long maxPackets = 0;
        long progressInterval = 0;
        long blfCacheSize = 0;  // MiB
        bool tolerant = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "-O" or "--OUTPUT":
                    outputPath = GetNextArg(args, ref i, "--output");
                    break;
                case "-F" or "--FORMAT":
                    formatSpec = GetNextArg(args, ref i, "--format");
                    break;
                case "-N" or "--MAX-PACKETS":
                    maxPackets = ParseLong(GetNextArg(args, ref i, "--max-packets"));
                    break;
                case "--PROGRESS":
                    progressInterval = ParseLong(GetNextArg(args, ref i, "--progress"));
                    break;
                case "--TOLERANT":
                    tolerant = true;
                    break;
                case "--PROFILE":
                    profileName = GetNextArg(args, ref i, "--profile");
                    break;
                case "--SETTINGS-PATH":
                    settingsPath = GetNextArg(args, ref i, "--settings-path");
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

        return Execute(
            sourceSpecs,
            outputPath,
            formatSpec,
            profileName,
            settingsPath,
            maxPackets,
            progressInterval,
            blfCacheSize,
            tolerant);
    }

    /// <summary>Executes the export pipeline.</summary>
    private static int Execute(
        List<string> sourceSpecs,
        string? outputPath,
        string? formatSpec,
        string? profileName,
        string? settingsPath,
        long maxPackets,
        long progressInterval,
        long blfCacheSize,   // MiB
        bool tolerant)
    {
        // Determine output target
        bool isStdout = string.IsNullOrEmpty(outputPath) || outputPath == "-";

        // Determine export format
        ExportFormatConfig formatConfig;
        try
        {
            if (!string.IsNullOrEmpty(formatSpec))
            {
                formatConfig = ExportFormatConfig.Parse(formatSpec);
            }
            else if (!isStdout)
            {
                formatConfig = ExportFormatConfig.FromExtension(
                    Path.GetExtension(outputPath!));
            }
            else
            {
                // Default to compact JSON for stdout
                formatConfig = ExportFormatConfig.Parse("json:style=compact");
            }
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
                // Apply --blf-cache-size to BLF sources
                if (config is BlfSourceConfig blfConfig && blfCacheSize > 0)
                {
                    return new BlfSourceConfig(blfConfig.Path)
                    {
                        CacheBudget = (int)Math.Min(blfCacheSize * 1024 * 1024, int.MaxValue) // bytes
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

        // Build settings manager — load profile if specified
        SettingsManager? settingsManager = BuildSettingsManager(settingsPath, profileName);

        // Create stack with full protocol parsing
        FrameInterfaceRegistry registry = new();
        Stack stack;
        try
        {
            StackBuilder stackBuilder = new(settingsManager, registry);
            stackBuilder.RegisterStandardProtocols();
            stack = stackBuilder.Build();
            settingsManager = null; // ownership transferred to stack; Stack.Dispose() calls SettingsManager.Dispose()
        }
        finally
        {
            settingsManager?.Dispose();
        }

        // Create cancellation and handle Ctrl+C gracefully
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancellation requested...");
        };

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
            DisposeSources(sources);
            return 2;
        }

        try
        {
            IPacketListener exporter = isStdout
                ? formatConfig.CreateStdoutExporter(cts.Token)
                : formatConfig.CreateFileExporter(outputPath!, cts.Token);

            // Safe IDisposable handling — exporter may or may not implement it
            using IDisposable? exporterDisposable = exporter as IDisposable;

            int packetIdCounter = 0;
            long packetCount = RunExportLoop(
                sources, exporter, stack, maxPackets, progressInterval, tolerant,
                ref packetIdCounter, cts.Token);

            exporter.OnFinish();

            if (!isStdout)
            {
                Console.Error.WriteLine($"Export complete: {packetCount} packets written.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            // Failures during export (writer, serializer, IO) abort the run
            // and surface as exit code 3 per README §Exit Codes.
            Console.Error.WriteLine($"Error during export: {ex.Message}");
            return 3;
        }
        finally
        {
            DisposeSources(sources);
        }
    }

    /// <summary>
    /// Drives the frame-read → parse → export loop across all active sources.
    /// </summary>
    /// <param name="sources">Active, already-started frame sources in round-robin order.</param>
    /// <param name="exporter">Target packet listener; <see cref="IPacketListener.OnFinish"/>
    /// is <b>not</b> called here — the caller is responsible.</param>
    /// <param name="stack">Protocol stack that owns parsed packets.</param>
    /// <param name="maxPackets">Stop after this many packets (0 = unlimited).</param>
    /// <param name="progressInterval">Print progress every N packets to stderr (0 = silent).</param>
    /// <param name="tolerant">When <see langword="true"/>, log and skip frames that throw.</param>
    /// <param name="packetIdCounter">Monotonically increasing packet ID counter; passed by reference
    /// so the caller can observe the final value if needed.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Number of packets exported successfully.</returns>
    internal static long RunExportLoop(
        List<IFrameSource> sources,
        IPacketListener exporter,
        Stack stack,
        long maxPackets,
        long progressInterval,
        bool tolerant,
        ref int packetIdCounter,
        CancellationToken cancellationToken)
    {
        long packetCount = 0;
        Packet? recyclePacket = null;

        // Round-robin across sources via RoundRobinSourceIterator.
        // Sources are removed from rotation as they are exhausted.
        RoundRobinSourceIterator iterator = new(sources);

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

            // Parse frame into packet — reuse the previous packet where possible to eliminate
            // per-frame heap allocation. All four RecycleError codes are structurally impossible
            // in this context (same stack, same registry, single thread, packet always sealed);
            // the non-null guard is a purely defensive fallback that keeps the loop alive.
            PacketId pid = new(Interlocked.Increment(ref packetIdCounter));
            Packet packet;
            if (recyclePacket is null || Packet.TryParseFrame(recyclePacket, pid, stack, frame.Value) is not null)
            {
                // First frame, or unexpected precondition failure: allocate fresh.
                packet = Packet.ParseFrame(pid, stack, frame.Value);
            }
            else
            {
                packet = recyclePacket;
            }

            // Export the packet; update recycle slot unconditionally since packet is sealed here.
            bool continueExport = exporter.OnPacket(packet);
            recyclePacket = packet;
            packetCount++;

            // Progress reporting (to stderr so it never pollutes stdout)
            if (progressInterval > 0 && packetCount % progressInterval == 0)
            {
                Console.Error.WriteLine($"Progress: {packetCount} packets exported");
            }

            iterator.Advance();

            if (!continueExport)
            {
                // Exporter signalled stop; packet was already counted
                break;
            }

            if (maxPackets > 0 && packetCount >= maxPackets)
            {
                break;
            }
        }

        return packetCount;
    }

    /// <summary>
    /// Constructs a <see cref="SettingsManager"/> using the provided settings path and profile.
    /// Delegates to <see cref="SettingsManagerFactory.Create"/> for consistent path resolution.
    /// </summary>
    private static SettingsManager BuildSettingsManager(string? settingsPath, string? profileName)
        => SettingsManagerFactory.Create(settingsPath, profileName);

    /// <summary>
    /// Disposes all sources, writing a warning to stderr for each failure.
    /// If every disposal fails the aggregate is re-thrown so callers are not
    /// silently left with unreleased resources.
    /// </summary>
    private static void DisposeSources(List<IFrameSource> sources)
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
    private static bool IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "--HELP" or "-H";

    /// <summary>Gets the next argument value, throwing if missing or null.</summary>
    private static string GetNextArg(string[] args, ref int index, string name)
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
    private static long ParseLong(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < 0)
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
    }

    /// <summary>Prints usage information for the export command.</summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ni export <sources...> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Parse and export packets to analysis formats.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Arguments:");
        Console.Error.WriteLine("  <sources...>          One or more source files or specs");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -o, --output          Output file path ('-' or omit for stdout)");
        Console.Error.WriteLine("  -f, --format          Export format spec (see below)");
        Console.Error.WriteLine("  -n, --max-packets     Maximum number of packets to export");
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
        Console.Error.WriteLine("  json                  Compact JSON (default for stdout / .json)");
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
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  ni export capture.pcap -o output.json -f json:style=pretty");
        Console.Error.WriteLine("  ni export input.pcap -o data.pbf -f pbf:format=columnar,compressed");
        Console.Error.WriteLine("  ni export input.pcap -o output.json -n 100 --progress 10");
        Console.Error.WriteLine("  ni export random:count=1000,mode=udp4 -f json");
        Console.Error.WriteLine("  ni export capture.pcap -f text");
        Console.Error.WriteLine("  ni export capture.pcap -f text:level=summary");
        Console.Error.WriteLine("  ni export capture.pcap -f text:level=full,truncate=0");
        Console.Error.WriteLine("  ni export capture.pcap -o out.txt");
    }
}
