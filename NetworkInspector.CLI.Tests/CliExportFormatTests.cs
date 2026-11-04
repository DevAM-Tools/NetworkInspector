// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Export and convert through registered formats: CSV, plugin kinds and formats, overwrite, and the frame/packet split.</summary>
internal sealed class CliExportFormatTests
{
    #region CSV

    [Test]
    public async Task CsvHasABomAHeaderAndTheDefaultColumns()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("out.csv");

        CliResult result = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", output);

        await Assert.That(result.Exit).IsEqualTo(0);
        byte[] bytes = File.ReadAllBytes(output);
        await Assert.That(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })).IsTrue();
        string[] lines = File.ReadAllLines(output);
        await Assert.That(lines.Length).IsEqualTo(21);
        await Assert.That(lines[0].TrimStart('\uFEFF')).IsEqualTo("No.,Time,Info,Length");
    }

    [Test]
    public async Task CsvOptionsChangeDelimiterHeaderBomAndColumns()
    {
        using CliSandbox sandbox = new();
        string plain = sandbox.Combine("plain.csv");
        string columns = sandbox.Combine("columns.csv");

        CliResult withOptions = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", plain, "-f", "csv:delimiter=;,header=false,bom=false");
        CliResult withColumns = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", columns, "-f", "csv:fields=ip.src;udp.dstport,text=false,repr=false");

        await Assert.That(withOptions.Exit).IsEqualTo(0);
        string[] plainLines = File.ReadAllLines(plain);
        await Assert.That(plainLines.Length).IsEqualTo(20);
        await Assert.That(plainLines.All(static line => line.Count(static c => c == ';') >= 3)).IsTrue();
        await Assert.That(File.ReadAllBytes(plain).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })).IsFalse();
        await Assert.That(withColumns.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(columns)[0].TrimStart('\uFEFF')).IsEqualTo("No.,Time,ip.src,udp.dstport");
    }

    [Test]
    public async Task ABadCsvOptionIsAUsageErrorWithOneLine()
    {
        using CliSandbox sandbox = new();

        CliResult delimiter = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("a.csv"), "-f", "csv:delimiter=ab");
        CliResult column = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("b.csv"), "-f", "csv:fields=nope.field");
        CliResult unknown = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("c.csv"), "-f", "csv:colour=red");
        CliResult retired = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("d.csv"), "-f", "csv:columns=ip.src");

        await Assert.That(delimiter.Exit).IsEqualTo(1);
        await Assert.That(delimiter.ErrorMessages).IsEquivalentTo(["Error: The delimiter must be exactly one character."], CollectionOrdering.Matching);
        await Assert.That(column.Exit).IsEqualTo(1);
        await Assert.That(column.ErrorMessages[0]).Contains("nope.field");
        await Assert.That(unknown.Exit).IsEqualTo(1);
        await Assert.That(unknown.ErrorMessages[0]).StartsWith("Error: Unknown option 'colour'; options: ");
        await Assert.That(File.Exists(sandbox.Combine("a.csv"))).IsFalse();

        // The former column list is gone; the message that lists the options names its replacement.
        await Assert.That(retired.Exit).IsEqualTo(1);
        await Assert.That(retired.ErrorMessages[0]).StartsWith("Error: Unknown option 'columns'; options: ");
        await Assert.That(retired.ErrorMessages[0]).Contains("fields");
    }

    #endregion

    #region Frame bytes

    /// <summary>
    /// Through the command line, JSON, Text, and CSV carry no frame bytes by default and one frame record per packet with
    /// <c>frames=true</c>; the CSV frame column comes last after the default columns.
    /// </summary>
    [Test]
    public async Task FrameBytesAreOffByDefaultAndOnWithTheFramesOptionInJsonTextAndCsv()
    {
        using CliSandbox sandbox = new();
        string jsonOff = sandbox.Combine("off.json");
        string jsonOn = sandbox.Combine("on.json");
        string textOff = sandbox.Combine("off.txt");
        string textOn = sandbox.Combine("on.txt");
        string csvOn = sandbox.Combine("on.csv");

        CliResult[] results =
        [
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", jsonOff),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", jsonOn, "-f", "json:frames=true"),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", textOff, "-f", "text"),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", textOn, "-f", "text:frames=true"),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", csvOn, "-f", "csv:frames=true"),
        ];

        await Assert.That(results.All(static result => result.Exit == 0)).IsTrue();
        await Assert.That(File.ReadAllText(jsonOff).Contains("frame_bytes", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(jsonOn).Split("frame_bytes").Length - 1).IsEqualTo(5);
        await Assert.That(File.ReadAllText(textOff).Contains("Frame bytes", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(textOn).Split("Frame bytes (").Length - 1).IsEqualTo(5);
        await Assert.That(File.ReadAllLines(csvOn)[0].TrimStart('\uFEFF')).IsEqualTo("No.,Time,Info,Length,frame_bytes");
    }

    /// <summary>
    /// A CSV long layout cannot hold the frame column, so <c>csv:shape=all,frames=true</c> exits with one usage error line and
    /// creates no file.
    /// </summary>
    [Test]
    public async Task TheFramesOptionOfCsvWithALongLayoutIsAUsageErrorWithOneLine()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("long.csv");

        CliResult result = await sandbox.RunAsync("export", "random:count=2", "-o", output, "-f", "csv:shape=all,frames=true");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: The FrameBytes column is only valid in packet rows and the selected-fields layout, not with the shape AllFields."],
            CollectionOrdering.Matching);
        await Assert.That(File.Exists(output)).IsFalse();
    }

    #endregion

    #region Additional buffers

    /// <summary>
    /// JSON, Text, and CSV accept <c>buffers=true</c> on a capture where no packet has an additional buffer: JSON and Text then
    /// write no buffer record, and the CSV adds the <c>buffers</c> column with an empty cell in every row.
    /// </summary>
    [Test]
    public async Task TheBuffersOptionIsAcceptedByTheJsonTextAndCsvFormatsAndAddsNothingWhenNoPacketHasABuffer()
    {
        using CliSandbox sandbox = new();
        string json = sandbox.Combine("buffers.json");
        string text = sandbox.Combine("buffers.txt");
        string csv = sandbox.Combine("buffers.csv");

        CliResult[] results =
        [
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", json, "-f", "json:buffers=true"),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", text, "-f", "text:buffers=true"),
            await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", csv, "-f", "csv:buffers=true"),
        ];

        // Random UDP frames are neither fragmented nor reassembled, so no packet has an additional buffer: the option is valid and
        // the JSON and the text have no buffer record, while the CSV has the column with an empty cell in every row.
        await Assert.That(results.All(static result => result.Exit == 0)).IsTrue();
        await Assert.That(File.ReadAllText(json).Contains("buffers", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(text).Contains("Buffer 1", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllLines(csv)[0].TrimStart('\uFEFF')).IsEqualTo("No.,Time,Info,Length,buffers");
    }

    /// <summary>
    /// The requirement end to end through the command line: a capture whose IPv4 datagram arrives in two fragments exports a buffer
    /// record with index 1 whose bytes are the reassembled datagram with `buffers=true`, and no buffer record without it.
    /// </summary>
    [Test]
    public async Task TheBuffersOptionExportsTheReassembledDatagramOfAFragmentedCaptureAndNothingWithoutIt()
    {
        using CliSandbox sandbox = new();
        string input = sandbox.Combine("fragments.pcapng");
        byte[] datagram = CliTraceWriter.WriteFragmentedUdpPcapng(input);
        string jsonOn = sandbox.Combine("on.json");
        string jsonOff = sandbox.Combine("off.json");
        string textOn = sandbox.Combine("on.txt");
        string csvOn = sandbox.Combine("on.csv");

        CliResult[] results =
        [
            await sandbox.RunAsync("export", input, "-o", jsonOn, "-f", "json:buffers=true,style=array"),
            await sandbox.RunAsync("export", input, "-o", jsonOff, "-f", "json:style=array"),
            await sandbox.RunAsync("export", input, "-o", textOn, "-f", "text:buffers=true"),
            await sandbox.RunAsync("export", input, "-o", csvOn, "-f", "csv:buffers=true"),
        ];

        await Assert.That(results.All(static result => result.Exit == 0)).IsTrue();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonOn));
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(2);
        await Assert.That(document.RootElement[0].TryGetProperty("buffers", out _)).IsFalse();
        JsonElement record = document.RootElement[1].GetProperty("buffers")[0];
        await Assert.That(record.GetProperty("index").GetInt32()).IsEqualTo(1);
        await Assert.That(record.GetProperty("bytes").GetString()).IsEqualTo(Convert.ToBase64String(datagram));
        await Assert.That(File.ReadAllText(jsonOff).Contains("buffers", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(textOn).Contains("Buffer 1 (32):", StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.ReadAllLines(csvOn)[2].EndsWith(",1:" + Convert.ToHexString(datagram), StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A CSV long layout cannot hold the buffers column, so <c>csv:shape=all,buffers=true</c> exits with one usage error line and
    /// creates no file.
    /// </summary>
    [Test]
    public async Task TheBuffersOptionOfCsvWithALongLayoutIsAUsageErrorWithOneLine()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("long_buffers.csv");

        CliResult result = await sandbox.RunAsync("export", "random:count=2", "-o", output, "-f", "csv:shape=all,buffers=true");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: The Buffers column is only valid in packet rows and the selected-fields layout, not with the shape AllFields."],
            CollectionOrdering.Matching);
        await Assert.That(File.Exists(output)).IsFalse();
    }

    #endregion

    #region DuckDB storage version

    /// <summary>
    /// The real command line: <c>storageVersion=v1.2.0</c> writes a file that DuckDB reports with that format, the default writes
    /// <c>v1.5.0</c>, and <c>storageVersion=v9</c> is a usage error that lists the six values and leaves no file.
    /// </summary>
    [Test]
    [Category("Slow")]
    public async Task TheStorageVersionOptionSelectsTheFileFormatAndABadValueIsAUsageErrorWithNoFile()
    {
        using CliSandbox sandbox = new();
        string older = sandbox.Combine("older.duckdb");
        string plain = sandbox.Combine("plain.duckdb");
        string bad = sandbox.Combine("bad.duckdb");

        CliResult olderResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", older, "-f", "duckdb:storageVersion=v1.2.0");
        CliResult plainResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", plain, "-f", "duckdb");
        CliResult badResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", bad, "-f", "duckdb:storageVersion=v9");

        await Assert.That(olderResult.Exit).IsEqualTo(0);
        await Assert.That(plainResult.Exit).IsEqualTo(0);
        await Assert.That(_ReportedStorageVersion(older)).IsEqualTo("v1.2.0+");
        await Assert.That(_ReportedStorageVersion(plain)).IsEqualTo("v1.5.0+");
        await Assert.That(badResult.Exit).IsEqualTo(1);
        await Assert.That(badResult.ErrorMessages).IsEquivalentTo(
            ["Error: storageVersion: allowed: v1.0.0, v1.1.0, v1.2.0, v1.3.0, v1.4.0, v1.5.0"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(bad)).IsFalse();
    }

    /// <summary>Reads the storage version tag DuckDB reports for a finished file (the oldest version with the file's format, plus <c>+</c>).</summary>
    /// <param name="path">The finished <c>.duckdb</c> file.</param>
    /// <returns>The reported tag text.</returns>
    /// <remarks>Sync: <c>ExportContentOptionsTests._ReportedStorageVersion</c> in NetworkInspector.Plugins.Tests (the same query).</remarks>
    private static string _ReportedStorageVersion(string path)
    {
        using DuckDB.NET.Data.DuckDBConnection connection = new($"Data Source={path};access_mode=READ_ONLY");
        connection.Open();
        using DuckDB.NET.Data.DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tags['storage_version'] FROM duckdb_databases() WHERE NOT internal";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!; // Invariant: the file is the only user database.
    }

    #endregion

    #region Parquet compression

    /// <summary>
    /// The real command line: <c>compression=snappy</c> writes SNAPPY column chunks, the default writes ZSTD, and
    /// <c>compression=lzma</c> is a usage error that lists the six codecs and creates no dataset.
    /// </summary>
    [Test]
    [Category("Slow")]
    public async Task TheCompressionOptionSelectsTheCodecAndABadValueIsAUsageErrorWithNoDataset()
    {
        using CliSandbox sandbox = new();
        string snappy = sandbox.Combine("snappy_parquet");
        string plain = sandbox.Combine("plain_parquet");
        string bad = sandbox.Combine("bad_parquet");

        CliResult snappyResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", snappy, "-f", "parquet:compression=snappy");
        CliResult plainResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", plain, "-f", "parquet");
        CliResult badResult = await sandbox.RunAsync("export", "random:count=5,seed=5,mode=udp4", "-o", bad, "-f", "parquet:compression=lzma");

        await Assert.That(snappyResult.Exit).IsEqualTo(0);
        await Assert.That(plainResult.Exit).IsEqualTo(0);
        await Assert.That(_ReportedCodecs(snappy)).IsEquivalentTo(["SNAPPY"], CollectionOrdering.Matching);
        await Assert.That(_ReportedCodecs(plain)).IsEquivalentTo(["ZSTD"], CollectionOrdering.Matching);
        await Assert.That(badResult.Exit).IsEqualTo(1);
        await Assert.That(badResult.ErrorMessages).IsEquivalentTo(
            ["Error: compression: allowed: none, snappy, gzip, brotli, lz4, zstd"], CollectionOrdering.Matching);
        await Assert.That(Directory.Exists(bad)).IsFalse();
    }

    /// <summary>Reads the distinct codecs DuckDB reports for every column chunk of every Parquet file of a dataset directory.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The codec names, sorted.</returns>
    /// <remarks>Sync: <c>ExportContentOptionsTests._ReportedCodecs</c> in NetworkInspector.Plugins.Tests (the same query).</remarks>
    private static string[] _ReportedCodecs(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories);
        string list = string.Join(", ", files.Select(static file => "'" + file.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'"));
        using DuckDB.NET.Data.DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDB.NET.Data.DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT compression FROM parquet_metadata([{list}]) ORDER BY 1";
        using DuckDB.NET.Data.DuckDBDataReader reader = command.ExecuteReader();
        List<string> codecs = [];
        while (reader.Read())
        {
            codecs.Add(reader.GetString(0));
        }

        return [.. codecs];
    }

    #endregion

    #region Plugin kinds and formats

    [Test]
    public async Task ThePluginSourceAndFormatWorkLikeBuiltInOnes()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        string udp = sandbox.Combine("udp.json");
        string tcp = sandbox.Combine("tcp.json");
        string text = sandbox.Combine("out.sampletxt");
        string noHeader = sandbox.Combine("noheader.sampletxt");

        CliResult udpRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", udp);
        CliResult tcpRun = await sandbox.RunAsync("export", "SampleGen:count=10,transport=tcp,port=41234", "-o", tcp);
        CliResult textRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", text, "-f", "SampleText");
        CliResult noHeaderRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", noHeader, "-f", "SampleText:includeHeader=false");

        await Assert.That(udpRun.Exit).IsEqualTo(0);
        ExportedPackets udpPackets = ExportedPackets.Read(udp);
        await Assert.That(udpPackets.Count).IsEqualTo(10);
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(udpPackets.Number(i, "sample.counter")).IsEqualTo((long)i);
        }

        await Assert.That(tcpRun.Exit).IsEqualTo(0);
        ExportedPackets tcpPackets = ExportedPackets.Read(tcp);
        await Assert.That(Enumerable.Range(0, tcpPackets.Count).All(i => tcpPackets.Number(i, "sample.counter") is not null)).IsTrue();
        await Assert.That(textRun.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(text).Length).IsEqualTo(11);
        await Assert.That(noHeaderRun.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(noHeader).Length).IsEqualTo(10);
    }

    [Test]
    public async Task WithoutThePluginTheKindIsUnknown()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", "SampleGen:count=1", "-o", sandbox.Combine("x.json"));

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: Unknown source kind 'SampleGen'. Known kinds: BLF, PCAPNG, PCAP, ASC, Random."], CollectionOrdering.Matching);
    }

    #endregion

    #region Overwrite

    [Test]
    public async Task AnExistingOutputIsRefusedUnlessOverwriteIsGiven()
    {
        using CliSandbox sandbox = new();
        string json = sandbox.Combine("out.json");
        string pcapng = sandbox.Combine("out.pcapng");
        File.WriteAllText(json, "keep");
        File.WriteAllText(pcapng, "keep");

        CliResult refused = await sandbox.RunAsync("export", "random:count=5", "-o", json);
        CliResult convertRefused = await sandbox.RunAsync("convert", "random:count=5", "-o", pcapng);
        string keptJson = File.ReadAllText(json);
        string keptPcapng = File.ReadAllText(pcapng);
        CliResult replaced = await sandbox.RunAsync("export", "random:count=5", "-o", json, "--overwrite");
        CliResult convertReplaced = await sandbox.RunAsync("convert", "random:count=5", "-o", pcapng, "--overwrite");

        await Assert.That(refused.Exit).IsEqualTo(1);
        await Assert.That(refused.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{json}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(convertRefused.Exit).IsEqualTo(1);
        await Assert.That(convertRefused.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{pcapng}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(keptJson).IsEqualTo("keep");
        await Assert.That(keptPcapng).IsEqualTo("keep");
        await Assert.That(replaced.Exit).IsEqualTo(0);
        await Assert.That(convertReplaced.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(json)).IsNotEqualTo("keep");
        await Assert.That(new FileInfo(pcapng).Length).IsGreaterThan(4L);
    }

    [Test]
    public async Task AnExistingFirstSplitFileIsTheOneNamedInTheOverwriteRefusal()
    {
        using CliSandbox sandbox = new();
        string exportFirst = sandbox.Combine("part_00001.json");
        string convertFirst = sandbox.Combine("part_00001.pcapng");
        File.WriteAllText(exportFirst, "keep");
        File.WriteAllText(convertFirst, "keep");

        CliResult export = await sandbox.RunAsync("export", "random:count=5", "-o", sandbox.Combine("part.json"), "--split-count", "2");
        CliResult convert = await sandbox.RunAsync("convert", "random:count=5", "-o", sandbox.Combine("part.pcapng"), "--split-count", "2");

        // The file that exists is the first split file, not the base name the user typed (which is never written in a split run).
        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(export.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{exportFirst}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(convert.Exit).IsEqualTo(1);
        await Assert.That(convert.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{convertFirst}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARefusedOverwriteRunKeepsThePreviousOutput()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("precious.csv");
        string converted = sandbox.Combine("precious.blf");
        File.WriteAllText(output, "my previous good export");
        File.WriteAllText(converted, "my previous good conversion");

        CliResult export = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", output, "-f", "csv:fields=nope.field",
            "--overwrite");
        CliResult convert = await sandbox.RunAsync("convert", "random:count=5,seed=1,mode=udp4", "-o", converted, "--output-format", "blf:compression=nope",
            "--overwrite");

        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(output)).IsEqualTo("my previous good export");
        await Assert.That(convert.Exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(converted)).IsEqualTo("my previous good conversion");
        await Assert.That(Directory.GetFiles(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task ASuccessfulOverwriteRunLeavesNoBackupBehind()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("out.csv");
        File.WriteAllText(output, "old");

        CliResult result = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", output, "--overwrite");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(output)).DoesNotContain("old");
        await Assert.That(Directory.GetFiles(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task OverwriteNeverDeletesAFolderThatNoExportWrote()
    {
        using CliSandbox sandbox = new();
        string folder = sandbox.Combine("important-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");

        CliResult refused = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", folder, "-f", "parquet", "--overwrite");

        await Assert.That(refused.Exit).IsEqualTo(1);
        await Assert.That(refused.Error).Contains("was not written by an export");
        await Assert.That(File.ReadAllText(Path.Combine(folder, "notes.txt"))).IsEqualTo("mine");
    }

    [Test]
    public async Task ADatasetWrittenByAnExportIsReplacedByTheNextOverwriteRun()
    {
        using CliSandbox sandbox = new();
        string folder = sandbox.Combine("dataset");

        CliResult first = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", folder, "-f", "parquet");
        CliResult second = await sandbox.RunAsync("export", "random:count=5,seed=2,mode=udp4", "-o", folder, "-f", "parquet", "--overwrite");

        await Assert.That(first.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(folder, ExportTargets.DatasetMarkerFileName))).IsTrue();
        await Assert.That(second.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(folder, ExportTargets.DatasetMarkerFileName))).IsTrue();
        await Assert.That(Directory.GetDirectories(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task ALeftoverSplitFileIsARuntimeErrorAndLeavesNoPartialOutput()
    {
        using CliSandbox sandbox = new();
        string leftover = sandbox.Combine("out_00002.json");
        File.WriteAllText(leftover, "keep");

        CliResult result = await sandbox.RunAsync("export", "random:count=5", "-o", sandbox.Combine("out.json"), "--split-count", "2");

        await Assert.That(result.Exit).IsEqualTo(3);
        await Assert.That(result.Error).Contains(leftover);
        await Assert.That(File.ReadAllText(leftover)).IsEqualTo("keep");
        await Assert.That(File.Exists(sandbox.Combine("out_00001.json"))).IsTrue();
        await Assert.That(File.Exists(sandbox.Combine("out_00003.json"))).IsFalse();
    }

    [Test]
    public async Task AnExportCancelledAfterTheFirstPacketDeletesTheOpenOutput()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("cancelled.json");
        using CancellationTokenSource cts = new();
        CancelAfterFirstPacketListener listener = new(output, cts);
        FrameInterfaceRegistry registry = new();
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, registry);
        builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        RandomFrameSource source = new(count: 100, seed: 42, mode: RandomFrameMode.UdpIPv4);
        source.Start(registry.RegisterSource(source), registry);
        List<IFrameSource> sources = [source];
        int packetId = 0;

        // The listener cancels from inside the first packet, after it wrote the output: the output is open at that moment.
        int count = ExportCommand.RunExportLoop(sources, _ => listener, new SplitOutputManager(output, maxSize: 0, maxCount: 0), stack,
            filter: null, maxPackets: 0, progressInterval: 0, tolerant: false, TextWriter.Null, ref packetId, cts.Token, out int outputsWritten, out _);

        await Assert.That(listener.WasOpenWhenCancelled).IsTrue();
        await Assert.That(outputsWritten).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsFalse();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task ACancelledExportRunReportsCancelledAndLeavesNoOutputFile()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("cancelled.json");
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        CliResult result = await sandbox.RunAsync(["export", "random:count=5", "-o", output], cts.Token);

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsFalse();
    }

    // Writes its output with the first packet, then cancels the run: the output is open and unfinished when the loop notices.
    private sealed class CancelAfterFirstPacketListener(string path, CancellationTokenSource cancel) : IPacketListener, IDisposable
    {
        public string UiName => "cancel after first packet";

        public string? Description => null;

        public bool WasOpenWhenCancelled { get; private set; }

        public bool OnPacket(Packet packet)
        {
            File.WriteAllText(path, "partial");
            WasOpenWhenCancelled = File.Exists(path);
            cancel.Cancel();
            return true;
        }

        public void OnFinish() => throw new InvalidOperationException("A cancelled export must not be finished.");

        public void Dispose()
        {
        }
    }

    #endregion

    #region CSV formula neutralization

    [Test]
    public async Task TheCsvFormulaNeutralizationOptionIsAcceptedOnTheCommandLineAndBadValuesAreRefused()
    {
        using CliSandbox sandbox = new();

        CliResult off = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("off.csv"), "-f", "csv:neutralizeFormulas=false");
        CliResult on = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("on.csv"), "-f", "csv:neutralizeFormulas=true");
        CliResult bad = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("bad.csv"), "-f", "csv:neutralizeFormulas=maybe");

        await Assert.That(off.Exit).IsEqualTo(0);
        await Assert.That(on.Exit).IsEqualTo(0);
        await Assert.That(bad.Exit).IsEqualTo(1);
        await Assert.That(bad.ErrorMessages.Length).IsEqualTo(1);
        await Assert.That(bad.ErrorMessages[0]).Contains("neutralizeFormulas");
        await Assert.That(File.Exists(sandbox.Combine("bad.csv"))).IsFalse();
    }

    #endregion

    #region Frame and packet formats

    [Test]
    public async Task AFormatOfTheOtherKindNamesTheRightCommand()
    {
        using CliSandbox sandbox = new();

        CliResult exportBlf = await sandbox.RunAsync("export", "random:count=1", "-o", sandbox.Combine("a.blf"), "-f", "blf");
        CliResult convertCsv = await sandbox.RunAsync("convert", "random:count=1", "-o", sandbox.Combine("a.csv"), "--output-format", "csv");
        CliResult exportBlfExtension = await sandbox.RunAsync("export", "random:count=1", "-o", sandbox.Combine("b.blf"));
        CliResult convertUnknownExtension = await sandbox.RunAsync("convert", "random:count=1", "-o", sandbox.Combine("c.unknownext"));

        await Assert.That(exportBlf.Exit).IsEqualTo(1);
        await Assert.That(exportBlf.ErrorMessages).IsEquivalentTo(["Error: Format 'BLF' writes frames; use 'ni convert'."], CollectionOrdering.Matching);
        await Assert.That(convertCsv.Exit).IsEqualTo(1);
        await Assert.That(convertCsv.ErrorMessages).IsEquivalentTo(["Error: Format 'CSV' writes packets; use 'ni export'."], CollectionOrdering.Matching);
        await Assert.That(exportBlfExtension.Exit).IsEqualTo(1);
        await Assert.That(exportBlfExtension.ErrorMessages[0]).Contains("Packet formats: CSV (.csv)");
        await Assert.That(convertUnknownExtension.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(sandbox.Combine("c.unknownext"))).IsTrue();
    }

    #endregion
}
