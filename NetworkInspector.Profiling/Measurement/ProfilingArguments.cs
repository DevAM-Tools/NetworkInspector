// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Parsed command line of the profiling tool. Options that take a value read the next argument; a missing or invalid
/// value is an error, never a silent default.
/// </summary>
internal sealed record ProfilingArguments
{
    #region Properties

    /// <summary>Wait for Enter before the timed phase (<c>--manual</c>).</summary>
    internal bool ManualStart { get; private set; }

    /// <summary>Print the scenario list and exit (<c>--list</c>).</summary>
    internal bool ListOnly { get; private set; }

    /// <summary>Select every scenario for <c>--ab</c> (<c>--all</c>).</summary>
    internal bool All { get; private set; }

    /// <summary>Scenario name for a single child run (<c>--run-once</c>).</summary>
    internal string? RunOnce { get; private set; }

    /// <summary>CSV file a single child run appends to (<c>--result</c>).</summary>
    internal string? ResultCsv { get; private set; }

    /// <summary>Variant label of a single child run, <c>A</c> or <c>B</c> (<c>--variant</c>).</summary>
    internal char Variant { get; private set; }

    /// <summary>Run number of a single child run, 1..<see cref="AbRunner.MaxRepeat"/> (<c>--run</c>).</summary>
    internal int Run { get; private set; }

    /// <summary>Baseline executable for an A/B comparison (<c>--ab</c>).</summary>
    internal string? AbBaseline { get; private set; }

    /// <summary>Runs per variant, 1..<see cref="AbRunner.MaxRepeat"/> (<c>--repeat</c>).</summary>
    internal int Repeat { get; private set; }

    /// <summary>Scenario names for <c>--ab</c> or the gate set of <c>--report</c> (<c>--set a,b,…</c>).</summary>
    internal IReadOnlyList<string>? Set { get; private set; }

    /// <summary>Output file of <c>--ab</c> (CSV) or <c>--report</c> (Markdown) (<c>--out</c>).</summary>
    internal string? OutPath { get; private set; }

    /// <summary>A/B CSV to report on (<c>--report</c>).</summary>
    internal string? ReportCsv { get; private set; }

    /// <summary>A/A CSV with per-scenario noise (<c>--noise</c>).</summary>
    internal string? NoiseCsv { get; private set; }

    /// <summary>Report title (<c>--title</c>).</summary>
    internal string? Title { get; private set; }

    /// <summary>
    /// Smallest geometric-mean Δ the <c>--report</c> gate accepts, in [−1, 1] (<c>--min-improvement</c>); default
    /// <see cref="AbReport.MinImprovement"/>.
    /// </summary>
    internal double MinImprovement { get; private set; } = AbReport.MinImprovement;

    /// <summary>
    /// Smallest per-scenario regression limit of the <c>--report</c> gate, in [0, 1] (<c>--floor</c>); default
    /// <see cref="AbReport.MinRegressionLimit"/>.
    /// </summary>
    internal double Floor { get; private set; } = AbReport.MinRegressionLimit;

    /// <summary>True when <c>--min-improvement</c> or <c>--floor</c> was given; both are valid only with <c>--report</c>.</summary>
    private bool _HasGateOption { get; set; }

    /// <summary>Directory for the reference captures (<c>--write-golden-inputs</c>).</summary>
    internal string? GoldenDirectory { get; private set; }

    /// <summary>Processor mask for the A/B child processes (<c>--affinity 0x…</c>); null when not set.</summary>
    internal long? AffinityMask { get; private set; }

    /// <summary>Reference-input directory of <c>--write-filter-verdicts</c>.</summary>
    internal string? VerdictInputs { get; private set; }

    /// <summary>Reference-input directory of <c>--write-session-digest</c>.</summary>
    internal string? DigestInputs { get; private set; }

    /// <summary>
    /// Warm-up seconds that replace every scenario's own warm-up (<c>--warmup</c>), in
    /// (0, <see cref="MaxPhaseSeconds"/>]; null keeps the scenario default.
    /// </summary>
    internal double? WarmupSeconds { get; private set; }

    /// <summary>
    /// Timed-phase seconds that replace every scenario's own duration (<c>--duration</c>), in
    /// (0, <see cref="MaxPhaseSeconds"/>]; null keeps the scenario default.
    /// </summary>
    internal double? DurationSeconds { get; private set; }

    /// <summary>Largest accepted <c>--warmup</c> or <c>--duration</c> value in seconds (developer input).</summary>
    internal const double MaxPhaseSeconds = 60;

    /// <summary>Bare scenario filter of the classic mode.</summary>
    internal string? Filter { get; private set; }

    #endregion

    #region Public API

