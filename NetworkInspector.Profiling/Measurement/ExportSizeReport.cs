// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Writes every agreed export configuration of a fixed sample to files and records their sizes, and compares two such records.
/// The before/after size gate of the export-size work uses it. Invariant: the sample and the configuration names (the table in
/// this doc comment) stay stable between the baseline binary and the current one, because the comparison joins rows by name.
/// <para>
/// <b>Why a measurement inside the profiling tool:</b> a size gate needs the same sample, the same configurations, and the same
/// way of counting bytes before and after a format change. A baseline binary published before the change (it keeps writing
/// today's formats) and the current binary both run this code, so the only difference between the two CSV files is the exporters.
/// </para>
/// <para>
/// <b>Sample:</b> <see cref="SampleFrameCount"/> Ethernet/IPv6/UDP frames of 512 bytes from
/// <see cref="SampleFrames.CreateSharedFrames"/> (fixed bytes, timestamps from the frame index), parsed with a field tree into the
/// full standard stack. Every configuration gets its own stack and its own frames, so no configuration depends on what an earlier
/// one parsed (a stack remembers the packet ids it parsed first).
/// </para>
/// <para>
/// <b>Determinism:</b> DuckDB runs with one thread and every batching exporter writes inline (<see cref="ExportWriteMode.Synchronous"/>),
/// so file sizes do not depend on thread timing. No exporter writes the wall clock into its output, so two runs on the same
/// build give identical sizes.
/// </para>
/// <para>
/// <b>Configurations</b> (the names are the CSV keys). The <c>-default</c> rows measure the default content of the exporter in the
/// running binary, by design: they show what a user gets without options. The rows whose size depends on the frame-bytes or buffer
/// switches (<c>duckdb-max</c>, <c>parquet-max</c>, <c>pbf-standard-texts</c>, <c>pbf-columnar-max</c>, <c>json-all</c>) name their
/// content explicitly, so both binaries write the same content under one name. The CSV rows and <c>text-full</c> start from the
/// exporter's own template or default, which has no frame bytes in either binary. With the frame bytes off by default,
/// <c>duckdb-default</c>, <c>parquet-default</c>, and <c>pbf-columnar-default</c> write no frame bytes and differ from their
/// <c>-max</c> rows, which write them, while <c>pbf-standard-default</c> and <c>pbf-standard-texts</c> are the same content. In
/// the baseline binary, whose default was frame bytes on (except in the standard layout), the <c>-default</c> and <c>-max</c> rows
/// of DuckDB, Parquet, and columnar PBF were the same content:
/// </para>
/// <code>
/// duckdb-default         DuckDbExporter, DuckDbExporter.DefaultContent
/// duckdb-max             DuckDB, Hierarchy, Info, FrameBytes on, texts on
/// parquet-default        ParquetExporter, ParquetExporter.DefaultContent (size = sum of all files of the dataset directory)
/// parquet-max            Parquet, Hierarchy, Info, FrameBytes on, texts on
/// pbf-standard-default   PbfExporter, standard layout, the layout's default content
/// pbf-standard-texts     PBF standard, Hierarchy, Info, FrameBytes off, texts on
/// pbf-columnar-default   PbfExporter, columnar layout, PbfExporter.DefaultContent
/// pbf-columnar-max       PBF columnar, Hierarchy, Info, FrameBytes on, texts on
/// csv-packets            CsvExporter, packet rows with the default columns
/// csv-all                CSV, AllFields (CsvExporter.ContentTemplate, texts on)
/// csv-hierarchy          CSV, Hierarchy (ContentTemplate with the hierarchy shape, texts on)
/// json-default           JsonExporter, compact format, JsonExporter.DefaultContent (the field tree)
/// json-all               JSON compact, AllFields, FrameBytes off, texts on
/// text-default           TextExporter, standard detail level, TextExporter.DefaultContent
/// text-full              Text, Full detail level
/// </code>
/// <para>
/// <b>Limits of the sample:</b> all frames carry the same bytes (only the timestamp and the frame number differ) and use IPv6
/// and UDP, so compressing formats see far more redundancy than a real capture, and IPv4, MAC variety, and application protocols
/// do not occur. The gate therefore answers "does a format change make the same data larger", not "how large is a real
/// capture". The sample is kept because every frame parses without protocol state (no reassembly, no session), so the bytes of a
/// configuration depend on the exporter alone.
/// </para>
/// <para>
/// <b>Size:</b> every configuration writes into its own sub-directory of the output directory; the size is the sum of the lengths
/// of all files below it, so a dataset directory (Parquet) is counted completely and a file the exporter leaves behind (for
/// example a DuckDB write-ahead log) is counted, never missed.
/// </para>
/// <para>
/// <b>Trust boundary:</b> every argument is a developer-supplied path or count on a developer machine; the tool reads only the
/// two size CSV files it also writes. Omitted validation (documented per Section 4.1): the CSV readers bound neither the line
/// length nor the row count, because the files hold one short line per configuration; a hostile or huge file would only cost
/// memory proportional to its rows in this developer tool, and it is read line by line, never as one string.
/// </para>
/// <para>
/// <b>Verification (not covered by the test suite):</b> this is a measurement tool of the profiling app, which is not shipped and
/// has no test project, so no automated test exercises <see cref="Compare"/>, the CSV reader, or the size options of
/// <see cref="ProfilingArguments"/>. The gate is verified by running the commands below by hand after an edit of this class. A
/// change here is not proven by a green test run.
/// </para>
/// <code>
/// dotnet build NetworkInspector.Profiling/NetworkInspector.Profiling.csproj -c Release
/// NetworkInspector.Profiling.exe --write-export-sizes &lt;dirA&gt; --out &lt;a.csv&gt; --frames 2000
/// NetworkInspector.Profiling.exe --write-export-sizes &lt;dirB&gt; --out &lt;b.csv&gt; --frames 2000
/// NetworkInspector.Profiling.exe --compare-export-sizes &lt;a.csv&gt; &lt;b.csv&gt; --out &lt;ab.md&gt;
/// </code>
/// <para>
/// Expected: the two CSV files are identical, the compare exits 0, and every row of the report is <c>0.0 %</c> (stability). Then
/// repeat the compare on a hand-edited copy of the second CSV: one row one byte larger exits 1 with a <c>larger</c> row; a removed
/// row exits 1 with a <c>missing</c> row; an extra row exits 0 with a <c>new</c> row; a line without a comma, a non-number, a
/// duplicate name, a wrong header, or a missing file exits 2 with a message that names the file and the line (gate).
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; called once from <c>Main</c>.</para>
/// </summary>
/// <remarks>
/// Sync: the configuration table above and <c>_Configurations</c>; the usage block of <see cref="Program"/> and the option parsing in
/// <see cref="ProfilingArguments"/> (<c>--write-export-sizes</c>, <c>--compare-export-sizes</c>, <c>--frames</c>).
/// </remarks>
/// <seealso cref="GoldenInputWriter"/>
/// <seealso cref="SampleFrames"/>
internal static class ExportSizeReport
{
    #region Sample and configurations

