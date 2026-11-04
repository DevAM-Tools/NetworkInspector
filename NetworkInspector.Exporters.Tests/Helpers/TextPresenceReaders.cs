// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Texts = NetworkInspector.Exporters.Tests.Helpers.TextProbePacketFactory.Texts;

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Reads the custom texts of the probe fields (<see cref="TextProbePacketFactory"/>) back from the output of every packet exporter,
/// so one assertion can compare what each format stored: a <see langword="null"/> text is what the format writes for "no
/// dissector set one", the empty string is "set but empty". Each reader states the format's encoding itself (a NULL, a missing
/// field, an absent-run entry, a missing key, an empty unquoted cell) and never shares code with the exporter, so a change of the
/// writer fails a test instead of the reader following it.
/// </summary>
/// <remarks>Thread safety: stateless; every method works on its own file or text.</remarks>
internal static class TextPresenceReaders
{
    #region PBF field numbers

    /// <summary>
    /// The PBF field numbers the readers of this class rely on, spelled as literals from the PBF README (§ Field record, § Columnar
    /// layout, § Texts) and not taken from the exporter's constants class, so a changed or swapped constant in the exporter fails a
    /// test instead of the reader following it.
    /// </summary>
    /// <remarks>
    /// Sync: NetworkInspector.Exporters/Pbf/README.md § Field record, § Columnar layout, § Texts, and
    /// NetworkInspector.Exporters/Pbf/PbfFieldNumbers.cs.
    /// </remarks>
    internal static class PbfNumbers
    {
        /// <summary>Packet field of a block payload, and the field records inside a packet (length-delimited).</summary>
        internal const int PacketField = 5;

        /// <summary>Frame bytes of a standard packet record (length-delimited).</summary>
        internal const int PacketFrameBytes = 6;

        /// <summary>Field id inside a field record.</summary>
        internal const int FieldFieldId = 1;

        /// <summary>Custom representation inside a field record (present only for a set text).</summary>
        internal const int FieldValueText = 6;

        /// <summary>Custom text inside a field record (present only for a set text; the text of a layer node is here too).</summary>
        internal const int FieldCustomText = 7;

        /// <summary>Same-as-previous flags of a field record.</summary>
        internal const int FieldSameFlags = 8;

        /// <summary>Child field record inside a field record.</summary>
        internal const int FieldChild = 9;

        /// <summary>Frame bytes column of a columnar block (one entry per packet).</summary>
        internal const int ColumnarFrameBytes = 13;

        /// <summary>The topology message of a columnar block.</summary>
        internal const int ColumnarTopology = 14;

        /// <summary>One field column message of a columnar block.</summary>
        internal const int ColumnarFieldColumns = 15;

        /// <summary>Node id per row inside the topology message.</summary>
        internal const int TopologyNodeIds = 2;

        /// <summary>Field id per row inside the topology message.</summary>
        internal const int TopologyFieldIds = 3;

        /// <summary>Parent node id per row inside the topology message (zigzag).</summary>
        internal const int TopologyParentNodeIds = 4;

        /// <summary>The presence-encoded custom text column inside the topology message.</summary>
        internal const int TopologyCustomTexts = 5;

        /// <summary>Field id inside a field column message.</summary>
        internal const int ColumnFieldId = 1;

        /// <summary>Position (node id or occurrence) per row inside a field column message.</summary>
        internal const int ColumnNodeIds = 4;

        /// <summary>The presence-encoded custom representation column of a field column.</summary>
        internal const int ColumnCustomRepresentations = 15;

        /// <summary>The presence-encoded custom text column of a field column.</summary>
        internal const int ColumnCustomTexts = 18;
    }

    #endregion

    #region DuckDB and Parquet

