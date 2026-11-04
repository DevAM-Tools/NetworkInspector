// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// The three export shapes of the Parquet exporter, fed by a tree-free parse with the exporter's own observer: the layout of each
/// shape (position column, shape marker, topology, catalog), the occurrence choice on the tunnel packet, and the rule that a
/// selected field no packet had has no file ("no file = never present").
/// </summary>
[Category("Slow")]
internal sealed class ParquetExportShapeTests
{
    #region Building and reading back datasets

    /// <summary>Builds a synchronous Parquet exporter for the content.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="content">What the export contains, including its shape.</param>
    /// <returns>The exporter; the caller disposes it.</returns>
    private static ParquetExporter _Build(string directory, ExportContent content) =>
        ParquetExporter.CreateBuilder().ToDirectory(directory).WithContent(content).WithWriteMode(ExportWriteMode.Synchronous).Build();

    /// <summary>Looks up the id of a field in the test stack.</summary>
    /// <param name="name">The field name, for example <c>ip.src</c>.</param>
    /// <returns>The field id, which names the field's file.</returns>
    private static int _FieldId(string name) => TestHarness.GetStack().GetFieldId(name)!.Value.Value;

    /// <summary>The path of the Parquet file of a field.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="name">The field name, for example <c>ip.src</c>.</param>
    /// <returns>The file path, which may not exist when no packet had the field.</returns>
    private static string _FieldFile(string directory, string name) =>
        Path.Combine(directory, "fields", Invariant($"field_{_FieldId(name)}.parquet"));

