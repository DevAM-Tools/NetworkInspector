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

            long packetCount = 0;
            int packetIdCounter = 0;

            // Round-robin across sources: track which sources are still active.
            // A source becomes inactive once it returns null (exhausted).
            // We keep cycling until all sources are exhausted.
            bool[] activeSources = new bool[sources.Count];
            Array.Fill(activeSources, true);
            int activeSourceCount = sources.Count;
            int currentSource = 0;

            while (activeSourceCount > 0
                && !cts.Token.IsCancellationRequested
                && (maxPackets == 0 || packetCount < maxPackets))
            {
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

                // Parse frame into packet
                PacketId pid = new(Interlocked.Increment(ref packetIdCounter));
                Packet packet = Packet.ParseFrame(pid, stack, frame.Value);

                // Export the packet
                if (!exporter.OnPacket(packet))
                {
                    // Exporter signalled stop
                    break;
                }

                packetCount++;

                // Progress reporting (to stderr so it never pollutes stdout)
                if (progressInterval > 0 && packetCount % progressInterval == 0)
                {
                    Console.Error.WriteLine($"Progress: {packetCount} packets exported");
                }

                currentSource = (currentSource + 1) % sources.Count;

                if (maxPackets > 0 && packetCount >= maxPackets)
                {
                    break;
                }
            }

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
    /// Constructs a <see cref="SettingsManager"/> using the provided settings path and profile.
    /// Delegates to <see cref="SettingsManagerFactory.Create"/> for consistent path resolution.
    /// </summary>
    private static SettingsManager BuildSettingsManager(string? settingsPath, string? profileName)
        => SettingsManagerFactory.Create(settingsPath, profileName);

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
        Console.Error.WriteLine("  pcap:path=<file>      Explicit PCAP/PCAPNG spec");
        Console.Error.WriteLine("  blf:path=<file>       Explicit BLF spec");
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
