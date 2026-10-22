// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Turns an A/B CSV into a Markdown comparison and a gate verdict.
/// <para>Per scenario s: Δs = median(B) / median(A) − 1 on throughput (higher is better); limit_s = max(floor, noise_s)
/// with noise_s from an A/A CSV (0 when absent). Over the gate set S: geomean = exp(mean ln(median(B)/median(A))) − 1.
/// PASS when geomean ≥ minImprovement and every scenario in S has Δs ≥ −limit_s. Defaults: floor 2 %,
/// minImprovement +3 %; <c>--floor</c> and <c>--min-improvement</c> override them (for example a "no regression beyond
/// 10 %" gate is <c>--floor 0.1 --min-improvement -0.1</c>).</para>
/// <para>Memory: scenarios that report retained bytes per unit get a separate retained-memory table (median per
/// variant). Retained memory is informational and never part of the gate.</para>
/// <para>Exit codes: 0 PASS, 3 FAIL, 1 invalid input.</para>
/// <para><b>Thread-safety:</b> stateless; all members are pure functions of their inputs except the file output.</para>
/// </summary>
internal static class AbReport
{
    #region Constants

    /// <summary>Default smallest geometric-mean improvement a candidate needs (+3 %).</summary>
    internal const double MinImprovement = 0.03;

    /// <summary>Default smallest per-scenario regression limit (2 %); noisier scenarios use their A/A noise instead.</summary>
    internal const double MinRegressionLimit = 0.02;

    private const int _ExitPass = 0;
    private const int _ExitInvalid = 1;
    private const int _ExitFail = 3;

    #endregion

    #region Public API

    /// <summary>
    /// Reads <paramref name="csv"/> (and the optional A/A <paramref name="noiseCsv"/>), writes the Markdown table to
    /// <paramref name="outMd"/>, prints one <c>GATE:</c> line, and returns the exit code.
    /// </summary>
    /// <param name="csv">A/B CSV.</param>
    /// <param name="noiseCsv">Optional A/A CSV with per-scenario noise.</param>
    /// <param name="set">Gate set; <see langword="null"/> gates every compared scenario.</param>
    /// <param name="outMd">Markdown output path.</param>
    /// <param name="title">Report title.</param>
    /// <param name="minImprovement">Smallest geometric-mean Δ that passes, in [−1, 1] (validated by the caller).</param>
    /// <param name="floor">Smallest per-scenario regression limit, in [0, 1] (validated by the caller).</param>
    internal static int Run(
        string csv,
        string? noiseCsv,
        IReadOnlyList<string>? set,
        string outMd,
        string title,
        double minImprovement,
        double floor)
    {
        if (!MeasurementCsv.TryRead(csv, out List<MeasurementRow> rows, out string error))
        {
            _Error(error);
            return _ExitInvalid;
        }

        // Noise per scenario from the A/A file; scenarios without A/A data fall back to the 2 % floor.
        Dictionary<string, double> noise = [];
        if (noiseCsv is not null)
        {
            if (!MeasurementCsv.TryRead(noiseCsv, out List<MeasurementRow> aa, out error))
            {
                _Error(error);
                return _ExitInvalid;
            }

            noise = ComputeNoise(aa);
        }

        List<ScenarioComparison> comparisons = Compare(rows, noise, floor, out error);
        if (error.Length > 0)
        {
            _Error(error);
        }

        if (comparisons.Count == 0)
        {
            _Error("No complete A/B pairs.");
            return _ExitInvalid;
        }

        // The gate set defaults to every compared scenario; unknown names are input errors.
        List<ScenarioComparison> gateSet = _SelectSet(comparisons, set, out error);
        if (error.Length > 0)
        {
            _Error(error);
            return _ExitInvalid;
        }

        double geomean = GeometricMeanDelta(gateSet);
        ScenarioComparison worst = _Worst(gateSet);
        bool pass = geomean >= minImprovement && gateSet.TrueForAll(static c => c.Delta >= -c.Limit);
        string gateLine =
            string.Create(CultureInfo.InvariantCulture, $"GATE: {(pass ? "PASS" : "FAIL")} geomean={geomean:+0.00%;-0.00%} ")
            + string.Create(
                CultureInfo.InvariantCulture,
                $"worst={worst.Scenario} {worst.Delta:+0.00%;-0.00%} (limit -{worst.Limit:0.00%})");
        _WriteMarkdown(outMd, title, comparisons, gateSet, geomean, gateLine);
        Console.WriteLine(gateLine);
        return pass
            ? _ExitPass
            : _ExitFail;
    }

