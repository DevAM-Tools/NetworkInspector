// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// CSV persistence for A/B measurement rows. Invariant culture, comma separated, UTF-8 without BOM, one header line.
/// <para><b>Thread-safety:</b> not thread-safe. <see cref="AbRunner"/> runs its child processes one at a time, so only
/// one process appends to a file at any moment.</para>
/// </summary>
internal static class MeasurementCsv
{
    #region Constants

    /// <summary>The exact header line every new A/B CSV file starts with.</summary>
    internal const string Header =
        "scenario,variant,run,work_unit,iterations,work_units,net_seconds,throughput,alloc_bytes_per_unit,gen0,gen1,gen2,pause_percent,retained_bytes_per_unit";

    /// <summary>Header of files written before the retained column existed; still readable (retained = NaN).</summary>
    internal const string LegacyHeader =
        "scenario,variant,run,work_unit,iterations,work_units,net_seconds,throughput,alloc_bytes_per_unit,gen0,gen1,gen2,pause_percent";

    private const int _ColumnCount = 14;
    private const int _LegacyColumnCount = 13;

    #endregion

    #region Public API

    /// <summary>
    /// Appends one row to <paramref name="path"/>. Creates the directory and writes the header when the file does not
    /// exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scenario or work unit name contains a comma.</exception>
    internal static void Append(string path, char variant, int run, in ScenarioMeasurement measurement)
    {
        // Scenario names are code constants without commas; a comma would shift every later column, so fail loudly.
        if (measurement.Scenario.Contains(',', StringComparison.Ordinal)
            || measurement.WorkUnitName.Contains(',', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Invariant($"Scenario '{measurement.Scenario}' contains a comma."));
        }

        // Header only for a new file; every child process appends to the same file.
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        bool writeHeader = !File.Exists(path);
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (writeHeader)
        {
            writer.WriteLine(Header);
        }

        // "R" keeps every double round-trippable so the report sees exactly what the child measured.
        ScenarioMeasurement m = measurement;
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"{m.Scenario},{variant},{run},{m.WorkUnitName},"));
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"{m.Iterations},{m.WorkUnits},{m.NetSeconds:R},{m.Throughput:R},"));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{m.AllocBytesPerUnit:R},{m.Gen0},{m.Gen1},{m.Gen2},{m.PausePercent:R},{m.RetainedBytesPerUnit:R}"));
    }

    /// <summary>
    /// Reads every row of <paramref name="path"/>. Returns <see langword="false"/> with a message naming the file and line
    /// when the file is missing, the header is neither <see cref="Header"/> nor <see cref="LegacyHeader"/>, or a row is
    /// malformed (wrong column count, variant other than <c>A</c>/<c>B</c>, unparsable number, or a throughput that is not
    /// positive). Rows of a legacy file carry <see cref="double.NaN"/> as retained bytes.
    /// </summary>
    internal static bool TryRead(string path, out List<MeasurementRow> rows, out string error)
    {
        rows = [];
        if (!File.Exists(path))
        {
            error = Invariant($"File not found: {path}");
            return false;
        }

        using StreamReader reader = new(path, System.Text.Encoding.UTF8);
        // Files written before the retained column existed stay readable; their rows have no retained value.
        string? header = reader.ReadLine();
        int columnCount;
        if (string.Equals(header, Header, StringComparison.Ordinal))
        {
            columnCount = _ColumnCount;
        }
        else if (string.Equals(header, LegacyHeader, StringComparison.Ordinal))
        {
            columnCount = _LegacyColumnCount;
        }
        else
        {
            error = Invariant($"{path}: unexpected header.");
            return false;
        }

        // Rows are parsed strictly; a silently skipped row would bias a median.
        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            if (!_TryParseRow(line, columnCount, out MeasurementRow row))
            {
                error = Invariant($"{path}:{lineNumber}: malformed row.");
                return false;
            }

            rows.Add(row);
        }

        error = string.Empty;
        return true;
    }

    #endregion

    #region Private helpers

    private static bool _TryParseRow(string line, int columnCount, out MeasurementRow row)
    {
        row = default;
        string[] c = line.Split(',');
        if (c.Length != columnCount || c[0].Length == 0 || c[1].Length != 1 || (c[1][0] != 'A' && c[1][0] != 'B'))
        {
            return false;
        }

        // "NaN" parses with NumberStyles.Float and the invariant culture, so throughput-only rows round-trip.
        double retained = double.NaN;
        if (columnCount == _ColumnCount
            && !double.TryParse(c[13], NumberStyles.Float, CultureInfo.InvariantCulture, out retained))
        {
            return false;
        }

        bool parsed = int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int run)
            & long.TryParse(c[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long iterations)
            & long.TryParse(c[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long units)
            & double.TryParse(c[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double net)
            & double.TryParse(c[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double throughput)
            & double.TryParse(c[8], NumberStyles.Float, CultureInfo.InvariantCulture, out double alloc)
            & int.TryParse(c[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen0)
            & int.TryParse(c[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen1)
            & int.TryParse(c[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen2)
            & double.TryParse(c[12], NumberStyles.Float, CultureInfo.InvariantCulture, out double pause);

        // A non-positive or NaN throughput cannot enter a logarithm or a ratio.
        if (!parsed || !(throughput > 0) || double.IsInfinity(throughput))
        {
            return false;
        }

        row = new MeasurementRow(
            c[1][0],
            run,
            new ScenarioMeasurement(c[0], c[3], iterations, units, net, throughput, alloc, gen0, gen1, gen2, pause, retained));
        return true;
    }

    #endregion
}
