// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The display line of a node without a value (a group or layer container) is kept by every hierarchy export, so a reader that
/// follows the rule of the exporter guide (a custom text is the whole line, else the UI name) rebuilds the same line as the Text
/// export: DuckDB and Parquet store it as the nullable column <c>topology.custom_text</c>, PBF Columnar as the presence-encoded text
/// column of the topology message. The three stored lists must be identical, and every stored text must be the Text line at the
/// node's position. With the text switched off no format writes the column.
/// </summary>
/// <remarks>
/// Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (Limits that the reader must know) and the Parquet and
/// PBF READMEs.
/// </remarks>
internal sealed class LayerTextTests
{
    #region Packets and export writers

    /// <summary>The packets of the tests: real dissector output and the probe packet.</summary>
    public enum LayerPacket
    {
        /// <summary>An Ethernet, IPv4, UDP packet.</summary>
        Udp,

        /// <summary>An Ethernet, IPv4, TCP SYN packet (groups with texts such as the flags).</summary>
        TcpSyn,

        /// <summary>The probe packet: a layer container with a text and a group container without one.</summary>
        Probe,
    }

    /// <summary>The content of the hierarchy exports of this class: no packet-level members, the texts as asked.</summary>
    /// <param name="customText">Whether custom texts are written.</param>
    /// <returns>The content.</returns>
    private static ExportContent _Content(bool customText) =>
        new() { Shape = ExportShape.Hierarchy, Info = false, FrameBytes = false, CustomText = customText };

    /// <summary>Parses the packet of a scenario; the probe factory (when used) must outlive the packet.</summary>
    /// <param name="which">The scenario.</param>
    /// <param name="probe">The probe factory that owns the stack of the probe packet.</param>
    /// <returns>A parsed tree packet.</returns>
    private static Packet _Packet(LayerPacket which, TextProbePacketFactory probe) =>
        which switch
        {
            LayerPacket.Udp => PacketGenerators.CreateEthernetUdpPackets(1)[0],
            LayerPacket.TcpSyn => PacketGenerators.CreateParsedPacket(0, FrameGenerators.BuildEthernetIpv4TcpSynFrame()),
            _ => probe.CreateTreePacket(),
        };

