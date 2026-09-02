// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
///
/// <para><b>Prerequisites:</b></para>
/// <para>Run the profiling tool with elevated (administrator) privileges for accurate
/// measurements. Without elevation the process priority cannot be raised to
/// <c>High</c>. A console warning is emitted when elevation fails, but the
/// profiling run continues with potentially skewed results.</para>
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        // Elevate process priority to High so OS scheduling noise does not
        // starve the workload. Threads stay unpinned so ingest and listeners
        // can run on separate cores.
        _ElevateProcessPriority();

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
        // Scenarios with parameterless constructors are auto-discovered via reflection
        // (ScenarioDiscovery.Discover). Scenarios that require constructor arguments are
        // registered here manually and prepended to the discovered list.
        //
        // To add a new auto-discovered scenario: create a class that implements
        // IProfilingScenario with a parameterless (public or internal) constructor.
        // No change to Program.cs is required.
        IProfilingScenario[] manual =
        [
            // ── Parsing ──────────────────────────────────────────────────────────
            // These require a bool constructor argument and cannot be auto-discovered.
            new ParseRandomFramesScenario(materialize: false),        // parse-random-frames
            new ParseRandomFramesScenario(materialize: true),         // parse-random-frames-materialized

            // ── Parsing (recycled — zero Packet heap allocations) ─────────────────
            new ParseRandomFramesRecycledScenario(materialize: false), // parse-random-frames-recycled
            new ParseRandomFramesRecycledScenario(materialize: true),  // parse-random-frames-materialized-recycled

            // ── Ingest / Redissect ───────────────────────────────────────────────
            new ParseIngestUdpScenario(),
            new RedissectParallelUdpScenario(threadCount: 1),
            new RedissectParallelUdpScenario(threadCount: 2),
            new RedissectParallelUdpScenario(threadCount: 4),
            new RedissectParallelUdpScenario(threadCount: 8),
            new SessionConcurrentRedissectScenario(listenerCount: 1),
            new SessionConcurrentRedissectScenario(listenerCount: 2),
            new SessionConcurrentRedissectScenario(listenerCount: 4),
            new SessionConcurrentRedissectScenario(listenerCount: 8),
            new SessionListenerScenario(materialize: false),
            new SessionListenerScenario(materialize: true),
            new RandomSourceParseScenario(materialize: false),
            new RandomSourceParseScenario(materialize: true),
        ];

        IProfilingScenario[] discovered = ScenarioDiscovery.Discover();
        IProfilingScenario[] scenarios = _MergeScenarios(manual, discovered);

        try
        {
            // ── --list: print all scenarios and exit ─────────────────────────────────
            if (listOnly)
            {
                _PrintScenarioList(scenarios);
                return 0;
            }

            // ── No filter: interactive numbered menu ─────────────────────────────────
            if (filter is null)
            {
                return _RunInteractiveMenu(scenarios, manualStart);
            }

            // ── Filter mode: run matching scenarios (for dotnet-trace or scripted use)
            return _RunFilteredScenarios(scenarios, filter, manualStart);
        }
        finally
        {
            // Dispose all IDisposable scenarios regardless of which code path was taken.
            foreach (IProfilingScenario s in discovered)
            {
                if (s is IDisposable d)
                {
                    d.Dispose();
                }
            }
        }
    }

    /// <summary>Prints a numbered list of all available scenarios.</summary>
    private static void _PrintScenarioList(IProfilingScenario[] scenarios)
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
    private static int _RunInteractiveMenu(IProfilingScenario[] scenarios, bool manualStart)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║         NetworkInspector Profiling Tool              ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");
            Console.WriteLine();

            _PrintScenarioList(scenarios);

            Console.WriteLine();
            Console.WriteLine("  [ 0] Exit");
            Console.WriteLine();
            Console.Write("Select a scenario (number): ");

            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input == "0")
            {
                return 0;
            }

            if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selection)
                || selection < 1 || selection > scenarios.Length)
            {
                Console.Error.WriteLine(
                    FormattableString.Invariant(
                        $"Invalid selection '{input}'. Enter a number between 1 and {scenarios.Length}."));
                continue;
            }

            _RunScenario(scenarios[selection - 1], manualStart);

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
    private static int _RunFilteredScenarios(IProfilingScenario[] scenarios, string? filter, bool manualStart)
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

            _RunScenario(scenario, manualStart);
            anyRan = true;
        }

        if (!anyRan)
        {
            Console.Error.WriteLine($"No scenario matched filter '{filter}'.");
            Console.Error.WriteLine();
            _PrintScenarioList(scenarios);
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
    private static void _RunScenario(IProfilingScenario scenario, bool manualStart)
    {
        Console.WriteLine();
        _WriteColored($"=== Scenario: {scenario.Name} ===", ConsoleColor.Cyan);
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
        long warmupIterations = _RunForDuration(scenario, scenario.WarmupDuration);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            FormattableString.Invariant(
                $"Warm-up: {warmupIterations} iterations in {scenario.WarmupDuration.TotalSeconds:F1} s."));
        Console.ResetColor();

        // Flush the heap again so GC activity does not skew the timed measurements.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        scenario.BeginTimedPhase();

        _PauseBeforeTimedPhase(manualStart);

        // Capture GC baseline before the timed phase.
        // GC.GetTotalAllocatedBytes(precise: true) performs a full heap walk (~10–50 ms);
        // this overhead is amortized across the 7-second timed phase and does not skew
        // in-loop measurements because it is taken outside the hot loop.
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        // Timed profiling phase: call Run() until the configured duration has elapsed.
        Console.WriteLine(
            FormattableString.Invariant($"Running for {scenario.Duration.TotalSeconds:F1} s..."));
        Stopwatch sw = Stopwatch.StartNew();
        long timedIterations = _RunForDuration(scenario, scenario.Duration);
        sw.Stop();

        // Capture GC counters after the timed phase.
        // The precise: true heap walk overhead (same as above) is again outside the hot loop.
        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        double iterationsPerSecond = timedIterations / sw.Elapsed.TotalSeconds;
        _WriteColored(
            FormattableString.Invariant(
                $"Completed {timedIterations} iterations in {sw.Elapsed.TotalSeconds:F3} s ({iterationsPerSecond:F1} iter/s)."),
            ConsoleColor.Green);

        // Print throughput metric when the scenario provides work-unit information.
        long totalWorkUnits = 0;
        if (scenario.WorkUnitsPerIteration > 0)
        {
            totalWorkUnits = timedIterations * scenario.WorkUnitsPerIteration;
        }

        if (totalWorkUnits > 0)
        {
            double unitsPerSecond = totalWorkUnits / sw.Elapsed.TotalSeconds;
            string throughput = _FormatRate(unitsPerSecond);
            _WriteColored(
                FormattableString.Invariant(
                    $"Throughput: {throughput} {scenario.WorkUnitName}/s ({totalWorkUnits:N0} {scenario.WorkUnitName} total)."),
                ConsoleColor.Yellow);
        }

        // Print GC statistics for the timed phase
        long allocDelta = allocAfter - allocBefore;
        double allocPerIter = (double)allocDelta / timedIterations;
        double allocPerSec = allocDelta / sw.Elapsed.TotalSeconds;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Allocations: {_FormatBytes(allocDelta)} total, {_FormatBytes((long)allocPerIter)}/iter, {_FormatRate(allocPerSec)}B/s"));
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Collections: Gen0={gen0After - gen0Before}, Gen1={gen1After - gen1Before}, Gen2={gen2After - gen2Before}"));
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Heap: {gcInfo.HeapSizeBytes / 1024.0 / 1024:F1} MB, Pause: {gcInfo.PauseTimePercentage:F1}%"));
        if (totalWorkUnits > 0)
        {
            double allocPerWorkUnit = allocDelta / (double)totalWorkUnits;
            Console.WriteLine(
                FormattableString.Invariant($"  Alloc/packet: {allocPerWorkUnit:F0} bytes"));
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
    private static void _PauseBeforeTimedPhase(bool manualStart)
    {
        TimeSpan startDelay = TimeSpan.FromSeconds(0.5);

        Console.WriteLine();

        if (manualStart)
        {
            Console.WriteLine("Attach your profiler now, then press Enter to start the timed phase.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine(
            FormattableString.Invariant($"Starting timed phase automatically in {startDelay.TotalSeconds:F1} s..."));
        Thread.Sleep(startDelay);
    }

    /// <summary>Writes a line to the console in the specified color, then resets to the default color.</summary>
    private static void _WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    /// <summary>
    /// Formats a rate value with an appropriate SI prefix (k, M, G) for readability.
    /// For example, 1_234_000 becomes "1,234.0 k" and 56_000 becomes "56.0 k".
    /// </summary>
    private static string _FormatRate(double rate) => rate switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{rate / 1_000_000_000:F2} G"),
        >= 1_000_000 => FormattableString.Invariant($"{rate / 1_000_000:F2} M"),
        >= 1_000 => FormattableString.Invariant($"{rate / 1_000:F1} k"),
        _ => FormattableString.Invariant($"{rate:F1} "),
    };

    /// <summary>Formats a byte count with appropriate SI prefix.</summary>
    private static string _FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => FormattableString.Invariant($"{bytes / 1_073_741_824.0:F2} GB"),
        >= 1_048_576 => FormattableString.Invariant($"{bytes / 1_048_576.0:F1} MB"),
        >= 1_024 => FormattableString.Invariant($"{bytes / 1_024.0:F1} KB"),
        _ => FormattableString.Invariant($"{bytes} B"),
    };

    /// <summary>
    /// Elevates the current process to <see cref="ProcessPriorityClass.High"/>.
    /// Failures are logged but do not abort the profiling run (e.g. when running
    /// without administrator privileges).
    /// </summary>
    private static void _ElevateProcessPriority()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();

            process.PriorityClass = ProcessPriorityClass.High;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Process priority: {process.PriorityClass}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNING: could not elevate process priority: {ex.Message}");
            Console.WriteLine("Measurements may be skewed by OS scheduling jitter. Run elevated for best results.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Combines manual registrations with auto-discovered scenarios, keeping the first
    /// occurrence of each <see cref="IProfilingScenario.Name"/>.
    /// </summary>
    private static IProfilingScenario[] _MergeScenarios(
        IProfilingScenario[] manual,
        IProfilingScenario[] discovered)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        int count = 0;
        IProfilingScenario[] merged = new IProfilingScenario[manual.Length + discovered.Length];

        foreach (IProfilingScenario scenario in manual)
        {
            if (names.Add(scenario.Name))
            {
                merged[count] = scenario;
                count++;
            }
        }

        foreach (IProfilingScenario scenario in discovered)
        {
            if (names.Add(scenario.Name))
            {
                merged[count] = scenario;
                count++;
            }
        }

        return merged.AsSpan(0, count).ToArray();
    }

    /// <summary>
    /// Calls <see cref="IProfilingScenario.Run"/> in a tight loop until
    /// <paramref name="duration"/> has elapsed. Returns the number of
    /// completed iterations.
    /// </summary>
    private static long _RunForDuration(IProfilingScenario scenario, TimeSpan duration)
    {
        long iterations = 0;
        Stopwatch timer = Stopwatch.StartNew();

        // Cache the target tick count so the hot loop compares two longs instead of
        // allocating a TimeSpan struct via Stopwatch.Elapsed on every iteration.
        long endTicks = (long)(duration.TotalSeconds * Stopwatch.Frequency);

        while (timer.ElapsedTicks < endTicks)
        {
            if (scenario.IsWorkComplete)
            {
                break;
            }

            scenario.Run();
            iterations++;
        }

        return iterations;
    }
}

