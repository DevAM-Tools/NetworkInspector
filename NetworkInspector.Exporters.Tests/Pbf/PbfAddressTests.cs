// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using AddressValues = NetworkInspector.Exporters.Tests.Helpers.AddressProbePacketFactory.Values;

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// The integer address encodings of both PBF layouts. In the standard layout an address is the value field 5: IPv4 fixed32, MAC
/// varint, EUI-64 fixed64, and IPv6 and UUID fixed64 for the high half plus field 11 fixed64 for the low half. In the columnar layout
/// IPv4 is a packed fixed32 column (field 19), MAC and EUI-64 a packed fixed64 column (field 20), and IPv6 and UUID two packed
/// fixed64 columns (fields 20 and 21, one entry per row). The files are written uncompressed and decoded with the shared protobuf
/// scanner, which states the wire types itself; the header reports format version 1.
/// </summary>
/// <remarks>Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).</remarks>
internal sealed class PbfAddressTests
{
    #region Exporting and decoding files

    /// <summary>Exports the packets of a factory to an uncompressed PBF file of the all-fields shape (flat records, no nesting).</summary>
    /// <param name="path">The target file.</param>
    /// <param name="factory">The factory that built the packets (and owns their stack).</param>
    /// <param name="values">The address values of each packet, in packet order.</param>
    /// <param name="format">The PBF layout.</param>
    /// <param name="maxPacketsPerBlock">The block size in packets.</param>
    private static void _Export(
        string path, AddressProbePacketFactory factory, IReadOnlyList<AddressValues> values, PbfExportFormat format, int maxPacketsPerBlock = 50_000)
    {
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(format)
            .WithCompressed(false)
            .WithContent(factory.Content(ExportShape.AllFields))
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithMaxPacketsPerBlock(maxPacketsPerBlock)
            .Build();
        foreach (Packet packet in factory.CreateTreePackets(values))
        {
            _ = exporter.OnPacket(packet);
        }

        exporter.OnFinish();
    }

