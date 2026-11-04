// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>One exporter configuration of the buffer matrix: a format with a shape (and a JSON style or a CSV layout).</summary>
internal enum BufferScenario
{
    /// <summary>DuckDB, hierarchy shape (field tree).</summary>
    DuckDbHierarchy,
    /// <summary>DuckDB, selected fields (tree-free parse).</summary>
    DuckDbSelectedFields,
    /// <summary>Parquet dataset, hierarchy shape.</summary>
    ParquetHierarchy,
    /// <summary>Parquet dataset, selected fields.</summary>
    ParquetSelectedFields,
    /// <summary>PBF standard layout, hierarchy shape.</summary>
    PbfStandardHierarchy,
    /// <summary>PBF standard layout, selected fields.</summary>
    PbfStandardSelectedFields,
    /// <summary>PBF columnar layout, hierarchy shape.</summary>
    PbfColumnarHierarchy,
    /// <summary>PBF columnar layout, selected fields.</summary>
    PbfColumnarSelectedFields,
    /// <summary>JSON pretty style, field tree.</summary>
    JsonPrettyTree,
    /// <summary>JSON array style, field tree.</summary>
    JsonArrayTree,
    /// <summary>JSON compact style, field tree.</summary>
    JsonCompactTree,
    /// <summary>JSON selected fields (array style).</summary>
    JsonSelectedFields,
    /// <summary>JSON all fields (array style).</summary>
    JsonAllFields,
    /// <summary>CSV packet rows.</summary>
    CsvPacketRows,
    /// <summary>CSV selected fields, wide layout.</summary>
    CsvWide,
    /// <summary>Text, field tree at the default detail level.</summary>
    TextTree,
    /// <summary>Text, selected fields.</summary>
    TextSelectedFields,
    /// <summary>Text, all fields.</summary>
    TextAllFields,
}

/// <summary>The optional packet bytes an export of the matrix writes.</summary>
[Flags]
internal enum BufferMembers
{
    /// <summary>Neither: the exporter's default (a plain builder where the shape allows it).</summary>
    None = 0,

    /// <summary>The frame bytes.</summary>
    Frames = 1,

    /// <summary>The additional packet buffers.</summary>
    Buffers = 2,
}

/// <summary>
/// An export of one scenario that is open for packets: the exporter, its parse demand, and the way to finish it and get what it
/// wrote. Two of them can be fed the very same parsed packets, which the tests of "the field tree leads" need.
/// </summary>
/// <remarks>Thread safety: not thread-safe; one test owns it.</remarks>
internal sealed class OpenBufferExport
{
    /// <summary>The exporter the packets are offered to.</summary>
    private readonly IPacketListener _Listener;

    /// <summary>Finishes the exporter, releases it, and returns what it wrote.</summary>
    private readonly Func<(string Path, string Text)> _Finish;

    /// <summary>Wraps an exporter.</summary>
    /// <param name="listener">The exporter.</param>
    /// <param name="demand">The parse demand of the exporter.</param>
    /// <param name="finish">Finishes the exporter and returns what it wrote: a path (file or directory) or a text.</param>
    internal OpenBufferExport(IPacketListener listener, ParseDemand demand, Func<(string Path, string Text)> finish)
    {
        _Listener = listener;
        Demand = demand;
        _Finish = finish;
    }

    /// <summary>The parse demand of the exporter: the parse options of the run follow from it.</summary>
    internal ParseDemand Demand { get; }

    /// <summary>Offers a parsed packet to the exporter.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>What <see cref="IPacketListener.OnPacket"/> returned.</returns>
    internal bool Offer(Packet packet) => _Listener.OnPacket(packet);

    /// <summary>Finishes the exporter.</summary>
    /// <returns>The path of the file or directory a file format wrote, or the text a stream format wrote (the other is empty).</returns>
    internal (string Path, string Text) Finish() => _Finish();
}

/// <summary>
/// What a run of the matrix produced: the output of the exporter and what the packets themselves reported as their additional
/// buffers, so a reader's result is compared with the source of truth and not with a constant.
/// </summary>
/// <param name="Path">The file or directory a file format wrote; empty for a stream format.</param>
/// <param name="Text">The text a stream format wrote; empty for a file format.</param>
/// <param name="FirstPacketId">The id of the first packet that was offered.</param>
/// <param name="Expected">The additional buffers of every offered packet, in packet and index order.</param>
/// <param name="PacketCount">The number of packets that were offered; 0 for a run that was not fed by <see cref="BufferExportScenarios.Feed"/>.</param>
/// <param name="PacketsWithFieldTree">How many of the offered packets carried a field tree (<see cref="Packet.HasFieldTree"/>).</param>
internal sealed record BufferRun(
    string Path, string Text, int FirstPacketId, List<BufferRow> Expected, int PacketCount = 0, int PacketsWithFieldTree = 0);

