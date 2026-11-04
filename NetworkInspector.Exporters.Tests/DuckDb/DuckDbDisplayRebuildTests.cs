// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// The reader rule of the exporter guide, executed: the display line of every field of a packet is rebuilt from the rows of a DuckDB
/// export (catalog, topology, field tables) with the documented rule and the documented default texts, and every rebuilt line equals
/// the line the Text export of the same packet shows at the same tree position. The rule used here is written out in the test, not
/// shared with the exporters, so a change of the rule or of a stored form fails the test.
/// Slow: every test writes and reads a DuckDB file.
/// </summary>
/// <remarks>Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).</remarks>
[Category("Slow")]
internal sealed class DuckDbDisplayRebuildTests
{
    #region The reader rule

    /// <summary>One field row of the export: what a reader finds for a node with a value.</summary>
    /// <param name="Value">The value as DuckDB returns it (BLOB as bytes).</param>
    /// <param name="Representation">The custom representation, or <see langword="null"/> (NULL) when none was set.</param>
    /// <param name="Text">The custom text, or <see langword="null"/> (NULL) when none was set.</param>
    private readonly record struct FieldRow(object Value, string? Representation, string? Text);

    /// <summary>
    /// The default text of a stored value, written from the exporter guide's table of default texts (§ Rebuilding the display line):
    /// what a reader turns each stored form into when no custom representation was set.
    /// </summary>
    /// <param name="type">The field type from <c>catalog.field_type</c>.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>The default text.</returns>
    /// <remarks>Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (the table of default texts).</remarks>
    /// <exception cref="NotSupportedException">The test packet holds a type the table here does not cover.</exception>
    private static string _DefaultText(FieldType type, object value)
    {
        switch (type)
        {
            case FieldType.Bool:
                return (bool)value
                    ? "True"
                    : "False";
            case FieldType.I64:
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            case FieldType.U64:
                return ((ulong)value).ToString(CultureInfo.InvariantCulture);
            case FieldType.F64:
                return ((double)value).ToString(CultureInfo.InvariantCulture);
            case FieldType.String:
                return (string)value;
            case FieldType.Bytes:
                return string.Join(' ', ((byte[])value).Select(static b => b.ToString("X2", CultureInfo.InvariantCulture)));
            case FieldType.Timestamp:
                return _IsoNanos((long)value);
            case FieldType.MacAddress:
                // Stored as UBIGINT: the 48-bit address, most significant byte first (the guide's integer form of a MAC address).
                return _HexPairs((ulong)value, byteCount: 6);
            case FieldType.Eui64:
                // Stored as UBIGINT: the 64-bit identifier, most significant byte first.
                return _HexPairs((ulong)value, byteCount: 8);
            case FieldType.IPv6Address:
                // Stored as UHUGEINT: the 128-bit number, read here from its decimal text. Eight groups of sixteen bits, upper-case hex,
                // the longest run of zero groups (two or more, the first when tied) written as ::.
                return _Ipv6Text(UInt128.Parse((string)value, CultureInfo.InvariantCulture));
            case FieldType.Uuid:
                // Stored as the native UUID; its text is the lower-case form with dashes, so upper-case it for the default text.
                return ((string)value).ToUpperInvariant();
            case FieldType.IPv4Address:
                // Stored as UINTEGER: the address as a number; the dotted form is its four bytes, most significant first.
                uint address = (uint)value;
                return ReaderRules.IPv4Text(address);
            default:
                throw new NotSupportedException(Invariant($"The test table has no default text for {type}."));
        }
    }

