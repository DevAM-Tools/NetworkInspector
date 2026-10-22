// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Interleaved A/B orchestration. Variant A is the baseline executable, variant B is the executable of the current
/// process. Each run is a fresh child process started with <c>--run-once</c>, so JIT tiering, PGO, and GC state start
/// equal for both variants. Odd runs execute A then B, even runs B then A; the alternation cancels slow thermal or
/// background drift.
/// <para><b>Thread-safety:</b> not thread-safe; runs its children one at a time.</para>
/// </summary>
internal static class AbRunner
{
    #region Constants

    /// <summary>Largest accepted repetition count (developer input, validated by <see cref="ProfilingArguments"/>).</summary>
    internal const int MaxRepeat = 50;

    /// <summary>
    /// Upper bound for one child run. A scenario runs about 10 s (2 s warm-up, 7 s timed, setup); 15 minutes only
    /// trips on a hung child.
    /// </summary>
    private static readonly TimeSpan _ChildTimeout = TimeSpan.FromMinutes(15);

    #endregion

    #region Public API

    /// <summary>
    /// Runs every scenario in <paramref name="names"/> <paramref name="repeat"/> times per variant and appends one CSV
    /// row per run to <paramref name="outCsv"/>. Returns 0 on success and 1 on invalid input or a failed child.
    /// </summary>
    /// <param name="baselineExe">Variant A executable.</param>
    /// <param name="names">Scenario names in run order.</param>
    /// <param name="repeat">Runs per variant and scenario.</param>
    /// <param name="outCsv">CSV file every child appends to.</param>
    /// <param name="affinityMask">Processor mask applied to every child right after it starts; null leaves them unpinned.</param>
    /// <param name="phases">Warm-up and timed-phase overrides passed to every child; both null keeps scenario defaults.</param>
    internal static int Run(
        string baselineExe,
        IReadOnlyList<string> names,
        int repeat,
        string outCsv,
        long? affinityMask,
        PhaseOverrides phases)
    {
        // The candidate is this process; launching through `dotnet x.dll` would make ProcessPath the host.
        string? candidateExe = Environment.ProcessPath;
        if (candidateExe is null
            || Path.GetFileNameWithoutExtension(candidateExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _Error("Run --ab through NetworkInspector.Profiling.exe, not through dotnet.");
            return 1;
        }

        if (!File.Exists(baselineExe) || (uint)(repeat - 1) >= MaxRepeat || names.Count == 0)
        {
            _Error(Invariant($"--ab needs an existing baseline executable, at least one scenario, and --repeat 1..{MaxRepeat}."));
            return 1;
        }

        // ProcessorAffinity exists on Windows and Linux only. Refuse before any child starts instead of measuring unpinned.
        if (affinityMask is not null && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            _Error("--affinity is supported on Windows and Linux only.");
            return 1;
        }

        // Scenario-major order: all runs of one scenario finish before the next starts, so a partial file is still
        // usable for the scenarios it covers.
        string fullBaseline = Path.GetFullPath(baselineExe);
        string fullCsv = Path.GetFullPath(outCsv);
        int total = names.Count * repeat * 2;
        int done = 0;
        foreach (string name in names)
        {
            for (int run = 1; run <= repeat; run++)
            {
                bool aFirst = (run & 1) == 1;
                string firstExe = aFirst ? fullBaseline : candidateExe;
                string secondExe = aFirst ? candidateExe : fullBaseline;
                char firstVariant = aFirst ? 'A' : 'B';
                char secondVariant = aFirst ? 'B' : 'A';
                if (_RunChild(firstExe, name, fullCsv, firstVariant, run, affinityMask, phases) != 0)
                {
                    return 1;
                }

                done++;
                _Progress(done, total, name, firstVariant, run);
                if (_RunChild(secondExe, name, fullCsv, secondVariant, run, affinityMask, phases) != 0)
                {
                    return 1;
                }

                done++;
                _Progress(done, total, name, secondVariant, run);
            }
        }

        return 0;
    }

    #endregion

    #region Private helpers

    private static int _RunChild(
        string exe,
        string name,
        string outCsv,
        char variant,
        int run,
        long? affinityMask,
        PhaseOverrides phases)
    {
        // Child stdout is drained and dropped (the CSV row is the result); stderr stays attached so failures show.
        ProcessStartInfo info = new(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("--run-once");
        info.ArgumentList.Add(name);
        info.ArgumentList.Add("--result");
        info.ArgumentList.Add(outCsv);
        info.ArgumentList.Add("--variant");
        info.ArgumentList.Add(variant.ToString());
        info.ArgumentList.Add("--run");
        info.ArgumentList.Add(run.ToString(CultureInfo.InvariantCulture));

        // Both variants get the same phase lengths, so a shortened run stays a fair comparison.
        if (phases.WarmupSeconds is double warmup)
        {
            info.ArgumentList.Add("--warmup");
            info.ArgumentList.Add(warmup.ToString("R", CultureInfo.InvariantCulture));
        }

        if (phases.DurationSeconds is double duration)
        {
            info.ArgumentList.Add("--duration");
            info.ArgumentList.Add(duration.ToString("R", CultureInfo.InvariantCulture));
        }

        using Process? child = Process.Start(info);
        if (child is null)
        {
            _Error(Invariant($"Cannot start {exe}."));
            return 1;
        }

        // Pin right after the start. The child spends its first seconds in setup and warm-up, so the timed phase always
        // runs on the masked processors; a child that already exited is reported as a failed run.
        if (affinityMask is long mask && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            try
            {
                child.ProcessorAffinity = (nint)mask;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A child that already exited has nothing to kill; any other child must not keep measuring unpinned.
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }

                _Error(Invariant($"{name} {variant} run {run}: cannot set processor affinity: {exception.Message}"));
                return 1;
            }
        }

        child.OutputDataReceived += static (_, _) => { };
        child.BeginOutputReadLine();
        if (!child.WaitForExit(_ChildTimeout))
        {
            child.Kill(entireProcessTree: true);
            _Error(Invariant($"{name} {variant} run {run} timed out."));
            return 1;
        }

        // The parameterless wait flushes the asynchronous stdout drain before the exit code is read.
        child.WaitForExit();
        if (child.ExitCode != 0)
        {
            _Error(Invariant($"{name} {variant} run {run} failed with exit code {child.ExitCode}."));
            return 1;
        }

        return 0;
    }

    private static void _Progress(int done, int total, string name, char variant, int run) =>
        Console.WriteLine(Invariant($"[{done}/{total}] {name} {variant}{run} done"));

    private static void _Error(string line) => Console.Error.WriteLine(line);

    #endregion
}