    /// <summary>Reads <c>custom_repr</c> and <c>custom_text</c> of every probe field table of a DuckDB export.</summary>
    /// <param name="path">The <c>.duckdb</c> file; the export must be finished.</param>
    /// <param name="factory">The factory that parsed the exported packet (names the field tables).</param>
    /// <returns>The texts per probe field name; a field with no row is absent from the map.</returns>
    internal static Dictionary<string, Texts> ReadDuckDb(string path, TextProbePacketFactory factory)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        Dictionary<string, Texts> result = [];
        foreach (string name in TextProbePacketFactory.FieldNames)
        {
            string table = Invariant($"field_{factory.IdOf(name).Value.ToString(CultureInfo.InvariantCulture)}");
            Texts? texts = _ReadTextsRow(connection, Invariant($"SELECT custom_repr, custom_text FROM {table}"));
            if (texts is not null)
            {
                result[name] = texts.Value;
            }
        }

        return result;
    }

    /// <summary>Reads the same texts from the per-field files of a Parquet dataset, through an in-memory DuckDB.</summary>
    /// <param name="directory">The dataset directory; the export must be finished.</param>
    /// <param name="factory">The factory that parsed the exported packet (names the field files).</param>
    /// <returns>The texts per probe field name; a field with no file is absent from the map.</returns>
    internal static Dictionary<string, Texts> ReadParquet(string directory, TextProbePacketFactory factory)
    {
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        Dictionary<string, Texts> result = [];
        foreach (string name in TextProbePacketFactory.FieldNames)
        {
            string file = Path.Combine(directory, "fields", Invariant($"field_{factory.IdOf(name).Value.ToString(CultureInfo.InvariantCulture)}.parquet"));
            if (!File.Exists(file))
            {
                continue;
            }

            Texts? texts = _ReadTextsRow(connection, Invariant($"SELECT custom_repr, custom_text FROM read_parquet('{file}')"));
            if (texts is not null)
            {
                result[name] = texts.Value;
            }
        }

        return result;
    }

    /// <summary>Reads the first row of a two-column text query; a NULL is an absent text.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">A query that selects the representation and the text column.</param>
    /// <returns>The texts of the first row, or <see langword="null"/> when the query returns no row.</returns>
    private static Texts? _ReadTextsRow(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? representation = reader.IsDBNull(0)
            ? null
            : reader.GetString(0);
        string? text = reader.IsDBNull(1)
            ? null
            : reader.GetString(1);
        return new Texts(representation, text);
    }

    /// <summary>The custom text of a topology row (a node without a value); <see langword="null"/> when NULL or no such node.</summary>
    /// <param name="path">The <c>.duckdb</c> file.</param>
    /// <param name="fieldId">The field id of the node.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    internal static string? ReadDuckDbTopologyText(string path, int fieldId)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT custom_text FROM topology WHERE field_id = {fieldId.ToString(CultureInfo.InvariantCulture)}");
        using DuckDBDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetString(0);
    }

    /// <summary>Whether a DuckDB table has a column.</summary>
    /// <param name="path">The <c>.duckdb</c> file.</param>
    /// <param name="table">The table name.</param>
    /// <param name="column">The column name.</param>
    /// <returns><see langword="true"/> when the table has the column.</returns>
    internal static bool DuckDbHasColumn(string path, string table, string column)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            Invariant($"SELECT COUNT(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    #endregion

    #region Topology texts

    /// <summary>One topology row as an export stored it: the node, its field, its parent, and the display line of a node without a value.</summary>
    /// <param name="NodeId">The depth-first node id, unique within its packet.</param>
    /// <param name="FieldId">The field id of the node.</param>
    /// <param name="ParentNodeId">The node id of the parent, -1 for a top-level node.</param>
    /// <param name="Text">The stored text; <see langword="null"/> for an absent text (a value node, or a dissector that set none).</param>
    internal readonly record struct TopologyRow(int NodeId, int FieldId, int ParentNodeId, string? Text);

    /// <summary>Reads the topology rows (with the text column) of a DuckDB export in node order of its packets.</summary>
    /// <param name="path">The <c>.duckdb</c> file; the export must be finished.</param>
    /// <returns>The rows.</returns>
    internal static List<TopologyRow> ReadDuckDbTopology(string path)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        return _ReadTopologyRows(connection, "SELECT node_id, field_id, parent_node_id, custom_text FROM topology ORDER BY packet_id, node_id");
    }

    /// <summary>Reads the rows of <c>topology.parquet</c> (with the <c>custom_text</c> column) in node order, through an in-memory DuckDB.</summary>
    /// <param name="directory">The dataset directory; the export must be finished.</param>
    /// <returns>The rows.</returns>
    internal static List<TopologyRow> ReadParquetTopology(string directory)
    {
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        string file = Path.Combine(directory, "topology.parquet");
        return _ReadTopologyRows(
            connection,
            Invariant($"SELECT node_id, field_id, parent_node_id, custom_text FROM read_parquet('{file}') ORDER BY packet_id, node_id"));
    }

    /// <summary>Whether <c>topology.parquet</c> has a column with the given name.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="column">The column name.</param>
    /// <returns><see langword="true"/> when the file has the column.</returns>
    internal static bool ParquetTopologyHasColumn(string directory, string column)
    {
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT COUNT(*) FROM parquet_schema('{Path.Combine(directory, "topology.parquet")}') WHERE name = '{column}'");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Runs a topology query of four columns (node, field, parent, text) and returns its rows in result order.</summary>
    /// <param name="connection">The open connection to query.</param>
    /// <param name="sql">The query; it selects the node id, the field id, the parent node id, and the text, in that order.</param>
    /// <returns>The rows in result order.</returns>
    private static List<TopologyRow> _ReadTopologyRows(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        List<TopologyRow> rows = [];
        while (reader.Read())
        {
            rows.Add(new TopologyRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3)
                    ? null
                    : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the topology message (field 14) of every columnar block of an uncompressed PBF file: node ids (field 2), field ids
    /// (field 3), and parents (field 4, zigzag), one entry per row, and the presence-encoded text column (field 5): a present entry
    /// (also an empty one) is a text, an absent-run entry is that many rows without a text, and a missing column is every row absent.
    /// </summary>
    /// <param name="path">The PBF file written without compression, columnar layout.</param>
    /// <returns>The rows in file order.</returns>
    /// <exception cref="InvalidOperationException">The columns of a topology message differ in length.</exception>
    internal static List<TopologyRow> ReadPbfColumnarTopology(string path)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<TopologyRow> rows = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField topologyField in block.Where(static field => field.Number == PbfNumbers.ColumnarTopology))
            {
                List<PbfField> topology = PbfFileScanner.Fields(topologyField.Payload!);
                List<PbfField> nodes = [.. topology.Where(static field => field.Number == PbfNumbers.TopologyNodeIds)];
                List<PbfField> fieldIds = [.. topology.Where(static field => field.Number == PbfNumbers.TopologyFieldIds)];
                List<PbfField> parents = [.. topology.Where(static field => field.Number == PbfNumbers.TopologyParentNodeIds)];
                string?[] texts = _TextColumn(topology, PbfNumbers.TopologyCustomTexts, nodes.Count);
                if (fieldIds.Count != nodes.Count || parents.Count != nodes.Count || texts.Length != nodes.Count)
                {
                    throw new InvalidOperationException(
                        Invariant(
                            $"Topology column lengths differ: {nodes.Count} nodes, {fieldIds.Count} fields, {parents.Count} parents, {texts.Length} texts."));
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    long parent = checked((long)(parents[i].Varint >> 1)) ^ -checked((long)(parents[i].Varint & 1));
                    rows.Add(new TopologyRow(checked((int)nodes[i].Varint), checked((int)fieldIds[i].Varint), checked((int)parent), texts[i]));
                }
            }
        }

        return rows;
    }

    #endregion

    #region PBF

    /// <summary>Reads the texts of the probe fields from an uncompressed PBF file in the standard layout.</summary>
    /// <param name="path">The PBF file.</param>
    /// <param name="factory">The factory that parsed the exported packet (names the field ids).</param>
    /// <returns>
    /// The texts per probe field name: a field record without a representation (field 6) or text (field 7) is absent, a
    /// zero-length field is the empty string.
    /// </returns>
    internal static Dictionary<string, Texts> ReadPbfStandard(string path, TextProbePacketFactory factory)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        Dictionary<string, Texts> result = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField packet in block)
            {
                // A packet is a length-delimited field 5; the header field 5 (maximum timestamp) is a varint.
                if (packet.Number == PbfNumbers.PacketField && packet.Payload is not null)
                {
                    _CollectStandardFields(PbfFileScanner.Fields(packet.Payload), factory, result, packetLevel: true);
                }
            }
        }

        return result;
    }

    /// <summary>Collects the field records of a packet or a field message (children included) into <paramref name="result"/>.</summary>
    /// <param name="fields">The fields of a packet message or a field message.</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <param name="result">The map to fill; the first record of a field wins.</param>
    /// <param name="packetLevel">
    /// Whether <paramref name="fields"/> are the fields of a packet message (its field records are field 5) or of a field message
    /// (its children are field 9; its field 5 is the value, which is no record).
    /// </param>
    private static void _CollectStandardFields(
        List<PbfField> fields, TextProbePacketFactory factory, Dictionary<string, Texts> result, bool packetLevel)
    {
        int recordNumber = packetLevel
            ? PbfNumbers.PacketField
            : PbfNumbers.FieldChild;
        foreach (PbfField entry in fields)
        {
            if (entry.Number != recordNumber || entry.Payload is null)
            {
                continue;
            }

            List<PbfField> record = PbfFileScanner.Fields(entry.Payload!);
            int id = checked((int)PbfFileScanner.Value(record, PbfNumbers.FieldFieldId));
            string? name = factory.Stack.GetField(new FieldId(id))?.Name;
            if (name is not null && TextProbePacketFactory.Expected.ContainsKey(name) && !result.ContainsKey(name))
            {
                result[name] = new Texts(
                    _StringField(record, PbfNumbers.FieldValueText),
                    _StringField(record, PbfNumbers.FieldCustomText));
            }

            _CollectStandardFields(record, factory, result, packetLevel: false);
        }
    }

    /// <summary>The string of the first field with a number, or <see langword="null"/> when the message has none.</summary>
    /// <param name="fields">The fields of one message.</param>
    /// <param name="number">The field number.</param>
    /// <returns>The decoded UTF-8 text (empty for a zero-length field), or <see langword="null"/> when absent.</returns>
    private static string? _StringField(List<PbfField> fields, int number)
    {
        foreach (PbfField field in fields)
        {
            if (field.Number == number && field.Payload is not null)
            {
                return Encoding.UTF8.GetString(field.Payload);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the texts of the probe fields from an uncompressed PBF file in the columnar layout: the representation and text columns
    /// of every field column, decoded row by row.
    /// </summary>
    /// <param name="path">The PBF file.</param>
    /// <param name="factory">The factory that parsed the exported packet (names the field ids).</param>
    /// <returns>
    /// The texts of the first row per probe field name; a missing text column is the absent text of every row, an absent-run entry
    /// an absent row, a present entry (also an empty one) a present text.
    /// </returns>
    internal static Dictionary<string, Texts> ReadPbfColumnar(string path, TextProbePacketFactory factory)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        Dictionary<string, Texts> result = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField columnField in block)
            {
                if (columnField.Number != PbfNumbers.ColumnarFieldColumns)
                {
                    continue;
                }

                List<PbfField> column = PbfFileScanner.Fields(columnField.Payload!);
                int id = checked((int)PbfFileScanner.Value(column, PbfNumbers.ColumnFieldId));
                string? name = factory.Stack.GetField(new FieldId(id))?.Name;
                if (name is null || !TextProbePacketFactory.Expected.ContainsKey(name))
                {
                    continue;
                }

                int rows = column.Count(static field => field.Number == PbfNumbers.ColumnNodeIds);
                string?[] representations = _TextColumn(column, PbfNumbers.ColumnCustomRepresentations, rows);
                string?[] texts = _TextColumn(column, PbfNumbers.ColumnCustomTexts, rows);
                // The first block that holds the field wins, as the summary says.
                result.TryAdd(name, new Texts(representations[0], texts[0]));
            }
        }

        return result;
    }

    /// <summary>The decoded rows of a text column of a field column; a missing column is <paramref name="rows"/> absent rows.</summary>
    /// <param name="column">The fields of one field column message.</param>
    /// <param name="number">The field number of the text column.</param>
    /// <param name="rows">The number of rows of the field column.</param>
    /// <returns>One entry per row.</returns>
    private static string?[] _TextColumn(List<PbfField> column, int number, int rows)
    {
        foreach (PbfField field in column)
        {
            if (field.Number == number && field.Payload is not null)
            {
                return PbfFileScanner.DecodeTextColumn(field.Payload);
            }
        }

        return new string?[rows];
    }

    /// <summary>Whether a field column message of the file holds a column with the given number for any probe field.</summary>
    /// <param name="path">The PBF file (columnar layout).</param>
    /// <param name="number">The column number, for example <see cref="PbfNumbers.ColumnCustomTexts"/>.</param>
    /// <returns><see langword="true"/> when some field column carries it.</returns>
    internal static bool PbfColumnarHasColumn(string path, int number)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField columnField in block)
            {
                if (columnField.Number == PbfNumbers.ColumnarFieldColumns
                    && PbfFileScanner.Fields(columnField.Payload!).Exists(field => field.Number == number))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a field record of a standard-layout file carries a field with the given number, anywhere in its packets.</summary>
    /// <param name="path">The PBF file (standard layout).</param>
    /// <param name="number">The field number, for example <see cref="PbfNumbers.FieldCustomText"/>.</param>
    /// <returns><see langword="true"/> when some field record carries it.</returns>
    internal static bool PbfStandardHasField(string path, int number)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField packet in block)
            {
                if (packet.Number == PbfNumbers.PacketField && packet.Payload is not null
                    && _AnyRecordHas(PbfFileScanner.Fields(packet.Payload), number, packetLevel: true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a field record or one of its descendants carries a field with the given number.</summary>
    /// <param name="fields">The fields of a packet message or a field message.</param>
    /// <param name="number">The field number to look for in field records.</param>
    /// <param name="packetLevel">
    /// Whether <paramref name="fields"/> are the fields of a packet message (records are field 5) or of a field message (children
    /// are field 9).
    /// </param>
    /// <returns><see langword="true"/> when found.</returns>
    private static bool _AnyRecordHas(List<PbfField> fields, int number, bool packetLevel)
    {
        int recordNumber = packetLevel
            ? PbfNumbers.PacketField
            : PbfNumbers.FieldChild;
        foreach (PbfField entry in fields)
        {
            if (entry.Number != recordNumber || entry.Payload is null)
            {
                continue;
            }

            List<PbfField> record = PbfFileScanner.Fields(entry.Payload);
            if (record.Exists(field => field.Number == number) || _AnyRecordHas(record, number, packetLevel: false))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region CSV

    /// <summary>One CSV cell: its text and whether it was written quoted.</summary>
    /// <param name="Text">The unquoted text of the cell.</param>
    /// <param name="Quoted">Whether the cell was enclosed in quotes (so an empty quoted cell is <c>""</c>).</param>
    internal readonly record struct CsvCell(string Text, bool Quoted);

    /// <summary>Splits one CSV line (comma delimiter, RFC 4180 quoting) and keeps whether each cell was quoted.</summary>
    /// <param name="line">One record without its line terminator.</param>
    /// <returns>The cells in order.</returns>
    internal static List<CsvCell> SplitCsv(string line)
    {
        List<CsvCell> cells = [];
        int position = 0;
        while (true)
        {
            if (position < line.Length && line[position] == '"')
            {
                StringBuilder text = new();
                position++;
                while (position < line.Length)
                {
                    if (line[position] == '"' && position + 1 < line.Length && line[position + 1] == '"')
                    {
                        text.Append('"');
                        position += 2;
                    }
                    else if (line[position] == '"')
                    {
                        position++;
                        break;
                    }
                    else
                    {
                        text.Append(line[position]);
                        position++;
                    }
                }

                cells.Add(new CsvCell(text.ToString(), Quoted: true));
            }
            else
            {
                int end = line.IndexOf(',', position);
                if (end < 0)
                {
                    end = line.Length;
                }

                cells.Add(new CsvCell(line[position..end], Quoted: false));
                position = end;
            }

            if (position >= line.Length)
            {
                return cells;
            }

            // The delimiter after a cell; a trailing delimiter means one more (empty) cell.
            position++;
            if (position == line.Length)
            {
                cells.Add(new CsvCell(string.Empty, Quoted: false));
                return cells;
            }
        }
    }

    /// <summary>
    /// The texts of one probe field from a CSV text: an empty unquoted cell is absent, any other cell (also <c>""</c>) is present.
    /// </summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The text, or <see langword="null"/> for an empty unquoted cell.</returns>
    internal static string? CsvText(CsvCell cell) =>
        cell.Quoted || cell.Text.Length > 0
            ? cell.Text
            : null;

    /// <summary>
    /// Reads the probe texts from a CSV export of the all-fields (long) or hierarchy layout: the <c>custom_repr</c> and
    /// <c>custom_text</c> cells of the row whose <c>field</c> cell is the probe field.
    /// </summary>
    /// <param name="csv">The whole CSV text (CRLF terminated records, a header row, no BOM).</param>
    /// <returns>The texts per probe field name.</returns>
    internal static Dictionary<string, Texts> ReadCsvRows(string csv)
    {
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        List<CsvCell> header = SplitCsv(lines[0]);
        int fieldColumn = header.FindIndex(static cell => cell.Text == "field");
        int reprColumn = header.FindIndex(static cell => cell.Text == "custom_repr");
        int textColumn = header.FindIndex(static cell => cell.Text == "custom_text");
        Dictionary<string, Texts> result = [];
        foreach (string line in lines.Skip(1))
        {
            List<CsvCell> cells = SplitCsv(line);
            string name = cells[fieldColumn].Text;
            if (TextProbePacketFactory.Expected.ContainsKey(name))
            {
                result[name] = new Texts(
                    reprColumn < 0
                        ? null
                        : CsvText(cells[reprColumn]),
                    textColumn < 0
                        ? null
                        : CsvText(cells[textColumn]));
            }
        }

        return result;
    }

    /// <summary>Reads the probe texts from a CSV export of the wide layout: the <c>.repr</c> and <c>.text</c> cells of the first row.</summary>
    /// <param name="csv">The whole CSV text (CRLF terminated records, a header row, no BOM).</param>
    /// <returns>The texts per probe field name.</returns>
    internal static Dictionary<string, Texts> ReadCsvWide(string csv)
    {
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        List<CsvCell> header = SplitCsv(lines[0]);
        List<CsvCell> row = SplitCsv(lines[1]);
        Dictionary<string, Texts> result = [];
        foreach (string name in TextProbePacketFactory.FieldNames)
        {
            int reprColumn = header.FindIndex(cell => cell.Text == name + ".repr");
            int textColumn = header.FindIndex(cell => cell.Text == name + ".text");
            result[name] = new Texts(
                reprColumn < 0
                    ? null
                    : CsvText(row[reprColumn]),
                textColumn < 0
                    ? null
                    : CsvText(row[textColumn]));
        }

        return result;
    }

    #endregion

    #region JSON

    /// <summary>
    /// Reads the probe texts from the JSON output of any format and shape: the tree (pretty and array keys, or the compact short
    /// keys), the selected-fields map, and the all-fields list. A missing key is an absent text.
    /// </summary>
    /// <param name="json">The whole JSON document (an array of packets).</param>
    /// <param name="factory">The factory that parsed the exported packet (names the compact field ids).</param>
    /// <param name="format">The JSON style of the document (decides the tree keys).</param>
    /// <param name="shape">The shape of the document.</param>
    /// <returns>The texts per probe field name (the first occurrence).</returns>
    internal static Dictionary<string, Texts> ReadJson(string json, TextProbePacketFactory factory, JsonExportFormat format, ExportShape shape)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement packet = document.RootElement[0];
        Dictionary<string, Texts> result = [];
        bool compact = format == JsonExportFormat.Compact;
        string fieldsKey = compact && shape == ExportShape.Hierarchy
            ? "CH"
            : "fields";
        JsonElement fields = packet.GetProperty(fieldsKey);

        if (shape == ExportShape.Hierarchy)
        {
            _CollectJsonTree(fields, factory, compact, result);
        }
        else if (shape == ExportShape.SelectedFields)
        {
            foreach (JsonProperty property in fields.EnumerateObject())
            {
                JsonElement first = property.Value.ValueKind == JsonValueKind.Array
                    ? property.Value[0]
                    : property.Value;
                result[property.Name] = _JsonTexts(first, "custom_representation", "custom_text");
            }
        }
        else
        {
            foreach (JsonElement entry in fields.EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString()!;
                result[name] = _JsonTexts(entry, "custom_representation", "custom_text");
            }
        }

        return result;
    }

    /// <summary>Collects the probe fields of a tree (and their descendants) into <paramref name="result"/>.</summary>
    /// <param name="fields">The array of field objects.</param>
    /// <param name="factory">The factory that names the compact field ids.</param>
    /// <param name="compact">Whether the document uses the compact short keys.</param>
    /// <param name="result">The map to fill; the first object of a field wins.</param>
    private static void _CollectJsonTree(JsonElement fields, TextProbePacketFactory factory, bool compact, Dictionary<string, Texts> result)
    {
        foreach (JsonElement field in fields.EnumerateArray())
        {
            string? name = compact
                ? factory.Stack.GetField(new FieldId(field.GetProperty("FI").GetInt32()))?.Name
                : field.GetProperty("name").GetString();
            if (name is not null && TextProbePacketFactory.Expected.ContainsKey(name) && !result.ContainsKey(name))
            {
                result[name] = compact
                    ? _JsonTexts(field, "CR", "CT")
                    : _JsonTexts(field, "custom_representation", "custom_text");
            }

            string childrenKey = compact
                ? "CH"
                : "children";
            if (field.TryGetProperty(childrenKey, out JsonElement children))
            {
                _CollectJsonTree(children, factory, compact, result);
            }
        }
    }

    /// <summary>The two text keys of a field object; a missing key is an absent text, <c>""</c> an empty one.</summary>
    /// <param name="field">The field or occurrence object.</param>
    /// <param name="representationKey">The key of the custom representation.</param>
    /// <param name="textKey">The key of the custom text.</param>
    /// <returns>The texts.</returns>
    private static Texts _JsonTexts(JsonElement field, string representationKey, string textKey)
    {
        return new Texts(_JsonText(field, representationKey), _JsonText(field, textKey));
    }

    /// <summary>
    /// The text under a key of a field object: an absent key is an absent text. A key must hold a string; a JSON <c>null</c> is no text
    /// (the writers omit the key for an absent text), so it fails here instead of reading as absent.
    /// </summary>
    /// <param name="field">The field or occurrence object (a scalar occurrence of a selected field has no keys).</param>
    /// <param name="key">The key.</param>
    /// <returns>The text, or <see langword="null"/> when the key is absent.</returns>
    /// <exception cref="InvalidOperationException">The key holds something other than a string.</exception>
    private static string? _JsonText(JsonElement field, string key)
    {
        if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(Invariant($"The key {key} holds {value.ValueKind}, not a string."));
        }

        return value.GetString();
    }

    #endregion
}
