// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using DescribedFieldCapture = NetworkInspector.Exporters.Tests.Columnar.DescribedFieldCapture;
using ValueOrStructureFieldCapture = NetworkInspector.Exporters.Tests.Columnar.ValueOrStructureFieldCapture;

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// Tests for the <see cref="ParquetExporter"/> — validates the builder contract, the directory
/// dataset layout, and reads written files back with <see cref="ParquetReader"/> to verify
/// packet/topology/catalog/field row content and string dictionary encoding.
/// </summary>
[Category("Slow")]
internal sealed class ParquetExporterTests
{
    [Test]
    public async Task EmptyExportCreatesNoDirectory()
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
    public async Task ExportToExistingDirectoryClearsOrphanFieldFiles()
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
    public async Task ExportCreatesExpectedDirectoryStructure()
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
    public async Task PacketsFileContainsCorrectPacketIdsAndTimestamps()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        (int[] packetIds, long[] timestamps) = await _ReadPacketsFile(target, "packets.parquet");

        await Assert.That(packetIds.Length).IsEqualTo(4);
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(packetIds[i]).IsEqualTo(packets[i].Id.Value);
            await Assert.That(timestamps[i]).IsEqualTo(packets[i].Timestamp.AsNanos);
        }
    }

    [Test]
    public async Task TopologyFileHasRowsInTheHierarchyShape()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithContent(ExportContents.Hierarchy())
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        long rowCount = await _CountRows(target, "topology.parquet");
        await Assert.That(rowCount).IsGreaterThan(0);
    }

    [Test]
    public async Task TopologyFileNotCreatedInTheFlatShapes()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithContent(ExportContents.AllFields())
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(target.EntryNames.Contains("topology.parquet")).IsFalse();
    }

    [Test]
    public async Task CatalogFileContainsOneRowPerObservedField()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        long catalogRows = await _CountRows(target, "catalog.parquet");
        string[] fieldFiles = [.. target.EntryNames.Where(static name => name.StartsWith("fields/field_", StringComparison.Ordinal))];

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

        // The catalog also names the structure nodes (protocols, groups) of the topology; they have a row but no table
        // (table_name NULL) and so no data file.
        string?[] tableNames = await _ReadNullableStringColumn(target, "catalog.parquet", "table_name");
        int rowsWithTable = tableNames.Count(static name => name is not null);
        int structureRows = tableNames.Length - rowsWithTable;

        await Assert.That(rowsWithTable).IsEqualTo(dataFileCount);
        await Assert.That(structureRows).IsGreaterThan(0);
        await Assert.That(catalogRows).IsEqualTo(rowsWithTable + structureRows);
    }

    [Test]
    public async Task CatalogFileHasOnlyValueFieldsWithoutTopology()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithContent(ExportContents.AllFields())
                .Build();

        foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(2))
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        string?[] tableNames = await _ReadNullableStringColumn(target, "catalog.parquet", "table_name");

        await Assert.That(tableNames.Length).IsGreaterThan(0);
        await Assert.That(tableNames.Count(static name => name is null)).IsEqualTo(0);
    }

    [Test]
    public async Task StringFieldWritesPlainValueColumn()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .Build();

        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com", "example.com", "other.example.org");
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        int dnsNameFieldId = await _FindFieldIdByName(target, "catalog.parquet", "dns.qry.name");
        await Assert.That(dnsNameFieldId).IsGreaterThanOrEqualTo(0)
            .Because("the DNS query packets must produce a dns.qry.name string field");

        string fieldPath = Invariant($"fields/field_{dnsNameFieldId}.parquet");
        string[] values = await _ReadStringColumn(target, fieldPath, "value");
        await Assert.That(values.Length).IsEqualTo(3);
        await Assert.That(values[0]).IsEqualTo("example.com");
        await Assert.That(values[1]).IsEqualTo("example.com");
        await Assert.That(values[2]).IsEqualTo("other.example.org");
        await Assert.That(target.EntryNames.Contains(Invariant($"fields/field_{dnsNameFieldId}_value_dict.parquet"))).IsFalse();
    }

    [Test]
    public async Task CustomTextColumnPreservesNullEntries()
    {
        // Absent custom text must remain null in Parquet (DuckDB parity), not coerced to "".
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithContent(ExportContents.Full)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        string[] fieldFiles = [.. target.EntryNames.Where(static name => name.StartsWith("fields/field_", StringComparison.Ordinal))];
        await Assert.That(fieldFiles.Length).IsGreaterThan(0);

        string fieldPath = fieldFiles[0];
        string?[] customTexts = await _ReadNullableStringColumn(target, fieldPath, "custom_text");
        await Assert.That(customTexts.Length).IsGreaterThan(0);
        foreach (string? text in customTexts)
        {
            await Assert.That(text).IsNull();
        }
    }

    [Test]
    public async Task TargetPacketCountLimitsExport()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithTargetPacketCount(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(3);

        long rowCount = await _CountRows(target, "packets.parquet");
        await Assert.That(rowCount).IsEqualTo(3);
    }

    [Test]
    public async Task BackgroundAndSynchronousModesWriteIdenticalRows()
    {
        MemoryExportTarget background = new(seekable: true);
        MemoryExportTarget synchronous = new(seekable: true);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3000);

        using (ParquetExporter first = ParquetExporter.CreateBuilder().ToTarget(background).WithMaxPacketsPerBlock(1000).Build())
        {
            foreach (Packet packet in packets)
            {
                first.OnPacket(packet);
            }
        }

        using (ParquetExporter second = ParquetExporter.CreateBuilder()
            .ToTarget(synchronous).WithMaxPacketsPerBlock(1000).WithWriteMode(ExportWriteMode.Synchronous).Build())
        {
            foreach (Packet packet in packets)
            {
                second.OnPacket(packet);
            }
        }

        // Decoded rows are compared, not bytes: the file footers may carry creation metadata.
        (int[] backgroundIds, long[] backgroundTimes) = await _ReadPacketsFile(background, "packets.parquet");
        (int[] synchronousIds, long[] synchronousTimes) = await _ReadPacketsFile(synchronous, "packets.parquet");

        await Assert.That(synchronousIds).IsEquivalentTo(backgroundIds);
        await Assert.That(synchronousTimes).IsEquivalentTo(backgroundTimes);
        await Assert.That(backgroundIds.Length).IsEqualTo(3000);
    }

    [Test]
    public async Task TenThousandPacketBatchesProduceThreeRowGroups()
    {
        MemoryExportTarget target = new(seekable: true);

        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .WithMaxPacketsPerBlock(10_000)
            .WithMaxBlockSize(long.MaxValue) // only the packet limit decides here; the default 16 MiB estimate would flush earlier
            .Build())
        {
            foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(25_000))
            {
                exporter.OnPacket(packet);
            }
        }

        using MemoryStream stream = new(target.Bytes("packets.parquet"));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        // 10 000 + 10 000 + 5 000: the limit decides the batch size, and the last batch is written at the end.
        await Assert.That(reader.RowGroupCount).IsEqualTo(3);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task BatchSizeOutOfRangeNamesTheRange(int count)
    {
        await Assert.That(() => ParquetExporter.CreateBuilder().WithMaxPacketsPerBlock(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and 2147483647");
        await Assert.That(() => ParquetExporter.CreateBuilder().WithMaxBlockSize(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and");
    }

    [Test]
    public async Task WriteModeMustBeDefined()
    {
        await Assert.That(() => ParquetExporter.CreateBuilder().WithWriteMode((ExportWriteMode)9)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MultipleFlushesProduceMultipleRowGroupsAllReadable()
    {
        MemoryExportTarget target = new(seekable: true);

        using ParquetExporter exporter =
            ParquetExporter.CreateBuilder()
                .ToTarget(target)
                .WithMaxPacketsPerBlock(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(10);

        using MemoryStream stream = new(target.Bytes("packets.parquet"));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        await Assert.That(reader.RowGroupCount).IsGreaterThan(1)
            .Because("10 packets with a 3-per-block limit must flush more than once");

        long totalRows = await _CountRows(target, "packets.parquet");
        await Assert.That(totalRows).IsEqualTo(10);
    }

    #region Catalog merge across batches

    /// <summary>
    /// Background mode alternates two batches. One sees the field with a value, the other only as a node without one; the
    /// catalog row with a table must win, because <c>fields/field_{id}.parquet</c> exists.
    /// </summary>
    [Test]
    public async Task ACatalogRowWithATableIsNotReplacedByAStructureRowOfAnotherBatch()
    {
        MemoryExportTarget target = new(seekable: true);
        using ValueOrStructureFieldCapture capture = ValueOrStructureFieldCapture.Create();
        const string catalogPath = "catalog.parquet";

        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(ExportWriteMode.Background)
            .Build())
        {
            foreach (Packet packet in capture.Packets)
            {
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        int fieldId = await _FindFieldIdByName(target, catalogPath, ValueOrStructureFieldCapture.FieldName);
        string[] names = await _ReadStringColumn(target, catalogPath, "name");
        string?[] tableNames = await _ReadNullableStringColumn(target, catalogPath, "table_name");
        int row = Array.IndexOf(names, ValueOrStructureFieldCapture.FieldName);

        await Assert.That(fieldId).IsGreaterThanOrEqualTo(0);
        await Assert.That(row).IsGreaterThanOrEqualTo(0);
        await Assert.That(tableNames[row]).IsEqualTo(Invariant($"field_{fieldId}"));
        await Assert.That(target.EntryNames.Contains(Invariant($"fields/field_{fieldId}.parquet"))).IsTrue();
    }

    /// <summary>The other write order: the structure row comes first, then the batch with the value; the value row replaces it.</summary>
    [Test]
    public async Task ACatalogRowWithATableReplacesAnEarlierStructureRow()
    {
        MemoryExportTarget target = new(seekable: true);
        using ValueOrStructureFieldCapture capture = ValueOrStructureFieldCapture.Create();
        const string catalogPath = "catalog.parquet";

        using (ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(ExportWriteMode.Background)
            .Build())
        {
            _ = exporter.OnPacket(capture.Packets[1]);
            _ = exporter.OnPacket(capture.Packets[0]);
            exporter.OnFinish();
        }

        int fieldId = await _FindFieldIdByName(target, catalogPath, ValueOrStructureFieldCapture.FieldName);
        string[] names = await _ReadStringColumn(target, catalogPath, "name");
        string?[] tableNames = await _ReadNullableStringColumn(target, catalogPath, "table_name");
        int row = Array.IndexOf(names, ValueOrStructureFieldCapture.FieldName);

        await Assert.That(row).IsGreaterThanOrEqualTo(0);
        await Assert.That(tableNames[row]).IsEqualTo(Invariant($"field_{fieldId}"));
    }

    #endregion

    #region Late packets and metadata

    /// <summary>A packet after <c>OnFinish</c> is refused and creates no directory, file, or count.</summary>
    [Test]
    public async Task APacketAfterFinishIsRefusedAndChangesNothing()
    {
        using TestDir dir = new("parquet_late");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToDirectory(outputPath).Build();
        exporter.OnFinish();

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(Directory.Exists(outputPath)).IsFalse();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    #endregion

    #region Catalog description and protocols

    /// <summary>
    /// <c>catalog.parquet</c> keeps its original column order and appends a nullable <c>description</c> column that
    /// carries <see cref="FieldInfo.Description"/> (text for a described field, null otherwise).
    /// </summary>
    [Test]
    public async Task CatalogFileAppendsDescriptionAsLastColumn()
    {
        MemoryExportTarget target = new(seekable: true);
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(2);
        const string catalogPath = "catalog.parquet";

        _Export(target, capture.Packets, ErrorToleranceMode.Tolerant);
        string[] columns = await _ReadColumnNames(target, catalogPath);
        string[] names = await _ReadStringColumn(target, catalogPath, "name");
        string?[] descriptions = await _ReadNullableStringColumn(target, catalogPath, "description");

        await Assert.That(string.Join(',', columns))
            .IsEqualTo("field_id,name,ui_name,field_type,protocol_id,table_name,description");
        int described = Array.IndexOf(names, DescribedFieldCapture.DescribedFieldName);
        int plain = Array.IndexOf(names, DescribedFieldCapture.PlainFieldName);
        await Assert.That(described).IsGreaterThanOrEqualTo(0);
        await Assert.That(plain).IsGreaterThanOrEqualTo(0);
        await Assert.That(descriptions[described]).IsEqualTo(DescribedFieldCapture.FieldDescription);
        await Assert.That(descriptions[plain]).IsNull();
    }

    /// <summary><c>protocols.parquet</c> holds one row per protocol of the parsing stack: id, name and display name.</summary>
    [Test]
    public async Task ProtocolsFileListsStackProtocols()
    {
        MemoryExportTarget target = new(seekable: true);
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        const string protocolsPath = "protocols.parquet";
        ReadOnlyMemory<ProtocolInfo> protocols = capture.Stack.Protocols;

        _Export(target, capture.Packets, ErrorToleranceMode.Tolerant);
        string[] columns = await _ReadColumnNames(target, protocolsPath);
        int[] ids = await _ReadIntColumn(target, protocolsPath, "protocol_id");
        string[] names = await _ReadStringColumn(target, protocolsPath, "name");
        string[] uiNames = await _ReadStringColumn(target, protocolsPath, "ui_name");

        await Assert.That(string.Join(',', columns)).IsEqualTo("protocol_id,name,ui_name");
        await Assert.That(ids.Length).IsEqualTo(protocols.Length);

        // A recurrence must be explainable from the failure message alone: the whole read column next to the whole expectation.
        string idContext = Invariant($"read ids [{string.Join(',', ids)}], stack ids [{string.Join(',', _ProtocolIds(protocols))}]");
        for (int i = 0; i < ids.Length; i++)
        {
            ProtocolInfo info = protocols.Span[i];
            await Assert.That(ids[i]).IsEqualTo(info.Id.Value).Because(Invariant($"row {i} of protocol_id; {idContext}"));
            await Assert.That(names[i]).IsEqualTo(info.Name);
            await Assert.That(uiNames[i]).IsEqualTo(info.UiName);
        }
        await Assert.That(Array.IndexOf(names, DescribedFieldCapture.ProtocolName)).IsGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// A required column is read independently of what the shared array pool still holds. Parquet.Net's nullable-buffer
    /// <c>ReadAsync</c> overload rents an uncleared <c>int[]</c> for the definition levels, and a required column has no
    /// definition levels in the file, so the stale content is read as levels and every negative stale value turns a row into
    /// <see langword="null"/>. The shared pool holds such values whenever any earlier code returned an array with negative
    /// content, which is why such a read fails in one full run out of many and never when a test runs alone. The test fills the
    /// pool with negative values right before the read, which makes the nullable overload fail every time, and checks that the
    /// plain-buffer read of <see cref="_ReadIntColumn"/> is unaffected.
    /// </summary>
    [Test]
    public async Task RequiredColumnReadIsIndependentOfStalePooledMemory()
    {
        MemoryExportTarget target = new(seekable: true);
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        const string protocolsPath = "protocols.parquet";
        ReadOnlyMemory<ProtocolInfo> protocols = capture.Stack.Protocols;

        _Export(target, capture.Packets, ErrorToleranceMode.Tolerant);
        int[] ids = await _ReadIntColumn(target, protocolsPath, "protocol_id", _FillSharedIntPoolWithNegativeValues);

        await Assert.That(ids.Length).IsEqualTo(protocols.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            await Assert.That(ids[i]).IsEqualTo(protocols.Span[i].Id.Value);
        }
    }


    #endregion

    #region Error tolerance

    /// <summary>
    /// Tolerant mode: when a table file cannot be created (a directory occupies <c>packets.parquet</c>), every failed
    /// flush raises <see cref="ParquetExporter.ItemSkipped"/>, the export continues, and no packet counts as written.
    /// The Parquet exporter writes only to a directory, so an unwritable table file stands in for a failing stream.
    /// </summary>
    [Test]
    public async Task TolerantUnwritableTableFileRaisesItemSkipped()
    {
        using TestDir dir = new("parquet_tolerant_failure");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Directory.CreateDirectory(Path.Combine(outputPath, "packets.parquet"));
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(1)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);

        bool[] results = new bool[packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            results[i] = exporter.OnPacket(packets[i]);
        }
        exporter.OnFinish();

        await Assert.That(Array.TrueForAll(results, static result => result)).IsTrue()
            .Because("tolerant mode must keep accepting packets after a failed flush");
        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(packets.Length);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    /// <summary>
    /// Strict mode: the first failed batch aborts the export (errors set, finished). Synchronous mode reports the failure in the
    /// call that completed the batch; background mode reports it one batch later (at the latest by <c>OnFinish</c>), so there
    /// only the end state is asserted.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task StrictUnwritableTableFileAbortsExport(ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_strict_failure");
        string outputPath = Path.Combine(dir.DirectoryPath, "dataset");
        Directory.CreateDirectory(Path.Combine(outputPath, "packets.parquet"));
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToDirectory(outputPath)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(mode)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);

        bool first = exporter.OnPacket(packets[0]);
        bool second = exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        if (mode == ExportWriteMode.Synchronous)
        {
            await Assert.That(first).IsFalse();
            await Assert.That(second).IsFalse();
        }

        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    #endregion

    // ========================================================================
    // Parquet read-back helpers
    // ========================================================================

    /// <summary>Exports <paramref name="packets"/> onto <paramref name="target"/> with default options.</summary>
    /// <param name="target">Target that receives the dataset entries.</param>
    /// <param name="packets">Packets to export.</param>
    /// <param name="mode">Error tolerance mode.</param>
    private static void _Export(MemoryExportTarget target, Packet[] packets, ErrorToleranceMode mode)
    {
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .Build();
        exporter.ErrorTolerance = mode;
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();
    }

    /// <summary>Returns the data column names of a Parquet file in schema order.</summary>
    /// <param name="target">Target after the export.</param>
    /// <param name="entry">Entry name of the Parquet file.</param>
    /// <returns>Column names.</returns>
    private static async Task<string[]> _ReadColumnNames(MemoryExportTarget target, string entry)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        DataField[] fields = reader.Schema.GetDataFields();
        string[] names = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            names[i] = fields[i].Name;
        }
        return names;
    }

    private static async Task<(int[] PacketIds, long[] Timestamps)> _ReadPacketsFile(MemoryExportTarget target, string entry)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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

    private static async Task<long> _CountRows(MemoryExportTarget target, string entry)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        long total = 0;
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            total += rowGroup.RowCount;
        }
        return total;
    }

    private static async Task<int> _FindFieldIdByName(MemoryExportTarget target, string catalogEntry, string fieldName)
    {
        using MemoryStream stream = new(target.Bytes(catalogEntry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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

    private static async Task<string[]> _ReadStringColumn(MemoryExportTarget target, string entry, string columnName)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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

    private static async Task<string?[]> _ReadNullableStringColumn(MemoryExportTarget target, string entry, string columnName)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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

    /// <summary>Copies the numeric ids of <paramref name="protocols"/> into an array, in stack order.</summary>
    /// <param name="protocols">The protocols of a stack.</param>
    /// <returns>The protocol ids.</returns>
    private static int[] _ProtocolIds(ReadOnlyMemory<ProtocolInfo> protocols)
    {
        int[] ids = new int[protocols.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = protocols.Span[i].Id.Value;
        }

        return ids;
    }

    /// <summary>
    /// Fills the shared <c>int[]</c> pool with negative values: rents several arrays of every size class a small column read
    /// can use, fills them with <c>-1</c>, and returns them. Rent and return happen on the calling thread, whose thread-local and
    /// per-core pool slots the next rent of the same thread takes first, so the call has to run right before the read it
    /// disturbs (an <see langword="await"/> in between may continue on another thread).
    /// </summary>
    private static void _FillSharedIntPoolWithNegativeValues()
    {
        System.Buffers.ArrayPool<int> pool = System.Buffers.ArrayPool<int>.Shared;
        int[][] held = new int[4][];
        for (int size = 16; size <= 4096; size *= 2)
        {
            for (int copy = 0; copy < held.Length; copy++)
            {
                held[copy] = pool.Rent(size);
                Array.Fill(held[copy], -1);
            }

            foreach (int[] array in held)
            {
                pool.Return(array);
            }
        }
    }

    /// <summary>
    /// Reads a required (non-nullable) <c>INT32</c> column of every row group. The buffer is a plain <c>int[]</c>, the same read the
    /// other tests use for required columns. The nullable-buffer overload (<c>int?[]</c>) must not be used on a required column:
    /// it rents an uncleared pooled array for the definition levels, a required column has none in the file, and the stale
    /// content decides which rows read as <see langword="null"/> (see <see cref="RequiredColumnReadIsIndependentOfStalePooledMemory"/>).
    /// </summary>
    /// <param name="target">Target after the export.</param>
    /// <param name="entry">Entry name of the Parquet file.</param>
    /// <param name="columnName">Name of the required <c>INT32</c> column.</param>
    /// <param name="beforeEachRead">Optional action that runs right before every row-group read; tests use it to disturb the shared array pool.</param>
    /// <returns>The values of all row groups in file order.</returns>
    private static async Task<int[]> _ReadIntColumn(MemoryExportTarget target, string entry, string columnName, Action? beforeEachRead = null)
    {
        using MemoryStream stream = new(target.Bytes(entry));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        DataField field = reader.Schema.FindDataField(columnName);

        List<int> values = [];
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            long rowCount = rowGroup.RowCount;
            int[] buffer = new int[rowCount];
            beforeEachRead?.Invoke();
            await rowGroup.ReadAsync<int>(field, buffer, null, CancellationToken.None);
            values.AddRange(buffer);
        }

        return values.ToArray();
    }
}