/// <summary>What feeding a capture to the open exports observed about the packets.</summary>
/// <param name="FirstPacketId">The id of the first packet that was offered.</param>
/// <param name="Expected">The additional buffers of every offered packet, in packet and index order.</param>
/// <param name="PacketCount">The number of packets that were offered.</param>
/// <param name="PacketsWithFieldTree">How many of the offered packets carried a field tree (<see cref="Packet.HasFieldTree"/>).</param>
internal sealed record FeedResult(int FirstPacketId, List<BufferRow> Expected, int PacketCount, int PacketsWithFieldTree);

/// <summary>
/// Creates the exporters of the buffer matrix and feeds them a capture: one parse per frame with the parse options that the exporter
/// demands (the field tree for the hierarchy shape, a tree-free parse for the flat shapes), and the packets handed to every open
/// export. The readers of <see cref="BufferReaders"/> read the result back.
/// </summary>
/// <remarks>Thread safety: stateless; each call uses its own exporters.</remarks>
internal static class BufferExportScenarios
{
    #region Shapes

    /// <summary>The shape of a scenario (the CSV packet rows have no content; they report the hierarchy shape).</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <returns>The shape the scenario's export is configured with.</returns>
    internal static ExportShape ShapeOf(BufferScenario scenario) =>
        scenario switch
        {
            BufferScenario.JsonAllFields or BufferScenario.TextAllFields => ExportShape.AllFields,
            BufferScenario.DuckDbSelectedFields or BufferScenario.ParquetSelectedFields or BufferScenario.PbfStandardSelectedFields
                or BufferScenario.PbfColumnarSelectedFields or BufferScenario.JsonSelectedFields or BufferScenario.CsvWide
                or BufferScenario.TextSelectedFields => ExportShape.SelectedFields,
            _ => ExportShape.Hierarchy,
        };

    /// <summary>The content of a scenario with a field selection where the shape needs one, and the given packet bytes members.</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <param name="members">Which packet bytes the export writes.</param>
    /// <returns>The content to give the exporter's builder.</returns>
    private static ExportContent _ContentOf(BufferScenario scenario, BufferMembers members)
    {
        ExportShape shape = ShapeOf(scenario);
        ExportContent content = shape == ExportShape.SelectedFields
            ? ExportContents.Selected(new FieldRequest("udp.dstport"))
            : new ExportContent { Shape = shape };
        return content with
        {
            FrameBytes = members.HasFlag(BufferMembers.Frames),
            Buffers = members.HasFlag(BufferMembers.Buffers),
        };
    }

    #endregion

    #region Open

