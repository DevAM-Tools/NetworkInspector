// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Atomicity tests for <see cref="ColumnarPacketBatch.AddPacket"/>: a packet that cannot be added completely must leave the
/// batch exactly as it was before the call.
/// </summary>
internal sealed class ColumnarPacketBatchAtomicityTests
{
    #region Packets from a shifted stack

    /// <summary>
    /// Parses a packet on a second stack whose field ids are shifted by one against the thread's standard stack (one extra
    /// field is registered first). Added to a batch that already holds columns from the standard stack, the shifted ids
    /// meet columns of another type, so the batch rejects the packet part-way through.
    /// </summary>
    /// <returns>The parsed packet; the caller disposes its stack.</returns>
    /// <exception cref="InvalidOperationException">The frame could not be parsed.</exception>
    private static Packet _CreateShiftedStackPacket()
    {
        SettingsManager settingsManager = new();
        FrameInterfaceRegistry registry = new();
        StackBuilder builder = new(settingsManager, registry);
        builder.RegisterField(ProtocolId.Invalid, "test.shift", "Shift", FieldType.Bool);
        builder.RegisterStandardProtocols();
        Stack stack = builder.Build();

        FrameSourceId sourceId = registry.RegisterSource(TestHarness.CreateNullFrameSource());
        FrameInterfaceId interfaceId = registry.Register(sourceId, "shifted", null, LinkType.Ethernet);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromNanos(5_000_000),
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            LinkType.Ethernet,
            interfaceId,
            registry).Value;

        ParseOptions options = new();
        if (!Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure)
            || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    /// <summary>Adds a packet of the shifted stack to the batch, asserts that the batch rejects it, and disposes the stack it came from.</summary>
    /// <param name="batch">A batch that already holds columns of the standard stack.</param>
    /// <returns>A task that completes when the rejection has been asserted and the stack is disposed.</returns>
    private static async Task _AssertTheShiftedPacketIsRejectedAsync(ColumnarPacketBatch batch)
    {
        Packet shifted = _CreateShiftedStackPacket();
        try
        {
            await Assert.That(() => batch.AddPacket(shifted)).Throws<InvalidOperationException>();
        }
        finally
        {
            shifted.Stack.Dispose();
        }
    }

    #endregion

    #region Batch state after a rejected packet

