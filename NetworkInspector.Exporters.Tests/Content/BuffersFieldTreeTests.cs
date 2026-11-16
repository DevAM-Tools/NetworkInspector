// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The field tree is the leading content of an export: the frame bytes and the additional buffers add packet-level records and never
/// change a field row. Each test feeds the very same parsed packets to two exporters of one format, one with both members on and one
/// with both off, and compares everything that is not a frame or a buffer record (DuckDB and Parquet: every field table or file, the
/// topology, the catalog, the protocols; PBF: every block field except the frame and the buffers; JSON: every key of every packet
/// except the frame and the buffers; Text: every line outside the dumps).
/// </summary>
internal sealed class BuffersFieldTreeTests
{
    #region Exporting twice and comparing rows

    /// <summary>The capture, exported twice from one parse: with frames and buffers, and without either.</summary>
    /// <param name="scenario">The format and shape.</param>
    /// <param name="dir">The directory of the outputs; only DuckDB and a disk Parquet export need it.</param>
    /// <returns>The output with both members on, and the output with both off.</returns>
    private static (BufferRun On, BufferRun Off) _ExportTwice(BufferScenario scenario, TestDir? dir)
    {
        OpenBufferExport on = BufferExportScenarios.Open(scenario, BufferMembers.Frames | BufferMembers.Buffers, dir, "on");
        OpenBufferExport off = BufferExportScenarios.Open(scenario, BufferMembers.None, dir, "off");

        // One parse serves both exporters, so they see the same packets with the same ids.
        FeedResult fed = BufferExportScenarios.Feed(BufferTrace.Create(), on, off);
        (string onPath, string onText) = on.Finish();
        (string offPath, string offText) = off.Finish();
        return (
            new BufferRun(onPath, onText, fed.FirstPacketId, fed.Expected, fed.PacketCount, fed.PacketsWithFieldTree, on.Bytes, on.Target),
            new BufferRun(offPath, offText, fed.FirstPacketId, fed.Expected, fed.PacketCount, fed.PacketsWithFieldTree, off.Bytes, off.Target));
    }

    /// <summary>Asserts that two relations (SQL table expressions) hold the same rows: the difference is empty in both directions.</summary>
    /// <param name="connection">The open DuckDB connection that can read both relations.</param>
    /// <param name="left">The SQL table expression of the export with the members on.</param>
    /// <param name="right">The SQL table expression of the export with the members off.</param>
    /// <param name="label">The name that identifies the compared relation in a failure message.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertSameRowsAsync(DuckDBConnection connection, string left, string right, string label)
    {
        // EXCEPT ALL keeps the duplicates: a row that one side holds twice and the other once is a difference, not a match.
        long leftOnly = DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM (SELECT * FROM {left} EXCEPT ALL SELECT * FROM {right})"));
        long rightOnly = DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM (SELECT * FROM {right} EXCEPT ALL SELECT * FROM {left})"));
        long leftCount = DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM {left}"));
        long rightCount = DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM {right}"));

        await Assert.That(leftOnly).IsEqualTo(0L).Because(Invariant($"{label}: rows only with the members on"));
        await Assert.That(rightOnly).IsEqualTo(0L).Because(Invariant($"{label}: rows only with the members off"));
        await Assert.That(leftCount).IsEqualTo(rightCount).Because(Invariant($"{label}: row count"));
    }

    #endregion

    #region DuckDB and Parquet

    /// <summary>
    /// Proves that the DuckDB export holds the same field tables, topology, catalog, and protocol rows with and without frames and
    /// buffers, and that the buffer table is the only addition.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task DuckDbFieldTablesTopologyCatalogAndProtocolsAreTheSameWithAndWithoutFramesAndBuffers()
    {
        using TestDir dir = new("tree_leads_duckdb");
        (BufferRun on, BufferRun off) = _ExportTwice(BufferScenario.DuckDbHierarchy, dir);

        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using (DuckDBCommand attach = connection.CreateCommand())
        {
            attach.CommandText = Invariant($"ATTACH '{on.Path}' AS a (READ_ONLY); ATTACH '{off.Path}' AS b (READ_ONLY);");
            attach.ExecuteNonQuery();
        }

        List<string> tablesOn = DuckDbReadBack.Texts(
            connection, "SELECT table_name FROM information_schema.tables WHERE table_catalog = 'a' ORDER BY table_name");
        List<string> tablesOff = DuckDbReadBack.Texts(
            connection, "SELECT table_name FROM information_schema.tables WHERE table_catalog = 'b' ORDER BY table_name");

        // Only the buffer table is new; every other table exists on both sides.
        await Assert.That(tablesOn.Contains("packet_buffers")).IsTrue();
        await Assert.That(tablesOff.Contains("packet_buffers")).IsFalse();
        tablesOn.Remove("packet_buffers");
        await Assert.That(tablesOn).IsEquivalentTo(tablesOff, CollectionOrdering.Matching);
        await Assert.That(tablesOn.Count(static name => name.StartsWith("field_", StringComparison.Ordinal))).IsGreaterThan(5);

        // The packet rows differ by the frame column only: the other columns are the same.
        await _AssertSameRowsAsync(
            connection, "(SELECT packet_id, timestamp_ns, info FROM a.packets)", "(SELECT packet_id, timestamp_ns, info FROM b.packets)", "packets");
        foreach (string table in tablesOn.Where(static name => name != "packets"))
        {
            await _AssertSameRowsAsync(connection, Invariant($"a.main.{table}"), Invariant($"b.main.{table}"), table);
        }
    }

