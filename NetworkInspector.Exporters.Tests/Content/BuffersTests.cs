// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The additional packet buffers (the bytes protocols attach to a packet: a reassembled datagram, a TCP PDU, a decoded HTTP body, an
/// unmasked WebSocket payload) are an opt-in of every packet exporter. By default no format writes them. With the member on, every
/// format and shape writes exactly what the packets report, in packet and index order, and the tree-free parse gives the same rows as
/// the parse with a field tree. Each format is read back with its own reader (<see cref="BufferReaders"/>), which never shares code
/// with the writer, and the expectation is taken from the parsed packets themselves, not from a constant.
/// </summary>
internal sealed class BuffersTests
{
    #region Defaults

    /// <summary>Proves that the buffers member is off in a plain content and in the default content of every exporter.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheBuffersMemberIsOffInEveryContentAndEveryExporterDefault()
    {
        await Assert.That(new ExportContent().Buffers).IsFalse();
        await Assert.That(DuckDbExporter.DefaultContent.Buffers).IsFalse();
        await Assert.That(ParquetExporter.DefaultContent.Buffers).IsFalse();
        await Assert.That(PbfExporter.DefaultContent.Buffers).IsFalse();
        await Assert.That(JsonExporter.DefaultContent.Buffers).IsFalse();
        await Assert.That(TextExporter.DefaultContent.Buffers).IsFalse();
        await Assert.That(CsvExporter.ContentTemplate.Buffers).IsFalse();
    }

    /// <summary>Proves that PBF, JSON, CSV, and Text write no buffer record unless the buffers member is on, although the packets carry buffers.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfStandardSelectedFields)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    [Arguments(BufferScenario.PbfColumnarSelectedFields)]
    [Arguments(BufferScenario.JsonPrettyTree)]
    [Arguments(BufferScenario.JsonArrayTree)]
    [Arguments(BufferScenario.JsonCompactTree)]
    [Arguments(BufferScenario.JsonSelectedFields)]
    [Arguments(BufferScenario.JsonAllFields)]
    [Arguments(BufferScenario.CsvPacketRows)]
    [Arguments(BufferScenario.CsvWide)]
    [Arguments(BufferScenario.TextTree)]
    [Arguments(BufferScenario.TextSelectedFields)]
    [Arguments(BufferScenario.TextAllFields)]
    public async Task NoBufferRecordsAreWrittenByDefaultInTheFastFormats(BufferScenario scenario)
    {
        using TestDir dir = new("buffers_default");

        BufferRun run = BufferExportScenarios.Run(scenario, BufferTrace.Create(), BufferMembers.None, dir);

        // The packets do carry additional buffers, so an empty result is the switch and not an empty capture.
        await Assert.That(run.Expected.Count).IsGreaterThan(0);
        await Assert.That(BufferExportScenarios.Read(scenario, run)).IsNull();
    }

    /// <summary>Proves that DuckDB and Parquet write no buffer table or file unless the buffers member is on, although the packets carry buffers.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(BufferScenario.DuckDbHierarchy)]
    [Arguments(BufferScenario.DuckDbSelectedFields)]
    [Arguments(BufferScenario.ParquetHierarchy)]
    [Arguments(BufferScenario.ParquetSelectedFields)]
    public async Task NoBufferRecordsAreWrittenByDefaultInDuckDbAndParquet(BufferScenario scenario)
    {
        using TestDir dir = new("buffers_default_db");

        BufferRun run = BufferExportScenarios.Run(scenario, BufferTrace.Create(), BufferMembers.None, dir);

        await Assert.That(run.Expected.Count).IsGreaterThan(0);
        await Assert.That(BufferExportScenarios.Read(scenario, run)).IsNull();
    }

    #endregion

    #region Buffers on

