// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// The three export shapes of the PBF columnar format, fed by a tree-free parse with the exporter's own observer: the file
/// header (format version 1 and the shape code), the blocks (no topology outside the hierarchy shape), and the position column of
/// the field columns (occurrence numbers in the flat shapes). The files are written uncompressed so the tests read the blocks
/// with a small protobuf scanner of their own.
/// </summary>
internal sealed class PbfColumnarShapeTests
{
    #region Helpers

    private static PbfExporter _Build(string path, ExportContent content) =>
        PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(PbfExportFormat.Columnar)
            .WithCompressed(false)
            .WithContent(content)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();

    private static int _FieldId(string name) => TestHarness.GetStack().GetFieldId(name)!.Value.Value;

    /// <summary>The positions written for one field across all blocks, in file order.</summary>
    private static List<int> _Positions(List<List<PbfField>> blocks, int fieldId)
    {
        List<int> positions = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField field in block)
            {
                if (field.Number != PbfFieldNumbers.ColumnarFieldColumns)
                {
                    continue;
                }

                List<PbfField> column = PbfFileScanner.Fields(field.Payload!);
                if ((int)PbfFileScanner.Value(column, PbfFieldNumbers.ColumnFieldId) != fieldId)
                {
                    continue;
                }

                foreach (PbfField entry in column)
                {
                    if (entry.Number == PbfFieldNumbers.ColumnNodeIds)
                    {
                        positions.Add((int)entry.Varint);
                    }
                }
            }
        }

        return positions;
    }

    #endregion

    [Test]
    [Arguments(ExportShape.SelectedFields, 1UL)]
    [Arguments(ExportShape.AllFields, 2UL)]
    [Arguments(ExportShape.Hierarchy, 0UL)]
    public async Task TheFileHeaderCarriesFormatVersionOneAndTheShapeCode(ExportShape shape, ulong expectedCode)
    {
        using TestDir dir = new("pbf_shape_header");
        string path = dir.FilePath("capture.pbf");
        ExportContent content = shape == ExportShape.SelectedFields
            ? ExportContents.Selected(new FieldRequest("udp.dstport"))
            : new ExportContent { Shape = shape };

        using (PbfExporter exporter = _Build(path, content))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        PbfVerifier verifier = PbfVerifier.Open(path);
        (List<PbfField> header, _) = PbfFileScanner.Read(path);

        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderVersion)).IsEqualTo(1UL);
        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderShape)).IsEqualTo(expectedCode);
    }

    [Test]
    public async Task FlatShapesWriteNoTopologyAndTheHierarchyDoes()
    {
        using TestDir dir = new("pbf_shape_topology");

        foreach ((ExportShape shape, bool expectTopology) in new[] { (ExportShape.AllFields, false), (ExportShape.Hierarchy, true) })
        {
            string path = dir.FilePath(Invariant($"{shape}.pbf"));
            using (PbfExporter exporter = _Build(path, new ExportContent { Shape = shape }))
            {
                _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
                exporter.OnFinish();
            }

            (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
            bool hasTopology = blocks.Exists(static block => block.Exists(static field => field.Number == PbfFieldNumbers.ColumnarTopology));

            await Assert.That(blocks.Count).IsGreaterThan(0);
            await Assert.That(hasTopology).IsEqualTo(expectTopology);
        }
    }

    [Test]
    [Arguments(Occurrences.First, new[] { 0 })]
    [Arguments(Occurrences.Last, new[] { 0 })]
    [Arguments(Occurrences.All, new[] { 0, 1 })]
    public async Task TheTunnelPacketWritesOccurrenceNumbersInThePositionColumn(Occurrences choice, int[] expected)
    {
        using TestDir dir = new("pbf_shape_occurrence");
        string path = dir.FilePath("capture.pbf");

        using (PbfExporter exporter = _Build(path, ExportContents.Selected(new FieldRequest("ip.src", choice))))
        {
            _ = TreeFreeFeed.Run(exporter, [PacketGenerators.CreateTunnelFrame(0)]);
            exporter.OnFinish();
        }

        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);

        await Assert.That(_Positions(blocks, _FieldId("ip.src"))).IsEquivalentTo(expected);
    }

    [Test]
    public async Task ATreePacketTheObserverDidNotSeeWritesTheSameFileAsTheTreeFreeFeed()
    {
        using TestDir dir = new("pbf_shape_feed");
        string treeFreePath = dir.FilePath("free.pbf");
        string treePath = dir.FilePath("tree.pbf");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"));

        using (PbfExporter exporter = _Build(treeFreePath, content))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        // A library user who ignores the parse demand passes ordinary tree packets: the exporter reads the selected fields
        // straight from the tree and must write what the tree-free feed wrote.
        using (PbfExporter exporter = _Build(treePath, content))
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                Packet packet = TestHarness.ParseFrame(frame);
                await Assert.That(packet.HasFieldTree).IsTrue();
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        (_, List<List<PbfField>> freeBlocks) = PbfFileScanner.Read(treeFreePath);
        (_, List<List<PbfField>> treeBlocks) = PbfFileScanner.Read(treePath);

        await Assert.That(string.Join(",", _Positions(treeBlocks, _FieldId("ip.src")))).IsEqualTo(string.Join(",", _Positions(freeBlocks, _FieldId("ip.src"))));
        await Assert.That(string.Join(",", _Positions(treeBlocks, _FieldId("udp.dstport")))).IsEqualTo(string.Join(",", _Positions(freeBlocks, _FieldId("udp.dstport"))));
        await Assert.That(File.ReadAllBytes(treePath).Length).IsEqualTo(File.ReadAllBytes(treeFreePath).Length);
    }

    [Test]
    public async Task APacketTheFilterRejectedLeavesNoRowsInTheBlock()
    {
        using TestDir dir = new("pbf_shape_commit");
        string path = dir.FilePath("capture.pbf");
        List<Frame> frames = [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]];

        using (PbfExporter exporter = _Build(path, ExportContents.Selected(new FieldRequest("udp.dstport"))))
        {
            int index = 0;
            _ = TreeFreeFeed.Run(exporter, frames, offer: _ => index++ != 1);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(2);
        }

        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);

        await Assert.That(_Positions(blocks, _FieldId("udp.dstport")).Count).IsEqualTo(2);
    }

    /// <summary>
    /// The hierarchy shape cannot write a packet parsed without a field tree: it is skipped and counted, with the error kind and
    /// message that all columnar exporters use, no exception reaches the caller, and the file holds no block.
    /// </summary>
    [Test]
    public async Task AHierarchyPacketWithoutAFieldTreeIsSkippedAsASerializationError()
    {
        using TestDir dir = new("pbf_shape_no_tree");
        string path = dir.FilePath("capture.pbf");
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in noTree);
        List<ExportErrorEventArgs> events = [];
        bool accepted;

        using (PbfExporter exporter = _Build(path, new ExportContent { Shape = ExportShape.Hierarchy }))
        {
            exporter.ItemSkipped += (_, args) => events.Add(args);

            accepted = exporter.OnPacket(packet);
            exporter.OnFinish();

            await Assert.That(exporter.SkippedCount).IsEqualTo(1);
            await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        }

        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(accepted).IsTrue(); // tolerant mode: the packet is skipped and the export goes on
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
        await Assert.That(blocks.Count).IsEqualTo(0);
    }
}