    /// <summary>
    /// Proves that the Parquet dataset holds the same field, topology, catalog, and protocol files with and without frames and
    /// buffers, and that the buffer file is the only addition.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task ParquetFieldFilesTopologyCatalogAndProtocolsAreTheSameWithAndWithoutFramesAndBuffers()
    {
        using TestDir dir = new("tree_leads_parquet");
        (BufferRun on, BufferRun off) = _ExportTwice(BufferScenario.ParquetHierarchy, dir);

        // The files of the dataset: the same names on both sides, except the buffer file that only the export with buffers has.
        List<string> filesOn = _DatasetFiles(on.Path);
        List<string> filesOff = _DatasetFiles(off.Path);
        await Assert.That(filesOn.Contains("packet_buffers.parquet")).IsTrue();
        await Assert.That(filesOff.Contains("packet_buffers.parquet")).IsFalse();
        filesOn.Remove("packet_buffers.parquet");
        await Assert.That(filesOn.Order(StringComparer.Ordinal)).IsEquivalentTo(filesOff.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(filesOn.Count(static name => name.StartsWith("fields", StringComparison.Ordinal))).IsGreaterThan(5);

        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        foreach (string file in filesOn)
        {
            string left = Invariant($"read_parquet('{Path.Combine(on.Path, file)}')");
            string right = Invariant($"read_parquet('{Path.Combine(off.Path, file)}')");
            if (file == "packets.parquet")
            {
                left = Invariant($"(SELECT packet_id, timestamp_ns, info FROM {left})");
                right = Invariant($"(SELECT packet_id, timestamp_ns, info FROM {right})");
            }

            await _AssertSameRowsAsync(connection, left, right, file);
        }
    }

    /// <summary>The relative paths of all Parquet files of a dataset directory.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The path of each Parquet file relative to the directory.</returns>
    private static List<string> _DatasetFiles(string directory) =>
        [.. Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(directory, path))];

    #endregion

    #region PBF

    /// <summary>Asserts that two field lists are the same entry by entry (number, value, and payload bytes).</summary>
    /// <param name="left">The fields of the export with the members on, without the member fields.</param>
    /// <param name="right">The fields of the export with the members off.</param>
    /// <param name="label">The name that identifies the compared message in a failure message.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertSameFieldsAsync(List<PbfField> left, List<PbfField> right, string label)
    {
        await Assert.That(left.Count).IsEqualTo(right.Count).Because(Invariant($"{label}: field count"));
        for (int i = 0; i < left.Count; i++)
        {
            await Assert.That(left[i].Number).IsEqualTo(right[i].Number).Because(Invariant($"{label}: number of entry {i}"));
            await Assert.That(left[i].Varint).IsEqualTo(right[i].Varint).Because(Invariant($"{label}: value of entry {i}"));
            await Assert.That(left[i].Payload
                    ?? [])
                .IsEquivalentTo(
                    right[i].Payload
                    ?? [],
                    CollectionOrdering.Matching)
                .Because(Invariant($"{label}: payload of entry {i}"));
        }
    }

    /// <summary>
    /// Proves that the columnar PBF blocks are byte-equal with and without frames and buffers once the frame-bytes and buffers
    /// columns are set aside.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PbfColumnarBlocksAreTheSameWithAndWithoutFramesAndBuffersExceptTheirOwnColumns()
    {
        (BufferRun on, BufferRun off) = _ExportTwice(BufferScenario.PbfColumnarHierarchy, dir: null);

        (_, List<List<PbfField>> blocksOn) = PbfFileScanner.FromBytes(on.Bytes!);
        (_, List<List<PbfField>> blocksOff) = PbfFileScanner.FromBytes(off.Bytes!);

        await Assert.That(blocksOn.Count).IsEqualTo(blocksOff.Count);
        for (int block = 0; block < blocksOn.Count; block++)
        {
            // The block fields 13 (frame bytes) and 16 (buffers) are the only ones that differ; all columns, the topology, and
            // the field columns are the same bytes.
            List<PbfField> withMembers = [.. blocksOn[block]];
            List<PbfField> fieldTree = [.. withMembers.Where(static f => f.Number is not (13 or 16))];
            await Assert.That(withMembers.Any(static f => f.Number == 13)).IsTrue();
            await Assert.That(withMembers.Any(static f => f.Number == 16)).IsTrue();
            await _AssertSameFieldsAsync(fieldTree, blocksOff[block], Invariant($"block {block}"));
        }
    }

