// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The order and the boundaries of the buffer rows: several buffers of one packet are written with the indexes 1, 2, 3 in that
/// order, a packet without additional buffers adds no row, a bound empty buffer is an empty value and not an absent one, the byte
/// estimate of a batch counts the buffers, and with one packet per batch the rows of each packet stay in their own batch.
/// </summary>
internal sealed class BuffersOrderTests
{
    #region Buffer values and packet fixtures

    /// <summary>A five-byte buffer; every call returns a new array, because a packet binds the array it is given and tests run in parallel.</summary>
    /// <returns>A new array.</returns>
    private static byte[] _A() => [0x01, 0x02, 0x03, 0x04, 0x05];

    /// <summary>A one-byte buffer; a new array per call (see <see cref="_A"/>).</summary>
    /// <returns>A new array.</returns>
    private static byte[] _B() => [0xFF];

    /// <summary>The packet ids of the four buffer rows of the three packets: packet 1 has three buffers, packet 2 one.</summary>
    /// <returns>A new array.</returns>
    private static int[] _RowPacketIds() => [1, 1, 1, 2];

    /// <summary>The buffer indexes of the four rows: 1, 2, 3 for packet 1, then 1 for packet 2.</summary>
    /// <returns>A new array.</returns>
    private static byte[] _RowIndexes() => [1, 2, 3, 1];

    /// <summary>The buffer index of the only row after a reset.</summary>
    /// <returns>A new array.</returns>
    private static byte[] _SingleIndex() => [1];

    /// <summary>A buffer of 300 bytes: longer than one hex dump line (16 bytes) and than a one-byte varint length (127); a new array per call.</summary>
    /// <returns>A new array.</returns>
    private static byte[] _C() => [.. Enumerable.Range(0, 300).Select(static i => unchecked((byte)(i * 7)))];

    /// <summary>The three packets of the exporter tests: no buffer, three buffers with an empty one in the middle, one buffer.</summary>
    /// <param name="factory">The factory that creates the packets; it owns the packets' memory.</param>
    /// <returns>The three packets.</returns>
    private static List<Packet> _Packets(BufferProbePacketFactory factory) =>
    [
        factory.CreatePacket(),
        factory.CreatePacket(_A(), [], _C()),
        factory.CreatePacket(_B()),
    ];

    /// <summary>The buffer rows the three packets of <see cref="_Packets"/> must give, packet ids 0, 1, and 2 of the factory.</summary>
    /// <returns>The four expected rows in export order.</returns>
    private static List<BufferRow> _ExpectedRows() =>
    [
        new BufferRow(1, 1, _A()),
        new BufferRow(1, 2, []),
        new BufferRow(1, 3, _C()),
        new BufferRow(2, 1, _B()),
    ];

    /// <summary>A batch of the hierarchy shape with the buffers member on or off and the given byte limit.</summary>
    /// <param name="buffers"><see langword="true"/> to collect the additional buffers.</param>
    /// <param name="maxBlockSize">The estimated byte size at which the batch asks for a flush.</param>
    /// <returns>A new batch that accepts up to 100 packets.</returns>
    private static ColumnarPacketBatch _Batch(bool buffers = true, long maxBlockSize = 1024 * 1024) =>
        new(new ExportContent { Buffers = buffers }, maxPacketsPerBlock: 100, maxBlockSize, isTimestampSorted: true);

    #endregion

    #region Batch

    /// <summary>Proves that a batch collects bound buffers as rows with the indexes 1, 2, 3 in order and adds no row for a bufferless packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ThreeBoundBuffersAreRowsOneToThreeInThisOrderAndAPacketWithoutBuffersAddsNoRow()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch batch = _Batch();

        foreach (Packet packet in _Packets(factory))
        {
            batch.AddPacket(packet);
        }