    /// <summary>Frames in the measured sample (deterministic, see <see cref="SampleFrames.CreateSharedFrames"/>).</summary>
    internal const int SampleFrameCount = 20_000;

    /// <summary>Exit code of <see cref="Compare"/> when no configuration is larger or missing after.</summary>
    private const int _ExitPass = 0;

    /// <summary>Exit code of <see cref="Compare"/> when at least one configuration is larger after or missing after.</summary>
    private const int _ExitGateFailed = 1;

    /// <summary>Exit code of <see cref="Compare"/> for an unreadable or malformed size file or an unwritable report.</summary>
    private const int _ExitInvalid = 2;

    /// <summary>The exact first line of a size CSV.</summary>
    private const string _CsvHeader = "config,bytes";

    /// <summary>
    /// Hierarchy content with every optional member on, named explicitly: the baseline binary has frames on by default and the
    /// current binary may not, so the <c>-max</c> rows must not depend on the default.
    /// </summary>
    private static readonly ExportContent _HierarchyMax = new()
    {
        Shape = ExportShape.Hierarchy,
        Info = true,
        FrameBytes = true,
        CustomText = true,
        CustomRepresentation = true,
    };

    /// <summary>Hierarchy content with texts and without frame bytes (the standard PBF layout has no frame column).</summary>
    private static readonly ExportContent _HierarchyTexts = _HierarchyMax with { FrameBytes = false };