    /// <summary>The field records of every packet of a standard-layout file: per packet, the registered name and the fields of each record.</summary>
    /// <param name="path">The PBF file.</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <returns>One list per packet, in file order; a record is its field name and its decoded fields.</returns>
    private static List<List<(string Name, List<PbfField> Fields)>> _StandardPackets(string path, AddressProbePacketFactory factory)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<List<(string Name, List<PbfField> Fields)>> packets = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField packet in block)
            {
                // A packet message is the length-delimited field 5; the block header's maximum timestamp (also 5) is a varint.
                if (packet.Number != PbfFieldNumbers.PacketField || packet.Payload is null)
                {
                    continue;
                }

                List<(string Name, List<PbfField> Fields)> records = [];
                foreach (PbfField record in PbfFileScanner.Fields(packet.Payload))
                {
                    if (record.Number != PbfFieldNumbers.PacketField || record.Payload is null)
                    {
                        continue;
                    }

                    List<PbfField> fields = PbfFileScanner.Fields(record.Payload!);
                    int id = checked((int)PbfFileScanner.Value(fields, PbfFieldNumbers.FieldFieldId));
                    records.Add((factory.Stack.GetField(new FieldId(id))!.Name, fields));
                }

                packets.Add(records);
            }
        }

        return packets;
    }

    /// <summary>The field with a number inside a record, or <see langword="null"/> when the record has none.</summary>
    /// <param name="fields">The fields of one record.</param>
    /// <param name="number">The field number.</param>
    /// <returns>The first such field.</returns>
    private static PbfField? _Find(List<PbfField> fields, int number)
    {
        foreach (PbfField field in fields)
        {
            if (field.Number == number)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>The fixed-width values of one packed column payload, little-endian.</summary>
    /// <param name="payload">The bytes of the packed field.</param>
    /// <param name="width">4 for fixed32, 8 for fixed64.</param>
    /// <returns>The values in row order.</returns>
    private static List<ulong> _Packed(byte[] payload, int width)
    {
        List<ulong> values = [];
        for (int offset = 0; offset < payload.Length; offset += width)
        {
            values.Add(width == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset))
                : BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset)));
        }

        return values;
    }

    /// <summary>The packed values of a column field for every block of a columnar file, in file order, per registered field name.</summary>
    /// <param name="path">The PBF file (columnar layout).</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <param name="number">The column number: 19, 20, or 21.</param>
    /// <param name="width">4 for fixed32, 8 for fixed64.</param>
    /// <returns>The values of that column per field name; a field without that column is not in the map.</returns>
    private static Dictionary<string, List<ulong>> _Columns(string path, AddressProbePacketFactory factory, int number, int width)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        Dictionary<string, List<ulong>> result = [];
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField columnField in block.Where(static field => field.Number == PbfFieldNumbers.ColumnarFieldColumns))
            {
                List<PbfField> column = PbfFileScanner.Fields(columnField.Payload!);
                string name = factory.Stack.GetField(new FieldId(checked((int)PbfFileScanner.Value(column, PbfFieldNumbers.ColumnFieldId))))!.Name;
                PbfField? packed = _Find(column, number);
                if (packed is null)
                {
                    continue;
                }

                if (!result.TryGetValue(name, out List<ulong>? values))
                {
                    values = [];
                    result[name] = values;
                }

                values.AddRange(_Packed(packed.Value.Payload!, width));
            }
        }

        return result;
    }

    /// <summary>Whether any field column of a columnar file carries a field with the given number for the named field.</summary>
    /// <param name="path">The PBF file (columnar layout).</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <param name="name">The registered field name.</param>
    /// <param name="number">The column number.</param>
    /// <returns><see langword="true"/> when some block's column of that field has the number.</returns>
    private static bool _HasColumn(string path, AddressProbePacketFactory factory, string name, int number)
    {
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        foreach (List<PbfField> block in blocks)
        {
            foreach (PbfField columnField in block.Where(static field => field.Number == PbfFieldNumbers.ColumnarFieldColumns))
            {
                List<PbfField> column = PbfFileScanner.Fields(columnField.Payload!);
                string columnName = factory.Stack
                    .GetField(new FieldId(checked((int)PbfFileScanner.Value(column, PbfFieldNumbers.ColumnFieldId))))!.Name;
                if (columnName == name && _Find(column, number) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion

    #region Standard layout

    /// <summary>
    /// A standard-layout record carries each address as the integer of its documented wire type (fixed32 for IPv4, varint for MAC,
    /// fixed64 for EUI-64 and for both halves of IPv6 and UUID), and the header reports format version 1.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StandardRecordsCarryEachAddressAsAnIntegerOfTheDocumentedWireType()
    {
        using TestDir dir = new("pbf_address_standard");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.pbf");
        AddressValues values = AddressProbePacketFactory.Readable;

        _Export(path, factory, [values], PbfExportFormat.Standard);

        (List<PbfField> header, _) = PbfFileScanner.Read(path);
        List<(string Name, List<PbfField> Fields)> packet = _StandardPackets(path, factory).Single();
        Dictionary<string, List<PbfField>> record = packet.ToDictionary(static entry => entry.Name, static entry => entry.Fields);
        PbfField ipv4 = _Find(record["adp.ipv4"], PbfFieldNumbers.FieldValue)!.Value;
        PbfField mac = _Find(record["adp.mac"], PbfFieldNumbers.FieldValue)!.Value;
        PbfField eui64 = _Find(record["adp.eui64"], PbfFieldNumbers.FieldValue)!.Value;
        PbfField ipv6High = _Find(record["adp.ipv6"], PbfFieldNumbers.FieldValue)!.Value;
        PbfField ipv6Low = _Find(record["adp.ipv6"], PbfFieldNumbers.FieldValueLow)!.Value;
        PbfField uuidHigh = _Find(record["adp.uuid"], PbfFieldNumbers.FieldValue)!.Value;
        PbfField uuidLow = _Find(record["adp.uuid"], PbfFieldNumbers.FieldValueLow)!.Value;

        // Wire types: 0 varint, 1 fixed64, 5 fixed32.
        await Assert.That(ipv4.WireType).IsEqualTo(5);
        await Assert.That(ipv4.Varint).IsEqualTo((ulong)values.IPv4.RawValue);
        await Assert.That(mac.WireType).IsEqualTo(0);
        await Assert.That(mac.Varint).IsEqualTo(values.Mac.RawValue);
        await Assert.That(eui64.WireType).IsEqualTo(1);
        await Assert.That(eui64.Varint).IsEqualTo(values.Eui64.RawValue);
        await Assert.That(ipv6High.WireType).IsEqualTo(1);
        await Assert.That(ipv6High.Varint).IsEqualTo(values.IPv6.High);
        await Assert.That(ipv6Low.WireType).IsEqualTo(1);
        await Assert.That(ipv6Low.Varint).IsEqualTo(values.IPv6.Low);
        await Assert.That(uuidHigh.WireType).IsEqualTo(1);
        await Assert.That(uuidHigh.Varint).IsEqualTo(values.Uuid.High);
        await Assert.That(uuidLow.WireType).IsEqualTo(1);
        await Assert.That(uuidLow.Varint).IsEqualTo(values.Uuid.Low);
        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderVersion)).IsEqualTo(1UL);
    }

    /// <summary>Only the 128-bit address types (IPv6 and UUID) write the low-half field in a standard-layout record.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task OnlyIPv6AndUuidRecordsCarryTheLowHalfField()
    {
        using TestDir dir = new("pbf_address_standard_low");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.pbf");

        _Export(path, factory, [AddressProbePacketFactory.Readable], PbfExportFormat.Standard);

        List<string> withLow = _StandardPackets(path, factory).Single()
            .Where(static entry => _Find(entry.Fields, PbfFieldNumbers.FieldValueLow) is not null)
            .Select(static entry => entry.Name)
            .ToList();

        await Assert.That(withLow).IsEquivalentTo(new List<string> { "adp.ipv6", "adp.ipv6dst", "adp.uuid" });
    }

    /// <summary>
    /// A 128-bit value that repeats the previous packet's value omits both halves and sets one same-value flag, and a value that
    /// differs only in the low half writes both halves again.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ARepeated128BitValueOmitsBothHalvesWithOneSameFlag()
    {
        using TestDir dir = new("pbf_address_standard_same");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("same.pbf");
        AddressValues first = AddressProbePacketFactory.Readable;
        AddressValues changedLow = first with { IPv6 = new IPv6Address(first.IPv6.High, first.IPv6.Low + 1) };

        // The same address twice, then one that differs only in the low half: a reader that restored only the high half would show the
        // first packet's low half for the third packet.
        _Export(path, factory, [first, first, changedLow], PbfExportFormat.Standard);

        List<List<(string Name, List<PbfField> Fields)>> packets = _StandardPackets(path, factory);
        List<PbfField> second = packets[1].Single(static entry => entry.Name == "adp.ipv6").Fields;
        List<PbfField> third = packets[2].Single(static entry => entry.Name == "adp.ipv6").Fields;

        // The repeat has neither half and carries the same-value flag (bit 0 of field 8); the changed value has both halves again.
        await Assert.That(_Find(second, PbfFieldNumbers.FieldValue)).IsNull();
        await Assert.That(_Find(second, PbfFieldNumbers.FieldValueLow)).IsNull();
        await Assert.That(PbfFileScanner.Value(second, PbfFieldNumbers.FieldSameFlags) & 1UL).IsEqualTo(1UL);
        await Assert.That(_Find(third, PbfFieldNumbers.FieldValue)!.Value.Varint).IsEqualTo(first.IPv6.High);
        await Assert.That(_Find(third, PbfFieldNumbers.FieldValueLow)!.Value.Varint).IsEqualTo(first.IPv6.Low + 1);
    }

    /// <summary>
    /// Boundary addresses written across several blocks come back unchanged and in feed order, and sorted as unsigned numbers
    /// they give the ascending address order.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StandardBoundaryAddressesRoundTripInFeedOrderAndSortInAddressOrder()
    {
        using TestDir dir = new("pbf_address_standard_order");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("boundaries.pbf");
        AddressValues[] feed = AddressProbePacketFactory.ShuffledBoundaries();
        AddressValues[] ascending = AddressProbePacketFactory.AscendingBoundaries();

        _Export(path, factory, feed, PbfExportFormat.Standard, maxPacketsPerBlock: 2);

        List<List<(string Name, List<PbfField> Fields)>> packets = _StandardPackets(path, factory);
        List<ulong> ipv4 = _StandardValues(packets, "adp.ipv4", PbfFieldNumbers.FieldValue);
        List<ulong> mac = _StandardValues(packets, "adp.mac", PbfFieldNumbers.FieldValue);
        List<ulong> eui64 = _StandardValues(packets, "adp.eui64", PbfFieldNumbers.FieldValue);
        List<ulong> ipv6High = _StandardValues(packets, "adp.ipv6", PbfFieldNumbers.FieldValue);
        List<ulong> ipv6Low = _StandardValues(packets, "adp.ipv6", PbfFieldNumbers.FieldValueLow);

        // Every value comes back unchanged and in feed order; sorted as unsigned numbers they give the ascending address order.
        await Assert.That(packets.Count).IsEqualTo(5);
        await Assert.That(ipv4).IsEquivalentTo(feed.Select(static v => (ulong)v.IPv4.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(mac).IsEquivalentTo(feed.Select(static v => v.Mac.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(eui64).IsEquivalentTo(feed.Select(static v => v.Eui64.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(ipv6High).IsEquivalentTo(feed.Select(static v => v.IPv6.High).ToList(), CollectionOrdering.Matching);
        await Assert.That(ipv6Low).IsEquivalentTo(feed.Select(static v => v.IPv6.Low).ToList(), CollectionOrdering.Matching);
        await Assert.That(ipv4.Order().ToList()).IsEquivalentTo(ascending.Select(static v => (ulong)v.IPv4.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(mac.Order().ToList()).IsEquivalentTo(ascending.Select(static v => v.Mac.RawValue).ToList(), CollectionOrdering.Matching);
    }

    /// <summary>The numeric values of one field of every packet of a standard-layout file, in file order.</summary>
    /// <param name="packets">The decoded packets.</param>
    /// <param name="name">The registered field name.</param>
    /// <param name="number">The field number inside the record.</param>
    /// <returns>One value per packet that carries the field.</returns>
    private static List<ulong> _StandardValues(List<List<(string Name, List<PbfField> Fields)>> packets, string name, int number)
    {
        List<ulong> values = [];
        foreach (List<(string Name, List<PbfField> Fields)> packet in packets)
        {
            foreach ((string recordName, List<PbfField> fields) in packet)
            {
                PbfField? field = _Find(fields, number);
                if (recordName == name && field is not null)
                {
                    values.Add(field.Value.Varint);
                }
            }
        }

        return values;
    }

    #endregion

    #region Columnar layout

    /// <summary>
    /// The columnar layout carries each address as a packed fixed-width integer column: IPv4 in the fixed32 column, the other types in
    /// the fixed64 columns, the low half only for IPv6 and UUID, and no address in the bytes column.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ColumnarColumnsCarryEachAddressAsPackedFixedWidthIntegers()
    {
        using TestDir dir = new("pbf_address_columnar");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.pbf");
        AddressValues values = AddressProbePacketFactory.Readable;

        _Export(path, factory, [values], PbfExportFormat.Columnar);

        (List<PbfField> header, _) = PbfFileScanner.Read(path);
        Dictionary<string, List<ulong>> fixed32 = _Columns(path, factory, PbfFieldNumbers.ColumnFixed32Values, 4);
        Dictionary<string, List<ulong>> high = _Columns(path, factory, PbfFieldNumbers.ColumnFixed64Values, 8);
        Dictionary<string, List<ulong>> low = _Columns(path, factory, PbfFieldNumbers.ColumnFixed64LowValues, 8);

        await Assert.That(fixed32["adp.ipv4"]).IsEquivalentTo(new List<ulong> { values.IPv4.RawValue });
        await Assert.That(high["adp.mac"]).IsEquivalentTo(new List<ulong> { values.Mac.RawValue });
        await Assert.That(high["adp.eui64"]).IsEquivalentTo(new List<ulong> { values.Eui64.RawValue });
        await Assert.That(high["adp.ipv6"]).IsEquivalentTo(new List<ulong> { values.IPv6.High });
        await Assert.That(low["adp.ipv6"]).IsEquivalentTo(new List<ulong> { values.IPv6.Low });
        await Assert.That(high["adp.uuid"]).IsEquivalentTo(new List<ulong> { values.Uuid.High });
        await Assert.That(low["adp.uuid"]).IsEquivalentTo(new List<ulong> { values.Uuid.Low });

        // Only the 128-bit types have the low column, and no address uses the bytes column.
        await Assert.That(low.Keys.Order().ToList()).IsEquivalentTo(new List<string> { "adp.ipv6", "adp.ipv6dst", "adp.uuid" });
        foreach (string name in AddressProbePacketFactory.FieldNames)
        {
            await Assert.That(_HasColumn(path, factory, name, PbfFieldNumbers.ColumnBytesValues)).IsFalse();
        }

        await Assert.That(PbfFileScanner.Value(header, PbfFieldNumbers.HeaderVersion)).IsEqualTo(1UL);
    }

    /// <summary>
    /// Boundary addresses written across several blocks come back unchanged and in feed order from the packed columns, and sorted
    /// as unsigned numbers they give the ascending address order.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ColumnarBoundaryAddressesRoundTripInFeedOrderAcrossBlocksAndSortInAddressOrder()
    {
        using TestDir dir = new("pbf_address_columnar_order");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("boundaries.pbf");
        AddressValues[] feed = AddressProbePacketFactory.ShuffledBoundaries();
        AddressValues[] ascending = AddressProbePacketFactory.AscendingBoundaries();

        // Two packets per block gives three blocks, so a column is written more than once per field.
        _Export(path, factory, feed, PbfExportFormat.Columnar, maxPacketsPerBlock: 2);

        Dictionary<string, List<ulong>> fixed32 = _Columns(path, factory, PbfFieldNumbers.ColumnFixed32Values, 4);
        Dictionary<string, List<ulong>> high = _Columns(path, factory, PbfFieldNumbers.ColumnFixed64Values, 8);
        Dictionary<string, List<ulong>> low = _Columns(path, factory, PbfFieldNumbers.ColumnFixed64LowValues, 8);

        await Assert.That(fixed32["adp.ipv4"]).IsEquivalentTo(feed.Select(static v => (ulong)v.IPv4.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(high["adp.mac"]).IsEquivalentTo(feed.Select(static v => v.Mac.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(high["adp.eui64"]).IsEquivalentTo(feed.Select(static v => v.Eui64.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(high["adp.ipv6"]).IsEquivalentTo(feed.Select(static v => v.IPv6.High).ToList(), CollectionOrdering.Matching);
        await Assert.That(low["adp.ipv6"]).IsEquivalentTo(feed.Select(static v => v.IPv6.Low).ToList(), CollectionOrdering.Matching);
        await Assert.That(high["adp.uuid"]).IsEquivalentTo(feed.Select(static v => v.Uuid.High).ToList(), CollectionOrdering.Matching);
        await Assert.That(low["adp.uuid"]).IsEquivalentTo(feed.Select(static v => v.Uuid.Low).ToList(), CollectionOrdering.Matching);
        List<ulong> sortedIPv4 = [.. fixed32["adp.ipv4"].Order()];
        await Assert.That(sortedIPv4).IsEquivalentTo(ascending.Select(static v => (ulong)v.IPv4.RawValue).ToList(), CollectionOrdering.Matching);
        await Assert.That(high["adp.eui64"].Order().ToList())
            .IsEquivalentTo(ascending.Select(static v => v.Eui64.RawValue).ToList(), CollectionOrdering.Matching);
    }

    #endregion

    #region Allocation

    /// <summary>
    /// After a warm-up, building a columnar block of 10 000 IPv6 values allocates less than 1 KiB per build, so no object is
    /// created for each value.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Category("Slow")]
    public async Task AWarmColumnarBuildOfTenThousandIPv6ValuesAllocatesNothingPerValue()
    {
        using AddressProbePacketFactory factory = new();
        AddressValues[] values = [.. Enumerable.Range(0, 5_000).Select(static index => AddressProbePacketFactory.Readable with
        {
            IPv6 = new IPv6Address(0x2001_0DB8_0000_0000UL, checked((ulong)index)),
            IPv6Dst = new IPv6Address(0x2001_0DB8_0001_0000UL, checked((ulong)index)),
        })];
        ColumnarBlockBuilder builder = new(32768, factory.Content(ExportShape.Hierarchy), maxPacketsPerBlock: 10_000);
        foreach (Packet packet in factory.CreateTreePackets(values))
        {
            _ = builder.AddPacket(packet);
        }

        // Warm-up grows the builder's buffers to the size of this block; Build is repeatable on the same batch.
        for (int i = 0; i < 3; i++)
        {
            _ = builder.Build();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            _ = builder.Build();
        }

        long perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        builder.Dispose();

        // The block holds 10 000 IPv6 values (5 000 packets, two IPv6 fields). The packed columns are written straight into the pooled
        // column buffer; a per-value array or boxed integer would cost at least 10 000 objects, far above this bound.
        await Assert.That(perBuild).IsLessThan(1024);
    }

    /// <summary>
    /// After a warm pass, adding 2 000 IPv6 packets to a standard-layout block allocates less than 8 bytes per written value, so
    /// no object is created for each value.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Category("Slow")]
    public async Task AWarmStandardAddOfIPv6PacketsAllocatesNothingPerValue()
    {
        using AddressProbePacketFactory factory = new();
        AddressValues[] values = [.. Enumerable.Range(0, 2_000).Select(static index => AddressProbePacketFactory.Readable with
        {
            IPv6 = new IPv6Address(0x2001_0DB8_0000_0000UL, checked((ulong)index)),
            IPv6Dst = new IPv6Address(0x2001_0DB8_0001_0000UL, checked((ulong)index)),
        })];
        List<Packet> packets = factory.CreateTreePackets(values);
        StandardBlockBuilder builder = new(32768, factory.Content(ExportShape.Hierarchy), maxPacketsPerBlock: 10_000, maxBlockSize: long.MaxValue);

        // One warm pass sizes the builder's buffers and the previous-value store; the block is then reset and filled again.
        foreach (Packet packet in packets)
        {
            _ = builder.AddPacket(packet);
        }

        builder.Reset();

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (Packet packet in packets)
        {
            _ = builder.AddPacket(packet);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        builder.Dispose();

        // 4 000 IPv6 values were written; a heap object per value (a byte array or a boxed integer) would cost at least 24 bytes each.
        await Assert.That(allocated / 4_000).IsLessThan(8);
    }

    #endregion
}
