// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using CoreField = NetworkInspector.Core.Fields.Field;

namespace NetworkInspector.Exporters.Tests.Csv;

/// <summary>
/// The three export shapes of the CSV exporter and its packet rows: the wide layout (one row per packet, one column per selected
/// field, several occurrences joined in one cell), the long layout (one row per field occurrence), the hierarchy layout (one row
/// per node with its parent and path), the two feeds (a tree-free parse with the exporter's observer and tree packets), and the
/// builder's refusals.
/// </summary>
internal sealed class CsvExporterShapeTests
{
    #region Helpers

    private static ExportContent _Selection(Occurrences occurrences = Occurrences.All, params string[] names)
    {
        FieldRequest[] requests = [.. names.Select(name => new FieldRequest(name, occurrences))];
        return CsvExporter.ContentTemplate with
        {
            Shape = ExportShape.SelectedFields,
            Fields = ExportContents.Selected(requests).Fields,
            CustomText = false,
            CustomRepresentation = false,
        };
    }

    private static CsvExporter.Builder _Builder(MemoryStream stream) =>
        CsvExporter.CreateBuilder().ToStream(stream).WithBom(false);

    /// <summary>Runs the exporter over the frames with a tree-free parse; returns the output lines.</summary>
    private static string[] _RunTreeFree(CsvExporter.Builder builder, MemoryStream stream, IReadOnlyList<Frame> frames)
    {
        using (CsvExporter exporter = builder.Build())
        {
            _ = TreeFreeFeed.Run(exporter, frames);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Blanks the value of the <c>packet.id</c> field row (the all-fields layout lists it): the harness hands out dense packet ids
    /// per thread, so two runs over the same frames carry different ids.
    /// </summary>
    private static string _WithoutPacketIds(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @",packet\.id,0,\d+,", ",packet.id,0,#,");

    private static string _SortedLines(string text) =>
        string.Join(";", text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    /// <summary>Cells of a line that has no quoted cell with a delimiter.</summary>
    private static string[] _Cells(string line) => line.Split(',');

    /// <summary>A SocketCAN frame whose eight data bytes count up from <paramref name="firstByte"/>.</summary>
    private static Frame _CanFrame(int id, byte firstByte)
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x123u);
        data[4] = 8;
        for (int i = 0; i < 8; i++)
        {
            data[8 + i] = (byte)(firstByte + i);
        }

        return TestHarness.CreateFrame(id, (long)id * 1_000_000, data, LinkType.CanSocketcan);
    }

    /// <summary>The names of all fields in the tree of a packet, as its own stack knows them.</summary>
    private static HashSet<string> _TreeNames(Packet packet)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        _CollectNames(packet, packet.RootField(), names);
        return names;
    }

    /// <summary>Adds the names of all descendants of <paramref name="parent"/> to <paramref name="names"/>.</summary>
    private static void _CollectNames(Packet packet, CoreField parent, HashSet<string> names)
    {
        foreach (CoreField child in parent.Children(materialize: true))
        {
            FieldInfo? info = packet.Stack.GetField(child.FieldId);
            names.Add(info!.Name); // Invariant: every field of a parsed packet is registered on its stack.
            _CollectNames(packet, child, names);
        }
    }

    /// <summary>
    /// The bytes the exporter allocates for the commit of a warm packet: one warm-up of 2 000 packets, then the sum of the bytes
    /// allocated inside 1 000 <c>OnPacket</c> calls. The parse before each call is not part of the sum, so nothing but the
    /// exporter is measured.
    /// </summary>
    private static long _MeasureCommit(Frame frame, CsvExporter exporter)
    {
        ParseOptions options = TreeFreeFeed.CreateOptions(exporter.ParseDemand);
        Packet? recycle = null;
        for (int i = 0; i < 2000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            _ = exporter.OnPacket(recycle);
        }

        long total = 0;
        for (int i = 0; i < 1000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = exporter.OnPacket(recycle);
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }

    #endregion

    #region Wide layout

    [Test]
    public async Task TheWideRowJoinsAllOccurrencesInOneCellAndAbsentFieldsGiveEmptyCells()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream).WithContent(_Selection(Occurrences.All, "ip.src", "udp.dstport"));

        string[] lines = _RunTreeFree(builder, stream, ShapeFrames.TunnelUdpCan());

        await Assert.That(lines[0]).IsEqualTo("No.,Time,ip.src,udp.dstport");
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(_Cells(lines[1])[2]).IsEqualTo("10.0.0.1|192.168.0.1");
        await Assert.That(_Cells(lines[1])[3]).IsEqualTo("53");
        await Assert.That(_Cells(lines[2])[2]).IsEqualTo("192.168.1.1");