    /// <summary>All-fields content with texts and without frame bytes, named explicitly for the same reason as <see cref="_HierarchyMax"/>.</summary>
    private static readonly ExportContent _AllFieldsTexts = _HierarchyMax with { Shape = ExportShape.AllFields, FrameBytes = false };

    /// <summary>
    /// The configurations in CSV order. Each factory builds a fresh exporter that writes to the given path: a file path when
    /// <see cref="Configuration.FileName"/> is not empty, the dataset directory otherwise.
    /// </summary>
    private static readonly Configuration[] _Configurations =
    [
        new("duckdb-default", "export.duckdb", static path => _Wrap(
            Exporters.DuckDb.DuckDbExporter.CreateBuilder()
                .ToFile(path)
                .WithThreads(1)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .Build())),
        new("duckdb-max", "export.duckdb", static path => _Wrap(
            Exporters.DuckDb.DuckDbExporter.CreateBuilder()
                .ToFile(path)
                .WithThreads(1)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .WithContent(_HierarchyMax)
                .Build())),
        new("parquet-default", string.Empty, static path => _Wrap(
            Exporters.Parquet.ParquetExporter.CreateBuilder()
                .ToDirectory(path)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .Build())),
        new("parquet-max", string.Empty, static path => _Wrap(
            Exporters.Parquet.ParquetExporter.CreateBuilder()
                .ToDirectory(path)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .WithContent(_HierarchyMax)
                .Build())),
        new("pbf-standard-default", "export.pbf", static path => _Wrap(
            Exporters.Pbf.PbfExporter.CreateBuilder()
                .ToFile(path)
                .WithFormat(Exporters.Pbf.PbfExportFormat.Standard)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .Build())),
        new("pbf-standard-texts", "export.pbf", static path => _Wrap(
            Exporters.Pbf.PbfExporter.CreateBuilder()
                .ToFile(path)
                .WithFormat(Exporters.Pbf.PbfExportFormat.Standard)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .WithContent(_HierarchyTexts)
                .Build())),
        new("pbf-columnar-default", "export.pbf", static path => _Wrap(
            Exporters.Pbf.PbfExporter.CreateBuilder()
                .ToFile(path)
                .WithFormat(Exporters.Pbf.PbfExportFormat.Columnar)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .Build())),
        new("pbf-columnar-max", "export.pbf", static path => _Wrap(
            Exporters.Pbf.PbfExporter.CreateBuilder()
                .ToFile(path)
                .WithFormat(Exporters.Pbf.PbfExportFormat.Columnar)
                .WithWriteMode(ExportWriteMode.Synchronous)
                .WithContent(_HierarchyMax)
                .Build())),
        new("csv-packets", "export.csv", static path => _Wrap(
            Exporters.Csv.CsvExporter.CreateBuilder()
                .ToFile(path)
                .Build())),
        new("csv-all", "export.csv", static path => _Wrap(
            Exporters.Csv.CsvExporter.CreateBuilder()
                .ToFile(path)
                .WithContent(Exporters.Csv.CsvExporter.ContentTemplate)
                .Build())),
        new("csv-hierarchy", "export.csv", static path => _Wrap(
            Exporters.Csv.CsvExporter.CreateBuilder()
                .ToFile(path)
                .WithContent(Exporters.Csv.CsvExporter.ContentTemplate with { Shape = ExportShape.Hierarchy })
                .Build())),
        new("json-default", "export.json", static path => _Wrap(
            Exporters.Json.JsonExporter.CreateBuilder()
                .ToFile(path)
                .Build())),
        new("json-all", "export.json", static path => _Wrap(
            Exporters.Json.JsonExporter.CreateBuilder()
                .ToFile(path)
                .WithContent(_AllFieldsTexts)
                .Build())),
        new("text-default", "export.txt", static path => _Wrap(
            Exporters.Text.TextExporter.CreateBuilder()
                .ToFile(path)
                .Build())),
        new("text-full", "export.txt", static path => _Wrap(
            Exporters.Text.TextExporter.CreateBuilder()
                .ToFile(path)
                .WithDetailLevel(Exporters.Text.TextDetailLevel.Full)
                .Build())),
    ];

    #endregion

    #region Writing sizes