    /// <summary>
    /// noise_s = max over all rows of scenario s of |throughput / median − 1|, where the median pools A and B rows (an
    /// A/A file measures the same build twice).
    /// </summary>
    internal static Dictionary<string, double> ComputeNoise(List<MeasurementRow> aa)
    {
        Dictionary<string, double> noise = new(StringComparer.Ordinal);
        foreach (IGrouping<string, MeasurementRow> group in aa.GroupBy(static r => r.Measurement.Scenario, StringComparer.Ordinal))
        {
            double[] values = [.. group.Select(static r => r.Measurement.Throughput)];
            double median = Median(values);
            double max = 0;
            foreach (double value in values)
            {
                max = Math.Max(max, Math.Abs((value / median) - 1));
            }

            noise[group.Key] = max;
        }

        return noise;
    }

    /// <summary>
    /// One comparison per scenario that has at least one A and one B row, sorted by name. Scenarios with only one
    /// variant are named in <paramref name="error"/> and left out. The regression limit is
    /// max(<paramref name="floor"/>, noise).
    /// </summary>
    internal static List<ScenarioComparison> Compare(
        List<MeasurementRow> rows,
        Dictionary<string, double> noise,
        double floor,
        out string error)
    {
        List<ScenarioComparison> result = [];
        List<string> incomplete = [];
        foreach (IGrouping<string, MeasurementRow> group in rows
            .GroupBy(static r => r.Measurement.Scenario, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            MeasurementRow[] a = [.. group.Where(static r => r.Variant == 'A')];
            MeasurementRow[] b = [.. group.Where(static r => r.Variant == 'B')];
            if (a.Length == 0 || b.Length == 0)
            {
                incomplete.Add(group.Key);
                continue;
            }

            double medianA = _MedianOf(a, static m => m.Throughput);
            double medianB = _MedianOf(b, static m => m.Throughput);
            double scenarioNoise = noise.GetValueOrDefault(group.Key);
            double limit = Math.Max(floor, scenarioNoise);
            result.Add(new ScenarioComparison(
                group.Key,
                a[0].Measurement.WorkUnitName,
                medianA,
                medianB,
                (medianB / medianA) - 1,
                Math.Max(_Spread(a), _Spread(b)),
                scenarioNoise,
                limit,
                _MedianOf(a, static m => m.AllocBytesPerUnit),
                _MedianOf(b, static m => m.AllocBytesPerUnit),
                _MedianOf(a, static m => m.Gen0),
                _MedianOf(b, static m => m.Gen0),
                _MedianOf(a, static m => m.Gen1),
                _MedianOf(b, static m => m.Gen1),
                _MedianOf(a, static m => m.Gen2),
                _MedianOf(b, static m => m.Gen2),
                _MedianOf(a, static m => m.PausePercent),
                _MedianOf(b, static m => m.PausePercent),
                _FiniteMedianOf(a, static m => m.RetainedBytesPerUnit),
                _FiniteMedianOf(b, static m => m.RetainedBytesPerUnit)));
        }

        error = incomplete.Count == 0
            ? string.Empty
            : Invariant($"Scenarios without both variants (ignored): {string.Join(", ", incomplete)}");
        return result;
    }

    /// <summary>Median of <paramref name="values"/>: copies, sorts, and averages the middle pair for even counts.</summary>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    internal static double Median(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Median of an empty set is undefined.", nameof(values));
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>exp(mean ln(median(B) / median(A))) − 1 over <paramref name="set"/>; 0 for an empty set.</summary>
    internal static double GeometricMeanDelta(List<ScenarioComparison> set)
    {
        if (set.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (ScenarioComparison comparison in set)
        {
            sum += Math.Log(comparison.MedianB / comparison.MedianA);
        }

        return Math.Exp(sum / set.Count) - 1;
    }

    #endregion

    #region Private helpers

    private static double _MedianOf(MeasurementRow[] rows, Func<ScenarioMeasurement, double> selector)
    {
        double[] values = new double[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            values[i] = selector(rows[i].Measurement);
        }

        return Median(values);
    }

    /// <summary>Median over the finite values only; <see cref="double.NaN"/> when no row has a finite value.</summary>
    private static double _FiniteMedianOf(MeasurementRow[] rows, Func<ScenarioMeasurement, double> selector)
    {
        List<double> values = new(rows.Length);
        foreach (MeasurementRow row in rows)
        {
            double value = selector(row.Measurement);
            if (double.IsFinite(value))
            {
                values.Add(value);
            }
        }

        return values.Count == 0
            ? double.NaN
            : Median([.. values]);
    }

    /// <summary>(max − min) / median of the throughput of one variant.</summary>
    private static double _Spread(MeasurementRow[] rows)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (MeasurementRow row in rows)
        {
            min = Math.Min(min, row.Measurement.Throughput);
            max = Math.Max(max, row.Measurement.Throughput);
        }

        return (max - min) / _MedianOf(rows, static m => m.Throughput);
    }

    private static List<ScenarioComparison> _SelectSet(List<ScenarioComparison> comparisons, IReadOnlyList<string>? set, out string error)
    {
        error = string.Empty;
        if (set is null)
        {
            return comparisons;
        }

        List<ScenarioComparison> selected = [];
        List<string> unknown = [];
        foreach (string name in set)
        {
            int index = comparisons.FindIndex(c => c.Scenario.Equals(name, StringComparison.Ordinal));
            if (index < 0)
            {
                unknown.Add(name);
                continue;
            }

            selected.Add(comparisons[index]);
        }

        if (unknown.Count > 0)
        {
            error = Invariant($"--set names without complete A/B data: {string.Join(", ", unknown)}");
        }

        return selected;
    }

    /// <summary>The scenario closest to (or furthest past) its regression limit: smallest Δ + limit.</summary>
    private static ScenarioComparison _Worst(List<ScenarioComparison> set)
    {
        ScenarioComparison worst = set[0];
        foreach (ScenarioComparison comparison in set)
        {
            if (comparison.Delta + comparison.Limit < worst.Delta + worst.Limit)
            {
                worst = comparison;
            }
        }

        return worst;
    }

    private static void _WriteMarkdown(
        string outMd,
        string title,
        List<ScenarioComparison> comparisons,
        List<ScenarioComparison> gateSet,
        double geomean,
        string gateLine)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outMd));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // Rows marked with * belong to the gate set; the rest are informational.
        HashSet<string> gateNames = new(gateSet.Select(static c => c.Scenario), StringComparer.Ordinal);
        using StreamWriter writer = new(outMd, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(Invariant($"# {title}"));
        writer.WriteLine();
        writer.WriteLine("Medians per variant; A = baseline, B = candidate; * = gate set.");
        writer.WriteLine();
        writer.WriteLine("| Scenario | Δ % | A → B (unit/s) | Alloc/unit A → B | Spread / noise / limit % | GC0/1/2 A → B | GC pause % A → B |");
        writer.WriteLine("|----------|-----|----------------|------------------|--------------------------|---------------|------------------|");
        foreach (ScenarioComparison c in comparisons)
        {
            string marker = gateNames.Contains(c.Scenario) ? " *" : string.Empty;
            writer.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"| {c.Scenario}{marker} | {c.Delta:+0.00%;-0.00%} | {c.MedianA:N0} → {c.MedianB:N0} {c.WorkUnit} | "));
            writer.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"{c.AllocA:F1} → {c.AllocB:F1} | {c.Spread:0.0%} / {c.Noise:0.0%} / {c.Limit:0.0%} | "));
            writer.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"{c.Gen0A:0.#}/{c.Gen1A:0.#}/{c.Gen2A:0.#} → {c.Gen0B:0.#}/{c.Gen1B:0.#}/{c.Gen2B:0.#} | "));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{c.PauseA:0.00} → {c.PauseB:0.00} |"));
        }

        writer.WriteLine();
        _WriteRetainedTable(writer, comparisons);
        writer.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"Geomean over all {comparisons.Count} scenarios: {GeometricMeanDelta(comparisons):+0.00%;-0.00%}. "));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Gate set: {gateSet.Count} scenarios, geomean {geomean:+0.00%;-0.00%}."));
        writer.WriteLine();
        writer.WriteLine(gateLine);
    }

    /// <summary>
    /// Retained managed memory per work unit for the scenarios that measure it (lower is better). Written only when at
    /// least one scenario has a finite retained value; a variant without one shows "n/a".
    /// </summary>
    private static void _WriteRetainedTable(StreamWriter writer, List<ScenarioComparison> comparisons)
    {
        List<ScenarioComparison> measured = comparisons.FindAll(
            static c => double.IsFinite(c.RetainedA) || double.IsFinite(c.RetainedB));
        if (measured.Count == 0)
        {
            return;
        }

        writer.WriteLine("## Retained memory");
        writer.WriteLine();
        writer.WriteLine("Retained managed bytes per work unit after a full scan with the object alive (median; lower is better).");
        writer.WriteLine();
        writer.WriteLine("| Scenario | A (B/unit) | B (B/unit) | Δ % |");
        writer.WriteLine("|----------|------------|------------|-----|");
        foreach (ScenarioComparison c in measured)
        {
            string delta = double.IsFinite(c.RetainedA) && double.IsFinite(c.RetainedB) && c.RetainedA != 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(c.RetainedB / c.RetainedA) - 1:+0.00%;-0.00%}")
                : "n/a";
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {c.Scenario} | {_FormatRetained(c.RetainedA)} | {_FormatRetained(c.RetainedB)} | {delta} |"));
        }

        writer.WriteLine();
    }

    private static string _FormatRetained(double value) => double.IsFinite(value)
        ? value.ToString("F3", CultureInfo.InvariantCulture)
        : "n/a";

    private static void _Error(string line) => Console.Error.WriteLine(line);

    #endregion
}

