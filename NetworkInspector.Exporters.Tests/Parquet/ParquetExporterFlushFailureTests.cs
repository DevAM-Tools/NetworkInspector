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
        using ColumnarPacketBatch probe = new(ColumnarDetailFlags.All, 1000, 1024 * 1024, true);
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

    [Test]
    public async Task FailedFlushDoesNotWriteTheSamePacketsAgain()
    {
        using TestDir dir = new("parquet_flush_failure");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(6);
        _BlockFieldFiles(outputPath, packets);
        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(2)
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
    public async Task FailedFlushCountsTheLostPacketsAsSkipped()
    {
        using TestDir dir = new("parquet_flush_counts");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        _BlockFieldFiles(outputPath, packets);
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(3)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        bool third = exporter.OnPacket(packets[2]);

        // The third packet filled the batch and the flush failed: all three packets are lost, none counts as written.
        await Assert.That(third).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(3);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FailedFlushStopsAStrictExportAndFinishDoesNotWriteTheBatchAgain()
    {
        using TestDir dir = new("parquet_flush_strict");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        _BlockFieldFiles(outputPath, packets);
        bool second;
        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(2)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Strict;
            exporter.OnPacket(packets[0]);
            second = exporter.OnPacket(packets[1]);
            exporter.OnFinish();
        }

        int[] ids = await _ReadPacketIds(Path.Combine(outputPath, "packets.parquet"));

        await Assert.That(second).IsFalse();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Length);
    }
}
