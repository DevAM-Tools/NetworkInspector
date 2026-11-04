// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>One additional packet buffer as an export stored it, or as a packet reports it.</summary>
/// <param name="PacketId">The id of the packet that owns the buffer.</param>
/// <param name="Index">The buffer index, 1 to 255 (index 0 is the frame).</param>
/// <param name="Bytes">The buffer bytes; an empty array for an empty buffer.</param>
internal readonly record struct BufferRow(int PacketId, int Index, byte[] Bytes);

/// <summary>
/// Reads the additional packet buffers back from the output of every packet exporter, with a reader per format that states the
/// format's layout itself (a table, a Parquet file, a packet field, a column, a key, a cell, a dump) and never shares code or field
/// number constants with the exporter, so a change of the writer fails a test instead of the reader following it. Every reader
/// returns <see langword="null"/> when the output has no buffer content at all (no table, file, field, column, key, or dump), and
/// otherwise the rows in the order the format stores them.
/// </summary>
/// <remarks>Thread safety: stateless; every method works on its own file or text.</remarks>
internal static class BufferReaders
{
    #region DuckDB and Parquet

    /// <summary>Reads the table <c>packet_buffers(packet_id, buffer_index, bytes)</c> of a DuckDB export in stored order.</summary>
    /// <param name="path">The <c>.duckdb</c> file; the export must be finished.</param>
    /// <returns>The rows; <see langword="null"/> when the file has no such table.</returns>
    internal static List<BufferRow>? ReadDuckDb(string path)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        using (DuckDBCommand exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'packet_buffers'";
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        return _ReadRows(connection, "SELECT packet_id, buffer_index, bytes FROM packet_buffers");
    }

    /// <summary>Reads <c>packet_buffers.parquet</c> of a Parquet dataset in stored order, through an in-memory DuckDB.</summary>
    /// <param name="directory">The dataset directory; the export must be finished.</param>
    /// <returns>The rows; <see langword="null"/> when the dataset has no such file.</returns>
    internal static List<BufferRow>? ReadParquet(string directory)
    {
        string file = Path.Combine(directory, "packet_buffers.parquet");
        if (!File.Exists(file))
        {
            return null;
        }

        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        return _ReadRows(connection, Invariant($"SELECT packet_id, buffer_index, bytes FROM read_parquet('{file}')"));
    }

    /// <summary>Runs a query of three columns (packet id, buffer index, bytes) and returns its rows in result order.</summary>
    /// <param name="connection">The open connection to query.</param>
    /// <param name="sql">The query; it selects the packet id, the buffer index, and the bytes, in that order.</param>
    /// <returns>The rows in result order.</returns>
    private static List<BufferRow> _ReadRows(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        List<BufferRow> rows = [];
        while (reader.Read())
        {
            // A NULL BLOB would be an absent buffer: an empty buffer is an empty BLOB, so a NULL is read as a failure of the writer.
            using Stream stream = reader.GetStream(2);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            rows.Add(new BufferRow(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                copy.ToArray()));
        }

        return rows;
    }

    #endregion

    #region PBF

