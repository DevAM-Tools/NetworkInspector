// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for <see cref="ColumnarBlockBuilder"/>: the encoded block is the same however often the builder is reused (its four
/// scratch buffers carry nothing from one block into the next), and a warm builder encodes a block without allocating per column.
/// </summary>
internal sealed class ColumnarBlockBuilderTests
{
    #region Encoding helpers

    /// <summary>The largest field id the builders of these tests accept; it sizes the per-field tables of a builder.</summary>
    private const int _MaxFieldId = 32768;

    /// <summary>Adds the packets to the builder, builds the block, copies its bytes, and resets the builder for the next block.</summary>
    /// <param name="builder">The builder to fill; it is empty again when the call returns.</param>
    /// <param name="packets">The packets of the block.</param>
    /// <returns>A copy of the encoded block.</returns>
    private static byte[] _BuildBlock(ColumnarBlockBuilder builder, Packet[] packets)
    {
        foreach (Packet packet in packets)
        {
            _ = builder.AddPacket(packet);
        }

        byte[] bytes = builder.Build().ToArray();
        builder.Reset();
        return bytes;
    }

    /// <summary>Encodes a text column with the builder and returns the bytes of the length-delimited field it wrote.</summary>
    /// <param name="rows">One entry per row: null for a row without a text.</param>
    /// <returns>The column message: tag, length, and the entries.</returns>
    private static byte[] _EncodeTextColumn(string?[] rows)
    {
        ColumnarBlockBuilder builder = new(_MaxFieldId, ExportContents.Full);
        PooledBuffer buffer = new(64);
        try
        {
            builder.WriteTextColumn(ref buffer, PbfFieldNumbers.ColumnCustomTexts, rows);
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Return();
            builder.Dispose();
        }
    }

    /// <summary>The payload of the single length-delimited field 18 of an encoded text column.</summary>
    /// <param name="encoded">The bytes of an encoded text column.</param>
    /// <returns>The bytes inside the length-delimited field.</returns>
    private static byte[] _ColumnPayload(byte[] encoded)
    {
        List<PbfField> fields = PbfFileScanner.Fields(encoded);
        return fields[0].Payload!;
    }

    #endregion

    #region Text columns

    /// <summary>
    /// A text column writes a run length for each stretch of absent rows and one entry for each present text (an empty text is a
    /// present entry of zero length).
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheTextColumnWritesRunsOfAbsentRowsAndOneEntryPerPresentText()
    {
        byte[] payload = _ColumnPayload(_EncodeTextColumn([null, null, "a", string.Empty, null]));

        // Two absent rows (sub-field 2 = 2), "a" (sub-field 1), the empty text (sub-field 1, zero length), one absent row.
        await Assert.That(Convert.ToHexString(payload)).IsEqualTo("1002" + "0A0161" + "0A00" + "1001");
        await Assert.That(string.Join("|", PbfFileScanner.DecodeTextColumn(payload).Select(static row => row ?? "∅")))
            .IsEqualTo("∅|∅|a||∅");
    }

    /// <summary>A text column in which no row has a text, or that has no rows, writes no bytes at all.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ATextColumnWithoutAnyTextIsNotWritten()
    {
        await Assert.That(_EncodeTextColumn([null, null, null]).Length).IsEqualTo(0);
        await Assert.That(_EncodeTextColumn([]).Length).IsEqualTo(0);
    }

    /// <summary>A text column in which every row has a text writes only entries and no run of absent rows.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ATextColumnWithOnlyTextsHasNoAbsentRun()
    {
        byte[] payload = _ColumnPayload(_EncodeTextColumn(["x", string.Empty]));

        await Assert.That(Convert.ToHexString(payload)).IsEqualTo("0A0178" + "0A00");
        await Assert.That(PbfFileScanner.Fields(payload).All(static field => field.Number == PbfFieldNumbers.TextColumnPresent)).IsTrue();
    }

