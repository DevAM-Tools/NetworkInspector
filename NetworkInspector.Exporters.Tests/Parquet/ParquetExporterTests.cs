// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// Tests for the <see cref="ParquetExporter"/> — validates the builder contract, the directory
/// dataset layout, and reads written files back with <see cref="ParquetReader"/> to verify
/// packet/topology/catalog/field row content and string dictionary encoding.
/// </summary>
internal sealed class ParquetExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        ParquetExporter.Builder builder =
            ParquetExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyExport_CreatesNoDirectory()
    {
        using TestDir dir = new("parquet_empty");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        exporter.OnFinish();

        await Assert.That(Directory.Exists(outputPath)).IsFalse();
    }

    [Test]
    public async Task Export_ToExistingDirectory_ClearsOrphanFieldFiles()
    {
        using TestDir dir = new("parquet_overwrite");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        string fieldsDir = Path.Combine(outputPath, "fields");
        Directory.CreateDirectory(fieldsDir);
        string orphanPath = Path.Combine(fieldsDir, "field_999999.parquet");
        await File.WriteAllTextAsync(orphanPath, "stale");

        using (ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build())
        {
            foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(2))
            {
                exporter.OnPacket(packet);
            }
            exporter.OnFinish();
        }

        await Assert.That(File.Exists(orphanPath)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(outputPath, "packets.parquet"))).IsTrue();
    }

    [Test]
    public async Task Export_CreatesExpectedDirectoryStructure()
    {
        using TestDir dir = new("parquet_structure");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(File.Exists(Path.Combine(outputPath, "packets.parquet"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(outputPath, "topology.parquet"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(outputPath, "catalog.parquet"))).IsTrue();

        string fieldsDir = Path.Combine(outputPath, "fields");
        await Assert.That(Directory.Exists(fieldsDir)).IsTrue();
        string[] fieldFiles = Directory.GetFiles(fieldsDir, "field_*.parquet");
        await Assert.That(fieldFiles.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task PacketsFile_ContainsCorrectPacketIdsAndTimestamps()
    {
        using TestDir dir = new("parquet_packets");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        (int[] packetIds, long[] timestamps) = await _ReadPacketsFile(Path.Combine(outputPath, "packets.parquet"));

        await Assert.That(packetIds.Length).IsEqualTo(4);
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(packetIds[i]).IsEqualTo(packets[i].Id.Value);
            await Assert.That(timestamps[i]).IsEqualTo(packets[i].Timestamp.AsNanos);
        }
    }

    [Test]
    public async Task TopologyFile_HasRows_WhenIncludeTopologySet()
    {
        using TestDir dir = new("parquet_topology_on");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithDetailFlags(ColumnarDetailFlags.IncludeTopology)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        long rowCount = await _CountRows(Path.Combine(outputPath, "topology.parquet"));
        await Assert.That(rowCount).IsGreaterThan(0);
    }

    [Test]
    public async Task TopologyFile_NotCreated_WhenIncludeTopologyNotSet()
    {
        using TestDir dir = new("parquet_topology_off");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithDetailFlags(ColumnarDetailFlags.None)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(File.Exists(Path.Combine(outputPath, "topology.parquet"))).IsFalse();
    }

    [Test]
    public async Task CatalogFile_ContainsOneRowPerObservedField()
    {
        using TestDir dir = new("parquet_catalog");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        long catalogRows = await _CountRows(Path.Combine(outputPath, "catalog.parquet"));
        string[] fieldFiles = Directory.GetFiles(Path.Combine(outputPath, "fields"), "field_*.parquet");

        // One field_{id}.parquet data file per catalog entry (dict files use a different suffix
        // and are excluded by the "field_*.parquet" glob only incidentally sharing the prefix —
        // assert the count matches the plain per-field data files, not dict companions).
        int dataFileCount = 0;
        foreach (string file in fieldFiles)
        {
            if (!Path.GetFileNameWithoutExtension(file).EndsWith("_dict", StringComparison.Ordinal))
            {
                dataFileCount++;
            }
        }

        await Assert.That(catalogRows).IsEqualTo(dataFileCount);
        await Assert.That(catalogRows).IsGreaterThan(0);
    }

    [Test]
    public async Task StringField_WritesPlainValueColumn()
    {
        using TestDir dir = new("parquet_string_value");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com", "example.com", "other.example.org");
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        int dnsNameFieldId = await _FindFieldIdByName(Path.Combine(outputPath, "catalog.parquet"), "dns.qry.name");
        await Assert.That(dnsNameFieldId).IsGreaterThanOrEqualTo(0)
            .Because("the DNS query packets must produce a dns.qry.name string field");

        string fieldPath = Path.Combine(outputPath, "fields", $"field_{dnsNameFieldId}.parquet");
        string[] values = await _ReadStringColumn(fieldPath, "value");
        await Assert.That(values.Length).IsEqualTo(3);
        await Assert.That(values[0]).IsEqualTo("example.com");
        await Assert.That(values[1]).IsEqualTo("example.com");
        await Assert.That(values[2]).IsEqualTo("other.example.org");
        await Assert.That(File.Exists(Path.Combine(outputPath, "fields", $"field_{dnsNameFieldId}_value_dict.parquet"))).IsFalse();
    }

    [Test]
    public async Task CustomTextColumn_PreservesNullEntries()
    {
        // Absent custom text must remain null in Parquet (DuckDB parity), not coerced to "".
        using TestDir dir = new("parquet_null_custom_text");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithDetailFlags(ColumnarDetailFlags.All)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        string fieldsDir = Path.Combine(outputPath, "fields");
        string[] fieldFiles = Directory.GetFiles(fieldsDir, "field_*.parquet");
        await Assert.That(fieldFiles.Length).IsGreaterThan(0);

        string fieldPath = fieldFiles[0];
        string?[] customTexts = await _ReadNullableStringColumn(fieldPath, "custom_text");
        await Assert.That(customTexts.Length).IsGreaterThan(0);
        foreach (string? text in customTexts)
        {
            await Assert.That(text).IsNull();
        }
    }

    [Test]
    public async Task TargetPacketCount_LimitsExport()
    {
        using TestDir dir = new("parquet_target_count");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithTargetPacketCount(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(3);

        long rowCount = await _CountRows(Path.Combine(outputPath, "packets.parquet"));
        await Assert.That(rowCount).IsEqualTo(3);
    }

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using TestDir dir = new("parquet_cancel");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        using CancellationTokenSource cts = new();

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithCancellationToken(cts.Token)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(20);
        exporter.OnPacket(packets[0]);
        await cts.CancelAsync().ConfigureAwait(false);
        bool continued = exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(continued).IsFalse();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleFlushes_ProduceMultipleRowGroups_AllReadable()
    {
        using TestDir dir = new("parquet_multi_flush");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .WithMaxPacketsPerBlock(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(10);

        await using ParquetReader reader = await ParquetReader.CreateAsync(Path.Combine(outputPath, "packets.parquet"));
        await Assert.That(reader.RowGroupCount).IsGreaterThan(1)
            .Because("10 packets with a 3-per-block limit must flush more than once");

        long totalRows = await _CountRows(Path.Combine(outputPath, "packets.parquet"));
        await Assert.That(totalRows).IsEqualTo(10);
    }

    [Test]
    public async Task DoubleFinish_DoesNotThrow()
    {
        using TestDir dir = new("parquet_double_finish");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToDirectory(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);

        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // Parquet read-back helpers
    // ========================================================================

    private static async Task<(int[] PacketIds, long[] Timestamps)> _ReadPacketsFile(string path)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField packetIdField = reader.Schema.FindDataField("packet_id");
        DataField timestampField = reader.Schema.FindDataField("timestamp_ns");

        List<int> packetIds = [];
        List<long> timestamps = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            int[] idBuffer = new int[rowCount];
            long[] tsBuffer = new long[rowCount];
            await rowGroup.ReadAsync<int>(packetIdField, idBuffer, null, CancellationToken.None);
            await rowGroup.ReadAsync<long>(timestampField, tsBuffer, null, CancellationToken.None);
            packetIds.AddRange(idBuffer);
            timestamps.AddRange(tsBuffer);
        }

        return (packetIds.ToArray(), timestamps.ToArray());
    }

    private static async Task<long> _CountRows(string path)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        long total = 0;
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            total += rowGroup.RowCount;
        }
        return total;
    }

    private static async Task<int> _FindFieldIdByName(string catalogPath, string fieldName)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(catalogPath);
        DataField fieldIdField = reader.Schema.FindDataField("field_id");
        DataField nameField = reader.Schema.FindDataField("name");

        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            int[] fieldIds = new int[rowCount];
            string[] names = new string[rowCount];
            await rowGroup.ReadAsync<int>(fieldIdField, fieldIds, null, CancellationToken.None);
            await rowGroup.ReadAsync(nameField, names, null, CancellationToken.None);

            for (int r = 0; r < rowCount; r++)
            {
                if (string.Equals(names[r], fieldName, StringComparison.Ordinal))
                {
                    return fieldIds[r];
                }
            }
        }

        return -1;
    }

    private static async Task<string[]> _ReadStringColumn(string path, string columnName)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField field = reader.Schema.FindDataField(columnName);

        List<string> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            string[] buffer = new string[rowCount];
            await rowGroup.ReadAsync(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }

    private static async Task<string?[]> _ReadNullableStringColumn(string path, string columnName)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField field = reader.Schema.FindDataField(columnName);

        List<string?> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            string?[] buffer = new string?[rowCount];
            await rowGroup.ReadAsync(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }

    private static async Task<int?[]> _ReadNullableIntColumn(string path, string columnName)
    {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        DataField field = reader.Schema.FindDataField(columnName);

        List<int?> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            int?[] buffer = new int?[rowCount];
            await rowGroup.ReadAsync<int>(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }
}