    /// <summary>
    /// Reads the repeated packet field 7 of every packet of a standard PBF file: a sub-message with the index (sub-field 1, varint)
    /// and the bytes (sub-field 2). The packet id is the packet's field 1.
    /// </summary>
    /// <param name="path">The PBF file written without compression.</param>
    /// <returns>The rows; <see langword="null"/> when no packet has the field.</returns>
    internal static List<BufferRow>? ReadPbfStandard(string path)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<BufferRow> rows = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField field in block)
            {
                // A packet is the length-delimited field 5; the header field 5 (maximum timestamp) is a varint.
                if (field.Number != 5 || field.Payload is null)
                {
                    continue;
                }

                List<PbfField> packet = PbfFileScanner.Fields(field.Payload);
                int packetId = checked((int)PbfFileScanner.Value(packet, 1));
                foreach (PbfField buffer in packet.Where(static f => f.Number == 7))
                {
                    List<PbfField> parts = PbfFileScanner.Fields(buffer.Payload!);
                    PbfField bytes = parts.Single(static f => f.Number == 2);
                    rows.Add(new BufferRow(packetId, checked((int)PbfFileScanner.Value(parts, 1)), bytes.Payload!));
                }
            }
        }

        return rows.Count == 0
            ? null
            : rows;
    }

    /// <summary>
    /// Reads the field 16 of every columnar block of a PBF file: a sub-message of three parallel columns. Sub-field 1 holds the packet
    /// ids as a base value (its sub-field 1) and differences (its sub-field 2), both zigzag varints; sub-field 2 repeats the index as a
    /// varint; sub-field 3 repeats the bytes.
    /// </summary>
    /// <param name="path">The PBF file written without compression.</param>
    /// <returns>The rows; <see langword="null"/> when no block has the field.</returns>
    /// <exception cref="InvalidOperationException">The three columns of a block differ in length.</exception>
    internal static List<BufferRow>? ReadPbfColumnar(string path)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<BufferRow> rows = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField column in block.Where(static f => f.Number == 16 && f.Payload is not null))
            {
                List<PbfField> parts = PbfFileScanner.Fields(column.Payload!);
                List<long> ids = [];
                long current = 0;
                foreach (PbfField entry in PbfFileScanner.Fields(parts.Single(static f => f.Number == 1).Payload!))
                {
                    long value = checked((long)(entry.Varint >> 1)) ^ -checked((long)(entry.Varint & 1));
                    current = entry.Number == 1
                        ? value
                        : current + value;
                    ids.Add(current);
                }

                List<PbfField> indexes = [.. parts.Where(static f => f.Number == 2)];
                List<PbfField> bytes = [.. parts.Where(static f => f.Number == 3)];
                if (ids.Count != indexes.Count || ids.Count != bytes.Count)
                {
                    throw new InvalidOperationException(
                        Invariant($"The three buffer columns differ in length: {ids.Count} ids, {indexes.Count} indexes, {bytes.Count} byte entries."));
                }

                for (int i = 0; i < ids.Count; i++)
                {
                    rows.Add(new BufferRow(checked((int)ids[i]), checked((int)indexes[i].Varint), bytes[i].Payload!));
                }
            }
        }

        return rows.Count == 0
            ? null
            : rows;
    }

    #endregion

    #region JSON, CSV, Text

    /// <summary>
    /// Reads the <c>buffers</c> key of every packet object of a JSON document in any style: an array of <c>{"index":n,"bytes":"…"}</c>
    /// records with standard Base64. The packet id is the key <c>id</c> (the compact style spells it <c>ID</c>).
    /// </summary>
    /// <param name="text">The JSON document.</param>
    /// <returns>The rows; <see langword="null"/> when no packet has the key.</returns>
    internal static List<BufferRow>? ReadJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        List<BufferRow> rows = [];
        bool anyKey = false;
        foreach (JsonElement packet in document.RootElement.EnumerateArray())
        {
            if (!packet.TryGetProperty("buffers", out JsonElement buffers))
            {
                continue;
            }

            // A key with an empty array is still a key: only a key that no packet has means "no buffer content at all".
            anyKey = true;

            int packetId = packet.TryGetProperty("id", out JsonElement id)
                ? id.GetInt32()
                : packet.GetProperty("ID").GetInt32();
            foreach (JsonElement buffer in buffers.EnumerateArray())
            {
                rows.Add(new BufferRow(
                    packetId, buffer.GetProperty("index").GetInt32(), Convert.FromBase64String(buffer.GetProperty("bytes").GetString()!)));
            }
        }

        return anyKey
            ? rows
            : null;
    }

    /// <summary>
    /// Reads the <c>buffers</c> column of a CSV written with comma delimiters and the packet number (<c>No.</c>) as its first column:
    /// the cell is <c>&lt;index&gt;:&lt;HEX&gt;</c> records joined by <c>;</c>, empty for a packet without additional buffers.
    /// </summary>
    /// <param name="text">The CSV text without a byte order mark.</param>
    /// <returns>The rows; <see langword="null"/> when the header has no <c>buffers</c> column.</returns>
    internal static List<BufferRow>? ReadCsv(string text)
    {
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines[0].Split(',');
        int column = Array.IndexOf(header, "buffers");
        if (column < 0)
        {
            return null;
        }

        // The packet columns come first and hold no comma, so the cells up to the buffers column split safely; the field columns
        // after them (texts) may hold commas and are not read.
        List<BufferRow> rows = [];
        foreach (string line in lines.Skip(1))
        {
            string[] cells = line.Split(',', column + 2);
            int packetId = int.Parse(cells[0], CultureInfo.InvariantCulture);
            if (cells[column].Length == 0)
            {
                continue;
            }

            foreach (string record in cells[column].Split(';'))
            {
                int colon = record.IndexOf(':', StringComparison.Ordinal);
                rows.Add(new BufferRow(
                    packetId,
                    int.Parse(record.AsSpan(0, colon), CultureInfo.InvariantCulture),
                    _FromUpperHex(record[(colon + 1)..])));
            }
        }

        return rows.Count == 0
            ? null
            : rows;
    }

    /// <summary>
    /// Reads the <c>Buffer i (n):</c> blocks of a text export: the title line, then one line per 16 bytes (offset, two spaces, hex
    /// pairs). Checks the length in each title against the bytes it read. The text does not write the packet id, so the packet of a
    /// block is the one whose <c>Packet N</c> header line precedes it (N is the 1-based position in the export).
    /// </summary>
    /// <param name="text">The text export.</param>
    /// <param name="firstPacketId">The id of the first exported packet; the packet at position N has the id firstPacketId + N - 1.</param>
    /// <returns>The rows; <see langword="null"/> when the text has no such block.</returns>
    /// <exception cref="InvalidOperationException">The length in a block's title differs from the number of bytes of its dump.</exception>
    internal static List<BufferRow>? ReadText(string text, int firstPacketId)
    {
        string[] lines = text.Split('\n');
        List<BufferRow> rows = [];
        int packetId = firstPacketId - 1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("Packet ", StringComparison.Ordinal) && line.Contains("  [", StringComparison.Ordinal))
            {
                packetId = firstPacketId + int.Parse(line.AsSpan(7, line.IndexOf(' ', 7) - 7), CultureInfo.InvariantCulture) - 1;
                continue;
            }

            // A title is "Buffer <digits> (<digits>):"; a field line that merely starts with the word is not one.
            if (!line.StartsWith("Buffer ", StringComparison.Ordinal) || line.Length < 9 || !char.IsAsciiDigit(line[7])
                || !line.EndsWith("):", StringComparison.Ordinal))
            {
                continue;
            }

            int open = line.IndexOf('(', StringComparison.Ordinal);
            int index = int.Parse(line.AsSpan(7, line.IndexOf(' ', 7) - 7), CultureInfo.InvariantCulture);
            int declared = int.Parse(line.AsSpan(open + 1, line.IndexOf(')', StringComparison.Ordinal) - open - 1), CultureInfo.InvariantCulture);
            List<byte> bytes = [];
            while (i + 1 < lines.Length && IsDumpLine(lines[i + 1]))
            {
                i++;
                int split = lines[i].IndexOf("  ", StringComparison.Ordinal);
                bytes.AddRange(_FromUpperHex(lines[i][(split + 2)..].Replace(" ", string.Empty, StringComparison.Ordinal)));
            }

            if (declared != bytes.Count)
            {
                throw new InvalidOperationException(Invariant($"The title says {declared} bytes, the dump holds {bytes.Count}."));
            }

            rows.Add(new BufferRow(packetId, index, bytes.ToArray()));
        }

        return rows.Count == 0
            ? null
            : rows;
    }

    /// <summary>
    /// Decodes hex digits like <see cref="Convert.FromHexString(string)"/> but only upper-case ones: the formats pin upper-case hex, and
    /// the framework decoder would accept a writer that switched to lower case.
    /// </summary>
    /// <param name="hex">The digits, without separators.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">A letter of the text is lower case.</exception>
    private static byte[] _FromUpperHex(string hex)
    {
        if (hex.AsSpan().IndexOfAnyInRange('a', 'f') >= 0)
        {
            throw new InvalidOperationException(Invariant($"The hex text is not upper case: {hex}"));
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>Whether a line is a dump line: an offset of four or more upper-case hex digits, two spaces, and hex pairs.</summary>
    /// <param name="line">The text line.</param>
    /// <returns><see langword="true"/> when the line has the shape of a dump line; otherwise <see langword="false"/>.</returns>
    internal static bool IsDumpLine(string line)
    {
        int split = line.IndexOf("  ", StringComparison.Ordinal);
        if (split < 4 || split > 8)
        {
            return false;
        }

        foreach (char c in line.AsSpan(0, split))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return line.Length > split + 2;
    }

    #endregion
}