    /// <summary>Reads the column names of a Parquet file.</summary>
    /// <param name="path">The Parquet file.</param>
    /// <returns>The data column names in schema order.</returns>
    private static async Task<string[]> _ColumnNames(string path)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        return reader.Schema.GetDataFields().Select(static field => field.Name).ToArray();
    }

    /// <summary>Reads a text column of a Parquet file over all row groups.</summary>
    /// <param name="path">The Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    private static async Task<string[]> _ReadStrings(string path, string column)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField field = reader.Schema.FindDataField(column);
        List<string> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            string[] buffer = new string[rowGroup.RowCount];
            await rowGroup.ReadAsync(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }

    /// <summary>Reads a 32-bit integer column of a Parquet file over all row groups.</summary>
    /// <param name="path">The Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    private static async Task<int[]> _ReadInts(string path, string column)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField field = reader.Schema.FindDataField(column);
        List<int> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            int[] buffer = new int[rowGroup.RowCount];
            await rowGroup.ReadAsync<int>(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }

    /// <summary>Reads the key and value columns of <c>export_info.parquet</c>.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The export info rows, key to value.</returns>
    private static async Task<Dictionary<string, string>> _ExportInfo(string directory)
    {
        string path = Path.Combine(directory, "export_info.parquet");
        string[] keys = await _ReadStrings(path, "key");
        string[] values = await _ReadStrings(path, "value");
        Dictionary<string, string> rows = [];
        for (int i = 0; i < keys.Length; i++)
        {
            rows[keys[i]] = values[i];
        }

        return rows;
    }

    /// <summary>Reads the file of <c>ip.src</c> as rows of the form <c>occurrence=dotted address</c>.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The rows in file order.</returns>
    private static async Task<List<string>> _IpSrcRows(string directory)
    {
        string path = _FieldFile(directory, "ip.src");
        int[] occurrences = await _ReadInts(path, "occurrence");

        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField value = reader.Schema.FindDataField("value");
        // An IPv4 address is a UINT32 column: the address as a number, whose four bytes (most significant first) are the dotted form.
        List<uint> addresses = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            uint[] buffer = new uint[rowGroup.RowCount];
            await rowGroup.ReadAsync<uint>(value, buffer, null, CancellationToken.None);
            addresses.AddRange(buffer);
        }

        List<string> rows = [];
        for (int i = 0; i < occurrences.Length; i++)
        {
            uint address = addresses[i];
            rows.Add(Invariant($"{occurrences[i]}={ReaderRules.IPv4Text(address)}"));
        }

        return rows;
    }

    #endregion

    #region Layout of the shapes

    /// <summary>
    /// A selected-fields export fed without a field tree writes one occurrence-keyed file per field that a packet had, the shape
    /// marker, the field list and occurrence choice in the export info, and no topology; a selected field no packet had has no file.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task SelectedFieldsWriteOccurrenceFilesTheShapeMarkerAndNoTopology()
    {
        using TestDir dir = new("parquet_shape_selected");
        string directory = Path.Combine(dir.DirectoryPath, "dataset");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"), new FieldRequest("dns.qry.name"));

        using (ParquetExporter exporter = _Build(directory, content))
        {
            List<bool> hadTree = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();

            await Assert.That(hadTree.Contains(true)).IsFalse();
        }

        Dictionary<string, string> info = await _ExportInfo(directory);

        await Assert.That(info["shape"]).IsEqualTo("selected_fields");
        await Assert.That(info["fields"]).IsEqualTo("ip.src;udp.dstport;dns.qry.name");
        await Assert.That(info["occurrences"]).IsEqualTo("ip.src=all;udp.dstport=all;dns.qry.name=all");
        await Assert.That(info["layout_version"]).IsEqualTo("1");
        await Assert.That(File.Exists(Path.Combine(directory, "topology.parquet"))).IsFalse();

        string[] columns = await _ColumnNames(_FieldFile(directory, "ip.src"));
        await Assert.That(string.Join(',', columns)).IsEqualTo("packet_id,occurrence,value,custom_repr,custom_text");

        // The catalog lists all three selected fields; the one no packet had has no file (documented: an empty Parquet file
        // would need a row group to be useful to readers).
        string[] catalogNames = await _ReadStrings(Path.Combine(directory, "catalog.parquet"), "name");
        await Assert.That(catalogNames.Length).IsEqualTo(3);
        await Assert.That(File.Exists(_FieldFile(directory, "dns.qry.name"))).IsFalse();
        await Assert.That(File.Exists(_FieldFile(directory, "udp.dstport"))).IsTrue();
    }

    /// <summary>
    /// Ordinary tree packets that bypass the exporter's parse demand give the same <c>ip.src</c> rows and the same
    /// <c>udp.dstport</c> occurrences as the tree-free feed.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ATreePacketTheObserverDidNotSeeWritesTheSameRowsAsTheTreeFreeFeed()
    {
        using TestDir dir = new("parquet_shape_feed");
        string treeFree = Path.Combine(dir.DirectoryPath, "free");
        string withTree = Path.Combine(dir.DirectoryPath, "tree");
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"));

        using (ParquetExporter exporter = _Build(treeFree, content))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        // A library user who ignores the parse demand passes ordinary tree packets: the exporter reads the selected fields
        // straight from the tree and must write what the tree-free feed wrote.
        using (ParquetExporter exporter = _Build(withTree, content))
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                Packet packet = TestHarness.ParseFrame(frame);
                await Assert.That(packet.HasFieldTree).IsTrue();
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        await Assert.That(await _IpSrcRows(withTree)).IsEquivalentTo(await _IpSrcRows(treeFree));

        // The packet ids are not compared: the tree-free feed numbers its packets itself, the parse helper numbers them by frame.
        await Assert.That(await _ReadInts(_FieldFile(withTree, "udp.dstport"), "occurrence"))
            .IsEquivalentTo(await _ReadInts(_FieldFile(treeFree, "udp.dstport"), "occurrence"));
    }

    /// <summary>
    /// The all-fields shape writes one file per cataloged value field, marks the export as <c>all_fields</c>, and writes no topology.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AllFieldsWriteEveryValueFieldAndNoTopology()
    {
        using TestDir dir = new("parquet_shape_all");
        string directory = Path.Combine(dir.DirectoryPath, "dataset");

        using (ParquetExporter exporter = _Build(directory, new ExportContent { Shape = ExportShape.AllFields }))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        string[] catalogNames = await _ReadStrings(Path.Combine(directory, "catalog.parquet"), "name");

        await Assert.That((await _ExportInfo(directory))["shape"]).IsEqualTo("all_fields");
        await Assert.That(File.Exists(Path.Combine(directory, "topology.parquet"))).IsFalse();
        await Assert.That(catalogNames.Length).IsGreaterThan(10);
        await Assert.That(Directory.GetFiles(Path.Combine(directory, "fields")).Length).IsEqualTo(catalogNames.Length);
    }

    /// <summary>
    /// The hierarchy shape keys the field rows by node id, writes the topology file, and marks the export as <c>hierarchy</c>.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task HierarchyKeepsNodeIdsAndTheTopologyAndIsMarkedAsHierarchy()
    {
        using TestDir dir = new("parquet_shape_hierarchy");
        string directory = Path.Combine(dir.DirectoryPath, "dataset");

        using (ParquetExporter exporter = _Build(directory, ExportContents.Full))
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        string[] columns = await _ColumnNames(_FieldFile(directory, "udp.dstport"));

        await Assert.That((await _ExportInfo(directory))["shape"]).IsEqualTo("hierarchy");
        await Assert.That(File.Exists(Path.Combine(directory, "topology.parquet"))).IsTrue();
        await Assert.That(string.Join(',', columns)).IsEqualTo("packet_id,node_id,value,custom_repr,custom_text");
    }

    #endregion

    #region Occurrence choice

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
        using TestDir dir = new("parquet_shape_occurrence");
        string directory = Path.Combine(dir.DirectoryPath, "dataset");

        using (ParquetExporter exporter = _Build(directory, ExportContents.Selected(new FieldRequest("ip.src", choice))))
        {
            _ = TreeFreeFeed.Run(exporter, [PacketGenerators.CreateTunnelFrame(0)]);
            exporter.OnFinish();
        }

        await Assert.That(await _IpSrcRows(directory)).IsEquivalentTo(expected);
    }

    #endregion

    #region Commit rule

    /// <summary>A packet that the filter rejected leaves no row in the packets file and no row in the field file.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketTheFilterRejectedLeavesNoRows()
    {
        using TestDir dir = new("parquet_shape_commit");
        string directory = Path.Combine(dir.DirectoryPath, "dataset");
        List<Frame> frames = [ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1], ShapeFrames.TunnelUdpCan()[1]];

        using (ParquetExporter exporter = _Build(directory, ExportContents.Selected(new FieldRequest("udp.dstport"))))
        {
            int index = 0;
            _ = TreeFreeFeed.Run(exporter, frames, offer: _ => index++ != 1);
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(2);
        }

        int[] packetIds = await _ReadInts(Path.Combine(directory, "packets.parquet"), "packet_id");
        int[] fieldPacketIds = await _ReadInts(_FieldFile(directory, "udp.dstport"), "packet_id");

        await Assert.That(packetIds.Length).IsEqualTo(2);
        await Assert.That(fieldPacketIds.Length).IsEqualTo(2);
    }

    #endregion

    #region Packets without a field tree

    /// <summary>
    /// The hierarchy shape cannot write a packet parsed without a field tree: it is skipped and counted, with the error kind and
    /// message that all columnar exporters use, and no exception reaches the caller.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AHierarchyPacketWithoutAFieldTreeIsSkippedAsASerializationError()
    {
        using TestDir dir = new("parquet_shape_no_tree");
        using ParquetExporter flat = _Build(Path.Combine(dir.DirectoryPath, "flat"), ExportContents.AllFields());
        ParseOptions options = TreeFreeFeed.CreateOptions(flat.ParseDemand);
        Packet packet = TestHarness.ParseFrame(ShapeFrames.TunnelUdpCan()[1], in options);
        using ParquetExporter exporter = _Build(Path.Combine(dir.DirectoryPath, "dataset"), ExportContents.Hierarchy());
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
    }

    #endregion
}
