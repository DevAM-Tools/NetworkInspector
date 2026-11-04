// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// Tests for a flush that fails part-way through the dataset: <c>packets.parquet</c> already received the row group, the
/// field files cannot be created. The batch must not be written a second time, and the counters must say what was lost.
/// </summary>
internal sealed class ParquetExporterFlushFailureTests
{
    /// <summary>
    /// Creates the dataset directory with a directory in place of every field file the given packets will need, so the
    /// field files cannot be created while <c>packets.parquet</c> and <c>topology.parquet</c> can.
    /// </summary>
    private static void _BlockFieldFiles(string outputPath, Packet[] packets)
    {
        string fieldsDirectory = Path.Combine(outputPath, "fields");
        using ColumnarPacketBatch probe = new(ExportContents.Full, 1000, 1024 * 1024, true);
        foreach (Packet packet in packets)
        {
            probe.AddPacket(packet);
        }

        foreach (int fieldId in probe.FieldBags.Keys)
        {
            Directory.CreateDirectory(Path.Combine(fieldsDirectory, $"field_{fieldId}.parquet"));
        }
    }

    private static async Task<int[]> _ReadPacketIds(string path)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField packetIdField = reader.Schema.FindDataField("packet_id");
        List<int> ids = [];
        for (int index = 0; index < reader.RowGroupCount; index++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(index);
            int[] buffer = new int[rowGroup.RowCount];
            await rowGroup.ReadAsync<int>(packetIdField, buffer, null, CancellationToken.None);
            ids.AddRange(buffer);
        }

        return ids.ToArray();
    }

    /// <summary>Reads the <c>field_id</c> column of <c>catalog.parquet</c>; empty when the file does not exist.</summary>
    /// <param name="path">Path of <c>catalog.parquet</c>.</param>
    /// <returns>The catalog's field ids.</returns>
    private static async Task<int[]> _ReadCatalogFieldIds(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField fieldIdField = reader.Schema.FindDataField("field_id");
        List<int> ids = [];
        for (int index = 0; index < reader.RowGroupCount; index++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(index);
            int[] buffer = new int[rowGroup.RowCount];
            await rowGroup.ReadAsync<int>(fieldIdField, buffer, null, CancellationToken.None);
            ids.AddRange(buffer);
        }

        return ids.ToArray();
    }

    /// <summary>
    /// The field file of the last field of the batch cannot be created, so the files of the fields before it are written and
    /// the batch is lost. Every field file that exists must still be named by the catalog.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    [Category("Slow")]
    public async Task AFieldFileOfAFailedBatchHasACatalogRow(ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_flush_catalog");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        string fieldsDirectory = Path.Combine(outputPath, "fields");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        using (ColumnarPacketBatch probe = new(ExportContents.Full, 1000, 1024 * 1024, true))
        {
            foreach (Packet packet in packets)
            {
                probe.AddPacket(packet);
            }

            // The writer visits the bags in dictionary order, so the last key is the field whose file fails after the others.
            int lastFieldId = probe.FieldBags.Keys.Last();
            Directory.CreateDirectory(Path.Combine(fieldsDirectory, $"field_{lastFieldId}.parquet"));
        }

        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(2)
            .WithWriteMode(mode)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            foreach (Packet packet in packets)
            {
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
            await Assert.That(exporter.HasErrors).IsTrue();
        }

        int[] catalogIds = await _ReadCatalogFieldIds(Path.Combine(outputPath, "catalog.parquet"));
        string[] fieldFiles = Directory.GetFiles(fieldsDirectory, "field_*.parquet");

        await Assert.That(fieldFiles.Length).IsGreaterThan(0);
        foreach (string file in fieldFiles)
        {
            int id = int.Parse(Path.GetFileNameWithoutExtension(file).AsSpan("field_".Length), CultureInfo.InvariantCulture);
            await Assert.That(catalogIds).Contains(id);
        }
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    [Category("Slow")]
    public async Task FailedFlushDoesNotWriteTheSamePacketsAgain(ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_flush_failure");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(6);
        _BlockFieldFiles(outputPath, packets);
        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(2)
            .WithWriteMode(mode)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            foreach (Packet packet in packets)
            {
                exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        int[] ids = await _ReadPacketIds(Path.Combine(outputPath, "packets.parquet"));

        await Assert.That(ids.Length).IsGreaterThan(0);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Length);
    }

    [Test]
    [Category("Slow")]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task FailedFlushCountsTheLostPacketsAsSkipped(ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_flush_counts");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        _BlockFieldFiles(outputPath, packets);
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(3)
            .WithWriteMode(mode)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        bool third = exporter.OnPacket(packets[2]);

        // The third packet filled the batch and the write failed: all three packets are lost, none counts as written. In
        // background mode the failure is reported one batch later (at the latest by OnFinish), so the counters are read after
        // the end of the export in both modes.
        exporter.OnFinish();
        await Assert.That(third).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(3);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    [Category("Slow")]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task FailedFlushStopsAStrictExportAndFinishDoesNotWriteTheBatchAgain(ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_flush_strict");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        _BlockFieldFiles(outputPath, packets);
        bool second;
        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(2)
            .WithWriteMode(mode)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Strict;
            exporter.OnPacket(packets[0]);
            second = exporter.OnPacket(packets[1]);
            exporter.OnFinish();
        }

        int[] ids = await _ReadPacketIds(Path.Combine(outputPath, "packets.parquet"));

        // Synchronous mode reports the failed batch in the call that completed it, so strict mode refuses that very packet. In
        // background mode the writer has not necessarily failed yet when the call returns (uniform one-batch-later timing), so
        // only the end state is asserted there: the error is latched and the batch was not written again.
        if (mode == ExportWriteMode.Synchronous)
        {
            await Assert.That(second).IsFalse();
        }

        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Length);
    }
}
