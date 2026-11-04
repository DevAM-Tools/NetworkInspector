// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Numbers = NetworkInspector.Exporters.Tests.Helpers.TextPresenceReaders.PbfNumbers;
using Texts = NetworkInspector.Exporters.Tests.Helpers.TextProbePacketFactory.Texts;

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The presence rule of the custom texts in every packet exporter and shape: a field's custom text and a value's custom
/// representation are written only when a dissector set one, an absent text is absence in the format's own way (NULL, no field, an
/// absent-run entry, no key, an empty unquoted cell), a set but empty text is distinguishable from it (<c>''</c>, a zero-length
/// field, an entry with an empty string, <c>""</c>), and a switched-off text is not written at all. The same packet is exported
/// through a field tree and through a tree-free parse; both feeds must give the same texts.
/// </summary>
internal sealed class TextPresenceTests
{
    #region Scenarios, exports, and read-back

    /// <summary>One exporter configuration of the matrix: a format with a shape (and a JSON style).</summary>
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

        /// <summary>CSV in the wide layout: one row per packet, the selected fields as columns.</summary>
        CsvWide,

        /// <summary>CSV in the long layout: one row per field value of every packet.</summary>
        CsvLong,

        /// <summary>CSV with the hierarchy shape: one row per node.</summary>
        CsvHierarchy,

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
    }

    /// <summary>What an export left behind: its path (file or directory) and, for the stream formats, its text.</summary>
    /// <param name="Path">The output file or directory; empty for the stream formats.</param>
    /// <param name="Text">The output of a stream format (CSV, JSON); empty for the file formats.</param>
    private sealed record Output(string Path, string Text);

    /// <summary>The shape of a scenario.</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <returns>The export shape the scenario uses.</returns>
    private static ExportShape _ShapeOf(Scenario scenario) =>
        scenario switch
        {
            Scenario.DuckDbHierarchy or Scenario.ParquetHierarchy or Scenario.PbfStandardHierarchy
                or Scenario.PbfColumnarHierarchy or Scenario.CsvHierarchy or Scenario.JsonPrettyTree
                or Scenario.JsonArrayTree or Scenario.JsonCompactTree => ExportShape.Hierarchy,
            Scenario.DuckDbAllFields or Scenario.ParquetAllFields or Scenario.PbfStandardAllFields
                or Scenario.PbfColumnarAllFields or Scenario.CsvLong or Scenario.JsonAllFields => ExportShape.AllFields,
            _ => ExportShape.SelectedFields,
        };

    /// <summary>
    /// Exports the probe packet with the scenario's exporter and the given switches, finishes the exporter, and returns what it wrote.
    /// </summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <param name="probe">The probe factory (its stack parsed the packet).</param>
    /// <param name="dir">The directory for file outputs.</param>
    /// <param name="text">Whether custom texts are written.</param>
    /// <param name="representation">Whether custom representations are written.</param>
    /// <param name="treeFree">Whether the packet is parsed tree-free with the exporter's own demand.</param>
    /// <returns>The output.</returns>
    private static Output _Export(Scenario scenario, TextProbePacketFactory probe, TestDir dir, bool text, bool representation, bool treeFree)
    {
        ExportContent content = probe.FlatContent(_ShapeOf(scenario), text, representation);
        string name = Invariant($"{scenario}_{treeFree}_{text}_{representation}");
        switch (scenario)
        {
            case Scenario.DuckDbHierarchy:
            case Scenario.DuckDbAllFields:
            case Scenario.DuckDbSelectedFields:
            {
                string path = dir.FilePath(name + ".duckdb");
                using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path).WithContent(content)
                    .WithWriteMode(ExportWriteMode.Synchronous).Build();
                probe.Feed(exporter, treeFree);
                exporter.OnFinish();
                return new Output(path, string.Empty);
            }

            case Scenario.ParquetHierarchy:
            case Scenario.ParquetAllFields:
            case Scenario.ParquetSelectedFields:
            {
                string path = dir.FilePath(name);
                using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToDirectory(path).WithContent(content)
                    .WithWriteMode(ExportWriteMode.Synchronous).Build();
                probe.Feed(exporter, treeFree);
                exporter.OnFinish();
                return new Output(path, string.Empty);
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
                using PbfExporter exporter = PbfExporter.CreateBuilder().ToFile(path).WithFormat(format).WithCompressed(false)
                    .WithContent(content).WithWriteMode(ExportWriteMode.Synchronous).Build();
                probe.Feed(exporter, treeFree);
                exporter.OnFinish();
                return new Output(path, string.Empty);
            }

            case Scenario.CsvWide:
            case Scenario.CsvLong:
            case Scenario.CsvHierarchy:
            {
                using MemoryStream stream = new();
                using (CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithContent(content).Build())
                {
                    probe.Feed(exporter, treeFree);
                    exporter.OnFinish();
                }

                return new Output(string.Empty, Encoding.UTF8.GetString(stream.ToArray()));
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
                using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format).WithContent(content).Build())
                {
                    probe.Feed(exporter, treeFree);
                    exporter.OnFinish();
                }

                return new Output(string.Empty, Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

    /// <summary>Reads the probe texts back from the output of a scenario with the format's own reader.</summary>
    /// <param name="scenario">The exporter configuration that wrote the output.</param>
    /// <param name="output">What the export wrote.</param>
    /// <param name="probe">The probe factory.</param>
    /// <returns>The texts per probe field name.</returns>
    private static Dictionary<string, Texts> _Read(Scenario scenario, Output output, TextProbePacketFactory probe)
    {
        switch (scenario)
        {
            case Scenario.DuckDbHierarchy:
            case Scenario.DuckDbAllFields:
            case Scenario.DuckDbSelectedFields:
                return TextPresenceReaders.ReadDuckDb(output.Path, probe);
            case Scenario.ParquetHierarchy:
            case Scenario.ParquetAllFields:
            case Scenario.ParquetSelectedFields:
                return TextPresenceReaders.ReadParquet(output.Path, probe);
            case Scenario.PbfStandardHierarchy:
            case Scenario.PbfStandardAllFields:
            case Scenario.PbfStandardSelectedFields:
                return TextPresenceReaders.ReadPbfStandard(output.Path, probe);
            case Scenario.PbfColumnarHierarchy:
            case Scenario.PbfColumnarAllFields:
            case Scenario.PbfColumnarSelectedFields:
                return TextPresenceReaders.ReadPbfColumnar(output.Path, probe);
            case Scenario.CsvWide:
                return TextPresenceReaders.ReadCsvWide(output.Text);
            case Scenario.CsvLong:
            case Scenario.CsvHierarchy:
                return TextPresenceReaders.ReadCsvRows(output.Text);
            case Scenario.JsonPrettyTree:
                return TextPresenceReaders.ReadJson(output.Text, probe, JsonExportFormat.Pretty, ExportShape.Hierarchy);
            case Scenario.JsonArrayTree:
                return TextPresenceReaders.ReadJson(output.Text, probe, JsonExportFormat.Array, ExportShape.Hierarchy);
            case Scenario.JsonCompactTree:
                return TextPresenceReaders.ReadJson(output.Text, probe, JsonExportFormat.Compact, ExportShape.Hierarchy);
            case Scenario.JsonSelectedFields:
                return TextPresenceReaders.ReadJson(output.Text, probe, JsonExportFormat.Array, ExportShape.SelectedFields);
            default:
                return TextPresenceReaders.ReadJson(output.Text, probe, JsonExportFormat.Array, ExportShape.AllFields);
        }
    }

    /// <summary>Renders a text for a failure message: absent is <c>∅</c>, a text is quoted.</summary>
    /// <param name="text">The text, or <see langword="null"/> when it is absent.</param>
    /// <returns>The rendered text.</returns>
    private static string _Quote(string? text) =>
        text is null
            ? "∅"
            : Invariant($"\"{text}\"");

    /// <summary>Renders the texts of all probe fields in one line per field, so one assertion shows every difference.</summary>
    /// <param name="texts">The texts per probe field name.</param>
    /// <returns>One line per probe field.</returns>
    private static string _Render(IReadOnlyDictionary<string, Texts> texts)
    {
        StringBuilder builder = new();
        foreach (string name in TextProbePacketFactory.FieldNames)
        {
            if (texts.TryGetValue(name, out Texts value))
            {
                builder.Append(name).Append(": repr=").Append(_Quote(value.Representation)).Append(" text=").Append(_Quote(value.Text)).Append('\n');
            }
            else
            {
                builder.Append(name).Append(": (no row)\n");
            }
        }

        return builder.ToString();
    }

    #endregion

    #region Presence

    /// <summary>
    /// Proves that PBF, CSV, and JSON write no text for a field whose dissector set none and the dissector's own text for the others,
    /// and that the field-tree feed and the tree-free feed give the same texts.
    /// </summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.PbfStandardHierarchy)]
    [Arguments(Scenario.PbfStandardAllFields)]
    [Arguments(Scenario.PbfStandardSelectedFields)]
    [Arguments(Scenario.PbfColumnarHierarchy)]
    [Arguments(Scenario.PbfColumnarAllFields)]
    [Arguments(Scenario.PbfColumnarSelectedFields)]
    [Arguments(Scenario.CsvWide)]
    [Arguments(Scenario.CsvLong)]
    [Arguments(Scenario.CsvHierarchy)]
    [Arguments(Scenario.JsonPrettyTree)]
    [Arguments(Scenario.JsonArrayTree)]
    [Arguments(Scenario.JsonCompactTree)]
    [Arguments(Scenario.JsonSelectedFields)]
    [Arguments(Scenario.JsonAllFields)]
    public async Task AbsentTextsAreAbsenceAndSetTextsAreTheirTextInEveryFastFormat(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_presence");

        Dictionary<string, Texts> tree = _Read(scenario, _Export(scenario, probe, dir, text: true, representation: true, treeFree: false), probe);
        Dictionary<string, Texts> free = _Read(scenario, _Export(scenario, probe, dir, text: true, representation: true, treeFree: true), probe);

        // The expectation is the dissector's own setting: nothing is replaced by a default text, nothing is invented for an absent
        // one, and an empty text stays distinguishable from an absent one in the same packet.
        await Assert.That(_Render(tree)).IsEqualTo(_Render(TextProbePacketFactory.Expected));

        // Both feeds (a field tree and a tree-free parse with the exporter's observer) give the same texts.
        await Assert.That(_Render(free)).IsEqualTo(_Render(tree));
    }

    /// <summary>
    /// Proves that DuckDB and Parquet store NULL for a field whose dissector set no text and the dissector's own text for the others,
    /// and that the field-tree feed and the tree-free feed give the same texts.
    /// </summary>
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
    public async Task AbsentTextsAreNullAndSetTextsAreTheirTextInDuckDbAndParquet(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_presence_columnar");

        Dictionary<string, Texts> tree = _Read(scenario, _Export(scenario, probe, dir, text: true, representation: true, treeFree: false), probe);
        Dictionary<string, Texts> free = _Read(scenario, _Export(scenario, probe, dir, text: true, representation: true, treeFree: true), probe);

        await Assert.That(_Render(tree)).IsEqualTo(_Render(TextProbePacketFactory.Expected));
        await Assert.That(_Render(free)).IsEqualTo(_Render(tree));
    }

    /// <summary>Proves that the DuckDB topology table holds a text only for the layer container, and NULL for a group and a value node.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheDuckDbTopologyTextBelongsToTheNodeWithoutAValueAndIsNullForTheOthers()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_presence_topology");

        Output output = _Export(Scenario.DuckDbHierarchy, probe, dir, text: true, representation: true, treeFree: false);

        // The layer container has a text; the group container (no value, no text) and a value node (its text is in its field table)
        // have none.
        await Assert.That(TextPresenceReaders.ReadDuckDbTopologyText(output.Path, probe.IdOf("txp").Value)).IsEqualTo(TextProbePacketFactory.LayerText);
        await Assert.That(TextPresenceReaders.ReadDuckDbTopologyText(output.Path, probe.IdOf("txp.group").Value)).IsNull();
        await Assert.That(TextPresenceReaders.ReadDuckDbTopologyText(output.Path, probe.IdOf("txp.text").Value)).IsNull();
    }

    /// <summary>Proves that the JSON tree of a real UDP packet has a custom text only for the field whose dissector set one.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheRealUdpPacketWritesATextOnlyForTheFieldThatHasOne()
    {
        // udp.srcport has no custom text; udp.checksum has one (the real dissector sets it). The JSON tree writes the key only for
        // the checksum; the text of the source port is not invented from its value.
        Packet packet = PacketGenerators.CreateEthernetUdpPackets(1)[0];
        using MemoryStream stream = new();
        using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Array).Build())
        {
            await Assert.That(exporter.OnPacket(packet)).IsTrue();
            exporter.OnFinish();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        Dictionary<string, JsonElement> byName = [];
        _Collect(document.RootElement[0].GetProperty("fields"), byName);

        await Assert.That(byName["udp.srcport"].TryGetProperty("custom_text", out _)).IsFalse();
        await Assert.That(byName["udp.checksum"].TryGetProperty("custom_text", out _)).IsTrue();
    }

    /// <summary>Proves that a real UDP packet in DuckDB has a NULL custom text for the source port and the dissector's text for the checksum.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheRealUdpPacketHasNullInDuckDbForTheSourcePortAndTheTextForTheChecksum()
    {
        Packet packet = PacketGenerators.CreateEthernetUdpPackets(1)[0];
        using TestDir dir = new("text_presence_udp_duckdb");
        string path = dir.FilePath("udp.duckdb");
        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path)
            .WithContent(new ExportContent { Shape = ExportShape.Hierarchy, Info = false, FrameBytes = false })
            .WithWriteMode(ExportWriteMode.Synchronous).Build())
        {
            await Assert.That(exporter.OnPacket(packet)).IsTrue();
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        string?[] texts = new string?[2];
        string[] names = ["udp.srcport", "udp.checksum"];
        for (int i = 0; i < names.Length; i++)
        {
            int id = packet.Stack.GetFieldId(names[i])!.Value.Value;
            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText = Invariant($"SELECT custom_text FROM field_{id.ToString(CultureInfo.InvariantCulture)}");
            using DuckDBDataReader reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            texts[i] = reader.IsDBNull(0)
                ? null
                : reader.GetString(0);
        }

        await Assert.That(texts[0]).IsNull();
        await Assert.That(texts[1]).IsEqualTo("0x0000");
    }

    /// <summary>Collects every field object of a JSON tree by its <c>name</c>.</summary>
    /// <param name="fields">The array of field objects; nested <c>children</c> arrays are visited too.</param>
    /// <param name="byName">Receives the first field object found for each name.</param>
    private static void _Collect(JsonElement fields, Dictionary<string, JsonElement> byName)
    {
        foreach (JsonElement field in fields.EnumerateArray())
        {
            if (field.TryGetProperty("name", out JsonElement name))
            {
                byName.TryAdd(name.GetString()!, field);
            }

            if (field.TryGetProperty("children", out JsonElement children))
            {
                _Collect(children, byName);
            }
        }
    }

    #endregion

    #region Rows of several packets

    /// <summary>Exports three probe packets (texts, no texts, texts again) with a PBF exporter of the given layout.</summary>
    /// <param name="probe">The probe factory.</param>
    /// <param name="dir">The directory of the output file.</param>
    /// <param name="format">The PBF layout.</param>
    /// <returns>The path of the finished file.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused a probe packet.</exception>
    private static string _ExportThreeProbePackets(TextProbePacketFactory probe, TestDir dir, PbfExportFormat format)
    {
        string path = dir.FilePath(Invariant($"three_{format}.pbf"));
        ExportContent content = probe.FlatContent(ExportShape.AllFields);
        using PbfExporter exporter = PbfExporter.CreateBuilder().ToFile(path).WithFormat(format).WithCompressed(false)
            .WithContent(content).WithWriteMode(ExportWriteMode.Synchronous).Build();
        foreach (bool omit in new[] { false, true, false })
        {
            if (!exporter.OnPacket(probe.CreateTreePacket(omit)))
            {
                throw new InvalidOperationException("The PBF exporter refused a probe packet.");
            }
        }

        exporter.OnFinish();
        return path;
    }

    /// <summary>
    /// Proves that a PBF columnar text column holds one entry per row, with absent entries between present ones, and that a field
    /// whose dissector never sets a text has no column.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APbfColumnarColumnHoldsOneEntryPerRowWithAbsentRowsBetweenThePresentOnes()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_rows_pbf_columnar");

        string path = _ExportThreeProbePackets(probe, dir, PbfExportFormat.Columnar);
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        Dictionary<string, string> rows = [];
        Dictionary<string, bool> hasTextColumn = [];
        foreach (PbfField columnField in blocks.SelectMany(static block => block).Where(static field => field.Number == Numbers.ColumnarFieldColumns))
        {
            List<PbfField> column = PbfFileScanner.Fields(columnField.Payload!);
            string? name = probe.Stack.GetField(new FieldId(checked((int)PbfFileScanner.Value(column, Numbers.ColumnFieldId))))?.Name;
            if (name is "txp.text" or "txp.empty" or "txp.plain")
            {
                hasTextColumn[name] = column.Exists(static field => field.Number == Numbers.ColumnCustomTexts);
                string?[] texts = column.Find(static field => field.Number == Numbers.ColumnCustomTexts) is { Payload: not null } found
                    ? PbfFileScanner.DecodeTextColumn(found.Payload)
                    : [null, null, null];
                rows[name] = string.Join("|", texts.Select(static row => _Quote(row)));
            }
        }

        // txp.text: text, no text, text. txp.empty: empty text, no text, empty text. txp.plain never has a text: no column at all.
        await Assert.That(rows["txp.text"]).IsEqualTo("\"Length: 8 bytes\"|∅|\"Length: 8 bytes\"");
        await Assert.That(rows["txp.empty"]).IsEqualTo("\"\"|∅|\"\"");
        await Assert.That(rows["txp.plain"]).IsEqualTo("∅|∅|∅");
        await Assert.That(hasTextColumn["txp.plain"]).IsFalse();
        await Assert.That(hasTextColumn["txp.text"]).IsTrue();
        await Assert.That(hasTextColumn["txp.empty"]).IsTrue();
    }

    /// <summary>Proves that a PBF standard field record has a text field only in packets whose dissector set a text, never a same-flag.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APbfStandardRecordHasATextFieldOnlyInThePacketsWhoseDissectorSetOne()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_rows_pbf_standard");

        string path = _ExportThreeProbePackets(probe, dir, PbfExportFormat.Standard);
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        int textFieldId = probe.IdOf("txp.text").Value;
        List<string> perPacket = [];
        IEnumerable<PbfField> packets = blocks.SelectMany(static block => block)
            .Where(static field => field.Number == Numbers.PacketField && field.Payload is not null);
        foreach (PbfField packet in packets)
        {
            foreach (PbfField record in PbfFileScanner.Fields(packet.Payload!).Where(static field => field.Number == Numbers.PacketField))
            {
                List<PbfField> fields = PbfFileScanner.Fields(record.Payload!);
                if (checked((int)PbfFileScanner.Value(fields, Numbers.FieldFieldId)) != textFieldId)
                {
                    continue;
                }

                // A text equal to the previous occurrence's is replaced by its same-flag, so a reader restores it from there.
                ulong flags = PbfFileScanner.Value(fields, Numbers.FieldSameFlags);
                bool sameText = flags != ulong.MaxValue && (flags & SameFlags.FieldSameCustomText) != 0;
                string? text = fields.Find(static field => field.Number == Numbers.FieldCustomText) is { Payload: not null } found
                    ? Encoding.UTF8.GetString(found.Payload)
                    : null;
                perPacket.Add(sameText
                    ? "same"
                    : _Quote(text));
            }
        }

        // Packet 2 has no text: no field 7 and no same-flag (an absent text is never flagged). Packet 3 writes its text again,
        // because the previous occurrence (packet 2) had none.
        await Assert.That(string.Join("|", perPacket)).IsEqualTo("\"Length: 8 bytes\"|∅|\"Length: 8 bytes\"");
    }

    #endregion

    #region Empty versus absent

    /// <summary>Proves that CSV writes an empty text as a quoted empty cell and an absent text as an empty unquoted cell.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnEmptyTextIsAQuotedEmptyCsvCellAndAnAbsentTextIsAnEmptyUnquotedCell()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_empty_csv");

        foreach (Scenario scenario in new[] { Scenario.CsvWide, Scenario.CsvLong, Scenario.CsvHierarchy })
        {
            Output output = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false);
            Dictionary<string, TextPresenceReaders.CsvCell[]> cells = _CsvTextCells(scenario, output.Text);

            // txp.empty has an empty text and an empty representation: the cell is ""; txp.plain has none: nothing between the delimiters.
            await Assert.That(cells["txp.empty"][0]).IsEqualTo(new TextPresenceReaders.CsvCell(string.Empty, Quoted: true));
            await Assert.That(cells["txp.empty"][1]).IsEqualTo(new TextPresenceReaders.CsvCell(string.Empty, Quoted: true));
            await Assert.That(cells["txp.plain"][0]).IsEqualTo(new TextPresenceReaders.CsvCell(string.Empty, Quoted: false));
            await Assert.That(cells["txp.plain"][1]).IsEqualTo(new TextPresenceReaders.CsvCell(string.Empty, Quoted: false));
        }
    }

    /// <summary>The representation and text cells (as written, with their quoting) of every probe field of a CSV output.</summary>
    /// <param name="scenario">The CSV layout that wrote the output.</param>
    /// <param name="csv">The CSV text.</param>
    /// <returns>For each probe field name, the representation cell and then the text cell.</returns>
    private static Dictionary<string, TextPresenceReaders.CsvCell[]> _CsvTextCells(Scenario scenario, string csv)
    {
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        List<TextPresenceReaders.CsvCell> header = TextPresenceReaders.SplitCsv(lines[0]);
        Dictionary<string, TextPresenceReaders.CsvCell[]> result = [];
        if (scenario == Scenario.CsvWide)
        {
            List<TextPresenceReaders.CsvCell> row = TextPresenceReaders.SplitCsv(lines[1]);
            foreach (string name in TextProbePacketFactory.FieldNames)
            {
                result[name] =
                [
                    row[header.FindIndex(cell => cell.Text == name + ".repr")],
                    row[header.FindIndex(cell => cell.Text == name + ".text")],
                ];
            }

            return result;
        }

        int fieldColumn = header.FindIndex(static cell => cell.Text == "field");
        int reprColumn = header.FindIndex(static cell => cell.Text == "custom_repr");
        int textColumn = header.FindIndex(static cell => cell.Text == "custom_text");
        foreach (string line in lines.Skip(1))
        {
            List<TextPresenceReaders.CsvCell> row = TextPresenceReaders.SplitCsv(line);
            string name = row[fieldColumn].Text;
            if (TextProbePacketFactory.Expected.ContainsKey(name))
            {
                result[name] = [row[reprColumn], row[textColumn]];
            }
        }

        return result;
    }

    /// <summary>Proves the documented limit that a wide CSV cell joining several occurrences cannot tell an empty text from an absent one.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AWideCsvCellThatJoinsSeveralOccurrencesCannotTellEmptyFromAbsent()
    {
        using TextProbePacketFactory probe = new();
        using MemoryStream stream = new();
        ExportContent content = probe.FlatContent(ExportShape.SelectedFields, names: [TextProbePacketFactory.DuplicateFieldName]);
        using (CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithContent(content).Build())
        {
            probe.Feed(exporter, treeFree: false);
            exporter.OnFinish();
        }

        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        List<TextPresenceReaders.CsvCell> header = TextPresenceReaders.SplitCsv(lines[0]);
        List<TextPresenceReaders.CsvCell> row = TextPresenceReaders.SplitCsv(lines[1]);

        // Occurrence 1 has an empty text, occurrence 2 has none: the joined cell is the bare separator, the same as for two absent
        // texts. This is the documented limit of the wide layout; the test pins it so a change is a decision, not an accident.
        await Assert.That(row[header.FindIndex(static cell => cell.Text == "txp.dup")].Text).IsEqualTo("1|2");
        await Assert.That(row[header.FindIndex(static cell => cell.Text == "txp.dup.text")]).IsEqualTo(new TextPresenceReaders.CsvCell("|", Quoted: false));
        await Assert.That(row[header.FindIndex(static cell => cell.Text == "txp.dup.repr")]).IsEqualTo(new TextPresenceReaders.CsvCell("|", Quoted: false));
    }

    /// <summary>
    /// Proves that a wide CSV cell for one selected occurrence (first or last) tells an empty text from an absent one, for the tree
    /// feed and the tree-free feed alike.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    /// <exception cref="InvalidOperationException">The field selection could not be created.</exception>
    [Test]
    public async Task AWideCsvCellWithOneOccurrenceTellsEmptyFromAbsentAlsoForTheTreeFeedWithFirstAndLast()
    {
        using TextProbePacketFactory probe = new();

        foreach (Occurrences occurrences in new[] { Occurrences.First, Occurrences.Last })
        {
            FieldRequest[] requests = [new FieldRequest(TextProbePacketFactory.DuplicateFieldName, occurrences)];
            if (!FieldSelection.TryCreate(probe.Stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
            {
                throw new InvalidOperationException(error.Message);
            }

            ExportContent content = probe.FlatContent(ExportShape.SelectedFields) with { Fields = selection };

            string[] cells = new string[2];
            for (int feed = 0; feed < 2; feed++)
            {
                using MemoryStream stream = new();
                using (CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithContent(content).Build())
                {
                    probe.Feed(exporter, treeFree: feed == 1);
                    exporter.OnFinish();
                }

                string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                List<TextPresenceReaders.CsvCell> header = TextPresenceReaders.SplitCsv(lines[0]);
                List<TextPresenceReaders.CsvCell> row = TextPresenceReaders.SplitCsv(lines[1]);
                cells[feed] = _Quote(TextPresenceReaders.CsvText(row[header.FindIndex(static cell => cell.Text == "txp.dup.text")]));
            }

            // First is the occurrence with the empty text: "" (quoted empty). Last is the occurrence without a text: absent.
            string expected = occurrences == Occurrences.First
                ? "\"\""
                : "∅";
            await Assert.That(cells[0]).IsEqualTo(expected);
            await Assert.That(cells[1]).IsEqualTo(expected);
        }
    }

    /// <summary>Proves that the JSON tree writes an empty text as an empty string and writes no key for an absent text.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheJsonTreeWritesAnEmptyTextAsAnEmptyStringAndNoKeyForAnAbsentOne()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_empty_json");

        foreach (Scenario scenario in new[] { Scenario.JsonPrettyTree, Scenario.JsonArrayTree, Scenario.JsonCompactTree })
        {
            string json = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false).Text;
            Dictionary<string, Texts> texts = _Read(scenario, new Output(string.Empty, json), probe);

            await Assert.That(texts["txp.empty"].Text).IsEqualTo(string.Empty);
            await Assert.That(texts["txp.empty"].Representation).IsEqualTo(string.Empty);
            await Assert.That(texts["txp.plain"].Text).IsNull();
            await Assert.That(texts["txp.plain"].Representation).IsNull();
        }
    }

    #endregion

    #region Switches

    /// <summary>Proves that PBF standard writes no text or representation field when both switches are off, and writes both when on.</summary>
    /// <param name="scenario">The shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.PbfStandardHierarchy)]
    [Arguments(Scenario.PbfStandardAllFields)]
    [Arguments(Scenario.PbfStandardSelectedFields)]
    public async Task PbfStandardWritesNoTextFieldsWhenTheSwitchesAreOff(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_pbf_standard");

        Output off = _Export(scenario, probe, dir, text: false, representation: false, treeFree: false);
        Output on = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false);

        await Assert.That(TextPresenceReaders.PbfStandardHasField(off.Path, Numbers.FieldValueText)).IsFalse();
        await Assert.That(TextPresenceReaders.PbfStandardHasField(off.Path, Numbers.FieldCustomText)).IsFalse();
        await Assert.That(TextPresenceReaders.PbfStandardHasField(on.Path, Numbers.FieldValueText)).IsTrue();
        await Assert.That(TextPresenceReaders.PbfStandardHasField(on.Path, Numbers.FieldCustomText)).IsTrue();
    }

    /// <summary>Proves that PBF columnar writes no text or representation column when both switches are off, and writes both when on.</summary>
    /// <param name="scenario">The shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.PbfColumnarHierarchy)]
    [Arguments(Scenario.PbfColumnarAllFields)]
    [Arguments(Scenario.PbfColumnarSelectedFields)]
    public async Task PbfColumnarWritesNoTextColumnsWhenTheSwitchesAreOff(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_pbf_columnar");

        Output off = _Export(scenario, probe, dir, text: false, representation: false, treeFree: false);
        Output on = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false);

        await Assert.That(TextPresenceReaders.PbfColumnarHasColumn(off.Path, Numbers.ColumnCustomRepresentations)).IsFalse();
        await Assert.That(TextPresenceReaders.PbfColumnarHasColumn(off.Path, Numbers.ColumnCustomTexts)).IsFalse();
        await Assert.That(TextPresenceReaders.PbfColumnarHasColumn(on.Path, Numbers.ColumnCustomRepresentations)).IsTrue();
        await Assert.That(TextPresenceReaders.PbfColumnarHasColumn(on.Path, Numbers.ColumnCustomTexts)).IsTrue();
    }

    /// <summary>Proves that CSV writes no text or representation column when both switches are off, and writes them when on.</summary>
    /// <param name="scenario">The CSV layout under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.CsvWide)]
    [Arguments(Scenario.CsvLong)]
    [Arguments(Scenario.CsvHierarchy)]
    public async Task CsvWritesNoTextColumnsWhenTheSwitchesAreOff(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_csv");

        string header = _Export(scenario, probe, dir, text: false, representation: false, treeFree: false)
            .Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[0];
        string onHeader = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false)
            .Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[0];

        // The long and hierarchy layouts name their text columns "custom_repr" and "custom_text"; the wide layout writes "<field>.repr"
        // and "<field>.text" after each field, so its header grows from the field columns alone to three columns per field. (The
        // probe fields are named "txp.text" and "txp.repr", so a substring test of the plain words would match field columns.)
        if (scenario == Scenario.CsvWide)
        {
            await Assert.That(TextPresenceReaders.SplitCsv(header).Count(static cell => cell.Text.StartsWith("txp.", StringComparison.Ordinal)))
                .IsEqualTo(TextProbePacketFactory.FieldNames.Count);
            await Assert.That(TextPresenceReaders.SplitCsv(onHeader).Count(static cell => cell.Text.StartsWith("txp.", StringComparison.Ordinal)))
                .IsEqualTo(TextProbePacketFactory.FieldNames.Count * 3);
            return;
        }

        await Assert.That(header.Contains("custom_repr", StringComparison.Ordinal)).IsFalse();
        await Assert.That(header.Contains("custom_text", StringComparison.Ordinal)).IsFalse();
        await Assert.That(onHeader.Contains("custom_repr", StringComparison.Ordinal)).IsTrue();
        await Assert.That(onHeader.Contains("custom_text", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that JSON writes no text or representation key when both switches are off, for both feeds, and writes them when on.</summary>
    /// <param name="scenario">The JSON style and shape under test.</param>
    /// <param name="textKey">The text that marks the custom text key in the output.</param>
    /// <param name="representationKey">The text that marks the custom representation key in the output.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Scenario.JsonPrettyTree, "custom_text", "custom_representation")]
    [Arguments(Scenario.JsonArrayTree, "custom_text", "custom_representation")]
    [Arguments(Scenario.JsonCompactTree, "\"CT\":", "\"CR\":")]
    [Arguments(Scenario.JsonSelectedFields, "custom_text", "custom_representation")]
    [Arguments(Scenario.JsonAllFields, "custom_text", "custom_representation")]
    public async Task JsonWritesNoTextKeyWhenTheSwitchesAreOff(Scenario scenario, string textKey, string representationKey)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_json");

        string off = _Export(scenario, probe, dir, text: false, representation: false, treeFree: false).Text;
        string treeFreeOff = _Export(scenario, probe, dir, text: false, representation: false, treeFree: true).Text;
        string on = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false).Text;

        await Assert.That(off.Contains(textKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(off.Contains(representationKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(treeFreeOff.Contains(textKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(treeFreeOff.Contains(representationKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(on.Contains(textKey, StringComparison.Ordinal)).IsTrue();
        await Assert.That(on.Contains(representationKey, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that DuckDB writes no text or representation column when both switches are off, and writes them when on.</summary>
    /// <param name="scenario">The shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(Scenario.DuckDbHierarchy)]
    [Arguments(Scenario.DuckDbAllFields)]
    [Arguments(Scenario.DuckDbSelectedFields)]
    public async Task DuckDbWritesNoTextColumnsWhenTheSwitchesAreOff(Scenario scenario)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_duckdb");

        Output off = _Export(scenario, probe, dir, text: false, representation: false, treeFree: false);
        Output on = _Export(scenario, probe, dir, text: true, representation: true, treeFree: false);

        // txp.both has a representation and a text, so a column that is missing with the switch off is the switch and not a field
        // without a text; the switched-on export is the positive control.
        string table = Invariant($"field_{probe.IdOf("txp.both").Value}");

        await Assert.That(TextPresenceReaders.DuckDbHasColumn(off.Path, table, "custom_repr")).IsFalse();
        await Assert.That(TextPresenceReaders.DuckDbHasColumn(off.Path, table, "custom_text")).IsFalse();
        await Assert.That(TextPresenceReaders.DuckDbHasColumn(on.Path, table, "custom_repr")).IsTrue();
        await Assert.That(TextPresenceReaders.DuckDbHasColumn(on.Path, table, "custom_text")).IsTrue();
        if (scenario == Scenario.DuckDbHierarchy)
        {
            // Only the hierarchy shape has a topology.
            await Assert.That(TextPresenceReaders.DuckDbHasColumn(off.Path, "topology", "custom_text")).IsFalse();
            await Assert.That(TextPresenceReaders.DuckDbHasColumn(on.Path, "topology", "custom_text")).IsTrue();
        }
    }

    /// <summary>Proves that Parquet writes no text or representation column when both switches are off, and writes them when on.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task ParquetWritesNoTextColumnsWhenTheSwitchesAreOff()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_parquet");

        Output off = _Export(Scenario.ParquetHierarchy, probe, dir, text: false, representation: false, treeFree: false);
        Output on = _Export(Scenario.ParquetHierarchy, probe, dir, text: true, representation: true, treeFree: false);

        // txp.both has a representation and a text (see the DuckDB test): the switched-on file is the positive control.
        string offFile = Path.Combine(off.Path, "fields", Invariant($"field_{probe.IdOf("txp.both").Value}.parquet"));
        string onFile = Path.Combine(on.Path, "fields", Invariant($"field_{probe.IdOf("txp.both").Value}.parquet"));
        await using ParquetReader offReader = await ParquetReader.CreateAsync(offFile);
        await using ParquetReader onReader = await ParquetReader.CreateAsync(onFile);

        await Assert.That(string.Join(',', offReader.Schema.GetDataFields().Select(static field => field.Name)))
            .IsEqualTo("packet_id,node_id,value");
        await Assert.That(string.Join(',', onReader.Schema.GetDataFields().Select(static field => field.Name)))
            .IsEqualTo("packet_id,node_id,value,custom_repr,custom_text");
    }

    /// <summary>Proves that no flat exporter asks the dissectors for a display text while both switches are off; the Text exporter asks when on.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task EveryFlatExporterAsksForNoDisplayTextWhenTheSwitchesAreOff()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("text_switch_demand");
        ExportContent off = probe.FlatContent(ExportShape.AllFields, text: false, representation: false);
        ExportContent textOnly = probe.FlatContent(ExportShape.AllFields, text: true, representation: false);

        using DuckDbExporter duckDb = DuckDbExporter.CreateBuilder().ToFile(dir.FilePath("a.duckdb")).WithContent(off).Build();
        using ParquetExporter parquet = ParquetExporter.CreateBuilder().ToDirectory(dir.FilePath("p")).WithContent(off).Build();
        using PbfExporter pbfStandard = PbfExporter.CreateBuilder().ToFile(dir.FilePath("s.pbf")).WithFormat(PbfExportFormat.Standard).WithContent(off).Build();
        using PbfExporter pbfColumnar = PbfExporter.CreateBuilder().ToFile(dir.FilePath("c.pbf")).WithFormat(PbfExportFormat.Columnar).WithContent(off).Build();
        using CsvExporter csv = CsvExporter.CreateBuilder().ToStream(Stream.Null).WithContent(off).Build();
        using JsonExporter json = JsonExporter.CreateBuilder().ToStream(Stream.Null).WithContent(off).Build();
        using TextExporter text = TextExporter.CreateBuilder().ToStream(Stream.Null).WithContent(off).Build();
        using TextExporter textWithTexts = TextExporter.CreateBuilder().ToStream(Stream.Null).WithContent(textOnly).Build();

        // The parse demand is what the dissectors see: with no text switch on, no dissector is asked to build a display text.
        await Assert.That(duckDb.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(parquet.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(pbfStandard.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(pbfColumnar.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(csv.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(json.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(text.ParseDemand.Observer.DisplayText.IsNone).IsTrue();
        await Assert.That(textWithTexts.ParseDemand.Observer.DisplayText.IsAll).IsTrue();
    }

    #endregion
}
