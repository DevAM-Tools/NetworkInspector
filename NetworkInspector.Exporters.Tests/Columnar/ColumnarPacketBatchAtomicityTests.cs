// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Atomicity tests for <see cref="ColumnarPacketBatch.AddPacket"/>: a packet that cannot be added completely must leave the
/// batch exactly as it was before the call.
/// </summary>
internal sealed class ColumnarPacketBatchAtomicityTests
{
    /// <summary>
    /// Parses a packet on a second stack whose field ids are shifted by one against the thread's standard stack (one extra
    /// field is registered first). Added to a batch that already holds columns from the standard stack, the shifted ids
    /// meet columns of another type, so the batch rejects the packet part-way through.
    /// </summary>
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
            new FrameId(0),
            Timestamp.FromNanos(5_000_000),
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            LinkType.Ethernet,
            interfaceId,
            registry).Value;

        ParseOptions options = new();
        if (!Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure)
            || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    [Test]
    public async Task RejectedPacketLeavesTheBatchExactlyAsItWas()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
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

        Packet shifted = _CreateShiftedStackPacket();

        await Assert.That(() => batch.AddPacket(shifted)).Throws<InvalidOperationException>();

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
            await Assert.That(entry.Value.PacketIds.Count).IsEqualTo(entry.Value.NodeIds.Count);
            await Assert.That(entry.Value.CustomTexts.Count).IsEqualTo(entry.Value.RowCount);
        }
    }

    [Test]
    public async Task BatchStaysUsableAfterARejectedPacket()
    {
        using ColumnarPacketBatch batch = new(
            ColumnarDetailFlags.All, maxPacketsPerBlock: 100, maxBlockSize: 1024 * 1024, isTimestampSorted: true);
        Packet[] good = PacketGenerators.CreateEthernetUdpPackets(3);
        batch.AddPacket(good[0]);
        Packet shifted = _CreateShiftedStackPacket();
        try
        {
            batch.AddPacket(shifted);
        }
        catch (InvalidOperationException)
        {
            // Expected: the shifted packet does not fit the existing columns.
        }

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

    [Test]
    public async Task FieldColumnBagTruncateRemovesTheRowsAfterTheGivenCount()
    {
        FieldColumnBag bag = new(7, FieldType.I64);
        bag.Add(1, 0, FieldValue.NewI64(10).Data, "a", "b");
        long sizeAfterFirstRow = bag.EstimatedSizeBytes;
        bag.Add(2, 1, FieldValue.NewI64(20).Data, null, null);
        bag.Add(3, 2, FieldValue.NewI64(30).Data, null, null);

        bag.TruncateTo(1, sizeAfterFirstRow);

        await Assert.That(bag.RowCount).IsEqualTo(1);
        await Assert.That(bag.I64Values.Count).IsEqualTo(1);
        await Assert.That(bag.NodeIds.Count).IsEqualTo(1);
        await Assert.That(bag.CustomRepresentations.Count).IsEqualTo(1);
        await Assert.That(bag.CustomTexts.Count).IsEqualTo(1);
        await Assert.That(bag.EstimatedSizeBytes).IsEqualTo(sizeAfterFirstRow);
    }

    [Test]
    public async Task FieldColumnBagTruncateKeepsFixedSizePayloadRowsInStep()
    {
        FieldColumnBag bag = new(8, FieldType.IPv4Address);
        bag.Add(1, 0, FieldValue.NewIPv4(new IPv4Address(0x0A000001u)).Data, null, null);
        long sizeAfterFirstRow = bag.EstimatedSizeBytes;
        bag.Add(2, 1, FieldValue.NewIPv4(new IPv4Address(0x0A000002u)).Data, null, null);

        bag.TruncateTo(1, sizeAfterFirstRow);

        await Assert.That(bag.RowCount).IsEqualTo(1);
        await Assert.That(bag.BytesValues.Count).IsEqualTo(1);
    }
}
