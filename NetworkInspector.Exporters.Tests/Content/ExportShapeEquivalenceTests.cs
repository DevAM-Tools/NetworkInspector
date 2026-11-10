// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The promise that makes the shapes interchangeable: for each selected field and packet, the hierarchy output, the selected-fields
/// output fed tree-free, and the selected-fields output fed from tree packets hold the same values, custom representations, and
/// custom texts (the hierarchy rows are numbered by node order to get their occurrence). Checked on DuckDB, whose SQL makes the
/// comparison direct; the other formats share the batch that is compared here.
/// </summary>
[Category("Slow")]
internal sealed class ExportShapeEquivalenceTests
{
    private static readonly string[] _Names = ["ip.src", "udp.length", "dns.qry.name", "can.id"];

    private static List<Frame> _Frames() =>
    [
        TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(8)),
        TestHarness.CreateFrame(1, 1_000_000, FrameGenerators.BuildDnsQueryFrame("example.com")),
        PacketGenerators.CreateTunnelFrame(2),
        PacketGenerators.CreateCanFrame(3),
        TestHarness.CreateFrame(4, 4_000_000, FrameGenerators.BuildDnsQueryFrame("a.example.org")),
    ];

    private static DuckDbExporter _Selected(string path) =>
        DuckDbExporter.CreateBuilder().ToFile(path).WithContent(_Selection()).WithWriteMode(ExportWriteMode.Synchronous).Build();

    private static ExportContent _Selection() =>
        ExportContents.Selected(_Names.Select(static name => new FieldRequest(name)).ToArray());

    /// <summary>
    /// All rows of the named fields as comparable lines, numbered by packet ordinal (the packet ids differ between the runs because
    /// the harness hands out dense ids per thread) and by occurrence within the packet.
    /// </summary>
    private static List<string> _Rows(string path, bool hierarchy)
    {
        List<string> lines = [];
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        foreach (string name in _Names)
        {
            int id = TestHarness.GetStack().GetFieldId(name)!.Value.Value;
            string order = hierarchy ? "node_id" : "occurrence";
            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT DENSE_RANK() OVER (ORDER BY packet_id) - 1, ROW_NUMBER() OVER (PARTITION BY packet_id ORDER BY " + order + ") - 1, "
                + "CAST(value AS VARCHAR), custom_repr, custom_text "
                + Invariant($"FROM field_{id} ORDER BY packet_id, 2");
            using DuckDBDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(string.Join('|', name, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), _Cell(reader, 3), _Cell(reader, 4)));
            }
        }

        return lines;
    }

    private static string _Cell(DuckDBDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
        {
            return "<null>";
        }

        return reader.GetString(column);
    }

    [Test]
    public async Task HierarchyTreeFreeAndTreeFedOutputsHoldTheSameValuesAndTexts()
    {
        using TestDir dir = new("shape_equivalence");
        string hierarchyPath = dir.FilePath("hierarchy.duckdb");
        string freePath = dir.FilePath("free.duckdb");
        string treePath = dir.FilePath("tree.duckdb");

        // Hierarchy: the field tree of every packet, then the rows of the selected fields are read back.
        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(hierarchyPath).WithWriteMode(ExportWriteMode.Synchronous).Build())
        {
            foreach (Frame frame in _Frames())
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        // Selected fields, tree-free: the exporter's observer stages the fields while each packet is parsed.
        using (DuckDbExporter exporter = _Selected(freePath))
        {
            _ = TreeFreeFeed.Run(exporter, _Frames());
            exporter.OnFinish();
        }

        // Selected fields, from tree packets: the library user ignores the parse demand.
        using (DuckDbExporter exporter = _Selected(treePath))
        {
            foreach (Frame frame in _Frames())
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        List<string> fromHierarchy = _Rows(hierarchyPath, hierarchy: true);
        List<string> fromTreeFree = _Rows(freePath, hierarchy: false);
        List<string> fromTree = _Rows(treePath, hierarchy: false);

        // The mix has values for every field (the tunnel packet holds ip.src twice, a DNS query has a name, CAN an id), so the
        // comparison is not vacuous.
        await Assert.That(string.Join(';', fromTreeFree)).IsEqualTo(string.Join(';', fromHierarchy));
        await Assert.That(string.Join(';', fromTree)).IsEqualTo(string.Join(';', fromHierarchy));
    }
}