    /// <summary>Compares what a reader found with what the packets reported, row by row.</summary>
    /// <param name="read">The rows the reader found.</param>
    /// <param name="expected">The rows the packets reported.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertRowsAsync(List<BufferRow>? read, List<BufferRow> expected)
    {
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Count).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(read[i].PacketId).IsEqualTo(expected[i].PacketId);
            await Assert.That(read[i].Index).IsEqualTo(expected[i].Index);
            await Assert.That(read[i].Bytes).IsEquivalentTo(expected[i].Bytes, CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// The fixture itself: the capture produces the four kinds of buffer the requirements name, in the packets that complete them, and
    /// nothing else. The other tests compare an export with these rows, so a capture that stopped producing a kind would fail here
    /// instead of making the comparisons vacuous.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheCaptureBindsTheReassembledDatagramThePduTheDecodedBodyAndTheUnmaskedPayload()
    {
        using TestDir dir = new("buffers_fixture");

        BufferRun run = BufferExportScenarios.Run(BufferScenario.JsonArrayTree, BufferTrace.Create(), BufferMembers.None, dir);

        // Packet 1 completes the datagram, packet 3 the request PDU, packet 4 holds the response PDU and its decoded body, packet 5
        // the upgrade response with the masked frame (PDU) and the unmasked payload.
        int first = run.FirstPacketId;
        await Assert.That(run.Expected.Select(static row => (row.PacketId, row.Index)).Select(pair => (pair.PacketId - first, pair.Index)))
            .IsEquivalentTo([(1, 1), (3, 1), (4, 1), (4, 2), (5, 1), (5, 2)], CollectionOrdering.Matching);
        await Assert.That(run.Expected.Count(static row => row.Bytes.SequenceEqual(BufferTrace.DecodedBody.Span))).IsEqualTo(1);
        await Assert.That(run.Expected.Count(static row => row.Bytes.SequenceEqual(BufferTrace.UnmaskedPayload.Span))).IsEqualTo(1);

        // The reassembled datagram is the UDP header followed by the payload that the two fragments carried.
        byte[] datagram = run.Expected.Single(row => row.PacketId - first == 1).Bytes;
        await Assert.That(datagram.AsSpan(8).SequenceEqual(BufferTrace.FragmentedPayload.Span)).IsTrue();
        await Assert.That(datagram.Length).IsEqualTo(8 + BufferTrace.FragmentedPayload.Length);
    }

    /// <summary>Proves that PBF, JSON, CSV, and Text with the buffers member on write the buffers of every packet as the packets reported them.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfStandardSelectedFields)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    [Arguments(BufferScenario.PbfColumnarSelectedFields)]
    [Arguments(BufferScenario.JsonPrettyTree)]
    [Arguments(BufferScenario.JsonArrayTree)]
    [Arguments(BufferScenario.JsonCompactTree)]
    [Arguments(BufferScenario.JsonSelectedFields)]
    [Arguments(BufferScenario.JsonAllFields)]
    [Arguments(BufferScenario.CsvPacketRows)]
    [Arguments(BufferScenario.CsvWide)]
    [Arguments(BufferScenario.TextTree)]
    [Arguments(BufferScenario.TextSelectedFields)]
    [Arguments(BufferScenario.TextAllFields)]
    public async Task BuffersOnWriteTheBuffersOfEveryPacketInTheFastFormats(BufferScenario scenario)
    {
        using TestDir dir = new("buffers_on");

        BufferRun run = BufferExportScenarios.Run(scenario, BufferTrace.Create(), BufferMembers.Buffers, dir);

        await _AssertRowsAsync(BufferExportScenarios.Read(scenario, run), run.Expected);
    }

    /// <summary>Proves that DuckDB and Parquet with the buffers member on write the buffers of every packet as the packets reported them.</summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(BufferScenario.DuckDbHierarchy)]
    [Arguments(BufferScenario.DuckDbSelectedFields)]
    [Arguments(BufferScenario.ParquetHierarchy)]
    [Arguments(BufferScenario.ParquetSelectedFields)]
    public async Task BuffersOnWriteTheBuffersOfEveryPacketInDuckDbAndParquet(BufferScenario scenario)
    {
        using TestDir dir = new("buffers_on_db");

        BufferRun run = BufferExportScenarios.Run(scenario, BufferTrace.Create(), BufferMembers.Buffers, dir);

        await _AssertRowsAsync(BufferExportScenarios.Read(scenario, run), run.Expected);
    }

    /// <summary>
    /// The buffers are independent of the frame bytes: with both on, every format writes the same buffer rows as with the buffers
    /// alone (the frame copy is index 0 and is not a buffer row).
    /// </summary>
    /// <param name="scenario">The format and shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfStandardHierarchy)]
    [Arguments(BufferScenario.PbfColumnarHierarchy)]
    [Arguments(BufferScenario.JsonCompactTree)]
    [Arguments(BufferScenario.CsvPacketRows)]
    [Arguments(BufferScenario.TextTree)]
    public async Task FramesAndBuffersTogetherWriteTheSameBufferRowsAsBuffersAlone(BufferScenario scenario)
    {
        using TestDir dir = new("buffers_with_frames");

        BufferRun run = BufferExportScenarios.Run(
            scenario, BufferTrace.Create(), BufferMembers.Frames | BufferMembers.Buffers, dir);

        await _AssertRowsAsync(BufferExportScenarios.Read(scenario, run), run.Expected);
    }

    #endregion

    #region Tree-free parse

    /// <summary>
    /// A tree-free parse binds the same buffers as the parse with a field tree: the buffers belong to the packet, not to the parse
    /// mode. The two runs parse separate captures (the stack keeps its reassembly state), so the packet ids are compared relative
    /// to each run's first packet.
    /// </summary>
    /// <param name="tree">The scenario that parses with a field tree.</param>
    /// <param name="treeFree">The scenario of the same format that parses without a field tree.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(BufferScenario.PbfColumnarHierarchy, BufferScenario.PbfColumnarSelectedFields)]
    [Arguments(BufferScenario.PbfStandardHierarchy, BufferScenario.PbfStandardSelectedFields)]
    [Arguments(BufferScenario.JsonArrayTree, BufferScenario.JsonSelectedFields)]
    [Arguments(BufferScenario.TextTree, BufferScenario.TextSelectedFields)]
    public async Task TheTreeFreeParseGivesTheSameBufferRowsAsTheTreeParseInTheFastFormats(BufferScenario tree, BufferScenario treeFree)
    {
        using TestDir dir = new("buffers_tree_free");

        await _AssertSameRowsAsync(tree, treeFree, dir);
    }

    /// <summary>Proves that the tree-free parse gives the same buffer rows as the parse with a field tree in DuckDB and Parquet.</summary>
    /// <param name="tree">The scenario that parses with a field tree.</param>
    /// <param name="treeFree">The scenario of the same format that parses without a field tree.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(BufferScenario.DuckDbHierarchy, BufferScenario.DuckDbSelectedFields)]
    [Arguments(BufferScenario.ParquetHierarchy, BufferScenario.ParquetSelectedFields)]
    public async Task TheTreeFreeParseGivesTheSameBufferRowsAsTheTreeParseInDuckDbAndParquet(BufferScenario tree, BufferScenario treeFree)
    {
        using TestDir dir = new("buffers_tree_free_db");

        await _AssertSameRowsAsync(tree, treeFree, dir);
    }

    /// <summary>Runs both scenarios on their own capture and asserts that the buffer rows are the same relative to the first packet.</summary>
    /// <param name="tree">The scenario that parses with a field tree.</param>
    /// <param name="treeFree">The scenario of the same format that parses without a field tree.</param>
    /// <param name="dir">The directory of the outputs.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    private static async Task _AssertSameRowsAsync(BufferScenario tree, BufferScenario treeFree, TestDir dir)
    {
        BufferRun treeRun = BufferExportScenarios.Run(tree, BufferTrace.Create(), BufferMembers.Buffers, dir);
        BufferRun freeRun = BufferExportScenarios.Run(treeFree, BufferTrace.Create(), BufferMembers.Buffers, dir);

        List<BufferRow>? treeRows = BufferExportScenarios.Read(tree, treeRun);
        List<BufferRow>? freeRows = BufferExportScenarios.Read(treeFree, freeRun);

        await Assert.That(treeRows).IsNotNull();
        await Assert.That(freeRows).IsNotNull();
        await Assert.That(freeRows!.Count).IsEqualTo(treeRows!.Count);
        for (int i = 0; i < treeRows.Count; i++)
        {
            await Assert.That(freeRows[i].PacketId - freeRun.FirstPacketId).IsEqualTo(treeRows[i].PacketId - treeRun.FirstPacketId);
            await Assert.That(freeRows[i].Index).IsEqualTo(treeRows[i].Index);
            await Assert.That(freeRows[i].Bytes).IsEquivalentTo(treeRows[i].Bytes, CollectionOrdering.Matching);
        }

        // The tree-free feed really was tree-free: no packet of the flat shape carried a field tree, while every packet of the tree run did.
        await Assert.That(freeRun.PacketCount).IsGreaterThan(0);
        await Assert.That(freeRun.PacketsWithFieldTree).IsEqualTo(0);
        await Assert.That(treeRun.PacketsWithFieldTree).IsEqualTo(treeRun.PacketCount);
    }

    #endregion
}
