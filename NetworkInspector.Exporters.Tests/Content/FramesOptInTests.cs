// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Numbers = NetworkInspector.Exporters.Tests.Helpers.TextPresenceReaders.PbfNumbers;

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The frame bytes are an opt-in of every packet exporter: no format writes them by default, and with the member on every format
/// and shape writes the exact frame of every packet (an empty frame is an empty value, not an absent one). Each format is read
/// back with its own reader, which never shares code with the writer, so a change of the writer fails a test instead of the
/// reader following it.
/// </summary>
internal sealed class FramesOptInTests
{
    #region Scenarios

    /// <summary>One exporter configuration of the matrix: a format with a shape (and a JSON style or a CSV layout).</summary>
    public enum Scenario
    {
        /// <summary>A DuckDB database with the hierarchy shape.</summary>
        DuckDbHierarchy,

        /// <summary>A DuckDB database with the all-fields shape.</summary>
        DuckDbAllFields,

        /// <summary>A DuckDB database with the selected-fields shape.</summary>
        DuckDbSelectedFields,

        /// <summary>A Parquet dataset with the hierarchy shape.</summary>
        ParquetHierarchy,

        /// <summary>A Parquet dataset with the all-fields shape.</summary>
        ParquetAllFields,

        /// <summary>A Parquet dataset with the selected-fields shape.</summary>
        ParquetSelectedFields,

        /// <summary>A standard-layout PBF file with the hierarchy shape.</summary>
        PbfStandardHierarchy,

        /// <summary>A standard-layout PBF file with the all-fields shape.</summary>
        PbfStandardAllFields,

        /// <summary>A standard-layout PBF file with the selected-fields shape.</summary>
        PbfStandardSelectedFields,

        /// <summary>A columnar-layout PBF file with the hierarchy shape.</summary>
        PbfColumnarHierarchy,

        /// <summary>A columnar-layout PBF file with the all-fields shape.</summary>
        PbfColumnarAllFields,

        /// <summary>A columnar-layout PBF file with the selected-fields shape.</summary>
        PbfColumnarSelectedFields,

        /// <summary>JSON in the pretty-printed style with the hierarchy shape.</summary>
        JsonPrettyTree,

        /// <summary>JSON in the array style with the hierarchy shape.</summary>
        JsonArrayTree,

        /// <summary>JSON in the compact style with the hierarchy shape.</summary>
        JsonCompactTree,

        /// <summary>JSON in the array style with the selected-fields shape.</summary>
        JsonSelectedFields,

        /// <summary>JSON in the array style with the all-fields shape.</summary>
        JsonAllFields,

        /// <summary>CSV with the packet columns only (no field selection).</summary>
        CsvPacketRows,

        /// <summary>CSV with the packet columns followed by the selected field columns.</summary>
        CsvWide,

        /// <summary>Text with the hierarchy shape.</summary>
        TextTree,

        /// <summary>Text with the selected-fields shape.</summary>
        TextSelectedFields,

        /// <summary>Text with the all-fields shape.</summary>
        TextAllFields,
    }

    /// <summary>How the frame member of the content is set for an export.</summary>
    private enum FrameMode
    {
        /// <summary>The member is never mentioned: a plain builder where the shape allows it, otherwise a content that leaves it at its default.</summary>
        Default,

        /// <summary>The member is on (CSV: the column).</summary>
        On,
    }

    /// <summary>The shape of a scenario (the CSV packet rows have no content; they report the hierarchy shape).</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <returns>The export shape the scenario uses.</returns>
    private static ExportShape _ShapeOf(Scenario scenario) =>
        scenario switch
        {
            Scenario.DuckDbAllFields or Scenario.ParquetAllFields or Scenario.PbfStandardAllFields or Scenario.PbfColumnarAllFields
                or Scenario.JsonAllFields or Scenario.TextAllFields => ExportShape.AllFields,
            Scenario.DuckDbSelectedFields or Scenario.ParquetSelectedFields or Scenario.PbfStandardSelectedFields
                or Scenario.PbfColumnarSelectedFields or Scenario.JsonSelectedFields or Scenario.CsvWide
                or Scenario.TextSelectedFields => ExportShape.SelectedFields,
            _ => ExportShape.Hierarchy,
        };

