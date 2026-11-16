// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// The three export shapes of the PBF standard layout, fed by a tree-free parse with the exporter's own observer. The hierarchy
/// shape keeps nested packet records; the flat shapes write a flat list of field records per packet (field id, occurrence, value,
/// texts), and the file header says which of the two a reader meets. The output is written uncompressed into memory and read with the shared
/// protobuf scanner.
/// </summary>
internal sealed class PbfStandardShapeTests
{
    #region Building exports and decoding files

    /// <summary>Builds a synchronous, uncompressed standard-layout PBF exporter for the content.</summary>
    /// <param name="stream">The target stream.</param>
    /// <param name="content">What the export contains, including its shape.</param>
    /// <returns>The exporter; the caller disposes it.</returns>
    private static PbfExporter _Build(Stream stream, ExportContent content) =>
        PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .WithContent(content)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();

    /// <summary>Content of the selected-fields shape for the requests, without frame bytes.</summary>
    /// <param name="requests">The fields to export, each with its occurrence choice.</param>
    /// <returns>The content.</returns>
    private static ExportContent _Selected(params FieldRequest[] requests) =>
        ExportContents.Selected(requests) with { FrameBytes = false };

    /// <summary>The packet messages of all blocks, each as its decoded fields.</summary>
    /// <param name="file">The bytes of the PBF file.</param>
    /// <returns>One list of decoded fields per packet, in file order.</returns>
    private static List<List<PbfField>> _Packets(byte[] file)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.FromBytes(file);
        List<List<PbfField>> packets = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField field in block)
            {
                // Inside a block payload the packet records are length-delimited; the block header's max timestamp (the same
                // number) is a varint.
                if (field.Number == PbfFieldNumbers.PacketField && field.Payload is not null)
                {
                    packets.Add(PbfFileScanner.Fields(field.Payload));
                }
            }
        }

        return packets;
    }

    /// <summary>The flat field records of one packet message.</summary>
    /// <param name="packet">The decoded fields of the packet message.</param>
    /// <returns>One list of decoded fields per field record, in file order.</returns>
    private static List<List<PbfField>> _Records(List<PbfField> packet)
    {
        List<List<PbfField>> records = [];
        foreach (PbfField field in packet)
        {
            if (field.Number == PbfFieldNumbers.PacketField && field.Payload is not null)
            {
                records.Add(PbfFileScanner.Fields(field.Payload));
            }
        }

        return records;
    }

    /// <summary>The occurrence number of a field record; a record without the field is the first occurrence.</summary>
    /// <param name="record">The decoded fields of the record.</param>
    /// <returns>The occurrence number, 0 for the first.</returns>
    private static int _Occurrence(List<PbfField> record)
    {
        ulong value = PbfFileScanner.Value(record, PbfFieldNumbers.FieldOccurrence);
        return value == ulong.MaxValue
            ? 0
            : checked((int)value);
    }

    /// <summary>The dotted text of the IPv4 address in a field record.</summary>
    /// <param name="record">The decoded fields of the record.</param>
    /// <returns>The dotted address, or <c>&lt;none&gt;</c> when the record has no fixed32 value field.</returns>
    private static string _Address(List<PbfField> record)
    {
        foreach (PbfField field in record)
        {
            // An IPv4 address is a fixed32 field (wire type 5) holding the address as a number; the scanner decodes it little-endian.
            if (field.Number == PbfFieldNumbers.FieldValue && field.WireType == 5)
            {
                uint address = checked((uint)field.Varint);
                return ReaderRules.IPv4Text(address);
            }
        }

        return "<none>";
    }

    /// <summary>
    /// One warm export (2 000 packets), then the bytes allocated by 1 000 parses, each followed by the exporter's commit when an
    /// exporter is given. The parse and the harness allocate a little per round; the caller judges the difference to a run
    /// without exporter.
    /// </summary>
    /// <param name="frame">The frame that is parsed in every round.</param>
    /// <param name="options">The parse options of the rounds.</param>
    /// <param name="exporter">The exporter that receives every packet; <see langword="null"/> measures the parse alone.</param>
    /// <returns>The bytes the current thread allocated during the 1 000 measured rounds.</returns>
    private static long _MeasureRounds(Frame frame, in ParseOptions options, PbfExporter? exporter)
    {
        Packet? recycle = null;
        for (int i = 0; i < 2000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            _ = exporter?.OnPacket(recycle);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            _ = exporter?.OnPacket(recycle);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    #endregion

    #region File header

    /// <summary>The file header carries format version 1 and the code of the export shape that a reader needs to decode the records.</summary>
    /// <param name="shape">The export shape.</param>
    /// <param name="expectedCode">The header's shape code for the shape.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ExportShape.SelectedFields, 1UL)]
    [Arguments(ExportShape.AllFields, 2UL)]
    [Arguments(ExportShape.Hierarchy, 0UL)]
    public async Task TheFileHeaderCarriesFormatVersionOneAndTheShapeCode(ExportShape shape, ulong expectedCode)
    {
        using MemoryStream stream = new();
        ExportContent content = shape == ExportShape.SelectedFields
            ? _Selected(new FieldRequest("udp.dstport"))
            : new ExportContent { Shape = shape, FrameBytes = false };

        using (PbfExporter exporter = _Build(stream, content))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        (List<PbfField> header, _) = PbfFileScanner.FromBytes(stream.ToArray());

        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderVersion)).IsEqualTo(1UL);
        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderShape)).IsEqualTo(expectedCode);
    }

    #endregion

    #region Flat records

    /// <summary>The tunnel packet writes flat records for the chosen occurrences of <c>ip.src</c>, each with its occurrence number and no children.</summary>
    /// <param name="choice">The occurrence choice of the field request.</param>
    /// <param name="expected">The expected records of the form <c>occurrence=address</c>.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(Occurrences.First, new[] { "0=10.0.0.1" })]
    [Arguments(Occurrences.Last, new[] { "0=192.168.0.1" })]
    [Arguments(Occurrences.All, new[] { "0=10.0.0.1", "1=192.168.0.1" })]
    public async Task TheTunnelPacketWritesFlatRecordsWithOccurrenceNumbers(Occurrences choice, string[] expected)
    {
        using MemoryStream stream = new();

        using (PbfExporter exporter = _Build(stream, _Selected(new FieldRequest("ip.src", choice))))
        {
            List<bool> hadTree = TreeFreeFeed.Run(exporter, [PacketGenerators.CreateTunnelFrame(0)]);
            exporter.OnFinish();

            await Assert.That(hadTree.Contains(true)).IsFalse();
        }

        List<List<PbfField>> records = _Records(_Packets(stream.ToArray()).Single());
        List<string> actual = records.Select(static record => Invariant($"{_Occurrence(record)}={_Address(record)}")).ToList();

        // A flat record carries no nested children: the shape code is what tells a reader to expect that.
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That(records.TrueForAll(static record => !record.Exists(static field => field.Number == PbfFieldNumbers.FieldChild))).IsTrue();
    }

    /// <summary>A packet that has the selected field writes one record; a packet without the field writes none.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APlainPacketGivesOneRecordAndAPacketWithoutTheFieldGivesNone()
    {
        using MemoryStream stream = new();

        using (PbfExporter exporter = _Build(stream, _Selected(new FieldRequest("ip.src"))))
        {
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[2]]);
            exporter.OnFinish();
        }

        List<List<PbfField>> packets = _Packets(stream.ToArray());

        await Assert.That(packets.Count).IsEqualTo(2);
        await Assert.That(_Records(packets[0]).Count).IsEqualTo(1);
        await Assert.That(_Records(packets[1]).Count).IsEqualTo(0);
    }

    /// <summary>A value equal to the previous packet's value is written as the same-value flag without the value and the field name.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ARepeatedValueIsFlaggedAsSameAsPreviousAndOmitted()
    {
        using MemoryStream stream = new();
        List<Frame> frames = [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]];

        using (PbfExporter exporter = _Build(stream, _Selected(new FieldRequest("udp.dstport"))))
        {
            _ = TreeFreeFeed.Run(exporter, frames);
            exporter.OnFinish();
        }

        List<List<PbfField>> packets = _Packets(stream.ToArray());
        List<PbfField> first = _Records(packets[0]).Single();
        List<PbfField> second = _Records(packets[1]).Single();

        // The first record states the value and the field's name; the second repeats the value, so it carries the flag and no value.
        await Assert.That(first.Exists(static field => field.Number == PbfFieldNumbers.FieldValue)).IsTrue();
        await Assert.That(first.Exists(static field => field.Number == PbfFieldNumbers.FieldName)).IsTrue();
        await Assert.That(second.Exists(static field => field.Number == PbfFieldNumbers.FieldValue)).IsFalse();
        await Assert.That(second.Exists(static field => field.Number == PbfFieldNumbers.FieldName)).IsFalse();
        await Assert.That(PbfFileScanner.Value(second, PbfFieldNumbers.FieldSameFlags) & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }

    #endregion

    #region Feeds and the commit rule

    /// <summary>A tree-free feed and a feed of ordinary tree packets write identical records for the same frames.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheTreeFreeFeedAndTheTreeFeedWriteIdenticalRecords()
    {
        using MemoryStream freeStream = new();
        using MemoryStream treeStream = new();
        List<Frame> frames =
        [
            .. ShapeFrames.TunnelUdpCan(),
            TestHarness.CreateFrame(3, 3_000_000, FrameGenerators.BuildDnsQueryFrame("example.com")),
        ];
        ExportContent content = _Selected(new FieldRequest("ip.src"), new FieldRequest("udp.length"), new FieldRequest("dns.qry.name"));

        using (PbfExporter exporter = _Build(freeStream, content))
        {
            _ = TreeFreeFeed.Run(exporter, frames);
            exporter.OnFinish();
        }

        // The library user ignores the parse demand and passes tree packets: the exporter walks them with the same rules.
        using (PbfExporter exporter = _Build(treeStream, content))
        {
            foreach (Frame frame in frames)
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        // Packet ids and timestamps differ between the runs; the records (field, occurrence, value, texts, flags) must not.
        static List<string> RecordsOf(MemoryStream stream) =>
            _Packets(stream.ToArray()).SelectMany(static packet => _Records(packet))
                .Select(static record => string.Join(
                    ';', record.Select(static field => Invariant($"{field.Number}:{field.Varint}:{Convert.ToHexString(field.Payload ?? [])}"))))
                .ToList();

        List<string> free = RecordsOf(freeStream);

        await Assert.That(free.Count).IsGreaterThan(4);
        await Assert.That(string.Join('|', RecordsOf(treeStream))).IsEqualTo(string.Join('|', free));
    }

    /// <summary>A packet that the filter rejected leaves no packet in the file and is not counted.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketTheFilterRejectedLeavesNoRecord()
    {
        using MemoryStream stream = new();
        List<Frame> frames = [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]];

        using (PbfExporter exporter = _Build(stream, _Selected(new FieldRequest("udp.dstport"))))
        {
            int index = 0;
            _ = TreeFreeFeed.Run(exporter, frames, offer: _ => index++ != 1);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(2);
        }

        await Assert.That(_Packets(stream.ToArray()).Count).IsEqualTo(2);
    }

    #endregion

    #region Hierarchy shape

    /// <summary>The hierarchy shape writes nested records, so at least one record of a packet has child records.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task HierarchyKeepsNestedRecordsWithChildren()
    {
        using MemoryStream stream = new();

        using (PbfExporter exporter = _Build(stream, new ExportContent { FrameBytes = false }))
        {
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]]);
            exporter.OnFinish();
        }

        List<List<PbfField>> records = _Records(_Packets(stream.ToArray()).Single());

        await Assert.That(records.Exists(static record => record.Exists(static field => field.Number == PbfFieldNumbers.FieldChild))).IsTrue();
    }

    #endregion

    #region Frame bytes

    /// <summary>
    /// The standard layout takes frame bytes in either order of the builder calls (the layout and the content do not have to agree
    /// in order), and the builder builds with the content it was given.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task FrameBytesAreAcceptedForTheStandardLayoutInEitherOrderOfTheBuilderCalls()
    {
        using MemoryStream stream = new();
        ExportContent withFrames = new() { FrameBytes = true };

        PbfExporter.Builder contentFirst = PbfExporter.CreateBuilder().ToStream(stream).WithContent(withFrames);
        PbfExporter.Builder formatFirst = PbfExporter.CreateBuilder().ToStream(stream).WithFormat(PbfExportFormat.Standard);

        await Assert.That(() => formatFirst.WithContent(withFrames)).ThrowsNothing();
        await Assert.That(() => contentFirst.WithFormat(PbfExportFormat.Standard)).ThrowsNothing();
        await Assert.That(() => formatFirst.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>A builder without <c>WithContent</c> writes the default content, which has packet info and no frame bytes.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheStandardLayoutWithoutWithContentWritesNoFrameBytes()
    {
        using MemoryStream stream = new();

        // A plain builder writes the default content, which has no frame bytes in either layout.
        using (PbfExporter exporter = PbfExporter.CreateBuilder().ToStream(stream).WithFormat(PbfExportFormat.Standard).WithCompressed(false).Build())
        {
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]]);
            exporter.OnFinish();
        }

        List<PbfField> packet = _Packets(stream.ToArray()).Single();

        await Assert.That(packet.Exists(static field => field.Number == PbfFieldNumbers.PacketInfo)).IsTrue();
        await Assert.That(packet.Exists(static field => field.Number == PbfFieldNumbers.PacketFrameBytes)).IsFalse();
    }

    /// <summary>
    /// With frame bytes on, every packet record of the standard layout holds the packet field 6 with the exact frame, in the
    /// hierarchy shape and in both flat shapes (an empty frame is a zero-length field).
    /// </summary>
    /// <param name="shape">The export shape.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ExportShape.Hierarchy)]
    [Arguments(ExportShape.AllFields)]
    [Arguments(ExportShape.SelectedFields)]
    public async Task TheStandardLayoutWritesTheExactFrameAsPacketField6(ExportShape shape)
    {
        using MemoryStream stream = new();
        List<Frame> frames = ShapeFrames.TunnelUdpCan();
        ExportContent content = shape == ExportShape.SelectedFields
            ? _Selected(new FieldRequest("udp.dstport")) with { FrameBytes = true }
            : new ExportContent { Shape = shape, FrameBytes = true };

        using (PbfExporter exporter = _Build(stream, content))
        {
            _ = TreeFreeFeed.Run(exporter, frames);
            exporter.OnFinish();
        }

        List<List<PbfField>> packets = _Packets(stream.ToArray());

        await Assert.That(packets.Count).IsEqualTo(frames.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            PbfField frameField = packets[i].Single(static field => field.Number == PbfFieldNumbers.PacketFrameBytes);

            await Assert.That(frameField.WireType).IsEqualTo(2);
            await Assert.That(frameField.Payload!).IsEquivalentTo(frames[i].Data.ToArray(), CollectionOrdering.Matching);
        }
    }

    /// <summary>An empty frame is exported as a zero-length packet field 6, never as a missing field.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnEmptyFrameIsAZeroLengthPacketField6()
    {
        using MemoryStream stream = new();
        ExportContent content = _Selected(new FieldRequest("udp.dstport")) with { FrameBytes = true };

        using (PbfExporter exporter = _Build(stream, content))
        {
            _ = TreeFreeFeed.Run(exporter, [TestHarness.CreateFrame(3, 4_000_000, [])]);
            exporter.OnFinish();
        }

        List<PbfField> packet = _Packets(stream.ToArray()).Single();
        PbfField frameField = packet.Single(static field => field.Number == PbfFieldNumbers.PacketFrameBytes);

        await Assert.That(frameField.WireType).IsEqualTo(2);
        await Assert.That(frameField.Payload!.Length).IsEqualTo(0);
    }

    #endregion

    #region Packets without a field tree

    /// <summary>
    /// The hierarchy shape cannot write a packet parsed without a field tree: it is skipped and counted, with the error kind and
    /// message that all columnar exporters use, no exception reaches the caller, and the file holds no packet.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AHierarchyPacketWithoutAFieldTreeIsSkippedAsASerializationError()
    {
        using MemoryStream stream = new();
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in noTree);
        List<ExportErrorEventArgs> events = [];
        bool accepted;

        using (PbfExporter exporter = _Build(stream, new ExportContent { FrameBytes = false }))
        {
            exporter.ItemSkipped += (_, args) => events.Add(args);

            accepted = exporter.OnPacket(packet);
            exporter.OnFinish();

            await Assert.That(exporter.SkippedCount).IsEqualTo(1);
            await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        }

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(accepted).IsTrue(); // tolerant mode: the packet is skipped and the export goes on
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
        await Assert.That(_Packets(stream.ToArray()).Count).IsEqualTo(0);
    }

    #endregion

    #region Bytes fields

    /// <summary>
    /// Exporting a bytes field in the selected-fields shape allocates no more per packet than parsing alone, apart from a small
    /// fixed slack, so no array is created for each bytes value.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task AStandardSelectedFieldsExportOfABytesFieldAddsNoAllocationPerPacket()
    {
        Frame frame = PacketGenerators.CreateCanFrame(0);
        ExportContent content = _Selected(new FieldRequest("can.data")) with { Info = false, CustomText = false, CustomRepresentation = false };
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(Stream.Null)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .WithContent(content)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        ParseOptions withExporter = TreeFreeFeed.CreateOptions(exporter.ParseDemand);
        ParseOptions without = new(FieldTreeMode.Skip);

        // The parse and the test harness allocate a little per round (packet id, the recycled packet's bookkeeping); that share is
        // measured on the same frames without an exporter, so only what the exporter adds is judged.
        long baseline = _MeasureRounds(frame, in without, exporter: null);
        long measured = _MeasureRounds(frame, in withExporter, exporter);

        // A few KB of slack cover one-off lazy initialization and runtime noise; one array per bytes value would add at least
        // 1 000 arrays of 32 bytes (32 KB) over the measured rounds and fail.
        await Assert.That(measured).IsLessThanOrEqualTo(baseline + 8192);
    }

    /// <summary>
    /// A bytes field of the flat shape is written with its eight data bytes, and a repeat of the same bytes is written as the
    /// same-value flag without the value.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ABytesFieldOfTheStandardFlatShapeIsWrittenAndRepeatedAsSameAsPrevious()
    {
        using MemoryStream stream = new();
        List<Frame> frames = [PacketGenerators.CreateCanFrame(0), PacketGenerators.CreateCanFrame(1)];

        using (PbfExporter exporter = _Build(stream, _Selected(new FieldRequest("can.data"))))
        {
            _ = TreeFreeFeed.Run(exporter, frames);
            exporter.OnFinish();
        }

        List<List<PbfField>> packets = _Packets(stream.ToArray());
        List<PbfField> first = _Records(packets[0]).Single();
        List<PbfField> second = _Records(packets[1]).Single();
        PbfField value = first.Single(static field => field.Number == PbfFieldNumbers.FieldValue);

        // The first record carries the eight data bytes; the second repeats them, so it carries the flag and no value.
        await Assert.That(value.Payload!.Length).IsEqualTo(8);
        await Assert.That(second.Exists(static field => field.Number == PbfFieldNumbers.FieldValue)).IsFalse();
        await Assert.That(PbfFileScanner.Value(second, PbfFieldNumbers.FieldSameFlags) & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }

    #endregion
}