        // The CAN frame has neither field: both cells are empty, and the row keeps the column count of the header.
        await Assert.That(_Cells(lines[3]).Length).IsEqualTo(4);
        await Assert.That(_Cells(lines[3])[2]).IsEmpty();
        await Assert.That(_Cells(lines[3])[3]).IsEmpty();
    }

    [Test]
    [Arguments(Occurrences.First, "10.0.0.1")]
    [Arguments(Occurrences.Last, "192.168.0.1")]
    public async Task FirstAndLastGiveASingleValueInTheCell(Occurrences choice, string expected)
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream).WithContent(_Selection(choice, "ip.src"));

        string[] lines = _RunTreeFree(builder, stream, [PacketGenerators.CreateTunnelFrame(0)]);

        await Assert.That(_Cells(lines[1])[2]).IsEqualTo(expected);
    }

    [Test]
    public async Task TheDelimiterInTheSeparatorQuotesTheWholeCellOnce()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream)
            .WithContent(_Selection(Occurrences.All, "ip.src"))
            .WithOccurrenceSeparator(",");

        string[] lines = _RunTreeFree(builder, stream, [PacketGenerators.CreateTunnelFrame(0)]);

        await Assert.That(lines[1]).EndsWith(",\"10.0.0.1,192.168.0.1\"");
    }

    [Test]
    public async Task ASemicolonSeparatorNeedsNoQuoting()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream)
            .WithContent(_Selection(Occurrences.All, "ip.src"))
            .WithOccurrenceSeparator(";");

        string[] lines = _RunTreeFree(builder, stream, [PacketGenerators.CreateTunnelFrame(0)]);

        await Assert.That(lines[1]).EndsWith(",10.0.0.1;192.168.0.1");
    }

    [Test]
    public async Task TextColumnsAppearBehindTheirFieldWhenTheContentAsksForTexts()
    {
        using MemoryStream stream = new();
        ExportContent content = _Selection(Occurrences.All, "udp.dstport") with { CustomText = true, CustomRepresentation = true };
        CsvExporter.Builder builder = _Builder(stream).WithContent(content);

        string[] lines = _RunTreeFree(builder, stream, [ShapeFrames.TunnelUdpCan()[1]]);

        await Assert.That(lines[0]).IsEqualTo("No.,Time,udp.dstport,udp.dstport.repr,udp.dstport.text");
        await Assert.That(_Cells(lines[1]).Length).IsEqualTo(5);
        await Assert.That(_Cells(lines[1])[2]).IsEqualTo("53");
    }

    #endregion

    #region Long and hierarchy layouts

    [Test]
    public async Task TheLongLayoutWritesOneRowPerOccurrenceInProductionOrder()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream).WithContent(CsvExporter.ContentTemplate with
        {
            CustomText = false,
            CustomRepresentation = false,
        });

        string[] lines = _RunTreeFree(builder, stream, [PacketGenerators.CreateTunnelFrame(0)]);
        string[] ipSource = [.. lines.Where(static line => line.Contains(",ip.src,", StringComparison.Ordinal))];

        await Assert.That(lines[0]).IsEqualTo("No.,Time,field,occurrence,value");
        await Assert.That(ipSource.Length).IsEqualTo(2);
        await Assert.That(_Cells(ipSource[0])[3]).IsEqualTo("0");
        await Assert.That(_Cells(ipSource[0])[4]).IsEqualTo("10.0.0.1");
        await Assert.That(_Cells(ipSource[1])[3]).IsEqualTo("1");
        await Assert.That(_Cells(ipSource[1])[4]).IsEqualTo("192.168.0.1");
    }

    [Test]
    public async Task TheHierarchyLayoutRebuildsTheTreeFromNodeAndParentIds()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = _Builder(stream).WithContent(CsvExporter.ContentTemplate with
        {
            Shape = ExportShape.Hierarchy,
            CustomText = false,
            CustomRepresentation = false,
        });

        string[] lines = _RunTreeFree(builder, stream, [ShapeFrames.TunnelUdpCan()[1]]);
        List<(int Node, int Parent, string Path)> nodes = [];
        foreach (string line in lines.Skip(1))
        {
            // The paths and field names of this packet contain no comma, so a plain split is exact here.
            string[] cells = _Cells(line);
            nodes.Add((int.Parse(cells[2], CultureInfo.InvariantCulture), int.Parse(cells[3], CultureInfo.InvariantCulture), cells[4]));
        }

        await Assert.That(lines[0]).IsEqualTo("No.,Time,node_id,parent_node_id,path,field,value");
        await Assert.That(nodes[0].Node).IsEqualTo(0);

        // A parent's id is smaller than its children's, node ids count up from 0, and a path extends its parent's path by one name.
        foreach ((int node, int parent, string path) in nodes)
        {
            await Assert.That(parent).IsLessThan(node);
            if (parent >= 0)
            {
                string parentPath = nodes[parent].Path;
                await Assert.That(path.StartsWith(parentPath + "/", StringComparison.Ordinal)).IsTrue();
            }
        }

        await Assert.That(nodes.Exists(static node => node.Path.EndsWith("/udp.dstport", StringComparison.Ordinal))).IsTrue();
    }

    #endregion

    #region Feeds and packet rows

    [Test]
    public async Task TheTreeFreeFeedAndTheTreeFeedWriteIdenticalCsvForTheFlatShapes()
    {
        foreach (ExportShape shape in new[] { ExportShape.SelectedFields, ExportShape.AllFields })
        {
            ExportContent content = shape == ExportShape.SelectedFields
                ? _Selection(Occurrences.All, "ip.src", "udp.dstport", "can.id")
                : CsvExporter.ContentTemplate;
            using MemoryStream free = new();
            using MemoryStream tree = new();

            using (CsvExporter exporter = _Builder(free).WithContent(content).WithColumn(CsvColumnKind.FrameLength).Build())
            {
                _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
                exporter.OnFinish();
            }

            using (CsvExporter exporter = _Builder(tree).WithContent(content).WithColumn(CsvColumnKind.FrameLength).Build())
            {
                foreach (Frame frame in ShapeFrames.TunnelUdpCan())
                {
                    _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
                }

                exporter.OnFinish();
            }

            // The packet length column replaces number and time, which differ between the two runs (dense ids per thread).
            string freeText = _WithoutPacketIds(Encoding.UTF8.GetString(free.ToArray()));
            string treeText = _WithoutPacketIds(Encoding.UTF8.GetString(tree.ToArray()));

            // The wide layout is identical byte for byte. The long layout holds the same rows, but a row order that follows the feed: a
            // parse delivers the packet-level info field last (the packet protocol appends it after the dispatch), the tree holds it
            // before the frame, so the rows are compared sorted.
            if (shape == ExportShape.SelectedFields)
            {
                await Assert.That(treeText).IsEqualTo(freeText);
            }
            else
            {
                await Assert.That(_SortedLines(treeText)).IsEqualTo(_SortedLines(freeText));
            }
        }
    }

    [Test]
    [Arguments(Occurrences.All)]
    [Arguments(Occurrences.First)]
    [Arguments(Occurrences.Last)]
    public async Task TheDirectTreeCellsEqualTheStagedCellsForEveryOccurrenceChoiceWithTexts(Occurrences choice)
    {
        // The tree feed reads the selected fields straight from the tree (no stage); the tree-free feed stages them. With the texts
        // and representations switched on, both must write the same bytes, cell by cell.
        ExportContent content = CsvExporter.ContentTemplate with
        {
            Shape = ExportShape.SelectedFields,
            Fields = ExportContents.Selected(new FieldRequest("ip.src", choice), new FieldRequest("udp.dstport", choice), new FieldRequest("can.id", choice)).Fields,
            CustomText = true,
            CustomRepresentation = true,
        };
        using MemoryStream free = new();
        using MemoryStream tree = new();

        using (CsvExporter exporter = _Builder(free).WithContent(content).WithColumn(CsvColumnKind.FrameLength).Build())
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        using (CsvExporter exporter = _Builder(tree).WithContent(content).WithColumn(CsvColumnKind.FrameLength).Build())
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        string freeText = Encoding.UTF8.GetString(free.ToArray());
        string treeText = Encoding.UTF8.GetString(tree.ToArray());

        await Assert.That(freeText.Split("\r\n").Length).IsGreaterThan(3);
        await Assert.That(treeText).IsEqualTo(freeText);
    }

    [Test]
    public async Task PacketRowsAreWrittenForAPacketParsedWithoutAFieldTreeWithTheSameBytes()
    {
        using MemoryStream free = new();
        using MemoryStream tree = new();
        Frame frame = ShapeFrames.TunnelUdpCan()[1];

        using (CsvExporter exporter = _Builder(free).WithColumn(CsvColumnKind.Info).WithColumn(CsvColumnKind.FrameLength).Build())
        {
            ParseOptions options = TreeFreeFeed.CreateOptions(exporter.ParseDemand);
            Packet packet = TestHarness.ParseFrame(frame, in options);

            await Assert.That(packet.HasFieldTree).IsFalse();
            await Assert.That(exporter.OnPacket(packet)).IsTrue();
            exporter.OnFinish();
        }

        using (CsvExporter exporter = _Builder(tree).WithColumn(CsvColumnKind.Info).WithColumn(CsvColumnKind.FrameLength).Build())
        {
            _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            exporter.OnFinish();
        }

        await Assert.That(Encoding.UTF8.GetString(free.ToArray())).IsEqualTo(Encoding.UTF8.GetString(tree.ToArray()));
    }

    [Test]
    public async Task ThePacketRowsDemandIsATreeFreeParseWithoutObserver()
    {
        using MemoryStream stream = new();
        using CsvExporter exporter = _Builder(stream).Build();

        ParseDemand demand = exporter.ParseDemand;

        await Assert.That(demand.Depth).IsEqualTo(ParseDepth.Packet);
        await Assert.That(demand.Observer.Observer).IsNull();
    }

    [Test]
    public async Task APacketWithNeitherTreeNorStagedFieldsIsSkippedWithTheNamedMessage()
    {
        using MemoryStream stream = new();
        List<ExportErrorEventArgs> errors = [];
        using CsvExporter exporter = _Builder(stream).WithContent(_Selection(Occurrences.All, "udp.dstport")).Build();
        exporter.ItemSkipped += (_, error) => errors.Add(error);
        ParseOptions noObserver = new(FieldTreeMode.Skip);

        _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]], in noObserver);

        await Assert.That(exporter.PacketCount).IsEqualTo(0);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("neither a field tree nor staged fields");
    }

    [Test]
    public async Task TheHierarchyShapeSkipsAPacketWithoutATree()
    {
        using MemoryStream stream = new();
        List<ExportErrorEventArgs> errors = [];
        using CsvExporter exporter = _Builder(stream).WithContent(CsvExporter.ContentTemplate with { Shape = ExportShape.Hierarchy }).Build();
        exporter.ItemSkipped += (_, error) => errors.Add(error);
        ParseOptions noTree = new(FieldTreeMode.Skip);

        _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]], in noTree);

        await Assert.That(exporter.PacketCount).IsEqualTo(0);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(errors[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
    }

    #endregion

    #region Field names of a second stack

    [Test]
    [Arguments(ExportShape.AllFields, 2, true)]
    [Arguments(ExportShape.AllFields, 2, false)]
    [Arguments(ExportShape.Hierarchy, 5, true)]
    [Arguments(ExportShape.Hierarchy, 5, false)]
    public async Task TheFieldNamesOfARowAreThoseOfTheStackOfItsOwnPacket(ExportShape shape, int nameColumn, bool customStackFirst)
    {
        // The custom stack registers other fields than the standard stack, so the same field id names another field in each. An
        // export that spans both (what a session restart produces) must label every row with the name of its own packet's stack.
        using CustomFieldPacketFactory factory = new();
        Packet standard = TestHarness.ParseFrame(TestHarness.CreateFrame(1, 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(8)));
        Packet custom = factory.Create(77, "hello");
        while (custom.Id.Value == standard.Id.Value)
        {
            // A session numbers its packets uniquely; two packets with one number would read as the staging of the same packet.
            custom = factory.Create(77, "hello");
        }

        Packet[] order = customStackFirst
            ? [custom, standard]
            : [standard, custom];
        using MemoryStream stream = new();
        using (CsvExporter exporter = _Builder(stream)
            .WithContent(CsvExporter.ContentTemplate with { Shape = shape, CustomText = false, CustomRepresentation = false })
            .Build())
        {
            foreach (Packet packet in order)
            {
                await Assert.That(exporter.OnPacket(packet)).IsTrue();
            }

            exporter.OnFinish();
        }

        string[][] rows = [.. Encoding.UTF8.GetString(stream.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(_Cells)];

        // The packets are told apart by the time column: the custom packet is one second after the epoch, the standard one a millisecond.
        foreach (Packet packet in order)
        {
            string time = packet.Timestamp.Format();
            string[] names = [.. rows.Where(cells => cells[1] == time).Select(cells => cells[nameColumn])];
            HashSet<string> own = _TreeNames(packet);

            await Assert.That(names.Length).IsGreaterThan(0);
            await Assert.That(names.All(own.Contains)).IsTrue();
        }
    }

    #endregion

    #region Bytes values

    [Test]
    [Arguments(ExportShape.SelectedFields)]
    [Arguments(ExportShape.AllFields)]
    public async Task ABytesFieldIsWrittenAsHexPairsInTheWideAndTheLongLayout(ExportShape shape)
    {
        using MemoryStream stream = new();
        ExportContent content = shape == ExportShape.SelectedFields
            ? _Selection(Occurrences.All, "can.data")
            : CsvExporter.ContentTemplate with { CustomText = false, CustomRepresentation = false };
        CsvExporter.Builder builder = _Builder(stream).WithContent(content);

        string[] lines = _RunTreeFree(builder, stream, [_CanFrame(0, 0xA1), _CanFrame(1, 0xB1)]);
        string[] dataCells = shape == ExportShape.SelectedFields
            ? [.. lines.Skip(1).Select(static line => _Cells(line)[2])]
            : [.. lines.Where(static line => line.Contains(",can.data,", StringComparison.Ordinal)).Select(static line => _Cells(line)[4])];

        // The second packet carries other bytes: each row holds the bytes of its own packet, not a leftover of the arena.
        await Assert.That(dataCells.Length).IsEqualTo(2);
        await Assert.That(dataCells[0]).IsEqualTo("A1 A2 A3 A4 A5 A6 A7 A8");
        await Assert.That(dataCells[1]).IsEqualTo("B1 B2 B3 B4 B5 B6 B7 B8");
    }

    [Test]
    [Category("Slow")]
    public async Task ACommittedPacketWithBytesFieldsAllocatesNothingInTheWideLayout()
    {
        // Texts are switched off: a text is a string per field by definition. What remains is the formatting of the values, of which
        // a bytes value used to copy its payload into a new array. The packet has three bytes fields among the selected ones.
        ExportContent content = _Selection(Occurrences.All, "can.id", "can.data", "frame.data", "frame.len");
        using CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(Stream.Null).WithBom(false).WithContent(content).Build();

        long allocated = _MeasureCommit(_CanFrame(0, 0xA1), exporter);

        // A few KB of slack cover one-off lazy initialization; one array per bytes value would add at least 2 000 arrays of 32 bytes.
        await Assert.That(allocated).IsLessThanOrEqualTo(4096L);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(exporter.PacketCount).IsEqualTo(3000);
    }

    [Test]
    [Category("Slow")]
    public async Task ACommittedPacketWithBytesFieldsAddsNoAllocationToItsInfoTextInTheLongLayout()
    {
        // The all-fields layout also formats packet.info, a deferred text that is built when the row is formatted. That string is
        // the one allocation of the layout, so it is measured alone and the whole layout may exceed it by the slack only.
        using CsvExporter infoOnly = CsvExporter.CreateBuilder()
            .ToStream(Stream.Null)
            .WithBom(false)
            .WithContent(_Selection(Occurrences.All, "packet.info"))
            .Build();
        using CsvExporter all = CsvExporter.CreateBuilder()
            .ToStream(Stream.Null)
            .WithBom(false)
            .WithContent(CsvExporter.ContentTemplate with { CustomText = false, CustomRepresentation = false })
            .Build();

        long infoText = _MeasureCommit(_CanFrame(0, 0xA1), infoOnly);
        long allocated = _MeasureCommit(_CanFrame(0, 0xA1), all);

        // Four bytes values per packet (frame, frame.data, can, can.data) would add 4 000 arrays of 40 bytes over the measured rounds.
        await Assert.That(infoText).IsGreaterThan(0L);
        await Assert.That(allocated).IsLessThanOrEqualTo(infoText + 4096L);
        await Assert.That(all.ErrorCount).IsEqualTo(0);
    }

    #endregion

    #region Builder refusals

    [Test]
    public async Task TheContentOfCsvRefusesInfoFrameBytesAndAShapeThatDoesNotMatchItsFields()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();

        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { Info = true })).Throws<ArgumentException>();
        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { FrameBytes = true })).Throws<ArgumentException>();
        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { Shape = ExportShape.SelectedFields })).Throws<ArgumentException>();
        await Assert.That(() => builder.WithContent(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("a\nb")]
    [Arguments("a\rb")]
    public async Task TheOccurrenceSeparatorRefusesEmptyTextAndLineBreaks(string separator)
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();

        await Assert.That(() => builder.WithOccurrenceSeparator(separator)).Throws<ArgumentException>();
    }

    #endregion
}