    /// <summary>
    /// The three frames of the matrix: 60 bytes, 1514 bytes (a full Ethernet frame), and 0 bytes. The last one is the boundary: a
    /// packet whose frame is empty must be written, with an empty value.
    /// </summary>
    /// <returns>The three frames.</returns>
    private static List<Frame> _Frames() =>
    [
        TestHarness.CreateFrame(0, 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(18)),
        TestHarness.CreateFrame(1, 2_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(1472)),
        TestHarness.CreateFrame(2, 3_000_000, []),
    ];

    /// <summary>The content of a scenario with a field selection where the shape needs one; the frame member is left at its default.</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <returns>The content for the scenario's shape.</returns>
    private static ExportContent _ContentOf(Scenario scenario)
    {
        ExportShape shape = _ShapeOf(scenario);
        return shape == ExportShape.SelectedFields
            ? ExportContents.Selected(new FieldRequest("udp.dstport"))
            : new ExportContent { Shape = shape };
    }

    #endregion

    #region Export

    /// <summary>
    /// Exports the frames with the scenario's exporter, finishes it, and returns what it wrote: a path for the file formats, a text
    /// for the stream formats.
    /// </summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <param name="frames">The frames to export.</param>
    /// <param name="mode">Whether the frame member is on.</param>
    /// <param name="dir">The directory for file outputs.</param>
    /// <returns>The path (file or directory) and the text of a stream format (empty for the file formats).</returns>
    private static (string Path, string Text) _Export(Scenario scenario, IReadOnlyList<Frame> frames, FrameMode mode, TestDir dir)
    {
        bool on = mode == FrameMode.On;
        bool plain = mode == FrameMode.Default && _ShapeOf(scenario) == ExportShape.Hierarchy;
        ExportContent content = _ContentOf(scenario);
        if (on)
        {
            content = content with { FrameBytes = true };
        }

        string name = Invariant($"{scenario}_{mode}");
        switch (scenario)
        {
            case Scenario.DuckDbHierarchy:
            case Scenario.DuckDbAllFields:
            case Scenario.DuckDbSelectedFields:
            {
                string path = dir.FilePath(name + ".duckdb");
                DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder().ToFile(path).WithWriteMode(ExportWriteMode.Synchronous);
                using DuckDbExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                _ = TreeFreeFeed.Run(exporter, frames);
                exporter.OnFinish();
                return (path, string.Empty);
            }

            case Scenario.ParquetHierarchy:
            case Scenario.ParquetAllFields:
            case Scenario.ParquetSelectedFields:
            {
                string path = dir.FilePath(name);
                ParquetExporter.Builder builder = ParquetExporter.CreateBuilder().ToDirectory(path).WithWriteMode(ExportWriteMode.Synchronous);
                using ParquetExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                _ = TreeFreeFeed.Run(exporter, frames);
                exporter.OnFinish();
                return (path, string.Empty);
            }

            case Scenario.PbfStandardHierarchy:
            case Scenario.PbfStandardAllFields:
            case Scenario.PbfStandardSelectedFields:
            case Scenario.PbfColumnarHierarchy:
            case Scenario.PbfColumnarAllFields:
            case Scenario.PbfColumnarSelectedFields:
            {
                string path = dir.FilePath(name + ".pbf");
                PbfExportFormat format = scenario is Scenario.PbfStandardHierarchy or Scenario.PbfStandardAllFields or Scenario.PbfStandardSelectedFields
                    ? PbfExportFormat.Standard
                    : PbfExportFormat.Columnar;
                PbfExporter.Builder builder = PbfExporter.CreateBuilder().ToFile(path).WithFormat(format).WithCompressed(false)
                    .WithWriteMode(ExportWriteMode.Synchronous);
                using PbfExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                _ = TreeFreeFeed.Run(exporter, frames);
                exporter.OnFinish();
                return (path, string.Empty);
            }

            case Scenario.CsvPacketRows:
            case Scenario.CsvWide:
            {
                using MemoryStream stream = new();
                CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false);
                if (scenario == Scenario.CsvWide)
                {
                    // CSV keeps packet-level data in columns: its content has neither info nor frame bytes, and the frame is the
                    // column added below.
                    builder.WithContent(content with { Info = false, FrameBytes = false })
                        .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Timestamp);
                }
                else if (on)
                {
                    builder.WithColumn(CsvColumnKind.PacketNumber);
                }

                if (on)
                {
                    builder.WithColumn(CsvColumnKind.FrameBytes);
                }

                using (CsvExporter exporter = builder.Build())
                {
                    _ = TreeFreeFeed.Run(exporter, frames);
                    exporter.OnFinish();
                }

                return (string.Empty, Encoding.UTF8.GetString(stream.ToArray()));
            }