    /// <summary>Writes the packet as a DuckDB export (synchronous).</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="dir">The directory of the output.</param>
    /// <param name="customText">Whether custom texts are written.</param>
    /// <returns>The path of the finished file.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _ExportDuckDb(Packet packet, TestDir dir, bool customText)
    {
        string path = dir.FilePath(Invariant($"layers_{customText}.duckdb"));
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path).WithContent(_Content(customText))
            .WithWriteMode(ExportWriteMode.Synchronous).Build();
        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();
        return accepted
            ? path
            : throw new InvalidOperationException("The DuckDB exporter refused the packet.");
    }

    /// <summary>Writes the packet as a Parquet dataset (synchronous).</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="dir">The directory of the output.</param>
    /// <param name="customText">Whether custom texts are written.</param>
    /// <returns>The path of the finished dataset directory.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _ExportParquet(Packet packet, TestDir dir, bool customText)
    {
        string path = dir.FilePath(Invariant($"layers_{customText}_parquet"));
        using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToDirectory(path).WithContent(_Content(customText))
            .WithWriteMode(ExportWriteMode.Synchronous).Build();
        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();
        return accepted
            ? path
            : throw new InvalidOperationException("The Parquet exporter refused the packet.");
    }

    /// <summary>Writes the packet as an uncompressed columnar PBF file (synchronous).</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="dir">The directory of the output.</param>
    /// <param name="customText">Whether custom texts are written.</param>
    /// <returns>The path of the finished file.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _ExportPbfColumnar(Packet packet, TestDir dir, bool customText)
    {
        string path = dir.FilePath(Invariant($"layers_{customText}.pbf"));
        using PbfExporter exporter = PbfExporter.CreateBuilder().ToFile(path).WithFormat(PbfExportFormat.Columnar).WithCompressed(false)
            .WithContent(_Content(customText)).WithWriteMode(ExportWriteMode.Synchronous).Build();
        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();
        return accepted
            ? path
            : throw new InvalidOperationException("The PBF exporter refused the packet.");
    }

    /// <summary>
    /// The tree lines of the Text export of the packet at the Full level without a length limit: the lines between the header and the
    /// blank separator line, one per node in pre-order.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>The lines.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string[] _TextLines(Packet packet)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithDetailLevel(TextDetailLevel.Full).WithMaxTextLength(0).Build())
        {
            bool accepted = exporter.OnPacket(packet);
            exporter.OnFinish();
            if (!accepted)
            {
                throw new InvalidOperationException("The Text exporter refused the packet.");
            }
        }

        string[] all = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        int end = Array.IndexOf(all, string.Empty, 1);
        return all[1..end];
    }

    /// <summary>The rows whose text is stored, each with the Text line at the node's position.</summary>
    /// <param name="rows">The topology rows of one packet in node order.</param>
    /// <param name="lines">The Text lines of the same packet, one per node.</param>
    /// <returns>One pair per stored text: the text and the Text line without its indentation.</returns>
    private static List<(string Stored, string Line)> _StoredTextsWithLines(List<TextPresenceReaders.TopologyRow> rows, string[] lines)
    {
        List<(string Stored, string Line)> pairs = [];
        foreach (TextPresenceReaders.TopologyRow row in rows)
        {
            if (row.Text is not null)
            {
                pairs.Add((row.Text, lines[row.NodeId].TrimStart(' ')));
            }
        }

        return pairs;
    }

    #endregion

    #region PBF Columnar

    /// <summary>Proves that PBF Columnar stores, for every node without a value, exactly the line the Text export shows at that node.</summary>
    /// <param name="which">The packet under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(LayerPacket.Udp)]
    [Arguments(LayerPacket.TcpSyn)]
    [Arguments(LayerPacket.Probe)]
    public async Task PbfColumnarStoresTheLineOfEveryNodeWithoutAValueAsTheTextExportShowsIt(LayerPacket which)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_pbf");
        Packet packet = _Packet(which, probe);

        List<TextPresenceReaders.TopologyRow> rows = TextPresenceReaders.ReadPbfColumnarTopology(_ExportPbfColumnar(packet, dir, customText: true));
        string[] lines = _TextLines(packet);
        List<(string Stored, string Line)> pairs = _StoredTextsWithLines(rows, lines);

        await Assert.That(rows.Count).IsEqualTo(lines.Length);
        await Assert.That(pairs.Count).IsGreaterThan(0);
        foreach ((string stored, string line) in pairs)
        {
            await Assert.That(stored).IsEqualTo(line);
        }
    }

    /// <summary>Proves that PBF Columnar keeps the text of the probe's layer container and leaves the group container and the value node absent.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PbfColumnarKeepsTheProbeLayerTextAndLeavesTheGroupAndTheValueNodesAbsent()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_pbf_probe");

        List<TextPresenceReaders.TopologyRow> rows = TextPresenceReaders.ReadPbfColumnarTopology(
            _ExportPbfColumnar(probe.CreateTreePacket(), dir, customText: true));

        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp").Value).Text).IsEqualTo(TextProbePacketFactory.LayerText);
        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp.group").Value).Text).IsNull();
        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp.text").Value).Text).IsNull();
    }

    /// <summary>Proves that PBF Columnar writes no custom-text column in the topology message when the text is switched off.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PbfColumnarWritesNoTopologyTextColumnWhenTheTextIsSwitchedOff()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_pbf_off");

        string path = _ExportPbfColumnar(probe.CreateTreePacket(), dir, customText: false);
        (_, List<List<PbfField>> blocks) = PbfFileScanner.Read(path);
        List<PbfField> topology = PbfFileScanner.Fields(blocks.SelectMany(static block => block)
            .First(static field => field.Number == TextPresenceReaders.PbfNumbers.ColumnarTopology).Payload!);

        await Assert.That(topology.Any(static field => field.Number == TextPresenceReaders.PbfNumbers.TopologyCustomTexts)).IsFalse();
        await Assert.That(TextPresenceReaders.ReadPbfColumnarTopology(path).All(static row => row.Text is null)).IsTrue();
    }

    #endregion

    #region Parquet and DuckDB

    /// <summary>Proves that Parquet stores, for every node without a value, exactly the line the Text export shows at that node.</summary>
    /// <param name="which">The packet under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(LayerPacket.Udp)]
    [Arguments(LayerPacket.TcpSyn)]
    [Arguments(LayerPacket.Probe)]
    public async Task ParquetStoresTheLineOfEveryNodeWithoutAValueAsTheTextExportShowsIt(LayerPacket which)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_parquet");
        Packet packet = _Packet(which, probe);

        List<TextPresenceReaders.TopologyRow> rows = TextPresenceReaders.ReadParquetTopology(_ExportParquet(packet, dir, customText: true));
        string[] lines = _TextLines(packet);
        List<(string Stored, string Line)> pairs = _StoredTextsWithLines(rows, lines);

        await Assert.That(rows.Count).IsEqualTo(lines.Length);
        await Assert.That(pairs.Count).IsGreaterThan(0);
        foreach ((string stored, string line) in pairs)
        {
            await Assert.That(stored).IsEqualTo(line);
        }
    }

    /// <summary>Proves that DuckDB, Parquet, and PBF Columnar store identical topology row lists, including the texts.</summary>
    /// <param name="which">The packet under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(LayerPacket.Udp)]
    [Arguments(LayerPacket.TcpSyn)]
    [Arguments(LayerPacket.Probe)]
    public async Task DuckDbParquetAndPbfColumnarStoreTheSameTopologyTexts(LayerPacket which)
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_same");
        Packet packet = _Packet(which, probe);

        List<TextPresenceReaders.TopologyRow> duckDb = TextPresenceReaders.ReadDuckDbTopology(_ExportDuckDb(packet, dir, customText: true));
        List<TextPresenceReaders.TopologyRow> parquet = TextPresenceReaders.ReadParquetTopology(_ExportParquet(packet, dir, customText: true));
        List<TextPresenceReaders.TopologyRow> pbf = TextPresenceReaders.ReadPbfColumnarTopology(_ExportPbfColumnar(packet, dir, customText: true));

        await Assert.That(duckDb.Count(static row => row.Text is not null)).IsGreaterThan(0);
        await Assert.That(parquet).IsEquivalentTo(duckDb, CollectionOrdering.Matching);
        await Assert.That(pbf).IsEquivalentTo(duckDb, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Proves that Parquet keeps the text of the probe's layer container and leaves the group container and the value node empty,
    /// and that it writes no custom-text column when the text is switched off.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task ParquetKeepsTheProbeLayerTextAndWritesNoColumnWhenTheTextIsSwitchedOff()
    {
        using TextProbePacketFactory probe = new();
        using TestDir dir = new("layer_text_parquet_probe");

        string on = _ExportParquet(probe.CreateTreePacket(), dir, customText: true);
        string off = _ExportParquet(probe.CreateTreePacket(), dir, customText: false);
        List<TextPresenceReaders.TopologyRow> rows = TextPresenceReaders.ReadParquetTopology(on);

        await Assert.That(TextPresenceReaders.ParquetTopologyHasColumn(on, "custom_text")).IsTrue();
        await Assert.That(TextPresenceReaders.ParquetTopologyHasColumn(off, "custom_text")).IsFalse();
        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp").Value).Text).IsEqualTo(TextProbePacketFactory.LayerText);
        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp.group").Value).Text).IsNull();
        await Assert.That(rows.First(row => row.FieldId == probe.IdOf("txp.text").Value).Text).IsNull();
    }

    #endregion
}