/// <summary>Per-scenario comparison of two variants (medians over runs).</summary>
/// <param name="Scenario">Scenario name.</param>
/// <param name="WorkUnit">Work unit label.</param>
/// <param name="MedianA">Median throughput of A (units/s).</param>
/// <param name="MedianB">Median throughput of B (units/s).</param>
/// <param name="Delta">MedianB / MedianA − 1.</param>
/// <param name="Spread">Largest (max − min) / median of either variant.</param>
/// <param name="Noise">A/A noise of the scenario (0 when no A/A file was given).</param>
/// <param name="Limit">Regression limit max(floor, A/A noise).</param>
/// <param name="AllocA">Median allocated bytes per unit of A.</param>
/// <param name="AllocB">Median allocated bytes per unit of B.</param>
/// <param name="Gen0A">Median gen-0 collections of A.</param>
/// <param name="Gen0B">Median gen-0 collections of B.</param>
/// <param name="Gen1A">Median gen-1 collections of A.</param>
/// <param name="Gen1B">Median gen-1 collections of B.</param>
/// <param name="Gen2A">Median gen-2 collections of A.</param>
/// <param name="Gen2B">Median gen-2 collections of B.</param>
/// <param name="PauseA">Median GC pause time percentage of A.</param>
/// <param name="PauseB">Median GC pause time percentage of B.</param>
/// <param name="RetainedA">Median finite retained bytes per unit of A, or NaN when A does not measure it.</param>
/// <param name="RetainedB">Median finite retained bytes per unit of B, or NaN when B does not measure it.</param>
internal readonly record struct ScenarioComparison(
    string Scenario,
    string WorkUnit,
    double MedianA,
    double MedianB,
    double Delta,
    double Spread,
    double Noise,
    double Limit,
    double AllocA,
    double AllocB,
    double Gen0A,
    double Gen0B,
    double Gen1A,
    double Gen1B,
    double Gen2A,
    double Gen2B,
    double PauseA,
    double PauseB,
    double RetainedA,
    double RetainedB);