    /// <summary>
    /// An IPv6 address as its default text: upper-case hex groups without leading zeros, the longest run of zero groups compressed to
    /// <c>::</c>.
    /// </summary>
    /// <param name="address">The stored 128-bit number.</param>
    /// <returns>The text, for example <c>2001:DB8::1</c>.</returns>
    private static string _Ipv6Text(UInt128 address)
    {
        ushort[] groups = new ushort[8];
        for (int index = 0; index < 8; index++)
        {
            groups[index] = checked((ushort)((address >> (16 * (7 - index))) & 0xFFFFUL));
        }

        // The longest run of zero groups of length two or more; the first one wins a tie.
        int bestStart = -1;
        int bestLength = 1;
        for (int start = 0; start < 8; start++)
        {
            int length = 0;
            while (start + length < 8 && groups[start + length] == 0)
            {
                length++;
            }

            if (length > bestLength)
            {
                bestStart = start;
                bestLength = length;
            }
        }

        StringBuilder text = new();
        for (int index = 0; index < 8; index++)
        {
            if (index == bestStart)
            {
                text.Append("::");
                index += bestLength - 1;
                continue;
            }

            if (text.Length > 0 && text[^1] != ':')
            {
                text.Append(':');
            }

            text.Append(groups[index].ToString("X", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>The low <paramref name="byteCount"/> bytes of a number as upper-case hex pairs joined with <c>:</c>, most significant byte first.</summary>
    /// <param name="value">The stored integer.</param>
    /// <param name="byteCount">The number of bytes of the address (6 for a MAC address, 8 for an EUI-64).</param>
    /// <returns>The text, for example <c>00:1A:2B:3C:4D:5E</c>.</returns>
    private static string _HexPairs(ulong value, int byteCount)
    {
        string[] pairs = new string[byteCount];
        for (int index = 0; index < byteCount; index++)
        {
            int shift = 8 * (byteCount - 1 - index);
            pairs[index] = ((value >> shift) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join(':', pairs);
    }

    /// <summary>A timestamp in nanoseconds since the Unix epoch as the default text writes it: ISO 8601 with nine digits and <c>Z</c>.</summary>
    /// <param name="nanos">The stored timestamp in nanoseconds since the Unix epoch.</param>
    /// <returns>The text, for example <c>1970-01-01T00:00:01.000000002Z</c>.</returns>
    private static string _IsoNanos(long nanos)
    {
        DateTime seconds = DateTime.UnixEpoch.AddSeconds(nanos / 1_000_000_000L);
        return seconds.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + "."
            + (nanos % 1_000_000_000L).ToString("D9", CultureInfo.InvariantCulture)
            + "Z";
    }

    /// <summary>
    /// The display line of a field by the documented rule: a custom text is the whole line; a node without a value shows its UI
    /// name; otherwise UI name, <c>": "</c>, and the custom representation, or the default text of the stored value.
    /// </summary>
    /// <param name="uiName">The UI name from <c>catalog.ui_name</c>.</param>
    /// <param name="type">The field type from <c>catalog.field_type</c>.</param>
    /// <param name="row">The field row, or <see langword="null"/> for a node without a value.</param>
    /// <param name="topologyText">The custom text of the topology row (nodes without a value).</param>
    /// <returns>The line.</returns>
    private static string _Rebuild(string uiName, FieldType type, FieldRow? row, string? topologyText)
    {
        string? text = row is null
            ? topologyText
            : row.Value.Text;
        if (text is not null)
        {
            return text;
        }

        if (row is null)
        {
            return uiName;
        }

        return uiName + ": " + (row.Value.Representation
            ?? _DefaultText(type, row.Value.Value));
    }

    #endregion

    #region Reading the export

    /// <summary>Reads the whole hierarchy export of one packet and rebuilds the indented line of every node in pre-order.</summary>
    /// <param name="path">The finished <c>.duckdb</c> file.</param>
    /// <returns>One line per node: two spaces of indentation per level of depth, then the rebuilt line.</returns>
    private static List<string> _RebuildLines(string path)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        Dictionary<int, (string UiName, FieldType Type)> catalog = [];
        using (DuckDBCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT field_id, ui_name, field_type FROM catalog";
            using DuckDBDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                catalog[reader.GetInt32(0)] = (reader.GetString(1), (FieldType)reader.GetInt32(2));
            }
        }

        // The rows of every value table, keyed by (field id, node id).
        Dictionary<(int FieldId, int NodeId), FieldRow> rows = [];
        foreach ((int fieldId, (_, FieldType type)) in catalog)
        {
            if (type == FieldType.None)
            {
                continue;
            }

            using DuckDBCommand command = connection.CreateCommand();
            // A 128-bit value (IPv6 as UHUGEINT, UUID as UUID) is read as its decimal or canonical text: the rule of the guide is
            // stated on the stored number or the stored UUID, not on how a client library maps the type to a CLR type.
            string valueColumn = type is FieldType.IPv6Address or FieldType.Uuid
                ? "CAST(value AS VARCHAR)"
                : "value";
            command.CommandText =
                Invariant($"SELECT node_id, {valueColumn}, custom_repr, custom_text FROM field_{fieldId.ToString(CultureInfo.InvariantCulture)}");
            using DuckDBDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                object value = reader.GetValue(1);
                if (value is Stream blob)
                {
                    using MemoryStream copy = new();
                    blob.CopyTo(copy);
                    value = copy.ToArray();
                }

                rows[(fieldId, reader.GetInt32(0))] = new FieldRow(
                    value,
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetString(2),
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetString(3));
            }
        }

        // The topology gives the order (node ids are pre-order) and the depth (the parent chain).
        List<string> lines = [];
        Dictionary<int, int> depthByNode = [];
        using (DuckDBCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT node_id, field_id, parent_node_id, custom_text FROM topology ORDER BY node_id";
            using DuckDBDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int nodeId = reader.GetInt32(0);
                int fieldId = reader.GetInt32(1);
                int parent = reader.GetInt32(2);
                string? topologyText = reader.IsDBNull(3)
                    ? null
                    : reader.GetString(3);
                int depth = parent < 0
                    ? 0
                    : depthByNode[parent] + 1;
                depthByNode[nodeId] = depth;

                (string uiName, FieldType type) = catalog[fieldId];
                FieldRow? row = rows.TryGetValue((fieldId, nodeId), out FieldRow found)
                    ? found
                    : null;
                lines.Add(new string(' ', depth * 2) + _Rebuild(uiName, type, row, topologyText));
            }
        }

        return lines;
    }

    /// <summary>
    /// Exports a packet to Text at the Full level without a length limit and returns all of its tree lines: the lines between the header
    /// and the blank separator line, so a node that one side lost or added shows as a difference in the count.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>The lines, one per node in pre-order.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string[] _TextLines(Packet packet)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithDetailLevel(TextDetailLevel.Full).WithMaxTextLength(0).Build())
        {
            bool accepted = exporter.OnPacket(packet);
            exporter.OnFinish();
            if (!accepted)
            {
                throw new InvalidOperationException("The Text exporter refused the packet.");
            }
        }

        string[] all = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        int end = Array.IndexOf(all, string.Empty, 1);
        return all[1..end];
    }

    /// <summary>Exports the packet to DuckDB (hierarchy, texts on) and to Text and compares every rebuilt line with its Text line.</summary>
    /// <param name="packet">A tree packet of its stack.</param>
    /// <param name="directoryPrefix">Prefix of the temporary directory.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    /// <exception cref="InvalidOperationException">The DuckDB exporter refused the packet.</exception>
    private static async Task _AssertRebuiltLinesEqualTheTextLines(Packet packet, string directoryPrefix)
    {
        using TestDir dir = new(directoryPrefix);
        string path = dir.FilePath("export.duckdb");
        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path)
            .WithContent(new ExportContent { Shape = ExportShape.Hierarchy, Info = false, FrameBytes = false })
            .WithWriteMode(ExportWriteMode.Synchronous).Build())
        {
            bool accepted = exporter.OnPacket(packet);
            exporter.OnFinish();
            if (!accepted)
            {
                throw new InvalidOperationException("The DuckDB exporter refused the packet.");
            }
        }

