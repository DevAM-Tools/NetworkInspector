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
/// <para><b>A/B measurement (interleaved child processes, CSV, report):</b></para>
/// <code>
///   profiling.exe --ab &lt;baselineExe&gt; --repeat &lt;n&gt; (--all | --set a,b,…) --out &lt;csv&gt;
///                 [--affinity 0x&lt;mask&gt;] [--warmup &lt;s&gt;] [--duration &lt;s&gt;]
///                                         — this executable is variant B, the baseline is variant A;
///                                           odd runs A then B, even runs B then A; one CSV row per run;
///                                           --affinity pins every child to the processor mask (Windows,
///                                           Linux); --warmup/--duration replace the phase lengths of
///                                           every child (both variants alike)
///   profiling.exe --report &lt;csv&gt; --out &lt;md&gt; [--noise &lt;aaCsv&gt;] [--set a,b,…] [--title &lt;text&gt;]
///                 [--min-improvement &lt;fraction&gt;] [--floor &lt;fraction&gt;]
///                                         — medians, Δ, noise limits, geomean, GC pause, retained memory,
///                                           gate verdict (exit 0 PASS, 3 FAIL, 1 invalid input);
///                                           defaults: --min-improvement 0.03, --floor 0.02
///   profiling.exe --run-once &lt;name&gt; --result &lt;csv&gt; --variant &lt;A|B&gt; --run &lt;n&gt;
///                                         — one child run (used by --ab)
///   profiling.exe --write-golden-inputs &lt;dir&gt;
///                                         — deterministic reference captures for export comparisons
///   profiling.exe --write-filter-verdicts &lt;dir&gt; --out &lt;file&gt;
///                                         — filter verdict oracle over the captures in &lt;dir&gt;
///   profiling.exe --write-session-digest &lt;dir&gt; --out &lt;file&gt;
///                                         — session result oracle over the captures in &lt;dir&gt;
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

        // Parse options. Values follow their option; a missing value is an error (exit 1), never a silent default.
        if (!ProfilingArguments.TryParse(args, out ProfilingArguments parsed, out string parseError))
        {
            Console.Error.WriteLine(parseError);
            return 1;
        }

        // ── Modes that do not need the scenario list ─────────────────────────────
        if (parsed.ReportCsv is not null)
        {
            return AbReport.Run(
                parsed.ReportCsv,
                parsed.NoiseCsv,
                parsed.Set,
                parsed.OutPath!,
                parsed.Title ?? "A/B comparison",
                parsed.MinImprovement,
                parsed.Floor);
        }

        if (parsed.GoldenDirectory is not null)
        {
            return GoldenInputWriter.Run(parsed.GoldenDirectory);
        }

        if (parsed.VerdictInputs is not null)
        {
            return FilterVerdictWriter.Run(parsed.VerdictInputs, parsed.OutPath!);
        }

        if (parsed.DigestInputs is not null)
        {
            return SessionDigestWriter.Run(parsed.DigestInputs, parsed.OutPath!);
        }

        PhaseOverrides phases = new(parsed.WarmupSeconds, parsed.DurationSeconds);

        // ── Scenario registry ────────────────────────────────────────────────────
        // Scenarios with parameterless constructors are auto-discovered via reflection
        // (ScenarioDiscovery.Discover). Scenarios that require constructor arguments are
        // registered in _ManualScenarios and prepended to the discovered list.
        //
        // To add a new auto-discovered scenario: create a class that implements
        // IProfilingScenario with a parameterless (public or internal) constructor.
        // No change to Program.cs is required.
        IProfilingScenario[] discovered = ScenarioDiscovery.Discover();
        IProfilingScenario[] scenarios = _MergeScenarios(_ManualScenarios(), discovered);

        try
        {
            // ── --run-once: one measured child run that appends a CSV row ────────────
            if (parsed.RunOnce is not null)
            {
                return _RunOnce(scenarios, parsed.RunOnce, parsed.ResultCsv!, parsed.Variant, parsed.Run, phases);
            }

            // ── --ab: interleaved A/B orchestration of child runs ────────────────────
            if (parsed.AbBaseline is not null)
            {
                if (!_TryResolveNames(scenarios, parsed.All, parsed.Set, out List<string> names, out string nameError))
                {
                    Console.Error.WriteLine(nameError);
                    return 1;
                }

                return AbRunner.Run(parsed.AbBaseline, names, parsed.Repeat, parsed.OutPath!, parsed.AffinityMask, phases);
            }

            // ── --list: print all scenarios and exit ─────────────────────────────────
            if (parsed.ListOnly)
            {
                _PrintScenarioList(scenarios);
                return 0;
            }

            // ── No filter: interactive numbered menu ─────────────────────────────────
            if (parsed.Filter is null)
            {
                return _RunInteractiveMenu(scenarios, parsed.ManualStart, phases);
            }

            // ── Filter mode: run matching scenarios (for dotnet-trace or scripted use)
            return _RunFilteredScenarios(scenarios, parsed.Filter, parsed.ManualStart, phases);
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

    /// <summary>
    /// Scenarios that need constructor arguments and therefore cannot be auto-discovered.
    /// </summary>
    private static IProfilingScenario[] _ManualScenarios() =>
        [
            // ── Parsing ──────────────────────────────────────────────────────────
            // These require a bool constructor argument and cannot be auto-discovered.
            new ParseRandomFramesScenario(materialize: false),        // parse-random-frames
            new ParseRandomFramesScenario(materialize: true),         // parse-random-frames-materialized

            // ── Parsing (recycled — zero Packet heap allocations) ─────────────────
            new ParseRandomFramesRecycledScenario(materialize: true),  // parse-random-frames-materialized-recycled
            new ParseComboScenario(ParseComboKind.Build),
            new ParseComboScenario(ParseComboKind.Skip),
            new ParseComboScenario(ParseComboKind.BuildCachePort),
            new ParseComboScenario(ParseComboKind.SkipCachePort),
            new ParseComboScenario(ParseComboKind.BuildCacheAll),
            new ParseComboScenario(ParseComboKind.SkipCacheAll),
            new ParseComboScenario(ParseComboKind.SkipMiss),
            new ParseComboScenario(ParseComboKind.SkipPort),
            new ParseComboScenario(ParseComboKind.SkipPorts),
            new ParseComboScenario(ParseComboKind.SkipAll),
            new ParseComboScenario(ParseComboKind.BuildPort),
            new ParseComboScenario(ParseComboKind.BuildAll),
            new ParseComboScenario(ParseComboKind.TcpSkipSeq),
            new ParseComboScenario(ParseComboKind.TcpSkipSeqRaw),
            new ParseComboScenario(ParseComboKind.FilterBuild),
            new ParseComboScenario(ParseComboKind.FilterObserver),
            new ParseComboScenario(ParseComboKind.FilterObserverFlank),

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
            new SessionReadMatchingScenario(MatchingReadKind.Match),
            new SessionReadMatchingScenario(MatchingReadKind.Pruned),
            new SessionReadMatchingScenario(MatchingReadKind.Sparse),
            new RandomSourceParseScenario(materialize: false),
            new RandomSourceParseScenario(materialize: true),

            // ── Frame copies: per-frame arrays vs frame slabs (PcapngSourceOptions.FrameSlabSize) ──
            new ReadPcapngCopyScenario(PcapngCopyBackend.Mapped, frameSlabSize: 0, holdFrames: false),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Mapped, frameSlabSize: 64 * 1024, holdFrames: false),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Mapped, frameSlabSize: 0, holdFrames: true),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Mapped, frameSlabSize: 64 * 1024, holdFrames: true),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Stream, frameSlabSize: 0, holdFrames: false),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Stream, frameSlabSize: 64 * 1024, holdFrames: false),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Stream, frameSlabSize: 0, holdFrames: true),
            new ReadPcapngCopyScenario(PcapngCopyBackend.Stream, frameSlabSize: 64 * 1024, holdFrames: true),

            // ── Index recording during parse ─────────────────────────────────────
            new ParseComboScenario(ParseComboKind.BuildIndex),
            new ParseComboScenario(ParseComboKind.SkipIndex),

            // ── Frame index: retained memory per frame / packet (footprint) ──────
            new FootprintSourceScenario(CaptureWorkload.PcapngUniform, count: 1_000_000),
            new FootprintSourceScenario(CaptureWorkload.PcapngImix, count: 500_000),
            new FootprintSourceScenario(CaptureWorkload.BlfCan, count: 1_000_000),
            new FootprintSourceScenario(CaptureWorkload.AscAbsolute, count: 1_000_000),
            new FootprintSourceScenario(CaptureWorkload.AscRelative, count: 1_000_000),
            new FootprintSessionScenario(interleaved: false),
            new FootprintSessionScenario(interleaved: true),

            // ── Frame index: FrameById throughput (access) ───────────────────────
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Random),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Sequential),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Reverse),
            new AccessScenario(CaptureWorkload.BlfCan, AccessOrder.Random),
            new AccessScenario(CaptureWorkload.BlfCan, AccessOrder.Sequential),
            new AccessScenario(CaptureWorkload.AscAbsolute, AccessOrder.Random),
            new AccessScenario(CaptureWorkload.AscAbsolute, AccessOrder.Sequential),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Sequential, threads: 4),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Reverse, threads: 4),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Random, threads: 4),
            new AccessScenario(CaptureWorkload.BlfCan, AccessOrder.Sequential, threads: 4),
            new AccessScenario(CaptureWorkload.AscAbsolute, AccessOrder.Sequential, threads: 4),
            new AccessScenario(CaptureWorkload.AscAbsolute, AccessOrder.Reverse),
            new AccessScenario(CaptureWorkload.AscEthernet, AccessOrder.Random, count: 50_000),
            new AccessScenario(CaptureWorkload.AscEthernet, AccessOrder.Sequential, count: 50_000),
            new AccessScenario(CaptureWorkload.AscEthernet, AccessOrder.Reverse, count: 50_000),
            new AccessScenario(CaptureWorkload.AscMixed, AccessOrder.Random),
            new AccessScenario(CaptureWorkload.AscMixed, AccessOrder.Sequential),
            new AccessScenario(CaptureWorkload.AscMixed, AccessOrder.Sequential, threads: 4),
            new SessionAccessScenario(AccessOrder.Sequential),
            new SessionAccessScenario(AccessOrder.Reverse),
            new SessionAccessScenario(AccessOrder.Random),
            new SessionAccessScenario(AccessOrder.Sequential, threads: 4),

            // ── Frame access speed: realistic large capture (5 000 000 IMIX frames ≈ 2 GB temp file), session pages ──
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Random, count: 5_000_000, sizeLabel: "large"),
            new AccessScenario(CaptureWorkload.PcapngImix, AccessOrder.Sequential, count: 5_000_000, sizeLabel: "large"),
            new SessionReadPacketsScenario(interleaved: false),
            new SessionReadPacketsScenario(interleaved: true),

            // ── Frame access speed: range read (current build only; compare with access-pcapng-random/-sequential) ──
            new BatchAccessScenario(AccessOrder.Random),
            new BatchAccessScenario(AccessOrder.Sequential),

            // ── Frame index: sequential read without preload ─────────────────────
            new ReadWorkloadScenario(CaptureWorkload.Pcapng512, "read-pcapng-full"),
            new ReadWorkloadScenario(CaptureWorkload.AscAbsolute, "read-asc"),
            new ReadWorkloadScenario(CaptureWorkload.AscEthernet, "read-asc-eth"),
            new ReadWorkloadScenario(CaptureWorkload.AscMixed, "read-asc-mixed"),

            // ── Field storage: CAN parsing, recycling with large frames, memory per stored packet ──
            new ParseCanScenario(ParseCanKind.Build),
            new ParseCanScenario(ParseCanKind.Materialized),
            new ParseCanScenario(ParseCanKind.FdBuild),
            new ParseCanScenario(ParseCanKind.Recycled),
            new ParseMixedRecycledScenario(),
            new StoreScenario(StoreKind.Can),
            new StoreScenario(StoreKind.CanMaterialized),
            new StoreScenario(StoreKind.CanFd),
            new StoreScenario(StoreKind.Udp),
            new StoreScenario(StoreKind.UdpMaterialized),
            new StoreScenario(StoreKind.Mixed),
            new StoreScenario(StoreKind.Sparse),
        ];

    /// <summary>
    /// Runs exactly one scenario (ordinal name match) and appends its measurement to <paramref name="resultCsv"/>.
    /// Driven by <see cref="AbRunner"/> with names already resolved, so no substring matching here.
    /// </summary>
    private static int _RunOnce(
        IProfilingScenario[] scenarios,
        string name,
        string resultCsv,
        char variant,
        int run,
        PhaseOverrides phases)
    {
        foreach (IProfilingScenario scenario in scenarios)
        {
            if (scenario.Name.Equals(name, StringComparison.Ordinal))
            {
                ScenarioMeasurement measurement = _RunScenario(scenario, manualStart: false, phases);
                MeasurementCsv.Append(resultCsv, variant, run, in measurement);
                return 0;
            }
        }

        Console.Error.WriteLine(Invariant($"Unknown scenario '{name}'."));
        return 1;
    }

    /// <summary>
    /// Resolves the scenario names of an A/B run: every scenario in list order for <c>--all</c>, else the exact names
    /// of <c>--set</c> in the given order. Unknown names fail with a message that lists them.
    /// </summary>
    private static bool _TryResolveNames(
        IProfilingScenario[] scenarios,
        bool all,
        IReadOnlyList<string>? set,
        out List<string> names,
        out string error)
    {
        names = [];
        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (IProfilingScenario scenario in scenarios)
        {
            known.Add(scenario.Name);
            if (all)
            {
                names.Add(scenario.Name);
            }
        }

        // --set keeps the caller's order; every name must exist exactly.
        List<string> unknown = [];
        foreach (string name in all ? [] : set ?? [])
        {
            if (known.Contains(name))
            {
                names.Add(name);
            }
            else
            {
                unknown.Add(name);
            }
        }

        error = unknown.Count == 0
            ? string.Empty
            : Invariant($"Unknown scenario names in --set: {string.Join(", ", unknown)}");
        return unknown.Count == 0;
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
    private static int _RunInteractiveMenu(IProfilingScenario[] scenarios, bool manualStart, PhaseOverrides phases)
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

            _ = _RunScenario(scenarios[selection - 1], manualStart, phases);

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
    private static int _RunFilteredScenarios(
        IProfilingScenario[] scenarios,
        string? filter,
        bool manualStart,
        PhaseOverrides phases)
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

            _ = _RunScenario(scenario, manualStart, phases);
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
    /// Runs a single scenario: setup → warm-up → [optional pause] → timed run → cleanup, prints the results, and
    /// returns them for the CSV of an A/B run.
    /// Both warm-up and timed phases are duration-based: the runner calls
    /// <see cref="IProfilingScenario.Run"/> in a tight loop until the configured
    /// time has elapsed. This ensures every scenario runs for a predictable
    /// amount of time regardless of how fast a single iteration is.
    /// </summary>
    private static ScenarioMeasurement _RunScenario(IProfilingScenario scenario, bool manualStart, PhaseOverrides phases)
    {
        TimeSpan warmupDuration = phases.Warmup(scenario);
        TimeSpan timedDuration = phases.Duration(scenario);
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

        // Warm-up: optional PrepareIteration (hoisted) then Run.
        (long warmupIterations, _, _) = _RunForDuration(scenario, warmupDuration);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            FormattableString.Invariant(
                $"Warm-up: {warmupIterations} iterations in {warmupDuration.TotalSeconds:F1} s."));
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

        // Process-wide count of Monitor waits on a lock another thread held: separates real lock contention from
        // time a sampling profiler attributes to the lock slow path.
        long contentionBefore = Monitor.LockContentionCount;

        // Timed profiling phase: call Run() until the configured duration has elapsed.
        Console.WriteLine(
            FormattableString.Invariant($"Running for {timedDuration.TotalSeconds:F1} s..."));
        Stopwatch sw = Stopwatch.StartNew();
        (long timedIterations, long runTicks, long runAlloc) = _RunForDuration(scenario, timedDuration);
        sw.Stop();

        // Capture GC counters after the timed phase.
        // The precise: true heap walk overhead (same as above) is again outside the hot loop.
        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);
        long contentionAfter = Monitor.LockContentionCount;
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        double wallSeconds = sw.Elapsed.TotalSeconds;
        double netSeconds = runTicks / (double)Stopwatch.Frequency;
        if (netSeconds <= 0)
        {
            netSeconds = wallSeconds;
        }

        double iterationsPerSecond = timedIterations / netSeconds;
        _WriteColored(
            FormattableString.Invariant(
                $"Completed {timedIterations} iterations in {wallSeconds:F3} s wall, {netSeconds:F3} s net Run ({iterationsPerSecond:F1} iter/s)."),
            ConsoleColor.Green);

        // Print throughput metric when the scenario provides work-unit information.
        long totalWorkUnits = 0;
        if (scenario.WorkUnitsPerIteration > 0)
        {
            totalWorkUnits = timedIterations * scenario.WorkUnitsPerIteration;
        }

        if (totalWorkUnits > 0)
        {
            double unitsPerSecond = totalWorkUnits / netSeconds;
            string throughput = _FormatRate(unitsPerSecond);
            _WriteColored(
                FormattableString.Invariant(
                    $"Throughput: {throughput} {scenario.WorkUnitName}/s ({totalWorkUnits:N0} {scenario.WorkUnitName} total)."),
                ConsoleColor.Yellow);
        }

        // Print GC statistics for the timed phase
        long allocDelta = allocAfter - allocBefore;
        double allocPerIter = timedIterations > 0 ? (double)allocDelta / timedIterations : 0;
        double allocPerSec = wallSeconds > 0 ? allocDelta / wallSeconds : 0;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Allocations: {_FormatBytes(allocDelta)} total, {_FormatBytes((long)allocPerIter)}/iter, {_FormatRate(allocPerSec)}B/s"));
        Console.WriteLine(
            FormattableString.Invariant(
                $"  Phase alloc (includes PrepareIteration when set): {_FormatBytes(allocDelta)}"));
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Collections: Gen0={gen0After - gen0Before}, Gen1={gen1After - gen1Before}, Gen2={gen2After - gen2Before}"));
        Console.WriteLine(
            FormattableString.Invariant(
                $"  GC Heap: {gcInfo.HeapSizeBytes / 1024.0 / 1024:F1} MB, Pause: {gcInfo.PauseTimePercentage:F1}%"));
        Console.WriteLine(
            FormattableString.Invariant($"  Lock contentions (timed phase): {contentionAfter - contentionBefore:N0}"));
        if (totalWorkUnits > 0)
        {
            double allocPerWorkUnit = runAlloc / (double)totalWorkUnits;
            Console.WriteLine(
                FormattableString.Invariant($"  Alloc/packet: {allocPerWorkUnit:F0} bytes (net Run)"));
        }
        Console.ResetColor();

        // Scenarios without work units report iterations, so every row has a positive rate to compare.
        bool hasUnits = totalWorkUnits > 0;

        // Read the footprint before Cleanup releases what the scenario measured.
        double retained = scenario.RetainedBytesPerUnit;
        if (double.IsFinite(retained))
        {
            _WriteColored(
                FormattableString.Invariant($"Retained: {retained:F3} bytes/{(hasUnits ? scenario.WorkUnitName : "iteration")}"),
                ConsoleColor.Yellow);
        }

        Console.WriteLine();

        scenario.Cleanup();

        long units = hasUnits ? totalWorkUnits : timedIterations;
        double allocPerUnit = units > 0 ? runAlloc / (double)units : 0;
        return new ScenarioMeasurement(
            scenario.Name,
            hasUnits ? scenario.WorkUnitName : "iterations",
            timedIterations,
            units,
            netSeconds,
            units / netSeconds,
            allocPerUnit,
            gen0After - gen0Before,
            gen1After - gen1Before,
            gen2After - gen2Before,
            gcInfo.PauseTimePercentage,
            retained);
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
    /// Runs one phase. Hoists <see cref="IProfilingScenario.PrepareIteration"/> once: a null
    /// delegate keeps the inner loop as <see cref="IProfilingScenario.Run"/> only.
    /// Returns iteration count, net Run ticks, and net Run allocated bytes
    /// (<see cref="GC.GetTotalAllocatedBytes"/> with <c>precise: false</c>).
    /// </summary>
    private static (long Iterations, long RunTicks, long RunAllocBytes) _RunForDuration(
        IProfilingScenario scenario,
        TimeSpan duration)
    {
        long iterations = 0;
        long runTicks = 0;
        long runAlloc = 0;
        Stopwatch timer = Stopwatch.StartNew();
        long endTicks = (long)(duration.TotalSeconds * Stopwatch.Frequency);
        Action? prepare = scenario.PrepareIteration;

        if (prepare is null)
        {
            while (timer.ElapsedTicks < endTicks)
            {
                if (scenario.IsWorkComplete)
                {
                    break;
                }

                _TimeRun(scenario, ref runTicks, ref runAlloc);
                iterations++;
            }
        }
        else
        {
            while (timer.ElapsedTicks < endTicks)
            {
                if (scenario.IsWorkComplete)
                {
                    break;
                }

                prepare();
                _TimeRun(scenario, ref runTicks, ref runAlloc);
                iterations++;
            }
        }

        return (iterations, runTicks, runAlloc);
    }

    private static void _TimeRun(IProfilingScenario scenario, ref long runTicks, ref long runAlloc)
    {
        long alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        long t0 = Stopwatch.GetTimestamp();
        scenario.Run();
        long t1 = Stopwatch.GetTimestamp();
        long alloc1 = GC.GetTotalAllocatedBytes(precise: false);
        runTicks += t1 - t0;
        long delta = alloc1 - alloc0;
        if (delta > 0)
        {
            runAlloc += delta;
        }
    }
}

