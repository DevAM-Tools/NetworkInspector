// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#pragma warning disable CA1303 // Console output in developer-tool does not require localization

namespace NetworkInspector.Profiling;

/// <summary>
/// Entry point for the NetworkInspector profiling tool.
///
/// <para><b>Usage:</b></para>
/// <code>
///   profiling.exe                         — interactive menu to select a scenario
///   profiling.exe [filter]                — run scenarios whose name contains [filter]
///   profiling.exe [filter] --manual       — require Enter before the timed phase
///   profiling.exe [filter] --auto         — start the timed phase automatically after warm-up
///   profiling.exe --list                  — list all available scenarios and exit
/// </code>
///
/// <para><b>Profiling with dotnet-trace:</b></para>
/// <code>
///   profiling.exe parse-random-frames     — start scenario, auto-begin after warm-up
///   dotnet-trace collect --process-id PID — attach trace in another terminal
///   profiling.exe parse-random-frames --manual
///   Press Enter in the profiling terminal — start the timed phase manually
/// </code>
///
/// <para><b>Profiling with Visual Studio:</b></para>
/// Set as startup project → Debug → Performance Profiler.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Elevate process priority to High and pin the current thread to a single
        // core so OS scheduling noise does not skew throughput measurements.
        ElevateProcessPriority();

        string? filter = null;
        bool manualStart = false;
        bool listOnly = false;

        // Simple argument parsing: first non-flag argument is the scenario filter.
        foreach (string arg in args)
        {
            if (arg.Equals("--manual", StringComparison.OrdinalIgnoreCase))
            {
                manualStart = true;
            }
            else if (arg.Equals("--auto", StringComparison.OrdinalIgnoreCase))
            {
                manualStart = false;
            }
            else if (arg.Equals("--list", StringComparison.OrdinalIgnoreCase))
            {
                listOnly = true;
            }
            else if (!arg.StartsWith('-'))
            {
                filter = arg;
            }
        }

        // ── Scenario registry ────────────────────────────────────────────────────
        // Each scenario is independent and can be run individually via the CLI
        // filter argument or the interactive menu.
        //
        // Disposable scenarios are declared individually so the using-declaration
        // pattern guarantees safe disposal even when scenarios are filtered out.
        using ExportPcapngScenario exportPcapng = new();
        using ExportBlfScenario exportBlf = new();
        using ExportJsonScenario exportJson = new();
        using ExportPbfScenario exportPbf = new();
        using ExportColumnarPbfScenario exportColumnarPbf = new();

        IProfilingScenario[] scenarios =
        [
            // ── Parsing ──────────────────────────────────────────────────────────
            new ParseRandomFramesScenario(materialize: false),        // parse-random-frames
            new ParseRandomFramesScenario(materialize: true),         // parse-random-frames-materialized

            // ── Parsing (recycled — zero Packet heap allocations) ─────────────────
            new ParseRandomFramesRecycledScenario(materialize: false), // parse-random-frames-recycled
            new ParseRandomFramesRecycledScenario(materialize: true),  // parse-random-frames-materialized-recycled

            // ── Exporters ────────────────────────────────────────────────────────
            exportPcapng,                                              // export-pcapng
            exportBlf,                                                 // export-blf
            exportJson,                                                // export-json
            exportPbf,                                                 // export-pbf
            exportColumnarPbf,                                         // export-columnar-pbf

            // ── Frame generation ─────────────────────────────────────────────────
            new GenerateFramesScenario(),                              // generate-frames

            // ── Direct source parse (no session overhead) ─────────────────────────
            new RandomSourceParseScenario(),                           // random-source-parse

            // ── FrameBuilder (Eth/IPv4/IPv6/UDP/TCP, fragmentation, interceptors) ──
            new FrameBuilderSingleFrameScenario(),                     // framebuilder-single-frame
            new FrameBuilderFragmentedScenario(),                      // framebuilder-fragmented
            new FrameBuilderTcpIPv6SessionScenario(),                  // framebuilder-tcp-ipv6-session
            new FrameBuilderCustomInterceptorScenario(),               // framebuilder-custom-interceptor
            new FrameBuilderValueReuseScenario(),                      // framebuilder-value-reuse

            // ── File reading ─────────────────────────────────────────────────────
            new ReadPcapngScenario(),                                  // read-pcapng
            new ReadBlfScenario(),                                     // read-blf
        ];

        // ── --list: print all scenarios and exit ─────────────────────────────────
        if (listOnly)
        {
            PrintScenarioList(scenarios);
            return 0;
        }

        // ── No filter: interactive numbered menu ─────────────────────────────────
        if (filter is null)
        {
            return RunInteractiveMenu(scenarios, manualStart);
        }

        // ── Filter mode: run matching scenarios (for dotnet-trace or scripted use)
        return RunFilteredScenarios(scenarios, filter, manualStart);
    }

    /// <summary>Prints a numbered list of all available scenarios.</summary>
    private static void PrintScenarioList(IProfilingScenario[] scenarios)
    {
        Console.WriteLine("Available profiling scenarios:");
        Console.WriteLine();

        for (int i = 0; i < scenarios.Length; i++)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"  [{i + 1,2}] {scenarios[i].Name,-25}");
            Console.ResetColor();
            Console.WriteLine($" — {scenarios[i].Description}");
        }
    }

    /// <summary>
    /// Presents an interactive numbered menu, lets the user pick a scenario,
    /// and runs it with a profiler-attach pause.
    /// </summary>
    private static int RunInteractiveMenu(IProfilingScenario[] scenarios, bool manualStart)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║         NetworkInspector Profiling Tool              ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");
            Console.WriteLine();

            PrintScenarioList(scenarios);

            Console.WriteLine();
            Console.WriteLine("  [ 0] Exit");
            Console.WriteLine();
            Console.Write("Select a scenario (number): ");

            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input == "0")
            {
                return 0;
            }

            if (!int.TryParse(input, out int selection) || selection < 1 || selection > scenarios.Length)
            {
                Console.Error.WriteLine($"Invalid selection '{input}'. Enter a number between 1 and {scenarios.Length}.");
                continue;
            }

            RunScenario(scenarios[selection - 1], manualStart);

            Console.WriteLine();
            Console.Write("Press Enter to return to the menu...");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Runs all scenarios whose name matches the given filter string.
    /// When the filter exactly matches a scenario name, only that scenario runs.
    /// Otherwise the filter is used as a substring match.
    /// </summary>
    private static int RunFilteredScenarios(IProfilingScenario[] scenarios, string? filter, bool manualStart)
    {
        bool anyRan = false;

        // Check for exact match first — this prevents "parse-random-frames" from
        // also matching "parse-random-frames-materialized".
        bool hasExactMatch = false;
        if (filter is not null)
        {
            foreach (IProfilingScenario s in scenarios)
            {
                if (s.Name.Equals(filter, StringComparison.OrdinalIgnoreCase))
                {
                    hasExactMatch = true;
                    break;
                }
            }
        }

        foreach (IProfilingScenario scenario in scenarios)
        {
            if (filter is not null)
            {
                bool matches = hasExactMatch
                    ? scenario.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)
                    : scenario.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    continue;
                }
            }

            RunScenario(scenario, manualStart);
            anyRan = true;
        }

        if (!anyRan)
        {
            Console.Error.WriteLine($"No scenario matched filter '{filter}'.");
            Console.Error.WriteLine();
            PrintScenarioList(scenarios);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Runs a single scenario: setup → warm-up → [optional pause] → timed run → cleanup.
    /// Both warm-up and timed phases are duration-based: the runner calls
    /// <see cref="IProfilingScenario.Run"/> in a tight loop until the configured
    /// time has elapsed. This ensures every scenario runs for a predictable
    /// amount of time regardless of how fast a single iteration is.
    /// </summary>
    private static void RunScenario(IProfilingScenario scenario, bool manualStart)
    {
        Console.WriteLine();
        WriteColored($"=== Scenario: {scenario.Name} ===", ConsoleColor.Cyan);
        Console.WriteLine($"    {scenario.Description}");
        Console.WriteLine();

        Console.Write("Setting up...");
        scenario.Setup();
        Console.WriteLine(" done.");

        // Flush the heap before warm-up so GC state is deterministic and clean.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Warm-up phase: call Run() for the configured warm-up duration so the
        // JIT has compiled all hot paths before the timed phase begins.
        long warmupIterations = RunForDuration(scenario, scenario.WarmupDuration);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Warm-up: {warmupIterations} iterations in {scenario.WarmupDuration.TotalSeconds:F1} s.");
        Console.ResetColor();

        // Flush the heap again so GC activity does not skew the timed measurements.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        PauseBeforeTimedPhase(manualStart);

        // Capture GC baseline before the timed phase
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        // Timed profiling phase: call Run() until the configured duration has elapsed.
        Console.WriteLine($"Running for {scenario.Duration.TotalSeconds:F1} s...");
        Stopwatch sw = Stopwatch.StartNew();
        long timedIterations = RunForDuration(scenario, scenario.Duration);
        sw.Stop();

        // Capture GC counters after the timed phase
        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        double iterationsPerSecond = timedIterations / sw.Elapsed.TotalSeconds;
        WriteColored(
            $"Completed {timedIterations} iterations in {sw.Elapsed.TotalSeconds:F3} s " +
            $"({iterationsPerSecond:F1} iter/s).",
            ConsoleColor.Green);

        // Print throughput metric when the scenario provides work-unit information.
        if (scenario.WorkUnitsPerIteration > 0)
        {
            double totalWorkUnits = (double)timedIterations * scenario.WorkUnitsPerIteration;
            double unitsPerSecond = totalWorkUnits / sw.Elapsed.TotalSeconds;
            string throughput = FormatRate(unitsPerSecond);
            WriteColored(
                $"Throughput: {throughput} {scenario.WorkUnitName}/s " +
                $"({totalWorkUnits:N0} {scenario.WorkUnitName} total).",
                ConsoleColor.Yellow);
        }

        // Print GC statistics for the timed phase
        long allocDelta = allocAfter - allocBefore;
        double allocPerIter = (double)allocDelta / timedIterations;
        double allocPerSec = allocDelta / sw.Elapsed.TotalSeconds;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"  GC Allocations: {FormatBytes(allocDelta)} total, {FormatBytes((long)allocPerIter)}/iter, {FormatRate(allocPerSec)}B/s");
        Console.WriteLine($"  GC Collections: Gen0={gen0After - gen0Before}, Gen1={gen1After - gen1Before}, Gen2={gen2After - gen2Before}");
        Console.WriteLine($"  GC Heap: {gcInfo.HeapSizeBytes / 1024.0 / 1024:F1} MB, Pause: {gcInfo.PauseTimePercentage:F1}%");
        if (scenario.WorkUnitsPerIteration > 0)
        {
            double totalWorkUnits = (double)timedIterations * scenario.WorkUnitsPerIteration;
            double allocPerWorkUnit = allocDelta / totalWorkUnits;
            Console.WriteLine($"  Alloc/packet: {allocPerWorkUnit:F0} bytes");
        }
        Console.ResetColor();

        Console.WriteLine();

        scenario.Cleanup();
    }

    /// <summary>
    /// Waits between warm-up and the timed phase so a profiler can attach cleanly.
    /// Manual mode keeps the existing Enter gate. The default mode waits briefly
    /// after the post-warm-up GC cycle and then starts automatically.
    /// </summary>
    private static void PauseBeforeTimedPhase(bool manualStart)
    {
        TimeSpan startDelay = TimeSpan.FromSeconds(0.5);

        Console.WriteLine();

        if (manualStart)
        {
            Console.WriteLine("Attach your profiler now, then press Enter to start the timed phase.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine($"Starting timed phase automatically in {startDelay.TotalSeconds:F1} s...");
        Thread.Sleep(startDelay);
    }

    /// <summary>Writes a line to the console in the specified color, then resets to the default color.</summary>
    private static void WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    /// <summary>
    /// Formats a rate value with an appropriate SI prefix (k, M, G) for readability.
    /// For example, 1_234_000 becomes "1,234.0 k" and 56_000 becomes "56.0 k".
    /// </summary>
    private static string FormatRate(double rate) => rate switch
    {
        >= 1_000_000_000 => $"{rate / 1_000_000_000:F2} G",
        >= 1_000_000 => $"{rate / 1_000_000:F2} M",
        >= 1_000 => $"{rate / 1_000:F1} k",
        _ => $"{rate:F1} ",
    };

    /// <summary>Formats a byte count with appropriate SI prefix.</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// Elevates the current process to <see cref="ProcessPriorityClass.High"/>
    /// and sets the processor affinity to a single core. This reduces OS scheduling
    /// jitter and yields more stable, reproducible throughput measurements.
    /// Failures are logged but do not abort the profiling run (e.g. when running
    /// without administrator privileges).
    /// </summary>
    private static void ElevateProcessPriority()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();

            process.PriorityClass = ProcessPriorityClass.High;

            // Pin to a single physical core (core 0) via affinity mask.
            // This prevents the OS from migrating the thread between cores,
            // which can cause L1/L2 cache evictions and TSC skew.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                process.ProcessorAffinity = (nint)0b11; // core 0, both hyperthreads
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Console.WriteLine($"Process priority: {process.PriorityClass}, Affinity: 0x{process.ProcessorAffinity:X}");
            }
            else
            {
                Console.WriteLine($"Process priority: {process.PriorityClass}");
            }
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"Could not elevate process priority: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Calls <see cref="IProfilingScenario.Run"/> in a tight loop until
    /// <paramref name="duration"/> has elapsed. Returns the number of
    /// completed iterations.
    /// </summary>
    private static long RunForDuration(IProfilingScenario scenario, TimeSpan duration)
    {
        long iterations = 0;
        Stopwatch timer = Stopwatch.StartNew();

        // The tight loop checks elapsed time after each iteration.
        // Stopwatch.Elapsed is cheap compared to the work inside Run(),
        // so overhead from the time check is negligible.
        while (timer.Elapsed < duration)
        {
            scenario.Run();
            iterations++;
        }

        return iterations;
    }
}