    /// <summary>A text column of one or two rows, with absent rows and empty texts in every position, decodes to the rows that were encoded.</summary>
    /// <param name="spec">The rows as text: <c>|</c> separates rows, the empty-set sign marks an absent row.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments("a")]
    [Arguments("∅|a")]
    [Arguments("a|∅")]
    [Arguments("")]
    [Arguments("∅")]
    public async Task ATextColumnOfOneOrTwoRowsDecodesToTheSameRows(string spec)
    {
        // The rows come as a spec: "|" separates rows, "∅" is an absent row, the empty string is an empty text. A spec of one absent
        // row has no text at all, so no column is written and the decoder is never asked.
        string?[] rows = [.. spec.Split('|').Select(static token => token == "∅"
            ? null
            : token)];
        byte[] encoded = _EncodeTextColumn(rows);

        if (rows.All(static row => row is null))
        {
            await Assert.That(encoded.Length).IsEqualTo(0);
            return;
        }

        string?[] decoded = PbfFileScanner.DecodeTextColumn(_ColumnPayload(encoded));

        await Assert.That(string.Join("|", decoded.Select(static row => row ?? "∅"))).IsEqualTo(spec);
    }

    /// <summary>A run of 300 absent rows is written as a two-byte varint and decodes back to the full row count.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnAbsentRunLongerThanOneVarintByteUsesAMultiByteVarint()
    {
        string?[] rows = new string?[301];
        rows[300] = "end";
        byte[] payload = _ColumnPayload(_EncodeTextColumn(rows));

        // 300 absent rows are the varint AC 02 (300 = 0b10_0101100: low seven bits 0x2C with the continuation bit, then 2), then the
        // entry "end".
        await Assert.That(Convert.ToHexString(payload)).IsEqualTo("10" + "AC02" + "0A03656E64");
        await Assert.That(PbfFileScanner.DecodeTextColumn(payload).Length).IsEqualTo(301);
    }

    #endregion

    #region Reusing a builder

    /// <summary>Building the same packets twice with one builder gives the same bytes both times.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task BuildingTheSamePacketsTwiceGivesTheSameBytes()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(20);
        ColumnarBlockBuilder builder = new(_MaxFieldId, ExportContents.Full);

        byte[] first = _BuildBlock(builder, packets);
        byte[] second = _BuildBlock(builder, packets);
        builder.Dispose();

        await Assert.That(first.Length).IsGreaterThan(0);
        await Assert.That(second.AsSpan().SequenceEqual(first)).IsTrue();
    }

    /// <summary>A small block built after a larger block with the same builder equals the block a fresh builder encodes.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task ABlockBuiltAfterALargerBlockCarriesNoLeftoversOfIt()
    {
        Packet[] many = PacketGenerators.CreateEthernetUdpPackets(50);
        Packet[] one = [many[0]];
        ColumnarBlockBuilder reused = new(_MaxFieldId, ExportContents.Full);
        ColumnarBlockBuilder fresh = new(_MaxFieldId, ExportContents.Full);

        _ = _BuildBlock(reused, many);
        byte[] afterLarger = _BuildBlock(reused, one);
        byte[] expected = _BuildBlock(fresh, one);
        reused.Dispose();
        fresh.Dispose();

        await Assert.That(afterLarger.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// After a warm-up, building a block of dozens of field columns allocates less than 512 bytes per build, which is far below
    /// what an object per column would cost.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AWarmBuilderEncodesABlockWithoutAllocatingPerColumn()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(100);
        ColumnarBlockBuilder builder = new(_MaxFieldId, ExportContents.Full);
        foreach (Packet packet in packets)
        {
            _ = builder.AddPacket(packet);
        }

        // Warm-up grows the four buffers to the size of this block; Build is repeatable on the same batch.
        for (int i = 0; i < 3; i++)
        {
            _ = builder.Build();
        }

        // Run alone, 400 repetitions of this test under the load of two other test projects gave one run with about 17 KB more in
        // the window than all others (858 B per build); the builder does not repeat that allocation. A per-column object appears
        // in every build, so the smallest single build keeps the proof and drops every build that hit a one-off.
        // Rounds of 20 builds were not enough: under load a whole 20-build round could carry one-off bytes.
        long perBuild = long.MaxValue;
        for (int round = 0; round < 100 && perBuild >= 512; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = builder.Build();
            perBuild = Math.Min(perBuild, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        int columns = builder.Batch.FieldBags.Count;
        builder.Dispose();

        // A buffer object per column (several per field) and a boxed enumerator per column would cost at least 100 bytes for each
        // of the dozens of field columns of this block; the dictionary enumerators that remain are two objects per build.
        await Assert.That(columns).IsGreaterThan(10);
        await Assert.That(perBuild).IsLessThan(512);
    }

    #endregion
}
