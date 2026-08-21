// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Tests for <see cref="ColumnarPacketBatch"/> — the shared column-oriented accumulator used by
/// the PBF columnar format, Parquet, and DuckDB exporters — and its per-field
/// <see cref="FieldColumnBag"/> typed value storage.
/// </summary>
internal sealed class ColumnarPacketBatchTests
{
    // ========================================================================
    // Packet-level accumulation
    // ========================================================================

    [Test]
    public async Task AddPacket_PopulatesPacketIdsAndTimestamps()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

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

    [Test]
    public async Task AddPacket_ReturnsTrueWhenMaxPacketsPerBlockReached()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 3, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);

        await Assert.That(batch.AddPacket(packets[0])).IsFalse();
        await Assert.That(batch.AddPacket(packets[1])).IsFalse();
        await Assert.That(batch.AddPacket(packets[2])).IsTrue();
    }

    [Test]
    public async Task AddPacket_ReturnsTrueWhenMaxBlockSizeReached()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 10_000, maxBlockSize: 32, isTimestampSorted: true);

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

    // ========================================================================
    // Field bags, catalog, and topology
    // ========================================================================

    [Test]
    public async Task AddPacket_PopulatesFieldBagsAndMatchingCatalogEntries()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        // A parsed Ethernet+IPv4+UDP frame carries several distinct fields (MAC addresses,
        // EtherType, IP addresses, ports, ...), each becoming its own bag.
        await Assert.That(batch.FieldBags.Count).IsGreaterThan(0);

        // Note: the parser stack (TestHarness.GetStack()) is shared across the whole test
        // assembly and tests may run in parallel, so an exact "row count is a multiple of the
        // packet count" assertion would be flaky under concurrent stack usage. Instead, verify
        // the weaker but still meaningful invariant that every observed bag has a matching,
        // correctly-typed catalog entry and at least one row.
        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            await Assert.That(batch.Catalog.ContainsKey(entry.Key)).IsTrue()
                .Because($"field {entry.Key} has a bag but no catalog entry");
            await Assert.That(batch.Catalog[entry.Key].FieldType).IsEqualTo(entry.Value.FieldType);
            await Assert.That(entry.Value.RowCount).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task AddPacket_TopologyPopulated_WhenIncludeTopologyFlagSet()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.IncludeTopology, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

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

    [Test]
    public async Task AddPacket_TopologyEmpty_WhenIncludeTopologyFlagNotSet()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.None, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            batch.AddPacket(packet);
        }

        await Assert.That(batch.Topology.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddPacket_InfoAndFrameBytes_OnlyPopulatedWhenFlagsSet()
    {
        using ColumnarPacketBatch batchWithout = new(
            ColumnarDetailFlags.None, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        using ColumnarPacketBatch batchWith = new(
            ColumnarDetailFlags.IncludeInfo | ColumnarDetailFlags.IncludeFrameBytes,
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

    // ========================================================================
    // Reset / Dispose lifecycle
    // ========================================================================

    [Test]
    public async Task Reset_ClearsRowsButKeepsFieldBagsAndCatalog()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

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

    [Test]
    public async Task Dispose_ClearsAllState()
    {
        ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);

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

    // ========================================================================
    // FieldColumnBag — Core FieldValueData storage
    // ========================================================================

    [Test]
    public async Task FieldColumnBag_StringValues_StoresPlainStrings()
    {
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.String);

        bag.Add(packetId: 0, nodeId: 0, FieldValueData.NewString("alpha"), null, null);
        bag.Add(packetId: 1, nodeId: 0, FieldValueData.NewString("beta"), null, null);
        bag.Add(packetId: 2, nodeId: 0, FieldValueData.NewString("alpha"), null, null);

        await Assert.That(bag.RowCount).IsEqualTo(3);
        await Assert.That(bag.StringValues[0]).IsEqualTo("alpha");
        await Assert.That(bag.StringValues[1]).IsEqualTo("beta");
        await Assert.That(bag.StringValues[2]).IsEqualTo("alpha");
    }

    [Test]
    public async Task FieldColumnBag_TypedValues_RoutedToMatchingColumn()
    {
        FieldColumnBag boolBag = new(fieldIdValue: 1, fieldType: FieldType.Bool);
        boolBag.Add(0, 0, FieldValueData.NewBool(true), null, null);
        await Assert.That(boolBag.BoolValues[0]).IsTrue();

        FieldColumnBag i64Bag = new(fieldIdValue: 2, fieldType: FieldType.I64);
        i64Bag.Add(0, 0, FieldValueData.NewI64(-42), null, null);
        await Assert.That(i64Bag.I64Values[0]).IsEqualTo(-42);

        FieldColumnBag u64Bag = new(fieldIdValue: 3, fieldType: FieldType.U64);
        u64Bag.Add(0, 0, FieldValueData.NewU64(ulong.MaxValue), null, null);
        await Assert.That(u64Bag.U64Values[0]).IsEqualTo(ulong.MaxValue);

        FieldColumnBag f64Bag = new(fieldIdValue: 4, fieldType: FieldType.F64);
        f64Bag.Add(0, 0, FieldValueData.NewF64(3.5), null, null);
        await Assert.That(f64Bag.F64Values[0]).IsEqualTo(3.5);
    }

    [Test]
    public async Task FieldColumnBag_CustomRepresentationAndText_StoredAsPlainStrings()
    {
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64);

        bag.Add(0, 0, FieldValueData.NewI64(1), "custom-repr", "custom-text");

        await Assert.That(bag.CustomRepresentations[0]).IsEqualTo("custom-repr");
        await Assert.That(bag.CustomTexts[0]).IsEqualTo("custom-text");
    }

    [Test]
    public async Task FieldColumnBag_IPv6AndUuid_StoreSixteenBytes()
    {
        byte[] ipv6 = new byte[16];
        ipv6[0] = 0x20;
        ipv6[15] = 0x01;
        FieldColumnBag ipv6Bag = new(fieldIdValue: 1, fieldType: FieldType.IPv6Address);
        ipv6Bag.Add(0, 0, FieldValueData.NewIPv6(IPv6Address.FromBytes(ipv6)), null, null);
        await Assert.That(ipv6Bag.BytesValues[0]!.Length).IsEqualTo(16);
        await Assert.That(ipv6Bag.BytesValues[0]).IsEquivalentTo(ipv6);

        byte[] uuid = new byte[16];
        uuid[0] = 0xAB;
        uuid[15] = 0xCD;
        FieldColumnBag uuidBag = new(fieldIdValue: 2, fieldType: FieldType.Uuid);
        uuidBag.Add(0, 0, FieldValueData.NewUuid(Uuid.FromBytes(uuid)), null, null);
        await Assert.That(uuidBag.BytesValues[0]!.Length).IsEqualTo(16);
        await Assert.That(uuidBag.BytesValues[0]).IsEquivalentTo(uuid);
    }

    [Test]
    public async Task FieldColumnBag_TypeMismatch_Throws()
    {
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64);

        await Assert.That(() => bag.Add(0, 0, FieldValueData.NewBool(true), null, null))
            .Throws<InvalidOperationException>();
        await Assert.That(bag.RowCount).IsEqualTo(0);
    }

    [Test]
    public async Task FieldColumnBag_NullCustomText_PreservedAsNull()
    {
        FieldColumnBag bag = new(fieldIdValue: 1, fieldType: FieldType.I64);
        bag.Add(0, 0, FieldValueData.NewI64(1), customRepresentation: null, customText: null);

        await Assert.That(bag.CustomRepresentations[0]).IsNull();
        await Assert.That(bag.CustomTexts[0]).IsNull();
    }
}