        List<string> rebuilt = _RebuildLines(path);
        string[] text = _TextLines(packet);

        // The complete lists are compared: a node that the export lost or added (a different count) fails, not only a different line.
        await Assert.That(rebuilt.Count).IsGreaterThan(5);
        await Assert.That(rebuilt).IsEquivalentTo(text, CollectionOrdering.Matching);
    }

    #endregion

    #region Rebuilt lines equal the Text export

    /// <summary>The lines rebuilt from the DuckDB rows of a real Ethernet, IPv4 and UDP packet equal the lines of the Text export.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryLineRebuiltFromTheDuckDbRowsOfARealPacketEqualsItsTextLine()
    {
        // A real Ethernet, IPv4, UDP packet: layers with custom texts, a layer with a custom representation, values with and
        // without texts, addresses, a timestamp, and raw byte leaves.
        await _AssertRebuiltLinesEqualTheTextLines(PacketGenerators.CreateEthernetUdpPackets(1)[0], "duckdb_rebuild_udp");
    }

    /// <summary>The lines rebuilt from the DuckDB rows of a TCP SYN packet equal the lines of the Text export.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryLineRebuiltFromTheDuckDbRowsOfATcpSynEqualsItsTextLine()
    {
        await _AssertRebuiltLinesEqualTheTextLines(
            PacketGenerators.CreateParsedPacket(0, FrameGenerators.BuildEthernetIpv4TcpSynFrame()), "duckdb_rebuild_tcp");
    }

    /// <summary>
    /// The lines rebuilt from the stored integer forms of every address type equal the lines of the Text export, so each documented
    /// conversion of a stored form gives the text that the Text export writes.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryLineRebuiltFromTheDuckDbRowsOfTheAddressProbeEqualsItsTextLine()
    {
        // Every address type in its stored integer form (UINTEGER, UHUGEINT, UBIGINT, UUID), rebuilt into the default text of the
        // guide: the documented conversion of each stored form gives the line the Text export writes.
        using AddressProbePacketFactory factory = new();

        await _AssertRebuiltLinesEqualTheTextLines(factory.CreateTreePacket(AddressProbePacketFactory.Readable), "duckdb_rebuild_addresses");
    }

    /// <summary>
    /// The lines rebuilt from the DuckDB rows of the text probe packet (empty texts, empty representations, a representation without
    /// a text, and both together) equal the lines of the Text export.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryLineRebuiltFromTheDuckDbRowsOfTheProbePacketEqualsItsTextLine()
    {
        // The probe covers what the real packets do not: an empty custom text (the line is empty), an empty custom representation,
        // a representation without a text, and a text next to a representation.
        using TextProbePacketFactory probe = new();

        await _AssertRebuiltLinesEqualTheTextLines(probe.CreateTreePacket(), "duckdb_rebuild_probe");
    }

    #endregion
}