    /// <summary>
    /// Parses <paramref name="args"/>. Returns <see langword="false"/> with a message that names the offending option
    /// when a value is missing or invalid, an option is unknown, or the options of a mode are incomplete.
    /// </summary>
    internal static bool TryParse(string[] args, out ProfilingArguments parsed, out string error)
    {
        parsed = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-'))
            {
                if (parsed.Filter is not null)
                {
                    error = Invariant($"Only one scenario filter is allowed; got '{parsed.Filter}' and '{arg}'.");
                    return false;
                }

                parsed.Filter = arg;
                continue;
            }

            if (!_TryApplyOption(parsed, args, ref i, out error))
            {
                return false;
            }
        }

        return _TryValidate(parsed, out error);
    }

    #endregion

    #region Private helpers

    private static bool _TryApplyOption(ProfilingArguments parsed, string[] args, ref int i, out string error)
    {
        error = string.Empty;
        string option = args[i].ToLowerInvariant();
        switch (option)
        {
            case "--manual":
                parsed.ManualStart = true;
                return true;
            case "--auto":
                parsed.ManualStart = false;
                return true;
            case "--list":
                parsed.ListOnly = true;
                return true;
            case "--all":
                parsed.All = true;
                return true;
            default:
                break;
        }

        // Every remaining option takes exactly one value.
        if (i + 1 >= args.Length)
        {
            error = Invariant($"Option {args[i]} needs a value.");
            return false;
        }

        i++;
        string value = args[i];
        switch (option)
        {
            case "--run-once":
                parsed.RunOnce = value;
                return true;
            case "--result":
                parsed.ResultCsv = value;
                return true;
            case "--variant":
                return _TryParseVariant(parsed, value, out error);
            case "--run":
                parsed.Run = _ParseCount(value, "--run", out error);
                return error.Length == 0;
            case "--ab":
                parsed.AbBaseline = value;
                return true;
            case "--repeat":
                parsed.Repeat = _ParseCount(value, "--repeat", out error);
                return error.Length == 0;
            case "--set":
                return _TryParseSet(parsed, value, out error);
            case "--out":
                parsed.OutPath = value;
                return true;
            case "--report":
                parsed.ReportCsv = value;
                return true;
            case "--noise":
                parsed.NoiseCsv = value;
                return true;
            case "--title":
                parsed.Title = value;
                return true;
            case "--write-golden-inputs":
                parsed.GoldenDirectory = value;
                return true;
            case "--affinity":
                return _TryParseAffinity(parsed, value, out error);
            case "--write-filter-verdicts":
                parsed.VerdictInputs = value;
                return true;
            case "--write-session-digest":
                parsed.DigestInputs = value;
                return true;
            case "--warmup":
                parsed.WarmupSeconds = _ParsePhaseSeconds(value, "--warmup", out error);
                return error.Length == 0;
            case "--duration":
                parsed.DurationSeconds = _ParsePhaseSeconds(value, "--duration", out error);
                return error.Length == 0;
            case "--min-improvement":
                parsed.MinImprovement = _ParseFraction(value, "--min-improvement", -1, 1, out error);
                parsed._HasGateOption = true;
                return error.Length == 0;
            case "--floor":
                parsed.Floor = _ParseFraction(value, "--floor", 0, 1, out error);
                parsed._HasGateOption = true;
                return error.Length == 0;
            default:
                error = Invariant($"Unknown option {args[i - 1]}.");
                return false;
        }
    }

    private static bool _TryParseVariant(ProfilingArguments parsed, string value, out string error)
    {
        if (value is not ("A" or "B"))
        {
            error = Invariant($"Option --variant must be A or B; got '{value}'.");
            return false;
        }

        parsed.Variant = value[0];
        error = string.Empty;
        return true;
    }

    /// <summary>Parses a count in 1..<see cref="AbRunner.MaxRepeat"/>; sets <paramref name="error"/> otherwise.</summary>
    private static int _ParseCount(string value, string option, out string error)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count < 1
            || count > AbRunner.MaxRepeat)
        {
            error = Invariant($"Option {option} must be an integer in 1..{AbRunner.MaxRepeat}; got '{value}'.");
            return 0;
        }

        error = string.Empty;
        return count;
    }

    /// <summary>
    /// Parses a fraction (invariant culture, e.g. <c>0.1</c> = 10 %) in [<paramref name="min"/>, <paramref name="max"/>];
    /// sets <paramref name="error"/> naming <paramref name="option"/> otherwise.
    /// </summary>
    private static double _ParseFraction(string value, string option, double min, double max, out string error)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction)
            || !double.IsFinite(fraction)
            || fraction < min
            || fraction > max)
        {
            error = Invariant($"Option {option} must be a number in [{min}, {max}]; got '{value}'.");
            return 0;
        }

        error = string.Empty;
        return fraction;
    }

    /// <summary>
    /// Parses a phase length in seconds (invariant culture) in (0, <see cref="MaxPhaseSeconds"/>]; sets
    /// <paramref name="error"/> naming <paramref name="option"/> otherwise.
    /// </summary>
    private static double _ParsePhaseSeconds(string value, string option, out string error)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds)
            || seconds <= 0
            || seconds > MaxPhaseSeconds)
        {
            error = Invariant($"Option {option} must be a number of seconds in (0, {MaxPhaseSeconds}]; got '{value}'.");
            return 0;
        }

        error = string.Empty;
        return seconds;
    }

    /// <summary>
    /// Parses a non-zero hexadecimal processor mask with a <c>0x</c> prefix whose bits all name existing logical
    /// processors. The mask is developer input; an out-of-range bit would pin to nothing on that machine.
    /// </summary>
    private static bool _TryParseAffinity(ProfilingArguments parsed, string value, out string error)
    {
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long mask)
            || mask <= 0)
        {
            error = Invariant($"Option --affinity needs a non-zero hexadecimal mask such as 0xFFFF; got '{value}'.");
            return false;
        }

        // ProcessorCount is far below 63 on every supported machine class; a larger count accepts every positive mask.
        int processors = Environment.ProcessorCount;
        if (processors < 63 && (mask >> processors) != 0)
        {
            error = Invariant($"Option --affinity names a processor at or above {processors}; got '{value}'.");
            return false;
        }

        parsed.AffinityMask = mask;
        error = string.Empty;
        return true;
    }

    private static bool _TryParseSet(ProfilingArguments parsed, string value, out string error)
    {
        string[] names = value.Split(',', StringSplitOptions.TrimEntries);
        foreach (string name in names)
        {
            if (name.Length == 0)
            {
                error = Invariant($"Option --set contains an empty name: '{value}'.");
                return false;
            }
        }

        parsed.Set = names;
        error = string.Empty;
        return true;
    }

    private static bool _TryValidate(ProfilingArguments parsed, out string error)
    {
        // At most one mode per invocation; mixing them would leave the intent ambiguous.
        int modes = (parsed.RunOnce is not null ? 1 : 0)
            + (parsed.AbBaseline is not null ? 1 : 0)
            + (parsed.ReportCsv is not null ? 1 : 0)
            + (parsed.GoldenDirectory is not null ? 1 : 0)
            + (parsed.VerdictInputs is not null ? 1 : 0)
            + (parsed.DigestInputs is not null ? 1 : 0);
        if (modes > 1)
        {
            error = "Use only one of --run-once, --ab, --report, --write-golden-inputs, --write-filter-verdicts, --write-session-digest.";
            return false;
        }

        if (parsed.AffinityMask is not null && parsed.AbBaseline is null)
        {
            error = "Option --affinity applies only to --ab.";
            return false;
        }

        // Phase lengths change measurements only; with a report or an oracle writer they would be silently ignored.
        bool hasPhaseOption = parsed.WarmupSeconds is not null || parsed.DurationSeconds is not null;
        if (hasPhaseOption
            && (parsed.ReportCsv is not null || parsed.GoldenDirectory is not null
                || parsed.VerdictInputs is not null || parsed.DigestInputs is not null))
        {
            error = "Options --warmup and --duration apply only to --ab, --run-once, and scenario runs.";
            return false;
        }

        if ((parsed.VerdictInputs is not null || parsed.DigestInputs is not null) && parsed.OutPath is null)
        {
            error = "Options --write-filter-verdicts and --write-session-digest need --out.";
            return false;
        }

        if (parsed.RunOnce is not null && (parsed.ResultCsv is null || parsed.Variant == default || parsed.Run == 0))
        {
            error = "Option --run-once needs --result, --variant, and --run.";
            return false;
        }

        if (parsed.AbBaseline is not null)
        {
            return _TryValidateAb(parsed, out error);
        }

        if (parsed.ReportCsv is not null && parsed.OutPath is null)
        {
            error = "Option --report needs --out.";
            return false;
        }

        // The gate options only change the report verdict; anywhere else they would be silently ignored.
        if (parsed._HasGateOption && parsed.ReportCsv is null)
        {
            error = "Options --min-improvement and --floor are valid only with --report.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool _TryValidateAb(ProfilingArguments parsed, out string error)
    {
        if (!File.Exists(parsed.AbBaseline))
        {
            error = Invariant($"Option --ab: baseline executable not found: {parsed.AbBaseline}");
            return false;
        }

        if (parsed.Repeat == 0 || parsed.OutPath is null)
        {
            error = "Option --ab needs --repeat and --out.";
            return false;
        }

        if (parsed.All == (parsed.Set is not null))
        {
            error = "Option --ab needs exactly one of --all and --set.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    #endregion
}