    /// <summary>Proves that a rejected packet leaves every count, size, and row of the batch exactly as it was before the call.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task RejectedPacketLeavesTheBatchExactlyAsItWas()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        Packet[] good = PacketGenerators.CreateEthernetUdpPackets(2);
        batch.AddPacket(good[0]);
        batch.AddPacket(good[1]);
        int packetCount = batch.PacketCount;
        int topologyCount = batch.Topology.Count;
        int catalogCount = batch.Catalog.Count;
        int bagCount = batch.FieldBags.Count;
        long estimatedSize = batch.EstimatedSizeBytes;
        long bagsSize = batch.FieldBagsEstimatedSizeBytes;
        Dictionary<int, int> rowCounts = [];
        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            rowCounts[entry.Key] = entry.Value.RowCount;
        }

        await _AssertTheShiftedPacketIsRejectedAsync(batch);

        await Assert.That(batch.PacketCount).IsEqualTo(packetCount);
        await Assert.That(batch.PacketIds.Count).IsEqualTo(packetCount);
        await Assert.That(batch.Timestamps.Count).IsEqualTo(packetCount);
        await Assert.That(batch.Infos.Count).IsEqualTo(packetCount);
        await Assert.That(batch.FrameBytesList.Count).IsEqualTo(packetCount);
        await Assert.That(batch.Topology.Count).IsEqualTo(topologyCount);
        await Assert.That(batch.Catalog.Count).IsEqualTo(catalogCount);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(bagCount);
        await Assert.That(batch.EstimatedSizeBytes).IsEqualTo(estimatedSize);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(bagsSize);
        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            await Assert.That(entry.Value.RowCount).IsEqualTo(rowCounts[entry.Key]);
            await Assert.That(entry.Value.PacketIds.Count).IsEqualTo(entry.Value.Positions.Count);
            await Assert.That(entry.Value.CustomTexts.Count).IsEqualTo(entry.Value.RowCount);
        }
    }

    /// <summary>
    /// A rejected packet leaves no catalog row behind that its rows do not back: every row with a table has a bag, and every
    /// structure row (no table) belongs to a node of the topology that remains.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task RejectedPacketLeavesNoOrphanCatalogRows()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        batch.AddPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);
        HashSet<int> topologyFieldIds = [];
        foreach (TopologyNode node in batch.Topology)
        {
            topologyFieldIds.Add(node.FieldId);
        }

        int catalogCountBefore = batch.Catalog.Count;

        await _AssertTheShiftedPacketIsRejectedAsync(batch);

        await Assert.That(batch.Catalog.Count).IsEqualTo(catalogCountBefore);
        foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
        {
            if (entry.Value.TableName is null)
            {
                await Assert.That(topologyFieldIds.Contains(entry.Key)).IsTrue();
            }
            else
            {
                await Assert.That(batch.FieldBags.ContainsKey(entry.Key)).IsTrue();
            }
        }
    }

    /// <summary>
    /// A rejected packet also returns its bytes to the arena: the frame copied for it is gone, and the next packet's frame
    /// reads back correctly.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task RejectedPacketReleasesItsFrameBytes()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        Packet[] good = PacketGenerators.CreateEthernetUdpPackets(2);
        batch.AddPacket(good[0]);

        await _AssertTheShiftedPacketIsRejectedAsync(batch);
        batch.AddPacket(good[1]);

        await Assert.That(batch.FrameBytesList.Count).IsEqualTo(2);
        await Assert.That(batch.FrameBytesList[1]).IsEquivalentTo(good[1].Frame.Data.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A packet that fails after the batch copied its additional buffers (the buffer copy comes before the field tree) leaves no
    /// buffer row, no buffer byte, and no estimate behind, and the next packet's buffers read back correctly: the buffer rows are
    /// rolled back with the rest of the packet.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task RejectedPacketLeavesNoBufferRowsAndTheBatchStaysUsable()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch batch = new(
            new ExportContent { Buffers = true }, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        batch.AddPacket(factory.CreatePacket([1, 2, 3], [4, 5]));
        long estimatedSize = batch.EstimatedSizeBytes;
        int rows = batch.BufferRowCount;

        // The faulty text of the packet is read while its field tree is added, after its three buffers (one of them large) were copied.
        factory.FaultyText = true;
        Packet faulty = factory.CreatePacket(new byte[2000], [9], new byte[10]);
        await Assert.That(() => batch.AddPacket(faulty)).Throws<InvalidOperationException>();

        await Assert.That(batch.PacketCount).IsEqualTo(1);
        await Assert.That(batch.BufferRowCount).IsEqualTo(rows);
        await Assert.That(batch.BufferPacketIdSpan.Length).IsEqualTo(rows);
        await Assert.That(batch.BufferIndexSpan.Length).IsEqualTo(rows);
        await Assert.That(batch.BufferBytesList.Count).IsEqualTo(rows);
        await Assert.That(batch.EstimatedSizeBytes).IsEqualTo(estimatedSize);

        factory.FaultyText = false;
        batch.AddPacket(factory.CreatePacket([7, 7, 7, 7]));

        await Assert.That(batch.BufferRowCount).IsEqualTo(rows + 1);
        await Assert.That(batch.GetBufferBytes(rows).ToArray()).IsEquivalentTo(new byte[] { 7, 7, 7, 7 }, CollectionOrdering.Matching);
        await Assert.That(batch.GetBufferBytes(0).ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(batch.BufferBytesList[rows]).IsEquivalentTo(new byte[] { 7, 7, 7, 7 }, CollectionOrdering.Matching);
    }

    /// <summary>Proves that a batch accepts further packets after it rejected one and keeps its size total equal to the sum over its bags.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BatchStaysUsableAfterARejectedPacket()
    {
        using ColumnarPacketBatch batch = new(
            ExportContents.Full, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        Packet[] good = PacketGenerators.CreateEthernetUdpPackets(3);
        batch.AddPacket(good[0]);

        // The shifted packet does not fit the existing columns: the batch rejects it and must stay usable.
        await _AssertTheShiftedPacketIsRejectedAsync(batch);

        batch.AddPacket(good[1]);
        batch.AddPacket(good[2]);

        long sumOfBags = 0;
        foreach (FieldColumnBag bag in batch.FieldBags.Values)
        {
            sumOfBags += bag.EstimatedSizeBytes;
        }

        await Assert.That(batch.PacketCount).IsEqualTo(3);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(sumOfBags);
        await Assert.That(batch.PacketIds[1]).IsEqualTo(good[1].Id.Value);
    }

    #endregion

    #region Truncating a field column bag

    /// <summary>Proves that truncating a bag removes the rows after the given count from every column and restores the size estimate.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagTruncateRemovesTheRowsAfterTheGivenCount()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(7, FieldType.I64, arena);
        bag.Add(1, 0, FieldValue.NewI64(10).Data, "a", "b");
        long sizeAfterFirstRow = bag.EstimatedSizeBytes;
        bag.Add(2, 1, FieldValue.NewI64(20).Data, null, null);
        bag.Add(3, 2, FieldValue.NewI64(30).Data, null, null);

        bag.TruncateTo(1, sizeAfterFirstRow);

        await Assert.That(bag.RowCount).IsEqualTo(1);
        await Assert.That(bag.I64Values.Count).IsEqualTo(1);
        await Assert.That(bag.Positions.Count).IsEqualTo(1);
        await Assert.That(bag.CustomRepresentations.Count).IsEqualTo(1);
        await Assert.That(bag.CustomTexts.Count).IsEqualTo(1);
        await Assert.That(bag.EstimatedSizeBytes).IsEqualTo(sizeAfterFirstRow);
    }

    /// <summary>Proves that truncating an address bag also cuts its inline fixed-size values, so rows and values stay in step.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagTruncateKeepsFixedSizePayloadRowsInStep()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(8, FieldType.IPv4Address, arena);
        bag.Add(1, 0, FieldValue.NewIPv4(new IPv4Address(0x0A000001u)).Data, null, null);
        long sizeAfterFirstRow = bag.EstimatedSizeBytes;
        bag.Add(2, 1, FieldValue.NewIPv4(new IPv4Address(0x0A000002u)).Data, null, null);

        bag.TruncateTo(1, sizeAfterFirstRow);

        await Assert.That(bag.RowCount).IsEqualTo(1);
        await Assert.That(_FixedValueCount(bag)).IsEqualTo(1);
    }

    /// <summary>Returns the number of address values a bag holds (a span cannot live in an async test).</summary>
    /// <param name="bag">An address bag.</param>
    /// <returns>The number of inline address values.</returns>
    private static int _FixedValueCount(FieldColumnBag bag) => bag.FixedValueSpan.Length;

    /// <summary>
    /// Truncating a bytes bag after its arrays were built also cuts the array cache, so a later row is not shadowed by a
    /// stale array.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FieldColumnBagTruncateCutsTheBytesArrayCache()
    {
        using BatchByteArena arena = new();
        FieldColumnBag bag = new(9, FieldType.Bytes, arena);
        bag.Add(1, 0, FieldValueData.NewBytes(_Bytes(1)), null, null);
        long sizeAfterFirstRow = bag.EstimatedSizeBytes;
        ArenaMark arenaAfterFirstRow = arena.Mark();
        bag.Add(2, 1, FieldValueData.NewBytes(_Bytes(2, 2)), null, null);
        int cachedBeforeTruncate = bag.BytesValues.Count;

        bag.TruncateTo(1, sizeAfterFirstRow);
        arena.TruncateTo(arenaAfterFirstRow);
        bag.Add(3, 1, FieldValueData.NewBytes(_Bytes(3, 3, 3)), null, null);

        await Assert.That(cachedBeforeTruncate).IsEqualTo(2);
        await Assert.That(bag.BytesValues.Count).IsEqualTo(2);
        await Assert.That(bag.BytesValues[1]).IsEquivalentTo(new byte[] { 3, 3, 3 }, CollectionOrdering.Matching);
    }

    /// <summary>Returns the given bytes as an array (a short form for test data).</summary>
    /// <param name="values">The bytes.</param>
    /// <returns>The same array.</returns>
    private static byte[] _Bytes(params byte[] values) => values;

    #endregion
}
