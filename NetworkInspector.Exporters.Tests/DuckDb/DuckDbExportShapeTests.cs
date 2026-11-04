// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// The three export shapes of the DuckDB exporter, fed the way the parse pipeline feeds them: a tree-free parse with the
/// exporter's own observer. Covers the file layout of each shape (the position column, the shape marker, the topology, the
/// catalog of selected fields), the occurrence choice on the one packet that holds a field twice (IPv4 in IPv4), and the commit
/// rule at exporter level (filter-rejected, muted, mismatched, and unobserved packets).
/// </summary>
[Category("Slow")]
internal sealed class DuckDbExportShapeTests
{
    #region Building exporters and reading back files

    /// <summary>Builds a DuckDB exporter for the content.</summary>
    /// <param name="path">The target file.</param>
    /// <param name="content">What the export contains, including its shape.</param>
    /// <param name="mode">The write mode; synchronous by default, so the file is complete when <c>OnFinish</c> returns.</param>
    /// <returns>The exporter; the caller disposes it.</returns>
    private static DuckDbExporter _Build(string path, ExportContent content, ExportWriteMode mode = ExportWriteMode.Synchronous) =>
        DuckDbExporter.CreateBuilder().ToFile(path).WithContent(content).WithWriteMode(mode).Build();

    /// <summary>Whether the database has a table of the name.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tableName">The table name.</param>
    /// <returns><see langword="true"/> when the table exists.</returns>
    private static bool _TableExists(DuckDBConnection connection, string tableName) =>
        DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName}'")) > 0;

    /// <summary>The column names of a table in column order.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tableName">The table name.</param>
    /// <returns>The names joined with commas.</returns>
    private static string _Columns(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            Invariant($"SELECT column_name FROM information_schema.columns WHERE table_name = '{tableName}' ORDER BY ordinal_position");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(',', names);
    }

    /// <summary>Looks up the id of a field in the test stack.</summary>
    /// <param name="name">The field name, for example <c>ip.src</c>.</param>
    /// <returns>The field id, which names the field's table.</returns>
    private static int _FieldId(string name) => TestHarness.GetStack().GetFieldId(name)!.Value.Value;

    /// <summary>Reads the key and value columns of the <c>export_info</c> table.</summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>The export info rows, key to value.</returns>
    private static Dictionary<string, string> _ExportInfo(DuckDBConnection connection)
    {
        Dictionary<string, string> rows = [];
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM export_info";
        using DuckDBDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    /// <summary>Reads the <c>ip.src</c> table as rows of the form <c>occurrence=dotted address</c>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>The rows ordered by packet id and occurrence.</returns>
    private static List<string> _IpSrcRows(DuckDBConnection connection)
    {
        List<string> rows = [];
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT packet_id, occurrence, value FROM field_{_FieldId("ip.src")} ORDER BY packet_id, occurrence");
        using DuckDBDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            // An IPv4 address is a UINTEGER: the address as a number, whose four bytes (most significant first) are the dotted form.
            uint address = (uint)reader.GetValue(2);
            rows.Add(Invariant($"{reader.GetInt32(1)}={ReaderRules.IPv4Text(address)}"));
        }

        return rows;
    }

    #endregion

    #region Layout of the shapes

    /// <summary>
    /// A selected-fields export fed without a field tree writes one occurrence-keyed table for every selected field (also one no
    /// packet had), the shape marker and the field list in the export info, a full catalog, and no topology.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task SelectedFieldsWriteOccurrenceTablesTheShapeMarkerAndNoTopology()
    {
        using TestDir dir = new("duckdb_shape_selected");
        string path = dir.FilePath("capture.duckdb");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"), new FieldRequest("dns.qry.name"));

        using (DuckDbExporter exporter = _Build(path, content))
        {
            List<bool> hadTree = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();

            // The pipeline built no field tree for this exporter: it demanded none.
            await Assert.That(hadTree.Contains(true)).IsFalse();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        Dictionary<string, string> info = _ExportInfo(connection);

        await Assert.That(info["shape"]).IsEqualTo("selected_fields");
        await Assert.That(info["fields"]).IsEqualTo("ip.src;udp.dstport;dns.qry.name");
        await Assert.That(info["occurrences"]).IsEqualTo("ip.src=all;udp.dstport=all;dns.qry.name=all");
        await Assert.That(info["layout_version"]).IsEqualTo("1");
        await Assert.That(_TableExists(connection, "topology")).IsFalse();
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM packets")).IsEqualTo(3L);

        // Every selected field has its table with the occurrence column, also the one no packet had; the catalog lists all three.
        await Assert.That(_Columns(connection, Invariant($"field_{_FieldId("ip.src")}"))).IsEqualTo("packet_id,occurrence,value,custom_repr,custom_text");
        await Assert.That(DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM field_{_FieldId("dns.qry.name")}"))).IsEqualTo(0L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog")).IsEqualTo(3L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL")).IsEqualTo(0L);
    }

    /// <summary>
    /// The all-fields shape writes a table with the occurrence column for every cataloged value field, marks the export as
    /// <c>all_fields</c>, and writes no topology.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AllFieldsWriteEveryValueFieldAndNoTopology()
    {
        using TestDir dir = new("duckdb_shape_all");
        string path = dir.FilePath("capture.duckdb");

        using (DuckDbExporter exporter = _Build(path, new ExportContent { Shape = ExportShape.AllFields }))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(_ExportInfo(connection)["shape"]).IsEqualTo("all_fields");
        await Assert.That(_TableExists(connection, "topology")).IsFalse();
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog")).IsGreaterThan(10L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL")).IsEqualTo(0L);
        await Assert.That(_Columns(connection, Invariant($"field_{_FieldId("udp.dstport")}"))).IsEqualTo("packet_id,occurrence,value,custom_repr,custom_text");
    }

    /// <summary>
    /// The hierarchy shape asks the pipeline for the field tree, keys the field tables by node id, writes the topology, and marks
    /// the export as <c>hierarchy</c>.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task HierarchyKeepsNodeIdsAndTheTopologyAndIsMarkedAsHierarchy()
    {
        using TestDir dir = new("duckdb_shape_hierarchy");
        string path = dir.FilePath("capture.duckdb");

        using (DuckDbExporter exporter = _Build(path, ExportContents.Full))
        {
            // The hierarchy demands the tree, so the plan builds one.
            List<bool> hadTree = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();

            await Assert.That(hadTree.TrueForAll(static tree => tree)).IsTrue();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(_ExportInfo(connection)["shape"]).IsEqualTo("hierarchy");
        await Assert.That(_ExportInfo(connection)["layout_version"]).IsEqualTo("1");
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM topology")).IsGreaterThan(0L);
        await Assert.That(_Columns(connection, Invariant($"field_{_FieldId("udp.dstport")}"))).IsEqualTo("packet_id,node_id,value,custom_repr,custom_text");
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL")).IsGreaterThan(0L);
    }

    #endregion

    #region Occurrences

    /// <summary>The tunnel packet exports the occurrences of <c>ip.src</c> that the choice names: the first, the last, or both.</summary>
    /// <param name="choice">The occurrence choice of the field request.</param>
    /// <param name="expected">The expected rows of the form <c>occurrence=address</c>.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(Occurrences.First, new[] { "0=10.0.0.1" })]
    [Arguments(Occurrences.Last, new[] { "0=192.168.0.1" })]
    [Arguments(Occurrences.All, new[] { "0=10.0.0.1", "1=192.168.0.1" })]
    public async Task TheTunnelPacketExportsTheChosenOccurrencesOfTheSourceAddress(Occurrences choice, string[] expected)
    {
        using TestDir dir = new("duckdb_shape_occurrence");
        string path = dir.FilePath("capture.duckdb");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src", choice));

        using (DuckDbExporter exporter = _Build(path, content))
        {
            _ = TreeFreeFeed.Run(exporter, [PacketGenerators.CreateTunnelFrame(0)]);
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(_IpSrcRows(connection)).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A packet that holds the field once gives one row in every occurrence mode, and a packet without the field gives none while
    /// it is counted in the packets table.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APlainPacketGivesOneRowInEveryModeAndAPacketWithoutTheFieldGivesNone()
    {
        foreach (Occurrences choice in new[] { Occurrences.First, Occurrences.Last, Occurrences.All })
        {
            using TestDir dir = new("duckdb_shape_occurrence_plain");
            string path = dir.FilePath("capture.duckdb");

            using (DuckDbExporter exporter = _Build(path, ExportContents.Selected(new FieldRequest("ip.src", choice))))
            {
                _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1], PacketGenerators.CreateCanFrame(2)]);
                exporter.OnFinish();
            }

            using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
            connection.Open();

            await Assert.That(_IpSrcRows(connection).Count).IsEqualTo(1);
            await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM packets")).IsEqualTo(2L);
        }
    }

    /// <summary>
    /// Ordinary tree packets that bypass the exporter's parse demand give the same <c>ip.src</c> rows and the same <c>udp.dstport</c>
    /// values as the tree-free feed.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ATreePacketTheObserverDidNotSeeWritesTheSameRowsAsTheTreeFreeFeed()
    {
        using TestDir treeFree = new("duckdb_shape_feed_free");
        using TestDir withTree = new("duckdb_shape_feed_tree");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"));

        using (DuckDbExporter exporter = _Build(treeFree.FilePath("a.duckdb"), content))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        // The library user ignores the parse demand and passes ordinary tree packets: the exporter walks them through the
        // same collector rules.
        using (DuckDbExporter exporter = _Build(withTree.FilePath("b.duckdb"), content))
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                Packet packet = TestHarness.ParseFrame(frame);
                await Assert.That(packet.HasFieldTree).IsTrue();
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        using DuckDBConnection a = new(Invariant($"Data Source={treeFree.FilePath("a.duckdb")}"));
        using DuckDBConnection b = new(Invariant($"Data Source={withTree.FilePath("b.duckdb")}"));
        a.Open();
        b.Open();

        await Assert.That(_IpSrcRows(b)).IsEquivalentTo(_IpSrcRows(a));
        string sumSql = Invariant($"SELECT CAST(SUM(value) AS BIGINT) FROM field_{_FieldId("udp.dstport")}");
        await Assert.That(DuckDbReadBack.Scalar(b, sumSql)).IsEqualTo(DuckDbReadBack.Scalar(a, sumSql));
    }

    #endregion

    #region Commit rule

    /// <summary>A packet whose fields were staged but that the filter never offered leaves no rows, and the next packet is written normally.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketTheFilterRejectedLeavesNoRowsAndTheNextPacketIsUnaffected()
    {
        using TestDir dir = new("duckdb_shape_commit_reject");
        string path = dir.FilePath("capture.duckdb");
        ExportContent content = ExportContents.Selected(new FieldRequest("udp.dstport"));
        List<Frame> frames = [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]];

        using (DuckDbExporter exporter = _Build(path, content))
        {
            int index = 0;

            // The second packet is parsed (its fields are staged) but never offered, as if the filter said no.
            _ = TreeFreeFeed.Run(exporter, frames, offer: _ => index++ != 1);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(2);
            await Assert.That(exporter.HasErrors).IsFalse();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM packets")).IsEqualTo(2L);
        await Assert.That(DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM field_{_FieldId("udp.dstport")}"))).IsEqualTo(2L);
    }

    /// <summary>Fields staged for a packet that was never offered are discarded by <c>OnFinish</c>, and no database file is created.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AStagingOpenAtTheEndOfTheExportIsDiscardedByOnFinish()
    {
        using TestDir dir = new("duckdb_shape_commit_finish");
        string path = dir.FilePath("capture.duckdb");
        ExportContent content = ExportContents.Selected(new FieldRequest("udp.dstport"));

        using (DuckDbExporter exporter = _Build(path, content))
        {
            // The only packet is parsed and not offered: its staged fields must not reach the file.
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]], offer: static _ => false);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(0);
        }

        // No packet ever reached the exporter, so no database was created.
        await Assert.That(File.Exists(path)).IsFalse();
    }

    /// <summary>
    /// A packet whose observer was muted by a failure is skipped with a serialization error that says the fields were not received
    /// completely, nothing of it is written, and the other packets are.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketParsedWithAMutedObserverIsSkippedWithAnErrorAndNothingOfItIsWritten()
    {
        using TestDir dir = new("duckdb_shape_commit_muted");
        string path = dir.FilePath("capture.duckdb");
        ExportContent content = ExportContents.Selected(new FieldRequest("udp.dstport"));
        List<ExportErrorEventArgs> errors = [];

        using (DuckDbExporter exporter = _Build(path, content))
        {
            exporter.ItemSkipped += (_, error) => errors.Add(error);
            ParseDemand demand = exporter.ParseDemand;
            ParseObserver wrapped = new()
            {
                Observer = new FailingOnceObserver(demand.Observer.Observer!, packetOrdinal: 1),
                Watch = demand.Observer.Watch,
                DisplayText = demand.Observer.DisplayText,
            };
            ParseOptions options = TreeFreeFeed.CreateOptions(ParseDemand.Fields(wrapped));

            // Packet ordinal 1 makes the wrapper throw: the parse mutes it and ends the packet incomplete.
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]], in options);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(2);
            await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        }

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(errors[0].Message).Contains("not received completely");

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(DuckDbReadBack.Scalar(connection, Invariant($"SELECT COUNT(*) FROM field_{_FieldId("udp.dstport")}"))).IsEqualTo(2L);
    }

    /// <summary>
    /// A packet parsed with neither the observer nor a field tree is skipped with a serialization error whose message names both
    /// missing sources.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketParsedWithoutTheObserverAndWithoutATreeIsSkippedWithAMessageThatNamesTheCause()
    {
        using TestDir dir = new("duckdb_shape_no_source");
        string path = dir.FilePath("capture.duckdb");
        List<ExportErrorEventArgs> errors = [];

        using (DuckDbExporter exporter = _Build(path, ExportContents.Selected(new FieldRequest("udp.dstport"))))
        {
            exporter.ItemSkipped += (_, error) => errors.Add(error);
            ParseOptions noObserver = new(FieldTreeMode.Skip);

            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]], in noObserver);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(0);
        }

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(errors[0].Message).Contains("neither a field tree nor staged fields");
    }

    /// <summary>
    /// The hierarchy shape cannot write a packet parsed without a field tree: it is skipped and counted, with the error kind and
    /// message that all columnar exporters use, no exception reaches the caller, and no row of the packet is written.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AHierarchyPacketWithoutAFieldTreeIsSkippedAsASerializationError()
    {
        using TestDir dir = new("duckdb_shape_no_tree");
        string path = dir.FilePath("capture.duckdb");
        List<ExportErrorEventArgs> errors = [];
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in noTree);
        bool accepted;

        using (DuckDbExporter exporter = _Build(path, ExportContents.Hierarchy()))
        {
            exporter.ItemSkipped += (_, error) => errors.Add(error);

            accepted = exporter.OnPacket(packet);
            exporter.OnFinish();

            await Assert.That(exporter.SkippedCount).IsEqualTo(1);
            await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(accepted).IsTrue(); // tolerant mode: the packet is skipped and the export goes on
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(errors[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM packets")).IsEqualTo(0L);
    }

    /// <summary>Strict mode ends the export at a hierarchy packet without a field tree, without an event and without a row.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AHierarchyPacketWithoutAFieldTreeEndsAStrictExport()
    {
        using TestDir dir = new("duckdb_shape_no_tree_strict");
        string path = dir.FilePath("capture.duckdb");
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in noTree);

        using DuckDbExporter exporter = _Build(path, ExportContents.Hierarchy());
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnPacket(packet);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    /// <summary>
    /// A packet offered while the staged fields belong to another packet is skipped with an error that names the staged packet, so
    /// it is never written with the fields of the other one.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnOfferedPacketWhileTheFieldsOfAnotherPacketAreStagedIsRefusedAndNothingIsWritten()
    {
        using TestDir dir = new("duckdb_shape_commit_mismatch");
        string path = dir.FilePath("capture.duckdb");
        List<ExportErrorEventArgs> errors = [];

        using (DuckDbExporter exporter = _Build(path, ExportContents.Selected(new FieldRequest("udp.dstport"))))
        {
            exporter.ItemSkipped += (_, error) => errors.Add(error);
            ParseOptions withObserver = TreeFreeFeed.CreateOptions(exporter.ParseDemand);
            ParseOptions withoutObserver = new(FieldTreeMode.Skip);

            // Packet A is parsed with the observer and not offered; packet B is parsed without it and offered: the staging
            // belongs to A, so B must not be written with A's fields.
            Packet staged = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in withObserver);
            Packet offered = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in withoutObserver);
            bool accepted = exporter.OnPacket(offered);
            exporter.OnFinish();

            await Assert.That(accepted).IsTrue(); // tolerant mode: the packet is skipped and the export goes on
            await Assert.That(exporter.PacketCount).IsEqualTo(0);
            await Assert.That(errors.Count).IsEqualTo(1);
            await Assert.That(errors[0].Message).Contains("while fields of packet " + staged.Id.Value.ToString(CultureInfo.InvariantCulture) + " are staged");
        }
    }

    /// <summary>
    /// A packet from a different stack than the one the field selection was resolved against is refused with a stack mismatch
    /// error before any file is created.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketOfAnotherStackThanTheSelectionIsRefusedBeforeAnythingIsCreated()
    {
        using TestDir dir = new("duckdb_shape_stack");
        string path = dir.FilePath("capture.duckdb");
        List<ExportErrorEventArgs> errors = [];

        // The selection is resolved against a stack of its own; the packet comes from the harness stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        using Stack otherStack = builder.Build();
        if (!FieldSelection.TryCreate(
            otherStack, [new FieldRequest("udp.dstport")], Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        ExportContent content = new() { Shape = ExportShape.SelectedFields, Fields = selection };

        using DuckDbExporter exporter = _Build(path, content);
        exporter.ItemSkipped += (_, error) => errors.Add(error);

        bool accepted = exporter.OnPacket(TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1]));

        await Assert.That(accepted).IsFalse();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    #endregion

    #region Observer that fails once

    /// <summary>Delegates to the exporter's observer and throws at the first field of one packet, which mutes it for that packet.</summary>
    /// <param name="inner">The exporter's observer that receives every call that does not fail.</param>
    /// <param name="packetOrdinal">The zero-based position of the packet, counted from the first <c>BeginPacket</c>, whose first field fails.</param>
    private sealed class FailingOnceObserver(IFieldObserver inner, int packetOrdinal) : IFieldObserver
    {
        /// <summary>The position of the current packet; -1 before the first packet begins.</summary>
        private int _Ordinal = -1;

        /// <summary>Whether the current packet already failed, so that only its first field throws.</summary>
        private bool _Thrown;

        /// <summary>Counts the packet, re-arms the failure for it, and forwards the call.</summary>
        /// <param name="id">The id of the packet that begins.</param>
        /// <param name="timestamp">The capture time of the packet.</param>
        public void BeginPacket(PacketId id, Timestamp timestamp)
        {
            _Ordinal++;
            _Thrown = false;
            inner.BeginPacket(id, timestamp);
        }

        /// <summary>Throws at the first field of the packet at the failing position and forwards every other field.</summary>
        /// <param name="visit">The visited field.</param>
        /// <exception cref="InvalidOperationException">The field is the first one of the packet at the failing position.</exception>
        public void OnField(in FieldVisit visit)
        {
            if (_Ordinal == packetOrdinal && !_Thrown)
            {
                _Thrown = true;
                throw new InvalidOperationException("The observer fails on purpose.");
            }

            inner.OnField(in visit);
        }

        /// <summary>Forwards the end of the packet.</summary>
        /// <param name="id">The id of the packet that ends.</param>
        /// <param name="complete">Whether the packet's fields were all delivered.</param>
        public void EndPacket(PacketId id, bool complete) => inner.EndPacket(id, complete);
    }

    #endregion
}