    /// <summary>Writes every configuration into <paramref name="outputDirectory"/> and the sizes as CSV.</summary>
    /// <param name="outputDirectory">Empty or missing directory; one sub-directory per configuration (see the type remarks).</param>
    /// <param name="csvPath">Target CSV: header <c>config,bytes</c>, one row per configuration in table order.</param>
    /// <param name="frameCount">Frames to export, at least 1; <see cref="SampleFrameCount"/> from the command line.</param>
    /// <param name="error">Reason when the method returns false (existing non-empty directory, export failure with its message).</param>
    /// <returns><see langword="true"/> when every configuration was written; the CSV exists only then.</returns>
    /// <remarks>
    /// A failed run writes no CSV: a partial file would let the gate compare a broken run. The CSV is written to a temporary file
    /// and moved into place, so an interrupted write never leaves a truncated CSV either. Prints one progress line per
    /// configuration to standard output, because a full run takes a while. File I/O exceptions of the whole run are caught once
    /// here and turned into <paramref name="error"/> (the command boundary); expected failures of a configuration travel as return values.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> or <paramref name="csvPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is not positive.</exception>
    internal static bool TryWriteSizes(string outputDirectory, string csvPath, int frameCount, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentException.ThrowIfNullOrEmpty(csvPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        // The exporters' finish contract documents AggregateException for a failed release step; the file system raises the
        // other two. Anything else is a bug of this harness and is allowed to surface. The core records which step it was in,
        // so the message names the configuration that was running when the exception came.
        string running = "the output directory check";
        try
        {
            return _TryWriteSizesCore(outputDirectory, csvPath, frameCount, ref running, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        {
            error = Invariant($"The export size run failed in {running}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The run without the command-boundary catch: checks the directory, exports every configuration, writes the CSV.</summary>
    /// <param name="outputDirectory">As in <see cref="TryWriteSizes"/>.</param>
    /// <param name="csvPath">As in <see cref="TryWriteSizes"/>.</param>
    /// <param name="frameCount">As in <see cref="TryWriteSizes"/>.</param>
    /// <param name="running">Set to the step that is about to run (a configuration name or the CSV), for the caller's exception message.</param>
    /// <param name="error">The reason when the method returns false.</param>
    /// <returns><see langword="true"/> when the CSV was written.</returns>
    private static bool _TryWriteSizesCore(
        string outputDirectory,
        string csvPath,
        int frameCount,
        ref string running,
        [NotNullWhen(false)] out string? error)
    {
        // A directory that already holds files would mix an old run into the sizes (a Parquet dataset is deleted by the exporter,
        // other leftovers are not), so only an empty or missing directory is accepted.
        if (Directory.Exists(outputDirectory))
        {
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(outputDirectory).GetEnumerator();
            if (entries.MoveNext())
            {
                error = Invariant($"The output directory is not empty: {outputDirectory}");
                return false;
            }
        }

        Directory.CreateDirectory(outputDirectory);

        // Sizes are collected first and written last: a failing configuration leaves no CSV behind.
        List<SizeRow> rows = new(_Configurations.Length);
        foreach (Configuration configuration in _Configurations)
        {
            running = Invariant($"configuration '{configuration.Name}'");
            if (!_TryExport(configuration, outputDirectory, frameCount, out long bytes, out string? failure))
            {
                error = failure;
                return false;
            }

            rows.Add(new SizeRow(configuration.Name, bytes));
            Console.Out.WriteLine(Invariant($"{configuration.Name}: {bytes:N0} bytes"));
        }

        running = "the size CSV";
        _WriteSizesCsv(csvPath, rows);
        error = null;
        return true;
    }

    /// <summary>
    /// Exports the sample in one configuration into its own sub-directory and measures what was written: feeds every frame,
    /// finishes and disposes the exporter, then judges its statistics and sums the file sizes.
    /// </summary>
    /// <param name="configuration">The configuration to run.</param>
    /// <param name="outputDirectory">The directory that receives the configuration's sub-directory.</param>
    /// <param name="frameCount">Frames to export.</param>
    /// <param name="bytes">The sum of all file lengths below the sub-directory when the method returns true.</param>
    /// <param name="error">The reason, naming the configuration, when the method returns false.</param>
    /// <returns><see langword="true"/> when the exporter accepted and wrote every packet without reporting an error.</returns>
    private static bool _TryExport(
        Configuration configuration,
        string outputDirectory,
        int frameCount,
        out long bytes,
        [NotNullWhen(false)] out string? error)
    {
        bytes = 0;
        string directory = Path.Combine(outputDirectory, configuration.Name);
        Directory.CreateDirectory(directory);
        string target = configuration.FileName.Length == 0
            ? directory
            : Path.Combine(directory, configuration.FileName);

        // A private stack and private frames per configuration: replays of already parsed ids would make the first
        // configuration different from the later ones.
        using Stack stack = StackHelper.CreateFullStack();
        Frame[] frames = SampleFrames.CreateSharedFrames(frameCount, stack);

        ExporterHandle handle = configuration.Create(target);
        ErrorCapture capture = new();
        handle.Errors.ItemSkipped += capture.OnItemSkipped;

        // OnFinish before Dispose, and Dispose before the size is read: the files must be complete and closed (DuckDB removes its
        // write-ahead log only when it closes cleanly). Dispose runs even when feeding fails.
        string? failure;
        try
        {
            failure = _Feed(handle, stack, frames);
            handle.Listener.OnFinish();
        }
        finally
        {
            handle.Owner.Dispose();
        }

        // A refused packet or an unparsable frame is reported with the exporter's own reason when it raised one (a fatal write
        // error stops the export and is the cause of the refusal).
        if (failure is not null && capture.First is { } reported)
        {
            failure = Invariant($"{failure} The exporter reported: {reported}");
        }

        // The statistics are the exporter's own account of what happened; a skipped or failed item changes the size and must
        // never pass as a measurement.
        failure ??= _JudgeStatistics(handle.Errors, capture, frameCount);
        if (failure is not null)
        {
            error = Invariant($"Configuration '{configuration.Name}': {failure}");
            return false;
        }

        long total = 0;
        int files = 0;
        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            total = checked(total + file.Length);
            files++;
        }

        if (files == 0)
        {
            error = Invariant($"Configuration '{configuration.Name}': the exporter wrote no file below {directory}.");
            return false;
        }

        bytes = total;
        error = null;
        return true;
    }

    /// <summary>Parses every frame with a field tree and hands the packet to the exporter.</summary>
    /// <param name="handle">The exporter.</param>
    /// <param name="stack">The stack that parses the frames.</param>
    /// <param name="frames">The sample frames.</param>
    /// <returns><see langword="null"/> when every packet was accepted; otherwise a description of the first failure.</returns>
    private static string? _Feed(ExporterHandle handle, Stack stack, Frame[] frames)
    {
        // Default options build a field tree for every packet, which every exporter accepts for every shape; the measurement
        // therefore does not depend on a parse plan that leaves the tree out.
        ParseOptions options = new();
        for (int i = 0; i < frames.Length; i++)
        {
            PacketId id = i;
            if (!Packet.TryParse(id, stack, frames[i], in options, out Packet? packet, out ParseFailure failure) || packet is null)
            {
                return Invariant($"frame {i} was not parsed: {failure}");
            }

            if (!handle.Listener.OnPacket(packet))
            {
                return Invariant($"the exporter stopped accepting packets at packet {i}.");
            }
        }

        return null;
    }

    /// <summary>Judges the statistics of a finished exporter against the number of frames fed.</summary>
    /// <param name="statistics">The finished exporter.</param>
    /// <param name="capture">The messages the exporter raised while it ran.</param>
    /// <param name="frameCount">Frames that were fed.</param>
    /// <returns><see langword="null"/> when the export is clean; otherwise a description of the problem.</returns>
    private static string? _JudgeStatistics(IErrorTolerantExporter statistics, ErrorCapture capture, int frameCount)
    {
        if (capture.Count > 0 || statistics.HasErrors || statistics.ErrorCount > 0 || statistics.SkippedCount > 0)
        {
            string message = capture.First ?? "no message was reported";
            return Invariant(
                $"the exporter reported {statistics.ErrorCount} error(s) and skipped {statistics.SkippedCount} item(s); first: {message}");
        }

        if (!statistics.IsFinished)
        {
            return "the exporter did not finish.";
        }

        if (statistics.WrittenCount != frameCount)
        {
            return Invariant($"the exporter wrote {statistics.WrittenCount} of {frameCount} packets.");
        }

        return null;
    }

    /// <summary>Writes the size CSV through a temporary file so an interrupted write leaves no truncated CSV.</summary>
    /// <param name="csvPath">Target path; missing directories are created, an existing file is replaced.</param>
    /// <param name="rows">The rows in configuration order.</param>
    private static void _WriteSizesCsv(string csvPath, List<SizeRow> rows)
    {
        string fullPath = Path.GetFullPath(csvPath);
        if (Path.GetDirectoryName(fullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = fullPath + ".tmp";
        using (StreamWriter writer = new(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.NewLine = "\n";
            writer.WriteLine(_CsvHeader);
            foreach (SizeRow row in rows)
            {
                writer.WriteLine(Invariant($"{row.Name},{row.Bytes}"));
            }
        }

        File.Move(temporary, fullPath, overwrite: true);
    }

    #endregion

    #region Comparing sizes

    /// <summary>Compares two size CSVs and writes a Markdown report.</summary>
    /// <param name="beforeCsv">The CSV of the baseline run.</param>
    /// <param name="afterCsv">The CSV of the run to judge.</param>
    /// <param name="reportPath">Markdown report; written whenever both inputs are valid, also when the gate fails.</param>
    /// <param name="log">Receives the summary line and the problem messages.</param>
    /// <returns>
    /// 0 when no configuration is larger or missing after; 1 when one is; 2
    /// on a malformed or unreadable input (the message names file and line) or an unwritable report.
    /// </returns>
    /// <remarks>
    /// A configuration only present after is listed as <c>new</c> and does not fail the gate: adding a measurement must not
    /// block a change. A configuration present before and absent after fails (<c>missing</c>), so a gate cannot be passed by
    /// dropping the row that grew. The rows are joined by name, never by position.
    /// </remarks>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    internal static int Compare(string beforeCsv, string afterCsv, string reportPath, TextWriter log)
    {
        ArgumentException.ThrowIfNullOrEmpty(beforeCsv);
        ArgumentException.ThrowIfNullOrEmpty(afterCsv);
        ArgumentException.ThrowIfNullOrEmpty(reportPath);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            if (!_TryReadSizes(beforeCsv, out List<SizeRow> before, out string? error)
                || !_TryReadSizes(afterCsv, out List<SizeRow> after, out error))
            {
                log.WriteLine(error);
                return _ExitInvalid;
            }

            List<Comparison> comparisons = _Join(before, after);
            _WriteReport(reportPath, beforeCsv, afterCsv, comparisons);

            int larger = _Count(comparisons, SizeStatus.Larger);
            int missing = _Count(comparisons, SizeStatus.Missing);
            int added = _Count(comparisons, SizeStatus.New);
            int smaller = _Count(comparisons, SizeStatus.Smaller);
            int unchanged = _Count(comparisons, SizeStatus.Same);
            log.WriteLine(
                Invariant($"Compared {comparisons.Count} configurations: {larger} larger, {missing} missing, {added} new, ")
                + Invariant($"{smaller} smaller, {unchanged} unchanged. Report: {reportPath}"));
            return larger == 0 && missing == 0
                ? _ExitPass
                : _ExitGateFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine(Invariant($"The size comparison failed: {ex.Message}"));
            return _ExitInvalid;
        }
    }

    /// <summary>
    /// Reads a size CSV strictly: a silently skipped row would let a broken run pass the gate.
    /// </summary>
    /// <param name="path">The CSV file.</param>
    /// <param name="rows">The rows in file order when the method returns true.</param>
    /// <param name="error">The problem, naming the file and the line, when the method returns false.</param>
    /// <returns><see langword="true"/> when the header and every row are valid.</returns>
    private static bool _TryReadSizes(string path, out List<SizeRow> rows, [NotNullWhen(false)] out string? error)
    {
        rows = [];
        if (!File.Exists(path))
        {
            error = Invariant($"File not found: {path}");
            return false;
        }

        // Line by line: the file is never held as one string. Names are unique; the join by name relies on it.
        using StreamReader reader = new(path, Encoding.UTF8);
        if (!string.Equals(reader.ReadLine(), _CsvHeader, StringComparison.Ordinal))
        {
            error = Invariant($"{path}:1: the first line must be the header '{_CsvHeader}'.");
            return false;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            int comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma <= 0)
            {
                error = Invariant($"{path}:{lineNumber}: expected '<config>,<bytes>' with a non-empty configuration name.");
                return false;
            }

            // NumberStyles.None: digits only, so a sign, a space, or a thousands separator is malformed, not silently accepted.
            if (!long.TryParse(line.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes))
            {
                error = Invariant($"{path}:{lineNumber}: the size is not a non-negative whole number of bytes.");
                return false;
            }

            string name = line[..comma];
            if (!names.Add(name))
            {
                error = Invariant($"{path}:{lineNumber}: the configuration '{name}' appears twice.");
                return false;
            }

            rows.Add(new SizeRow(name, bytes));
        }

        error = null;
        return true;
    }

    /// <summary>Joins the two runs by configuration name: every before row in before order, then the rows only present after.</summary>
    /// <param name="before">The baseline rows.</param>
    /// <param name="after">The rows to judge.</param>
    /// <returns>One comparison per configuration of either run.</returns>
    private static List<Comparison> _Join(List<SizeRow> before, List<SizeRow> after)
    {
        Dictionary<string, long> afterByName = new(after.Count, StringComparer.Ordinal);
        foreach (SizeRow row in after)
        {
            afterByName.Add(row.Name, row.Bytes);
        }

        HashSet<string> seen = new(before.Count, StringComparer.Ordinal);
        List<Comparison> comparisons = new(before.Count + after.Count);
        foreach (SizeRow row in before)
        {
            seen.Add(row.Name);
            if (!afterByName.TryGetValue(row.Name, out long afterBytes))
            {
                comparisons.Add(new Comparison(row.Name, row.Bytes, null, SizeStatus.Missing));
                continue;
            }

            SizeStatus status = afterBytes > row.Bytes
                ? SizeStatus.Larger
                : afterBytes < row.Bytes
                    ? SizeStatus.Smaller
                    : SizeStatus.Same;
            comparisons.Add(new Comparison(row.Name, row.Bytes, afterBytes, status));
        }

        foreach (SizeRow row in after)
        {
            if (!seen.Contains(row.Name))
            {
                comparisons.Add(new Comparison(row.Name, null, row.Bytes, SizeStatus.New));
            }
        }

        return comparisons;
    }

    /// <summary>Counts the comparisons with the given status.</summary>
    /// <param name="comparisons">The comparisons.</param>
    /// <param name="status">The status to count.</param>
    /// <returns>The number of comparisons with <paramref name="status"/>.</returns>
    private static int _Count(List<Comparison> comparisons, SizeStatus status)
    {
        int count = 0;
        foreach (Comparison comparison in comparisons)
        {
            if (comparison.Status == status)
            {
                count++;
            }
        }

        return count;
    }

    #endregion

    #region Report output

    /// <summary>Writes the Markdown report: one row per configuration with before, after, delta, relative delta, and status.</summary>
    /// <param name="reportPath">Target path; missing directories are created, an existing file is replaced.</param>
    /// <param name="beforeCsv">Path of the baseline CSV, named in the report.</param>
    /// <param name="afterCsv">Path of the judged CSV, named in the report.</param>
    /// <param name="comparisons">The rows to write.</param>
    private static void _WriteReport(string reportPath, string beforeCsv, string afterCsv, List<Comparison> comparisons)
    {
        string fullPath = Path.GetFullPath(reportPath);
        if (Path.GetDirectoryName(fullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        int larger = _Count(comparisons, SizeStatus.Larger);
        int missing = _Count(comparisons, SizeStatus.Missing);
        using StreamWriter writer = new(fullPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.WriteLine("# Export size comparison");
        writer.WriteLine();
        writer.WriteLine(Invariant($"Before: `{beforeCsv}` · After: `{afterCsv}`"));
        writer.WriteLine();
        writer.WriteLine(larger == 0 && missing == 0
            ? "Result: PASS — no configuration is larger or missing."
            : Invariant($"Result: FAIL — {larger} larger, {missing} missing."));
        writer.WriteLine();
        writer.WriteLine("| Configuration | Before (bytes) | After (bytes) | Delta (bytes) | Delta | Status |");
        writer.WriteLine("|---|---:|---:|---:|---:|---|");
        foreach (Comparison comparison in comparisons)
        {
            writer.WriteLine(_FormatRow(comparison));
        }
    }

    /// <summary>Formats one report row; a side that does not exist shows an em dash.</summary>
    /// <param name="comparison">The row to format.</param>
    /// <returns>The Markdown table row.</returns>
    private static string _FormatRow(Comparison comparison)
    {
        string before = comparison.Before is { } b
            ? Invariant($"{b}")
            : "—";
        string after = comparison.After is { } a
            ? Invariant($"{a}")
            : "—";
        string delta = "—";
        string percent = "—";
        if (comparison.Before is { } baseline && comparison.After is { } current)
        {
            long difference = current - baseline;
            delta = Invariant($"{difference:+0;-0;0}");

            // A baseline of 0 bytes has no relative delta; a sign is shown for any non-zero change, also when it rounds to 0.0.
            if (baseline == 0)
            {
                percent = difference == 0
                    ? "0.0 %"
                    : "n/a";
            }
            else
            {
                percent = Invariant($"{difference * 100.0 / baseline:+0.0;-0.0;0.0} %");
            }
        }

        string status = comparison.Status.ToString().ToLowerInvariant();
        return Invariant($"| {comparison.Name} | {before} | {after} | {delta} | {percent} | {status} |");
    }

    #endregion

    #region Types and helpers

    /// <summary>Wraps an exporter so the harness can feed it, read its statistics, and dispose it without casts.</summary>
    /// <typeparam name="TExporter">An exporter type that is a packet listener, reports errors, and is disposable.</typeparam>
    /// <param name="exporter">The exporter.</param>
    /// <returns>The handle over the exporter.</returns>
    private static ExporterHandle _Wrap<TExporter>(TExporter exporter)
        where TExporter : IPacketListener, IErrorTolerantExporter, IDisposable =>
        new(exporter, exporter, exporter);

    /// <summary>One measured configuration.</summary>
    /// <param name="Name">The CSV key; no comma.</param>
    /// <param name="FileName">The output file name inside the configuration's sub-directory; empty for a dataset directory.</param>
    /// <param name="Create">Builds a fresh exporter that writes to the given file or directory path.</param>
    private readonly record struct Configuration(string Name, string FileName, Func<string, ExporterHandle> Create);

    /// <summary>The three views of one exporter that the harness needs.</summary>
    /// <param name="Listener">Receives the packets and the finish call.</param>
    /// <param name="Errors">Reports the statistics and raises the skipped-item event.</param>
    /// <param name="Owner">Closes the output.</param>
    private readonly record struct ExporterHandle(IPacketListener Listener, IErrorTolerantExporter Errors, IDisposable Owner);

    /// <summary>One CSV row.</summary>
    /// <param name="Name">The configuration name.</param>
    /// <param name="Bytes">The total size in bytes, not negative.</param>
    private readonly record struct SizeRow(string Name, long Bytes);

    /// <summary>The verdict for one configuration.</summary>
    private enum SizeStatus
    {
        /// <summary>The size is equal.</summary>
        Same,

        /// <summary>The size shrank.</summary>
        Smaller,

        /// <summary>The size grew; fails the gate.</summary>
        Larger,

        /// <summary>The configuration exists before and not after; fails the gate.</summary>
        Missing,

        /// <summary>The configuration exists only after; informational.</summary>
        New,
    }

    /// <summary>One report row; a side is <see langword="null"/> when the configuration is absent there.</summary>
    /// <param name="Name">The configuration name.</param>
    /// <param name="Before">The baseline size, or <see langword="null"/> for <see cref="SizeStatus.New"/>.</param>
    /// <param name="After">The judged size, or <see langword="null"/> for <see cref="SizeStatus.Missing"/>.</param>
    /// <param name="Status">The verdict.</param>
    private readonly record struct Comparison(string Name, long? Before, long? After, SizeStatus Status);

    /// <summary>
    /// Collects the messages an exporter raises for skipped items while the export runs; the first message names the cause in
    /// the failure text. Raised on the caller's thread (the exporters' documented contract), so no synchronization is needed.
    /// </summary>
    private sealed class ErrorCapture
    {
        /// <summary>Number of skipped-item events raised so far.</summary>
        internal int Count { get; private set; }

        /// <summary>The message of the first event, with its item index; <see langword="null"/> before any event.</summary>
        internal string? First { get; private set; }

        /// <summary>Handler of <see cref="IErrorTolerantExporter.ItemSkipped"/>.</summary>
        /// <param name="sender">The exporter.</param>
        /// <param name="e">The skipped item.</param>
        internal void OnItemSkipped(object? sender, ExportErrorEventArgs e)
        {
            Count++;
            First ??= Invariant($"item {e.ItemIndex}: {e.Message}");
        }
    }

    #endregion
}