    /// <summary>Opens the exporter of a scenario.</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <param name="members">Which packet bytes the export writes (CSV: which columns it gets).</param>
    /// <param name="dir">The directory for file outputs.</param>
    /// <param name="name">Distinguishes two exports of one test in the same directory.</param>
    /// <param name="maxPacketsPerBatch">Packets per batch for the batching formats; 0 keeps the default.</param>
    /// <returns>The open export.</returns>
    internal static OpenBufferExport Open(
        BufferScenario scenario, BufferMembers members, TestDir dir, string name = "export", int maxPacketsPerBatch = 0)
    {
        bool plain = members == BufferMembers.None && ShapeOf(scenario) == ExportShape.Hierarchy;
        ExportContent content = _ContentOf(scenario, members);
        string baseName = Invariant($"{scenario}_{name}");
        switch (scenario)
        {
            case BufferScenario.DuckDbHierarchy:
            case BufferScenario.DuckDbSelectedFields:
            {
                string path = dir.FilePath(baseName + ".duckdb");
                DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder().ToFile(path).WithWriteMode(ExportWriteMode.Synchronous);
                if (maxPacketsPerBatch > 0)
                {
                    builder.WithMaxPacketsPerBlock(maxPacketsPerBatch);
                }

                DuckDbExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                return new OpenBufferExport(exporter, exporter.ParseDemand, () => _Finish(exporter, path, string.Empty));
            }

            case BufferScenario.ParquetHierarchy:
            case BufferScenario.ParquetSelectedFields:
            {
                string path = dir.FilePath(baseName);
                ParquetExporter.Builder builder = ParquetExporter.CreateBuilder().ToDirectory(path).WithWriteMode(ExportWriteMode.Synchronous);
                if (maxPacketsPerBatch > 0)
                {
                    builder.WithMaxPacketsPerBlock(maxPacketsPerBatch);
                }

                ParquetExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                return new OpenBufferExport(exporter, exporter.ParseDemand, () => _Finish(exporter, path, string.Empty));
            }

            case BufferScenario.PbfStandardHierarchy:
            case BufferScenario.PbfStandardSelectedFields:
            case BufferScenario.PbfColumnarHierarchy:
            case BufferScenario.PbfColumnarSelectedFields:
            {
                string path = dir.FilePath(baseName + ".pbf");
                PbfExportFormat format = scenario is BufferScenario.PbfStandardHierarchy or BufferScenario.PbfStandardSelectedFields
                    ? PbfExportFormat.Standard
                    : PbfExportFormat.Columnar;
                PbfExporter.Builder builder = PbfExporter.CreateBuilder().ToFile(path).WithFormat(format).WithCompressed(false)
                    .WithWriteMode(ExportWriteMode.Synchronous);
                if (maxPacketsPerBatch > 0)
                {
                    builder.WithMaxPacketsPerBlock(maxPacketsPerBatch);
                }

                PbfExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                return new OpenBufferExport(exporter, exporter.ParseDemand, () => _Finish(exporter, path, string.Empty));
            }

            case BufferScenario.CsvPacketRows:
            case BufferScenario.CsvWide:
                return _OpenCsv(scenario, members, content);

            case BufferScenario.TextTree:
            case BufferScenario.TextSelectedFields:
            case BufferScenario.TextAllFields:
            {
                MemoryStream stream = new();
                TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(stream);
                TextExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                return new OpenBufferExport(exporter, exporter.ParseDemand, () => _FinishStream(exporter, stream));
            }

            default:
            {
                JsonExportFormat format = scenario switch
                {
                    BufferScenario.JsonPrettyTree => JsonExportFormat.Pretty,
                    BufferScenario.JsonCompactTree => JsonExportFormat.Compact,
                    _ => JsonExportFormat.Array,
                };
                MemoryStream stream = new();
                JsonExporter.Builder builder = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format);
                JsonExporter exporter = (plain
                    ? builder
                    : builder.WithContent(content)).Build();
                return new OpenBufferExport(exporter, exporter.ParseDemand, () => _FinishStream(exporter, stream));
            }
        }
    }

    /// <summary>Opens a CSV export: packet rows or the wide layout, with the packet number first and the requested byte columns last.</summary>
    /// <param name="scenario">The CSV configuration: <see cref="BufferScenario.CsvPacketRows"/> or <see cref="BufferScenario.CsvWide"/>.</param>
    /// <param name="members">Which byte columns (frame bytes, buffers) the export gets.</param>
    /// <param name="content">The content of a scenario; the wide layout uses it for its field selection.</param>
    /// <returns>The open export, which writes to a memory stream.</returns>
    private static OpenBufferExport _OpenCsv(BufferScenario scenario, BufferMembers members, ExportContent content)
    {
        MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false);
        if (scenario == BufferScenario.CsvWide)
        {
            // CSV keeps packet-level data in columns: its content has neither info nor frame bytes nor buffers; they are the
            // columns added below.
            builder.WithContent(content with { Info = false, FrameBytes = false, Buffers = false })
                .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Timestamp);
        }
        else
        {
            builder.WithColumn(CsvColumnKind.PacketNumber);
        }

        if (members.HasFlag(BufferMembers.Frames))
        {
            builder.WithColumn(CsvColumnKind.FrameBytes);
        }

        if (members.HasFlag(BufferMembers.Buffers))
        {
            builder.WithColumn(CsvColumnKind.Buffers);
        }

        CsvExporter exporter = builder.Build();
        return new OpenBufferExport(exporter, exporter.ParseDemand, () => _FinishStream(exporter, stream));
    }

    /// <summary>Finishes a file exporter and disposes it.</summary>
    /// <typeparam name="TExporter">The exporter type.</typeparam>
    /// <param name="exporter">The exporter to finish.</param>
    /// <param name="path">The file or directory the exporter wrote.</param>
    /// <param name="text">The text to report; empty for a file format.</param>
    /// <returns>The given path and text, unchanged.</returns>
    private static (string Path, string Text) _Finish<TExporter>(TExporter exporter, string path, string text)
        where TExporter : IPacketListener, IDisposable
    {
        exporter.OnFinish();
        exporter.Dispose();
        return (path, text);
    }

    /// <summary>Finishes a stream exporter, disposes it, and returns the text it wrote.</summary>
    /// <typeparam name="TExporter">The exporter type.</typeparam>
    /// <param name="exporter">The exporter to finish.</param>
    /// <param name="stream">The memory stream the exporter wrote to; it is disposed.</param>
    /// <returns>An empty path and the UTF-8 text of the stream.</returns>
    private static (string Path, string Text) _FinishStream<TExporter>(TExporter exporter, MemoryStream stream)
        where TExporter : IPacketListener, IDisposable
    {
        exporter.OnFinish();
        exporter.Dispose();
        string text = Encoding.UTF8.GetString(stream.ToArray());
        stream.Dispose();
        return (string.Empty, text);
    }

    #endregion

    #region Run

    /// <summary>
    /// Feeds frames to one or more open exports: every frame is parsed once with the parse options of the first export, and the
    /// parsed packet is offered to all of them in order (so two exports see exactly the same packets and packet ids). The additional
    /// buffers of every packet are recorded as the expectation before the first export sees the packet.
    /// </summary>
    /// <param name="frames">The frames, in capture order.</param>
    /// <param name="exports">The open exports; all must demand the same parse (the same shape).</param>
    /// <returns>The first packet id, the expected buffer rows, and how many packets carried a field tree.</returns>
    /// <exception cref="InvalidOperationException">An export refused a packet.</exception>
    internal static FeedResult Feed(IReadOnlyList<Frame> frames, params OpenBufferExport[] exports)
    {
        ParseOptions options = TreeFreeFeed.CreateOptions(exports[0].Demand);
        List<BufferRow> expected = [];
        int firstPacketId = -1;
        int packetCount = 0;
        int withFieldTree = 0;
        Packet? recycle = null;
        foreach (Frame frame in frames)
        {
            Packet packet = TestHarness.ParseFrame(frame, in options, recycle);
            recycle = packet;
            packetCount++;
            if (packet.HasFieldTree)
            {
                withFieldTree++;
            }

            if (firstPacketId < 0)
            {
                firstPacketId = packet.Id.Value;
            }

            for (int index = 1; index < packet.BufferCount; index++)
            {
                expected.Add(new BufferRow(packet.Id.Value, index, packet.Buffer(index)!.Value.ToArray()));
            }

            foreach (OpenBufferExport export in exports)
            {
                if (!export.Offer(packet))
                {
                    throw new InvalidOperationException("An exporter of the buffer matrix refused a packet of the capture.");
                }
            }
        }

        return new FeedResult(firstPacketId, expected, packetCount, withFieldTree);
    }

    /// <summary>Opens the exporter of a scenario, feeds it the frames, finishes it, and returns the output with the expectation.</summary>
    /// <param name="scenario">The exporter configuration.</param>
    /// <param name="frames">The frames, in capture order.</param>
    /// <param name="members">Which packet bytes the export writes.</param>
    /// <param name="dir">The directory for file outputs.</param>
    /// <returns>What the exporter wrote and what the packets reported.</returns>
    internal static BufferRun Run(BufferScenario scenario, IReadOnlyList<Frame> frames, BufferMembers members, TestDir dir)
    {
        OpenBufferExport export = Open(scenario, members, dir);
        FeedResult fed = Feed(frames, export);
        (string path, string text) = export.Finish();
        return new BufferRun(path, text, fed.FirstPacketId, fed.Expected, fed.PacketCount, fed.PacketsWithFieldTree);
    }

    #endregion

    #region Reading

    /// <summary>Reads the buffers back with the reader of the scenario's format.</summary>
    /// <param name="scenario">The exporter configuration that wrote the output.</param>
    /// <param name="run">What the run wrote.</param>
    /// <returns>The rows in stored order; <see langword="null"/> when the output has no buffer content at all.</returns>
    internal static List<BufferRow>? Read(BufferScenario scenario, BufferRun run)
    {
        switch (scenario)
        {
            case BufferScenario.DuckDbHierarchy:
            case BufferScenario.DuckDbSelectedFields:
                return BufferReaders.ReadDuckDb(run.Path);
            case BufferScenario.ParquetHierarchy:
            case BufferScenario.ParquetSelectedFields:
                return BufferReaders.ReadParquet(run.Path);
            case BufferScenario.PbfStandardHierarchy:
            case BufferScenario.PbfStandardSelectedFields:
                return BufferReaders.ReadPbfStandard(run.Path);
            case BufferScenario.PbfColumnarHierarchy:
            case BufferScenario.PbfColumnarSelectedFields:
                return BufferReaders.ReadPbfColumnar(run.Path);
            case BufferScenario.CsvPacketRows:
            case BufferScenario.CsvWide:
                return BufferReaders.ReadCsv(run.Text);
            case BufferScenario.TextTree:
            case BufferScenario.TextSelectedFields:
            case BufferScenario.TextAllFields:
                return BufferReaders.ReadText(run.Text, run.FirstPacketId);
            default:
                return BufferReaders.ReadJson(run.Text);
        }
    }

    #endregion
}
