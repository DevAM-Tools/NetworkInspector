// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Tests for <see cref="ColumnarPacketBatch"/> — the shared column-oriented accumulator used by
/// the PBF columnar format, Parquet, and DuckDB exporters — and its per-field
/// <see cref="FieldColumnBag"/> typed value storage.
/// </summary>
internal sealed class ColumnarPacketBatchTests
{
    #region Packet-level accumulation

    /// <summary>Proves that adding packets records each packet's id and nanosecond timestamp in order.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketPopulatesPacketIdsAndTimestamps()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        await Assert.That(batch.PacketCount).IsEqualTo(3);
        await Assert.That(batch.PacketIds.Count).IsEqualTo(3);
        await Assert.That(batch.Timestamps.Count).IsEqualTo(3);

        for (int i = 0; i < 3; i++)
        {
            await Assert.That(batch.PacketIds[i]).IsEqualTo(packets[i].Id.Value);
            await Assert.That(batch.Timestamps[i]).IsEqualTo(packets[i].Timestamp.AsNanos);
        }
    }

    /// <summary>Proves that adding a packet asks for a flush exactly when the packet-count limit of the block is reached.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketReturnsTrueWhenMaxPacketsPerBlockReached()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 3, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);

        await Assert.That(batch.AddPacket(packets[0])).IsFalse();
        await Assert.That(batch.AddPacket(packets[1])).IsFalse();
        await Assert.That(batch.AddPacket(packets[2])).IsTrue();
    }

    /// <summary>Proves that adding packets asks for a flush once the estimated byte size reaches the block size limit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketReturnsTrueWhenMaxBlockSizeReached()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 10_000, maxBlockSize: 32, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(50);

        bool flushed = false;
        foreach (Packet packet in packets)
        {
            if (batch.AddPacket(packet))
            {
                flushed = true;
                break;
            }
        }

        await Assert.That(flushed).IsTrue();
    }

    #endregion

    #region Field bags, catalog, and topology

    /// <summary>Proves that adding packets creates field bags and that every bag has a catalog entry of the same type and at least one row.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketPopulatesFieldBagsAndMatchingCatalogEntries()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        // A parsed Ethernet+IPv4+UDP frame carries several distinct fields (MAC addresses,
        // EtherType, IP addresses, ports, ...), each becoming its own bag.
        await Assert.That(batch.FieldBags.Count).IsGreaterThan(0);

        // The parser stack (TestHarness.GetStack()) is kept per test thread, but a test may continue on another thread
        // after an await, so an exact "row count is a multiple of the packet count" assertion would depend on scheduling.
        // Instead, verify the weaker but meaningful invariant that every observed bag has a matching, correctly-typed
        // catalog entry and at least one row.
        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            await Assert.That(batch.Catalog.ContainsKey(entry.Key)).IsTrue()
                .Because(Invariant($"field {entry.Key} has a bag but no catalog entry"));
            await Assert.That(batch.Catalog[entry.Key].FieldType).IsEqualTo(entry.Value.FieldType);
            await Assert.That(entry.Value.RowCount).IsGreaterThan(0);
        }
    }

    /// <summary>Proves that the hierarchy shape records topology nodes and that a top-level node of a packet has the parent id -1.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketPopulatesTopologyInTheHierarchyShape()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(), maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        batch.AddPacket(packets[0]);
        batch.AddPacket(packets[1]);

        await Assert.That(batch.Topology.Count).IsGreaterThan(0);

        // Every topology row for packet 0 must reference packet 0's ID, and top-level fields
        // (direct children of the packet root) must use ParentNodeId == -1.
        int firstPacketId = packets[0].Id.Value;
        bool hasTopLevelNode = false;
        foreach (TopologyNode node in batch.Topology)
        {
            if (node.PacketId == firstPacketId && node.ParentNodeId == -1)
            {
                hasTopLevelNode = true;
                break;
            }
        }

        await Assert.That(hasTopLevelNode).IsTrue();
    }

    /// <summary>Proves that the all-fields shape records no topology nodes.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketLeavesTopologyEmptyOutsideTheHierarchyShape()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.AllFields(), maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        await Assert.That(batch.Topology.Count).IsEqualTo(0);
    }

    /// <summary>Proves that the batch collects the packet info and the frame bytes only when the content asks for them.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketPopulatesInfoAndFrameBytesOnlyWhenFlagsSet()
    {
        using ColumnarPacketBatch batchWithout = new(
            ExportContents.Hierarchy(), maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        using ColumnarPacketBatch batchWith = new(
            ExportContents.Hierarchy(info: true, frameBytes: true),
            maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet packet = PacketGenerators.CreateEthernetUdpPackets(1)[0];
        batchWithout.AddPacket(packet);
        batchWith.AddPacket(packet);

        await Assert.That(batchWithout.Infos.Count).IsEqualTo(0);
        await Assert.That(batchWithout.FrameBytesList.Count).IsEqualTo(0);
        await Assert.That(batchWith.Infos.Count).IsEqualTo(1);
        await Assert.That(batchWith.FrameBytesList.Count).IsEqualTo(1);
        await Assert.That(batchWith.FrameBytesList[0].Length).IsGreaterThan(0);
    }

    #endregion

    #region Resetting and disposing

    /// <summary>Proves that resetting a batch clears its rows but keeps the field bags and the catalog, and that the batch is reusable.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ResetClearsRowsButKeepsFieldBagsAndCatalog()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        int fieldBagCountBeforeReset = batch.FieldBags.Count;
        int catalogCountBeforeReset = batch.Catalog.Count;

        batch.Reset();

        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.Topology.Count).IsEqualTo(0);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(fieldBagCountBeforeReset)
            .Because("field metadata (bags) must survive Reset() for reuse by the next block");
        await Assert.That(batch.Catalog.Count).IsEqualTo(catalogCountBeforeReset);

        foreach (FieldColumnBag bag in batch.FieldBags.Values)
        {
            await Assert.That(bag.RowCount).IsEqualTo(0);
        }

        // The batch must still be usable after Reset().
        batch.AddPacket(packets[0]);
        await Assert.That(batch.PacketCount).IsEqualTo(1);
    }

    /// <summary>Proves that disposing a batch clears all its state and that disposing twice does not throw.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DisposeClearsAllState()
    {
        ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        batch.Dispose();

        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(0);
        await Assert.That(batch.Catalog.Count).IsEqualTo(0);

        // Double-dispose must not throw.
        batch.Dispose();
    }

    #endregion

    #region Typed value storage of a field column bag

    /// <summary>Proves that a string bag stores its values as plain strings in row order.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagStoresPlainStrings()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.String, arena);

        bag.Add(packetId: 0, position: 0, FieldValueData.NewString("alpha"), null, null);
        bag.Add(packetId: 1, position: 0, FieldValueData.NewString("beta"), null, null);
        bag.Add(packetId: 2, position: 0, FieldValueData.NewString("alpha"), null, null);

        await Assert.That(bag.RowCount).IsEqualTo(3);
        await Assert.That(bag.StringValues[0]).IsEqualTo("alpha");
        await Assert.That(bag.StringValues[1]).IsEqualTo("beta");
        await Assert.That(bag.StringValues[2]).IsEqualTo("alpha");
    }

    /// <summary>Proves that a bag stores a boolean, signed, unsigned, and floating-point value in the column of its own type.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagRoutesTypedValuesToMatchingColumn()
    {
        using BatchByteArena arena = new();
        FieldColumnBag boolBag = new(fieldIdValue: 1, fieldType: FieldType.Bool, arena);
        boolBag.Add(0, 0, FieldValueData.NewBool(true), null, null);
        await Assert.That(boolBag.BoolValues[0]).IsTrue();

        FieldColumnBag i64Bag = new(fieldIdValue: 2, fieldType: FieldType.I64, arena);
        i64Bag.Add(0, 0, FieldValueData.NewI64(-42), null, null);
        await Assert.That(i64Bag.I64Values[0]).IsEqualTo(-42);

        FieldColumnBag u64Bag = new(fieldIdValue: 3, fieldType: FieldType.U64, arena);
        u64Bag.Add(0, 0, FieldValueData.NewU64(ulong.MaxValue), null, null);
        await Assert.That(u64Bag.U64Values[0]).IsEqualTo(ulong.MaxValue);

        FieldColumnBag f64Bag = new(fieldIdValue: 4, fieldType: FieldType.F64, arena);
        f64Bag.Add(0, 0, FieldValueData.NewF64(3.5), null, null);
        await Assert.That(f64Bag.F64Values[0]).IsEqualTo(3.5);
    }

    /// <summary>Proves that a bag stores the custom representation and the custom text of a row as plain strings.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagStoresCustomRepresentationAndTextAsPlainStrings()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64, arena);

        bag.Add(0, 0, FieldValueData.NewI64(1), "custom-repr", "custom-text");

        await Assert.That(bag.CustomRepresentations[0]).IsEqualTo("custom-repr");
        await Assert.That(bag.CustomTexts[0]).IsEqualTo("custom-text");
    }

    /// <summary>Proves that IPv6 and UUID rows are kept inline with both 64-bit halves and have no byte-array form.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagKeepsIPv6AndUuidValuesInlineWithBothHalves()
    {
        using BatchByteArena arena = new();
        byte[] ipv6 = new byte[16];
        ipv6[0] = 0x20;
        ipv6[15] = 0x01;
        FieldColumnBag ipv6Bag = new(fieldIdValue: 1, fieldType: FieldType.IPv6Address, arena);
        ipv6Bag.Add(0, 0, FieldValueData.NewIPv6(IPv6Address.FromBytes(ipv6)), null, null);
        IPv6Address storedIpv6 = _ReadIPv6(ipv6Bag, 0);
        await Assert.That(storedIpv6.High).IsEqualTo(0x2000_0000_0000_0000UL);
        await Assert.That(storedIpv6.Low).IsEqualTo(1UL);
        await Assert.That(ipv6Bag.BytesValues.Count).IsEqualTo(0);

        byte[] uuid = new byte[16];
        uuid[0] = 0xAB;
        uuid[15] = 0xCD;
        FieldColumnBag uuidBag = new(fieldIdValue: 2, fieldType: FieldType.Uuid, arena);
        uuidBag.Add(0, 0, FieldValueData.NewUuid(Uuid.FromBytes(uuid)), null, null);
        Uuid storedUuid = _ReadUuid(uuidBag, 0);
        await Assert.That(storedUuid.High).IsEqualTo(0xAB00_0000_0000_0000UL);
        await Assert.That(storedUuid.Low).IsEqualTo(0xCDUL);
        await Assert.That(uuidBag.BytesValues.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Reads the IPv6 address of row <paramref name="row"/> of an IPv6 bag through <see cref="FieldColumnBag.FixedValueSpan"/>; a
    /// span cannot live in an async test, so the read sits in this method.
    /// </summary>
    /// <param name="bag">An IPv6 bag.</param>
    /// <param name="row">Row index.</param>
    /// <returns>The address of the row.</returns>
    private static IPv6Address _ReadIPv6(FieldColumnBag bag, int row)
    {
        return bag.FixedValueSpan[row].TryGetAsIPv6(out IPv6Address address)
            ? address
            : throw new InvalidOperationException("The row does not hold an IPv6 address.");
    }

    /// <summary>Reads the UUID of row <paramref name="row"/> of a UUID bag through <see cref="FieldColumnBag.FixedValueSpan"/>.</summary>
    /// <param name="bag">A UUID bag.</param>
    /// <param name="row">Row index.</param>
    /// <returns>The UUID of the row.</returns>
    private static Uuid _ReadUuid(FieldColumnBag bag, int row)
    {
        return bag.FixedValueSpan[row].TryGetAsUuid(out Uuid uuid)
            ? uuid
            : throw new InvalidOperationException("The row does not hold a UUID.");
    }

    /// <summary>Proves that adding a value of another type to a bag throws <see cref="InvalidOperationException"/> and adds no row.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagThrowsOnTypeMismatch()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64, arena);

        await Assert.That(() => bag.Add(0, 0, FieldValueData.NewBool(true), null, null))
            .Throws<InvalidOperationException>();
        await Assert.That(bag.RowCount).IsEqualTo(0);
    }

    /// <summary>Proves that a row without a custom representation or text keeps both as <see langword="null"/>.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagPreservesNullCustomText()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64, arena);
        bag.Add(0, 0, FieldValueData.NewI64(1), customRepresentation: null, customText: null);

        await Assert.That(bag.CustomRepresentations[0]).IsNull();
        await Assert.That(bag.CustomTexts[0]).IsNull();
    }

    #endregion

    #region Running size estimate

    /// <summary>
    /// The incrementally maintained field-bag total must equal the sum over all bags after every packet, and the
    /// batch estimate must contain it (packet-level bytes come on top).
    /// </summary>
    /// <param name="full">Whether every optional member is set; custom strings and info change the per-row byte counts.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunningFieldBagTotalEqualsSumOfBags(bool full)
    {
        using ColumnarPacketBatch batch = new(
            full
                ? ExportContents.Full
                : ExportContents.Hierarchy(),
            maxPacketsPerBlock: 1000,
            maxBlockSize: long.MaxValue,
            isTimestampSorted: true);
        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com", "a.example.org", "b.example.net");

        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);

            long sum = BatchSizes.SumOfBags(batch);
            await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(sum);
            await Assert.That(batch.EstimatedSizeBytes).IsGreaterThan(sum);
        }

        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsGreaterThan(0);
    }

    /// <summary><see cref="ColumnarPacketBatch.Reset"/> must zero the running totals, and they must track again afterwards.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ResetZeroesRunningTotalAndTrackingResumes()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 1000, maxBlockSize: long.MaxValue, isTimestampSorted: true);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        batch.AddPacket(packets[0]);
        batch.AddPacket(packets[1]);

        batch.Reset();
        long afterReset = batch.EstimatedSizeBytes;
        long bagsAfterReset = batch.FieldBagsEstimatedSizeBytes;
        batch.AddPacket(packets[2]);

        await Assert.That(afterReset).IsEqualTo(0);
        await Assert.That(bagsAfterReset).IsEqualTo(0);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(BatchSizes.SumOfBags(batch));
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsGreaterThan(0);
    }

    /// <summary>
    /// <see cref="ColumnarPacketBatch.AddPacket"/> signals a flush exactly when the
    /// estimate reaches the size threshold (packet-count threshold out of reach).
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketSignalsFlushExactlyWhenEstimateReachesThreshold()
    {
        const long maxBlockSize = 600;
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 10_000, maxBlockSize: maxBlockSize, isTimestampSorted: true);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(20);
        bool sawFlush = false;

        foreach (Packet packet in packets)
        {
            bool flush = batch.AddPacket(packet);

            await Assert.That(flush).IsEqualTo(batch.EstimatedSizeBytes >= maxBlockSize);
            if (flush)
            {
                sawFlush = true;
                batch.Reset();
            }
        }

        await Assert.That(sawFlush).IsTrue();
    }

    #endregion

    #region Structure nodes

    /// <summary>
    /// With the topology, a protocol or group node (no value) has a catalog row without a table, so every field id of the
    /// topology can be named; value fields keep their <c>field_{id}</c> table name.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StructureNodesHaveCatalogRowsWithoutTable()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(), maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);

        batch.AddPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);
        int unnamed = 0;
        int structureRows = 0;
        int wrongStructureRows = 0;
        foreach (TopologyNode node in batch.Topology)
        {
            if (!batch.Catalog.TryGetValue(node.FieldId, out FieldCatalogEntry? entry))
            {
                unnamed++;
            }
            else if (entry.TableName is null)
            {
                structureRows++;
                if (entry.FieldType != FieldType.None || entry.Name.Length == 0)
                {
                    wrongStructureRows++;
                }
            }
        }

        await Assert.That(unnamed).IsEqualTo(0).Because("every topology field id needs a catalog row");
        await Assert.That(structureRows).IsGreaterThan(0);
        await Assert.That(wrongStructureRows).IsEqualTo(0);
        foreach (FieldCatalogEntry entry in batch.Catalog.Values)
        {
            if (entry.TableName is not null)
            {
                await Assert.That(entry.TableName).IsEqualTo(FormattableString.Invariant($"field_{entry.FieldIdValue}"));
            }
        }
    }

    /// <summary>Without the topology there is no structure node in the export, so the catalog holds value fields only.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StructureNodesHaveNoCatalogRowsWithoutTopology()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.AllFields(), maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);

        batch.AddPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(batch.Catalog.Count).IsGreaterThan(0);
        foreach (FieldCatalogEntry entry in batch.Catalog.Values)
        {
            await Assert.That(entry.TableName).IsNotNull();
        }
    }

    /// <summary>The display line of a node without a value is exported with the topology row; value nodes keep theirs in the field table.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StructureNodeCustomTextIsInTopology()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(5, "text", containerText: "Test container line");
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(customText: true),
            maxPacketsPerBlock: 100,
            maxBlockSize: long.MaxValue,
            isTimestampSorted: true);

        batch.AddPacket(packet);
        List<string> texts = [];
        foreach (TopologyNode node in batch.Topology)
        {
            if (node.CustomText is not null)
            {
                texts.Add(node.CustomText);
            }
        }

        await Assert.That(texts.Count).IsEqualTo(1);
        await Assert.That(texts[0]).IsEqualTo("Test container line");
    }

    /// <summary>Without <see cref="ExportContent.CustomText"/> no topology row carries text.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StructureNodeCustomTextIsOmittedWithoutTheFlag()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(5, "text", containerText: "Test container line");
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(), maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);

        batch.AddPacket(packet);
        int withText = 0;
        foreach (TopologyNode node in batch.Topology)
        {
            if (node.CustomText is not null)
            {
                withText++;
            }
        }

        await Assert.That(batch.Topology.Count).IsGreaterThan(0);
        await Assert.That(withText).IsEqualTo(0);
    }

    #endregion

    #region Byte payloads

    /// <summary>A bytes value is copied into the batch: changing the source array afterwards does not change the stored row.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagCopiesBytesIntoTheSlab()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.Bytes, arena);
        byte[] source = [1, 2, 3, 4];

        bag.Add(0, 0, FieldValueData.NewBytes(source), null, null);
        source[0] = 99;
        bag.Add(0, 1, FieldValueData.NewBytes(ReadOnlyMemory<byte>.Empty), null, null);

        await Assert.That(bag.RowCount).IsEqualTo(2);
        await Assert.That(bag.BytesValues[0]).IsEquivalentTo(new byte[] { 1, 2, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That(_ReadBlob(bag, 0)).IsEquivalentTo(new byte[] { 1, 2, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That(bag.BytesValues[1]!.Length).IsEqualTo(0);
        await Assert.That(_ReadBlob(bag, 1).Length).IsEqualTo(0);
    }

    /// <summary>An IPv4 row is kept as the inline value, readable as the integer, and has no array or blob form.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagKeepsAnIPv4ValueAsItsInteger()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.IPv4Address, arena);

        bag.Add(0, 0, FieldValue.NewIPv4(new IPv4Address(0x0A000001u)).Data, null, null);

        await Assert.That(_ReadIPv4(bag, 0).RawValue).IsEqualTo(0x0A000001u);
        await Assert.That(bag.BytesValues.Count).IsEqualTo(0);
    }

    /// <summary>Reads the IPv4 address of row <paramref name="row"/> of an IPv4 bag (a span cannot live in an async test).</summary>
    /// <param name="bag">An IPv4 bag.</param>
    /// <param name="row">Row index.</param>
    /// <returns>The address of the row.</returns>
    private static IPv4Address _ReadIPv4(FieldColumnBag bag, int row)
    {
        return bag.FixedValueSpan[row].TryGetAsIPv4(out IPv4Address address)
            ? address
            : throw new InvalidOperationException("The row does not hold an IPv4 address.");
    }

    /// <summary>Frame bytes are readable as a span and as the array list, both equal to the packet's frame.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FrameBytesAreReadableAsSpanAndAsArrays()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(frameBytes: true), maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);

        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        for (int i = 0; i < packets.Length; i++)
        {
            byte[] expected = packets[i].Frame.Data.ToArray();
            await Assert.That(batch.GetFrameBytes(i).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(batch.FrameBytesList[i]).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// Once warm (bags exist, lists and arena chunks are sized by an earlier batch), adding packets with frame bytes, byte
    /// values and topology allocates nothing.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task WarmAddPacketWithFrameAndBytesAllocatesNothing()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(100);
        using ColumnarPacketBatch batch = new(
            ExportContents.Hierarchy(frameBytes: true),
            maxPacketsPerBlock: 10_000,
            maxBlockSize: long.MaxValue,
            isTimestampSorted: true);

        long allocated = _MeasureWarmAddPacketAllocation(batch, packets);

        await Assert.That(allocated).IsEqualTo(0);
    }

    /// <summary>Adds the packets once to warm the batch, resets it, and returns the bytes allocated by adding them again.</summary>
    /// <param name="batch">The batch to measure.</param>
    /// <param name="packets">Packets that are added twice.</param>
    /// <returns>Allocated bytes of the second pass on the current thread.</returns>
    private static long _MeasureWarmAddPacketAllocation(ColumnarPacketBatch batch, Packet[] packets)
    {
        // The first pass creates the bags, grows the lists, rents the arena chunks and materializes lazy fields; Reset keeps
        // all of that, so the second pass only appends.
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        batch.Reset();

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Returns a copy of the blob of row <paramref name="row"/> (a span cannot live in an async test).</summary>
    /// <param name="bag">A bytes bag.</param>
    /// <param name="row">Row index.</param>
    /// <returns>The row's bytes.</returns>
    private static byte[] _ReadBlob(FieldColumnBag bag, int row)
    {
        return bag.GetBlob(row).ToArray();
    }

    #endregion

    #region Catalog description

    /// <summary>
    /// The catalog entry carries <see cref="FieldInfo.Description"/>: the registered text for a described field and
    /// <see langword="null"/> for a field without one.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task CatalogEntryCarriesFieldDescription()
    {
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);

        batch.AddPacket(capture.Packets[0]);
        FieldCatalogEntry described = _FindEntry(batch, DescribedFieldCapture.DescribedFieldName);
        FieldCatalogEntry plain = _FindEntry(batch, DescribedFieldCapture.PlainFieldName);

        await Assert.That(described.Description).IsEqualTo(DescribedFieldCapture.FieldDescription);
        await Assert.That(plain.Description).IsNull();
    }

    /// <summary>Returns the catalog entry with the given field name; throws when absent so the test fails visibly.</summary>
    /// <param name="batch">The batch to inspect.</param>
    /// <param name="name">Field name.</param>
    /// <returns>The matching entry.</returns>
    /// <exception cref="InvalidOperationException">The catalog has no entry with that name.</exception>
    private static FieldCatalogEntry _FindEntry(ColumnarPacketBatch batch, string name)
    {
        foreach (FieldCatalogEntry entry in batch.Catalog.Values)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new InvalidOperationException(Invariant($"Catalog entry '{name}' not found."));
    }

    #endregion
}