            case Scenario.TextTree:
            case Scenario.TextSelectedFields:
            case Scenario.TextAllFields:
            {
                using MemoryStream stream = new();
                TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(stream);
                using (TextExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build())
                {
                    _ = TreeFreeFeed.Run(exporter, frames);
                    exporter.OnFinish();
                }

                return (string.Empty, Encoding.UTF8.GetString(stream.ToArray()));
            }

            default:
            {
                JsonExportFormat format = scenario switch
                {
                    Scenario.JsonPrettyTree => JsonExportFormat.Pretty,
                    Scenario.JsonCompactTree => JsonExportFormat.Compact,
                    _ => JsonExportFormat.Array,
                };
                using MemoryStream stream = new();
                JsonExporter.Builder builder = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format);
                using (JsonExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build())
                {
                    _ = TreeFreeFeed.Run(exporter, frames);
                    exporter.OnFinish();
                }

                return (string.Empty, Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

    #endregion

    #region Readers

    /// <summary>
    /// Reads the frame of every packet back with the format's own reader.
    /// </summary>
    /// <param name="scenario">The exporter configuration that wrote the output.</param>
    /// <param name="output">What the export wrote.</param>
    /// <returns>
    /// <see langword="null"/> when the output has no frame content at all (no column, key, field, record, or block); otherwise one
    /// entry per packet: its frame, or <see langword="null"/> for a packet whose frame is missing or NULL.
    /// </returns>
    private static List<byte[]?>? _Read(Scenario scenario, (string Path, string Text) output)
    {
        switch (scenario)
        {
            case Scenario.DuckDbHierarchy:
            case Scenario.DuckDbAllFields:
            case Scenario.DuckDbSelectedFields:
                return _ReadDuckDb(output.Path);
            case Scenario.ParquetHierarchy:
            case Scenario.ParquetAllFields:
            case Scenario.ParquetSelectedFields:
                return _ReadParquet(output.Path);
            case Scenario.PbfStandardHierarchy:
            case Scenario.PbfStandardAllFields:
            case Scenario.PbfStandardSelectedFields:
                return _ReadPbfStandard(output.Path);
            case Scenario.PbfColumnarHierarchy:
            case Scenario.PbfColumnarAllFields:
            case Scenario.PbfColumnarSelectedFields:
                return _ReadPbfColumnar(output.Path);
            case Scenario.CsvPacketRows:
            case Scenario.CsvWide:
                return _ReadCsv(output.Text);
            case Scenario.TextTree:
            case Scenario.TextSelectedFields:
            case Scenario.TextAllFields:
                return _ReadText(output.Text);
            default:
                return _ReadJson(output.Text);
        }
    }

    /// <summary>The BLOB of the first column of every row of a query, in row order; a NULL is <see langword="null"/>.</summary>
    /// <param name="connection">The open DuckDB connection.</param>
    /// <param name="sql">The query whose first column is a BLOB.</param>
    /// <returns>One entry per row: the bytes, or <see langword="null"/> for a NULL.</returns>
    private static List<byte[]?> _ReadBlobs(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        List<byte[]?> rows = [];
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                rows.Add(null);
                continue;
            }

            using Stream stream = reader.GetStream(0);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            rows.Add(copy.ToArray());
        }

