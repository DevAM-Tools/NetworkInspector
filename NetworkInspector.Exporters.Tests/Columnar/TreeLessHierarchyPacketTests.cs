// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// The exporters with a hierarchy shape (Text, JSON, CSV, DuckDB, Parquet, PBF standard and columnar) agree on how a hierarchy
/// export answers a packet that was parsed without a field tree: the packet is skipped and counted, one event carries the same
/// error kind and message, and no exception reaches the caller. The DuckDB exporter writes a database, so the class carries the
/// slow marker like the other DuckDB tests.
/// </summary>
[Category("Slow")]
internal sealed class TreeLessHierarchyPacketTests
{
    /// <summary>One exporter of the comparison with its label, so a failed assertion names the format.</summary>
    /// <param name="Name">Format label.</param>
    /// <param name="Listener">The exporter as packet listener.</param>
    /// <param name="Tolerant">The same exporter as error-tolerant exporter (statistics and the skip event).</param>
    private sealed record HierarchyExporter(string Name, IPacketListener Listener, IErrorTolerantExporter Tolerant);

    [Test]
    public async Task ATreeLessHierarchyPacketIsSkippedWithTheSameKindInEveryHierarchyFormat()
    {
        using TestDir dir = new("columnar_no_tree");
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(8)), in noTree);
        ExportContent hierarchy = ExportContents.Hierarchy();

        using MemoryStream textStream = new();
        using TextExporter text = TextExporter.CreateBuilder().ToStream(textStream).Build();
        using MemoryStream jsonStream = new();
        using JsonExporter json = JsonExporter.CreateBuilder().ToStream(jsonStream).Build();
        using MemoryStream csvStream = new();
        using CsvExporter csv = CsvExporter.CreateBuilder()
            .ToStream(csvStream)
            .WithContent(CsvExporter.ContentTemplate with { Shape = ExportShape.Hierarchy })
            .Build();
        using DuckDbExporter duckDb = DuckDbExporter.CreateBuilder()
            .ToFile(dir.FilePath("capture.duckdb"))
            .WithContent(hierarchy)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        MemoryExportTarget parquetTarget = new(seekable: true);
        using ParquetExporter parquet = ParquetExporter.CreateBuilder()
            .ToTarget(parquetTarget)
            .WithContent(hierarchy)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        using MemoryStream pbfStream = new();
        using PbfExporter pbf = PbfExporter.CreateBuilder()
            .ToStream(pbfStream)
            .WithFormat(PbfExportFormat.Columnar)
            .WithContent(hierarchy)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        using MemoryStream pbfStandardStream = new();
        using PbfExporter pbfStandard = PbfExporter.CreateBuilder()
            .ToStream(pbfStandardStream)
            .WithFormat(PbfExportFormat.Standard)
            .WithContent(hierarchy)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        HierarchyExporter[] exporters =
        [
            new("Text", text, text),
            new("JSON", json, json),
            new("CSV", csv, csv),
            new("DuckDB", duckDb, duckDb),
            new("Parquet", parquet, parquet),
            new("PBF columnar", pbf, pbf),
            new("PBF standard", pbfStandard, pbfStandard),
        ];

        await Assert.That(packet.HasFieldTree).IsFalse();

        foreach (HierarchyExporter exporter in exporters)
        {
            List<ExportErrorEventArgs> events = [];
            exporter.Tolerant.ItemSkipped += (_, args) => events.Add(args);

            bool accepted = exporter.Listener.OnPacket(packet);
            exporter.Listener.OnFinish();

            // Tolerant mode (the default): the packet is skipped, the export goes on, and exactly one event states why.
            await Assert.That(accepted).IsTrue().Because(exporter.Name);
            await Assert.That(exporter.Tolerant.SkippedCount).IsEqualTo(1).Because(exporter.Name);
            await Assert.That(exporter.Tolerant.WrittenCount).IsEqualTo(0).Because(exporter.Name);
            await Assert.That(events.Count).IsEqualTo(1).Because(exporter.Name);
            await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError).Because(exporter.Name);
            await Assert.That(events[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).").Because(exporter.Name);
        }
    }
}