        await Assert.That(batch.PacketCount).IsEqualTo(3);
        await Assert.That(batch.BufferRowCount).IsEqualTo(4);
        await Assert.That(batch.BufferPacketIdSpan.ToArray()).IsEquivalentTo(_RowPacketIds(), CollectionOrdering.Matching);
        await Assert.That(batch.BufferIndexSpan.ToArray()).IsEquivalentTo(_RowIndexes(), CollectionOrdering.Matching);
        await Assert.That(batch.GetBufferBytes(0).ToArray()).IsEquivalentTo(_A(), CollectionOrdering.Matching);
        await Assert.That(batch.GetBufferBytes(1).ToArray()).IsEquivalentTo(Array.Empty<byte>(), CollectionOrdering.Matching);
        await Assert.That(batch.GetBufferBytes(2).ToArray()).IsEquivalentTo(_C(), CollectionOrdering.Matching);
        await Assert.That(batch.GetBufferBytes(3).ToArray()).IsEquivalentTo(_B(), CollectionOrdering.Matching);
        await Assert.That(batch.BufferBytesList.Count).IsEqualTo(4);
        await Assert.That(batch.BufferBytesList[2]).IsEquivalentTo(_C(), CollectionOrdering.Matching);
    }

    /// <summary>Proves that a batch without the buffers member collects no buffer row, and that with it the size estimate grows by the buffer rows.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task WithoutTheMemberNoBufferRowIsCollectedAndTheEstimateIgnoresTheBuffers()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch on = _Batch(buffers: true);
        using ColumnarPacketBatch off = _Batch(buffers: false);
        Packet packet = factory.CreatePacket(_A(), [], _C());

        on.AddPacket(packet);
        off.AddPacket(packet);

        await Assert.That(off.BufferRowCount).IsEqualTo(0);
        await Assert.That(off.BufferBytesList.Count).IsEqualTo(0);

        // The estimate with buffers on is larger by exactly five bytes per row (packet id and index) plus the buffer bytes, so the
        // batch limits bound the memory the buffers take.
        long expectedDelta = 3 * 5L + _A().Length + 0 + _C().Length;
        await Assert.That(on.EstimatedSizeBytes - off.EstimatedSizeBytes).IsEqualTo(expectedDelta);
    }

    /// <summary>Proves that the copied buffer bytes alone can push a batch over its byte limit, so a flush is requested only with the member on.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuffersAloneCanMakeABatchReachItsByteLimit()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch on = _Batch(buffers: true, maxBlockSize: 200);
        using ColumnarPacketBatch off = _Batch(buffers: false, maxBlockSize: 200);
        Packet packet = factory.CreatePacket(_C());

        bool flushOn = on.AddPacket(packet);
        bool flushOff = off.AddPacket(packet);

        // The packet itself is small; only the 300 copied buffer bytes cross the limit of 200 bytes.
        await Assert.That(flushOn).IsTrue();
        await Assert.That(flushOff).IsFalse();
    }

    /// <summary>Proves that resetting a batch drops its buffer rows and size estimate, and that it then collects new rows from index 1.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ResetForgetsTheBufferRowsAndTheBatchCollectsNewOnes()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch batch = _Batch();
        batch.AddPacket(factory.CreatePacket(_A(), _B()));
        _ = batch.BufferBytesList;

        batch.Reset();

        await Assert.That(batch.BufferRowCount).IsEqualTo(0);
        await Assert.That(batch.BufferBytesList.Count).IsEqualTo(0);
        await Assert.That(batch.EstimatedSizeBytes).IsEqualTo(0L);

        batch.AddPacket(factory.CreatePacket(_C()));

        await Assert.That(batch.BufferRowCount).IsEqualTo(1);
        await Assert.That(batch.BufferIndexSpan.ToArray()).IsEquivalentTo(_SingleIndex(), CollectionOrdering.Matching);
        await Assert.That(batch.BufferBytesList[0]).IsEquivalentTo(_C(), CollectionOrdering.Matching);
    }

    #endregion

    #region Exporters

    /// <summary>Exports the three packets of <see cref="_Packets"/> with the buffers on and reads the buffer rows back.</summary>
    /// <param name="scenario">The format and shape.</param>
    /// <param name="dir">The directory of the output; only DuckDB needs it (Parquet writes to memory without it).</param>
    /// <param name="maxPacketsPerBatch">The packets per batch; 0 keeps the default.</param>
    /// <returns>The buffer rows the reader found, or <see langword="null"/> when the format has no buffer records.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused a packet.</exception>
    private static async Task<List<BufferRow>?> _ExportAndRead(BufferScenario scenario, TestDir? dir, int maxPacketsPerBatch = 0)
    {
        using BufferProbePacketFactory factory = new();
        OpenBufferExport export = BufferExportScenarios.Open(scenario, BufferMembers.Buffers, dir, "order", maxPacketsPerBatch);
        foreach (Packet packet in _Packets(factory))
        {
            if (!export.Offer(packet))
            {
                throw new InvalidOperationException("The exporter refused a packet of the order fixture.");
            }
        }

        (string path, string text) = export.Finish();
        return await BufferExportScenarios.ReadAsync(scenario, new BufferRun(path, text, FirstPacketId: 0, [], Bytes: export.Bytes, Target: export.Target));
    }

    /// <summary>Compares the rows a reader found with the rows the three packets must give, in order.</summary>
    /// <param name="read">The rows the reader found.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertExpectedRowsAsync(List<BufferRow>? read)
    {
        List<BufferRow> expected = _ExpectedRows();
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(read[i].PacketId).IsEqualTo(expected[i].PacketId);
            await Assert.That(read[i].Index).IsEqualTo(expected[i].Index);
            await Assert.That(read[i].Bytes).IsEquivalentTo(expected[i].Bytes, CollectionOrdering.Matching);
        }
    }

    /// <summary>Proves that each fast format writes the buffers of a packet in index order and writes an empty buffer as an empty value.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    [Arguments(BufferScenario.JsonPrettyTree)]
    [Arguments(BufferScenario.JsonArrayTree)]
    [Arguments(BufferScenario.JsonCompactTree)]
    [Arguments(BufferScenario.CsvPacketRows)]
    [Arguments(BufferScenario.TextTree)]
    public async Task EveryFastFormatWritesTheBuffersInIndexOrderAndAnEmptyBufferAsAnEmptyValue(BufferScenario scenario)
    {
        List<BufferRow>? read = await _ExportAndRead(scenario, dir: null);

        await _AssertExpectedRowsAsync(read);
    }

    /// <summary>Proves that DuckDB and Parquet write the buffers of a packet in index order and write an empty buffer as an empty value.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(BufferScenario.DuckDbHierarchy)]
    [Arguments(BufferScenario.ParquetHierarchy)]
    public async Task DuckDbAndParquetWriteTheBuffersInIndexOrderAndAnEmptyBufferAsAnEmptyValue(BufferScenario scenario)
    {
        using TestDir? dir = scenario == BufferScenario.DuckDbHierarchy
            ? new("buffers_order_db")
            : null;

        List<BufferRow>? read = await _ExportAndRead(scenario, dir);

        await _AssertExpectedRowsAsync(read);
    }

    /// <summary>Proves that the buffer rows of every packet keep their order when each batch holds one packet.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(BufferScenario.DuckDbHierarchy)]
    [Arguments(BufferScenario.ParquetHierarchy)]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    public async Task WithOnePacketPerBatchTheRowsOfEveryPacketAreStillWrittenInOrder(BufferScenario scenario)
    {
        using TestDir? dir = scenario == BufferScenario.DuckDbHierarchy
            ? new("buffers_one_per_batch")
            : null;

        List<BufferRow>? read = await _ExportAndRead(scenario, dir, maxPacketsPerBatch: 1);

        await _AssertExpectedRowsAsync(read);
    }

    /// <summary>
    /// A packet at the limit of the table, 255 additional buffers, is written completely in every fast format: the indexes 1 to 255
    /// (index 128 and above need a two-byte varint), buffers of 0 to 254 bytes (the empty buffer, one-byte and two-byte lengths), in
    /// order. The length prefix of a PBF standard record is computed from the varint sizes, so this proves the computation for every
    /// index and length class.
    /// </summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    [Arguments(BufferScenario.JsonArrayTree)]
    [Arguments(BufferScenario.CsvPacketRows)]
    [Arguments(BufferScenario.TextTree)]
    public async Task APacketWithTheMaximumOf255BuffersIsWrittenCompletely(BufferScenario scenario)
    {
        using BufferProbePacketFactory factory = new();
        byte[][] buffers = [.. Enumerable.Range(0, 255).Select(static i => Enumerable.Repeat(checked((byte)i), i).ToArray())];
        OpenBufferExport export = BufferExportScenarios.Open(scenario, BufferMembers.Buffers, dir: null, "max");
        Packet packet = factory.CreatePacket(buffers);

        bool offered = export.Offer(packet);
        (string path, string text) = export.Finish();
        BufferRun run = new(path, text, FirstPacketId: 0, [], Bytes: export.Bytes, Target: export.Target);
        List<BufferRow>? read = await BufferExportScenarios.ReadAsync(scenario, run);

        await Assert.That(offered).IsTrue();
        await Assert.That(packet.BufferCount).IsEqualTo(256);
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(255);
        for (int i = 0; i < 255; i++)
        {
            await Assert.That(read[i].Index).IsEqualTo(i + 1);
            await Assert.That(read[i].Bytes).IsEquivalentTo(buffers[i], CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// With one packet per batch a PBF columnar file has one block per packet, and a block carries the buffer column only for the
    /// rows of its own packet: the first block (no buffer) has no column, the second has the three rows of packet 1, the third the
    /// one row of packet 2.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task WithOnePacketPerBatchEachPbfColumnarBlockHoldsOnlyTheBufferRowsOfItsPacket()
    {
        using BufferProbePacketFactory factory = new();
        OpenBufferExport export = BufferExportScenarios.Open(
            BufferScenario.PbfColumnarHierarchy, BufferMembers.Buffers, dir: null, "blocks", maxPacketsPerBatch: 1);
        foreach (Packet packet in _Packets(factory))
        {
            await Assert.That(export.Offer(packet)).IsTrue();
        }

        _ = export.Finish();
        (_, List<List<PbfField>> blocks) = PbfFileScanner.FromBytes(export.Bytes!);

        await Assert.That(blocks.Count).IsEqualTo(3);
        await Assert.That(blocks[0].Any(static f => f.Number == 16)).IsFalse();
        await Assert.That(_IndexCount(blocks[1])).IsEqualTo(3);
        await Assert.That(_IndexCount(blocks[2])).IsEqualTo(1);
    }

    /// <summary>The number of rows of the buffer column of a block: its repeated index entries.</summary>
    /// <param name="block">The top-level fields of a columnar block; exactly one is the buffer column (field 16).</param>
    /// <returns>The number of index entries in the buffer column.</returns>
    private static int _IndexCount(List<PbfField> block)
    {
        PbfField column = block.Single(static f => f.Number == 16);
        return PbfFileScanner.Fields(column.Payload!).Count(static f => f.Number == 2);
    }

    /// <summary>
    /// A bound empty buffer is a record with no bytes in every format, never a missing record: the title with the length 0 and no
    /// dump line in the text, <c>1:</c> in CSV (shown by the reader's exact comparison above), an empty Base64 string in JSON.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnEmptyBufferIsATextBlockWithLengthZeroAndNoDumpLine()
    {
        using BufferProbePacketFactory factory = new();
        OpenBufferExport export = BufferExportScenarios.Open(BufferScenario.TextTree, BufferMembers.Buffers, dir: null);
        bool offered = export.Offer(factory.CreatePacket([], _B()));

        (_, string text) = export.Finish();
        string[] lines = text.Split('\n');
        int title = Array.IndexOf(lines, "Buffer 1 (0):");

        await Assert.That(offered).IsTrue();
        await Assert.That(title).IsGreaterThanOrEqualTo(0);
        await Assert.That(lines[title + 1]).IsEqualTo("Buffer 2 (1):");
        await Assert.That(lines[title + 2]).IsEqualTo("0000  FF");
    }

    #endregion
}