        return rows;
    }

    /// <summary>Reads <c>packets.frame_bytes</c> of a DuckDB export; <see langword="null"/> when the table has no such column.</summary>
    /// <param name="path">The DuckDB database file.</param>
    /// <returns>One entry per packet in packet-id order, or <see langword="null"/> when there is no frame column.</returns>
    private static List<byte[]?>? _ReadDuckDb(string path)
    {
        if (!TextPresenceReaders.DuckDbHasColumn(path, "packets", "frame_bytes"))
        {
            return null;
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        return _ReadBlobs(connection, "SELECT frame_bytes FROM packets ORDER BY packet_id");
    }

    /// <summary>Reads <c>frame_bytes</c> of <c>packets.parquet</c> through an in-memory DuckDB; <see langword="null"/> without the column.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>One entry per packet in packet-id order, or <see langword="null"/> when there is no frame column.</returns>
    private static List<byte[]?>? _ReadParquet(string directory)
    {
        string file = Path.Combine(directory, "packets.parquet");
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using (DuckDBCommand schema = connection.CreateCommand())
        {
            schema.CommandText = Invariant($"SELECT COUNT(*) FROM parquet_schema('{file}') WHERE name = 'frame_bytes'");
            if (Convert.ToInt64(schema.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        return _ReadBlobs(connection, Invariant($"SELECT frame_bytes FROM read_parquet('{file}') ORDER BY packet_id"));
    }

    /// <summary>The packet messages of every standard block of a PBF file.</summary>
    /// <param name="path">The PBF file.</param>
    /// <returns>The decoded fields of each packet message.</returns>
    private static List<List<PbfField>> _StandardPackets(string path)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<List<PbfField>> packets = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField field in block)
            {
                // A packet is the length-delimited field 5; the header field 5 (maximum timestamp) is a varint.
                if (field.Number == Numbers.PacketField && field.Payload is not null)
                {
                    packets.Add(PbfFileScanner.Fields(field.Payload));
                }
            }
        }

        return packets;
    }

    /// <summary>Reads the packet field 6 of every packet of a standard PBF file; <see langword="null"/> when no packet has it.</summary>
    /// <param name="path">The PBF file.</param>
    /// <returns>One entry per packet, or <see langword="null"/> when no packet has a frame field.</returns>
    private static List<byte[]?>? _ReadPbfStandard(string path)
    {
        List<byte[]?> frames = [];
        bool any = false;
        foreach (List<PbfField> packet in _StandardPackets(path))
        {
            PbfField? frame = null;
            foreach (PbfField field in packet)
            {
                if (field.Number == Numbers.PacketFrameBytes)
                {
                    frame = field;
                }
            }

            any |= frame is not null;
            frames.Add(frame?.Payload);
        }

        return any
            ? frames
            : null;
    }

    /// <summary>Reads the frame column (13) of every columnar block of a PBF file; <see langword="null"/> when no block has it.</summary>
    /// <param name="path">The PBF file.</param>
    /// <returns>One entry per packet in block order, or <see langword="null"/> when no block has a frame column.</returns>
    private static List<byte[]?>? _ReadPbfColumnar(string path)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<byte[]?> frames = [];
        bool any = false;
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField column in block)
            {
                if (column.Number != Numbers.ColumnarFrameBytes || column.Payload is null)
                {
                    continue;
                }

                any = true;
                foreach (PbfField row in PbfFileScanner.Fields(column.Payload))
                {
                    frames.Add(row.Payload);
                }
            }
        }

        return any
            ? frames
            : null;
    }

    /// <summary>Reads the <c>frame_bytes</c> key of every packet object; <see langword="null"/> when no packet has it.</summary>
    /// <param name="text">The JSON export, an array of packet objects.</param>
    /// <returns>One entry per packet, or <see langword="null"/> when no packet has the key.</returns>
    private static List<byte[]?>? _ReadJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        List<byte[]?> frames = [];
        bool any = false;
        foreach (JsonElement packet in document.RootElement.EnumerateArray())
        {
            if (packet.TryGetProperty("frame_bytes", out JsonElement value))
            {
                any = true;
                frames.Add(Convert.FromBase64String(value.GetString()!));
            }
            else
            {
                frames.Add(null);
            }
        }

        return any
            ? frames
            : null;
    }

    /// <summary>
    /// Reads the <c>frame_bytes</c> cell of every row of a CSV text written with the packet columns <c>No.</c>, <c>Time</c> (wide only),
    /// and <c>frame_bytes</c> first; <see langword="null"/> when the header has no such column.
    /// </summary>
    /// <param name="text">The CSV export.</param>
    /// <returns>One entry per data row, or <see langword="null"/> when the header has no frame column.</returns>
    private static List<byte[]?>? _ReadCsv(string text)
    {
        string[] rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] header = rows[0].Split(',');
        int column = Array.IndexOf(header, "frame_bytes");
        if (column < 0)
        {
            return null;
        }

        // The packet columns come first and hold no comma, so the cells up to the frame column split safely; the field columns
        // after them (texts) may hold commas and are not read.
        List<byte[]?> frames = [];
        foreach (string row in rows.Skip(1))
        {
            frames.Add(Convert.FromHexString(row.Split(',', column + 2)[column]));
        }

        return frames;
    }

    /// <summary>
    /// Reads the <c>Frame bytes (n):</c> block that follows the lines of every packet: the title line, then one line per 16 bytes
    /// (offset, two spaces, hex pairs). Checks the count in the title against the bytes it read.
    /// </summary>
    /// <param name="text">The Text export.</param>
    /// <returns>The frames in packet order; <see langword="null"/> when the text has no such block.</returns>
    /// <exception cref="InvalidOperationException">The count in a title differs from the number of bytes the dump holds.</exception>
    private static List<byte[]?>? _ReadText(string text)
    {
        string[] lines = text.Split('\n');
        List<byte[]?> frames = [];
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Frame bytes (", StringComparison.Ordinal))
            {
                continue;
            }

            int declared = int.Parse(lines[i].AsSpan("Frame bytes (".Length, lines[i].IndexOf(')', StringComparison.Ordinal) - "Frame bytes (".Length),
                CultureInfo.InvariantCulture);
            List<byte> bytes = [];
            while (i + 1 < lines.Length && lines[i + 1].Length > 6 && lines[i + 1].IndexOf("  ", StringComparison.Ordinal) >= 4
                && lines[i + 1].IndexOf("  ", StringComparison.Ordinal) <= 8 && !lines[i + 1].StartsWith("Packet ", StringComparison.Ordinal))
            {
                i++;
                int split = lines[i].IndexOf("  ", StringComparison.Ordinal);
                bytes.AddRange(Convert.FromHexString(lines[i][(split + 2)..].Replace(" ", string.Empty, StringComparison.Ordinal)));
            }

            if (declared != bytes.Count)
            {
                throw new InvalidOperationException(Invariant($"The title says {declared} bytes, the dump holds {bytes.Count}."));
            }

            frames.Add(bytes.ToArray());
        }

        return frames.Count == 0
            ? null
            : frames;
    }

    #endregion

    #region Defaults

    /// <summary>Proves that the frame member is off in a plain content and in the default content of every exporter.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheFrameMemberIsOffInEveryContentAndEveryExporterDefault()
    {
        await Assert.That(new ExportContent().FrameBytes).IsFalse();
        await Assert.That(DuckDbExporter.DefaultContent.FrameBytes).IsFalse();
        await Assert.That(ParquetExporter.DefaultContent.FrameBytes).IsFalse();
        await Assert.That(PbfExporter.DefaultContent.FrameBytes).IsFalse();
        await Assert.That(JsonExporter.DefaultContent.FrameBytes).IsFalse();
        await Assert.That(TextExporter.DefaultContent.FrameBytes).IsFalse();
        await Assert.That(CsvExporter.ContentTemplate.FrameBytes).IsFalse();
    }

    /// <summary>Proves that PBF, JSON, CSV, and Text write no frame bytes unless the frame member is on.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.PbfStandardHierarchy)]
    [Arguments(Scenario.PbfStandardAllFields)]
    [Arguments(Scenario.PbfStandardSelectedFields)]
    [Arguments(Scenario.PbfColumnarHierarchy)]
    [Arguments(Scenario.PbfColumnarAllFields)]
    [Arguments(Scenario.PbfColumnarSelectedFields)]
    [Arguments(Scenario.JsonPrettyTree)]
    [Arguments(Scenario.JsonArrayTree)]
    [Arguments(Scenario.JsonCompactTree)]
    [Arguments(Scenario.JsonSelectedFields)]
    [Arguments(Scenario.JsonAllFields)]
    [Arguments(Scenario.CsvPacketRows)]
    [Arguments(Scenario.CsvWide)]
    [Arguments(Scenario.TextTree)]
    [Arguments(Scenario.TextSelectedFields)]
    [Arguments(Scenario.TextAllFields)]
    public async Task NoFrameBytesAreWrittenByDefaultInTheFastFormats(Scenario scenario)
    {
        using TestDir dir = new("frames_default");

        List<byte[]?>? frames = _Read(scenario, _Export(scenario, _Frames(), FrameMode.Default, dir));

        await Assert.That(frames).IsNull();
    }

    /// <summary>Proves that DuckDB and Parquet write no frame column unless the frame member is on.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(Scenario.DuckDbHierarchy)]
    [Arguments(Scenario.DuckDbAllFields)]
    [Arguments(Scenario.DuckDbSelectedFields)]
    [Arguments(Scenario.ParquetHierarchy)]
    [Arguments(Scenario.ParquetAllFields)]
    [Arguments(Scenario.ParquetSelectedFields)]
    public async Task NoFrameBytesAreWrittenByDefaultInDuckDbAndParquet(Scenario scenario)
    {
        using TestDir dir = new("frames_default_db");

        List<byte[]?>? frames = _Read(scenario, _Export(scenario, _Frames(), FrameMode.Default, dir));

        await Assert.That(frames).IsNull();
    }

    #endregion

    #region Frames on

    /// <summary>Compares what a reader found with the frames that were exported, packet by packet.</summary>
    /// <param name="read">The frames the reader found.</param>
    /// <param name="frames">The frames that were exported.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertFramesAsync(List<byte[]?>? read, List<Frame> frames)
    {
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            // An empty frame must come back as an empty value, never as a missing one.
            await Assert.That(read[i]).IsNotNull();
            await Assert.That(read[i]!).IsEquivalentTo(frames[i].Data.ToArray(), CollectionOrdering.Matching);
        }
    }

    /// <summary>Proves that PBF, JSON, CSV, and Text with the frame member on write the exact frame of every packet, even an empty one.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.PbfStandardHierarchy)]
    [Arguments(Scenario.PbfStandardAllFields)]
    [Arguments(Scenario.PbfStandardSelectedFields)]
    [Arguments(Scenario.PbfColumnarHierarchy)]
    [Arguments(Scenario.PbfColumnarAllFields)]
    [Arguments(Scenario.PbfColumnarSelectedFields)]
    [Arguments(Scenario.JsonPrettyTree)]
    [Arguments(Scenario.JsonArrayTree)]
    [Arguments(Scenario.JsonCompactTree)]
    [Arguments(Scenario.JsonSelectedFields)]
    [Arguments(Scenario.JsonAllFields)]
    [Arguments(Scenario.CsvPacketRows)]
    [Arguments(Scenario.CsvWide)]
    [Arguments(Scenario.TextTree)]
    [Arguments(Scenario.TextSelectedFields)]
    [Arguments(Scenario.TextAllFields)]
    public async Task FrameBytesOnWriteTheExactFrameOfEveryPacketInTheFastFormats(Scenario scenario)
    {
        using TestDir dir = new("frames_on");
        List<Frame> frames = _Frames();

        List<byte[]?>? read = _Read(scenario, _Export(scenario, frames, FrameMode.On, dir));

        await _AssertFramesAsync(read, frames);
    }

    /// <summary>Proves that DuckDB and Parquet with the frame member on write the exact frame of every packet, even an empty one.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(Scenario.DuckDbHierarchy)]
    [Arguments(Scenario.DuckDbAllFields)]
    [Arguments(Scenario.DuckDbSelectedFields)]
    [Arguments(Scenario.ParquetHierarchy)]
    [Arguments(Scenario.ParquetAllFields)]
    [Arguments(Scenario.ParquetSelectedFields)]
    public async Task FrameBytesOnWriteTheExactFrameOfEveryPacketInDuckDbAndParquet(Scenario scenario)
    {
        using TestDir dir = new("frames_on_db");
        List<Frame> frames = _Frames();

        List<byte[]?>? read = _Read(scenario, _Export(scenario, frames, FrameMode.On, dir));

        await _AssertFramesAsync(read, frames);
    }

    /// <summary>
    /// The formats that compress against the previous packet (the compact JSON style and the PBF standard layout) must write
    /// every frame in full: a frame is never "same as the previous one".
    /// </summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.JsonCompactTree)]
    [Arguments(Scenario.PbfStandardHierarchy)]
    [Arguments(Scenario.PbfColumnarHierarchy)]
    public async Task IdenticalFramesAreEachWrittenInFull(Scenario scenario)
    {
        using TestDir dir = new("frames_identical");
        byte[] bytes = FrameGenerators.BuildEthernetIpv4UdpFrame(18);
        List<Frame> frames =
        [
            TestHarness.CreateFrame(0, 1_000_000, bytes),
            TestHarness.CreateFrame(1, 2_000_000, bytes),
            TestHarness.CreateFrame(2, 3_000_000, bytes),
        ];

        List<byte[]?>? read = _Read(scenario, _Export(scenario, frames, FrameMode.On, dir));

        await _AssertFramesAsync(read, frames);
    }

    /// <summary>Proves that the JSON frame is written as standard Base64 with padding, not the URL-safe alphabet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheJsonFrameIsStandardBase64WithPadding()
    {
        using TestDir dir = new("frames_json_text");
        byte[] frame = [0xFB, 0xFF, 0x00, 0x01];
        List<Frame> frames = [TestHarness.CreateFrame(0, 1_000_000, frame)];

        string json = _Export(Scenario.JsonArrayTree, frames, FrameMode.On, dir).Text;
        using JsonDocument document = JsonDocument.Parse(json);

        // "+/" are the standard alphabet's 62nd and 63rd characters (the URL-safe form would write "-_"), and four bytes need "=" padding.
        await Assert.That(document.RootElement[0].GetProperty("frame_bytes").GetString()).IsEqualTo("+/8AAQ==");
    }

    /// <summary>Proves that the CSV frame cell is upper-case hexadecimal without separators.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheCsvFrameIsUpperCaseHexWithoutSeparators()
    {
        using TestDir dir = new("frames_csv_hex");
        List<Frame> frames = [TestHarness.CreateFrame(0, 1_000_000, [0xAB, 0x00, 0x0F, 0xFF])];

        string csv = _Export(Scenario.CsvPacketRows, frames, FrameMode.On, dir).Text;

        // The packet number is the dense id of the test thread's parse, so only the header and the end of the row are fixed.
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("No.,frame_bytes");
        await Assert.That(lines[1].EndsWith(",AB000FFF", StringComparison.Ordinal)).IsTrue();
    }

    #endregion
}