    /// <summary>
    /// Proves that the standard PBF packet messages are byte-equal with and without frames and buffers once the frame-bytes and
    /// buffers fields are set aside.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PbfStandardPacketsAreTheSameWithAndWithoutFramesAndBuffersExceptTheirOwnFields()
    {
        (BufferRun on, BufferRun off) = _ExportTwice(BufferScenario.PbfStandardHierarchy, dir: null);

        List<List<PbfField>> packetsOn = _Packets(on.Bytes!);
        List<List<PbfField>> packetsOff = _Packets(off.Bytes!);

        await Assert.That(packetsOn.Count).IsEqualTo(packetsOff.Count);
        await Assert.That(packetsOn.Count).IsEqualTo(7);
        for (int packet = 0; packet < packetsOn.Count; packet++)
        {
            // The packet fields 6 (frame bytes) and 7 (buffers) are the only ones that differ; the packet header and every field
            // record are the same bytes.
            List<PbfField> fieldTree = [.. packetsOn[packet].Where(static f => f.Number is not (6 or 7))];
            await Assert.That(packetsOn[packet].Any(static f => f.Number == 6)).IsTrue();
            await _AssertSameFieldsAsync(fieldTree, packetsOff[packet], Invariant($"packet {packet}"));
        }
    }

    /// <summary>The packet messages (the length-delimited field 5) of every block of a standard PBF file, in order.</summary>
    /// <param name="file">The bytes of the PBF file.</param>
    /// <returns>The decoded fields of each packet message.</returns>
    private static List<List<PbfField>> _Packets(byte[] file)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.FromBytes(file);
        return [.. blocks.SelectMany(static block => block)
            .Where(static f => f.Number == 5 && f.Payload is not null)
            .Select(static f => PbfFileScanner.Fields(f.Payload!))];
    }

    #endregion

    #region JSON and Text

    /// <summary>Every key of every packet object with its raw JSON text, without the frame and the buffers keys.</summary>
    /// <param name="json">The JSON export, an array of packet objects.</param>
    /// <returns>One comma-joined <c>key:value</c> text per packet.</returns>
    private static List<string> _PacketsWithoutByteKeys(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(static packet => string.Join(
            ",",
            packet.EnumerateObject()
                .Where(static property => property.Name is not ("frame_bytes" or "buffers"))
                .Select(static property => property.Name + ":" + property.Value.GetRawText())))];
    }

    /// <summary>
    /// Proves that every packet object of the JSON export carries the same keys and values with and without frames and buffers once
    /// the frame and buffers keys are set aside.
    /// </summary>
    /// <param name="scenario">The JSON layout under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.JsonPrettyTree)]
    [Arguments(BufferScenario.JsonArrayTree)]
    [Arguments(BufferScenario.JsonCompactTree)]
    public async Task JsonPacketsAreTheSameWithAndWithoutFramesAndBuffersExceptTheirOwnKeys(BufferScenario scenario)
    {
        (BufferRun on, BufferRun off) = _ExportTwice(scenario, dir: null);

        List<string> withMembers = _PacketsWithoutByteKeys(on.Text);
        List<string> without = _PacketsWithoutByteKeys(off.Text);

        await Assert.That(on.Text.Contains("\"buffers\":", StringComparison.Ordinal) || on.Text.Contains("\"buffers\": ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(off.Text.Contains("\"buffers\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(withMembers.Count).IsEqualTo(7);
        await Assert.That(withMembers).IsEquivalentTo(without, CollectionOrdering.Matching);
    }

    /// <summary>Proves that the Text export has the same lines outside the frame and buffer dumps with and without those members.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TextLinesOutsideTheDumpsAreTheSameWithAndWithoutFramesAndBuffers()
    {
        (BufferRun on, BufferRun off) = _ExportTwice(BufferScenario.TextTree, dir: null);

        List<string> withMembers = _LinesWithoutDumps(on.Text);
        List<string> without = _LinesWithoutDumps(off.Text);

        await Assert.That(on.Text.Contains("Buffer 1 (", StringComparison.Ordinal)).IsTrue();
        await Assert.That(on.Text.Contains("Frame bytes (", StringComparison.Ordinal)).IsTrue();
        await Assert.That(withMembers.Count).IsGreaterThan(50);
        await Assert.That(withMembers).IsEquivalentTo(without, CollectionOrdering.Matching);
    }

    /// <summary>The lines of a text export without the title and the dump lines of the frame and buffer blocks.</summary>
    /// <param name="text">The text export.</param>
    /// <returns>The lines that are not part of a frame or buffer dump.</returns>
    private static List<string> _LinesWithoutDumps(string text)
    {
        List<string> lines = [];
        bool inDump = false;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("Frame bytes (", StringComparison.Ordinal) || line.StartsWith("Buffer ", StringComparison.Ordinal))
            {
                inDump = true;
                continue;
            }

            if (inDump && BufferReaders.IsDumpLine(line))
            {
                continue;
            }

            inDump = false;
            lines.Add(line);
        }

        return lines;
    }

    #endregion
}
