// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using DescribedFieldCapture = NetworkInspector.Exporters.Tests.Columnar.DescribedFieldCapture;
using Field = NetworkInspector.Core.Fields.Field;
using ValueOrStructureFieldCapture = NetworkInspector.Exporters.Tests.Columnar.ValueOrStructureFieldCapture;

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for the <see cref="DuckDbExporter"/> — validates the builder contract, the base table
/// layout, and reads written data back with a <see cref="DuckDBConnection"/> to verify
/// packet/topology/catalog/field row content.
/// </summary>
[Category("Slow")]
internal sealed class DuckDbExporterTests
{
    #region Offering packets

    /// <summary>Offers a packet that the exporter must accept; a refusal ends the test here instead of leaving the checks below vacuous.</summary>
    /// <param name="exporter">The exporter.</param>
    /// <param name="packet">The packet.</param>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static void _Offer(DuckDbExporter exporter, Packet packet)
    {
        if (!exporter.OnPacket(packet))
        {
            throw new InvalidOperationException("The exporter refused a packet that the test needs written.");
        }
    }

    #endregion

    #region Builder contract and base tables

    /// <summary>Exporting to a path that already holds a database replaces it, so only the rows of the second export remain.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task ExportToExistingPathOverwritesPreviousDatabase()
    {
        using TestDir dir = new("duckdb_overwrite");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter first =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build())
        {
            foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(4))
            {
                first.OnPacket(packet);
            }
            first.OnFinish();
        }

        using (DuckDbExporter second =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build())
        {
            foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(2))
            {
                second.OnPacket(packet);
            }
            second.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        long packetRows = _CountRows(connection, "packets");

        await Assert.That(packetRows).IsEqualTo(2);
    }

    /// <summary>Building an exporter without an output target throws <see cref="InvalidOperationException"/>.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task BuilderRequiresOutput()
    {
        DuckDbExporter.Builder builder =
            DuckDbExporter.CreateBuilder();

        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    /// <summary>An exporter that is finished before it received a packet creates no database file.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task EmptyExportCreatesNoFile()
    {
        using TestDir dir = new("duckdb_empty");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build();

        exporter.OnFinish();

        await Assert.That(File.Exists(outputPath)).IsFalse();
    }

    /// <summary>An export creates the packets table with one row per packet and a catalog that has rows.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ExportCreatesBaseTablesWithPacketRows()
    {
        using TestDir dir = new("duckdb_packets");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build())
        {
            Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
            foreach (Packet packet in packets)
            {
                _Offer(exporter, packet);
            }
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(4);
        }

        await Assert.That(File.Exists(outputPath)).IsTrue();

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();

        long packetRows = _CountRows(connection, "packets");
        await Assert.That(packetRows).IsEqualTo(4);

        long catalogRows = _CountRows(connection, "catalog");
        await Assert.That(catalogRows).IsGreaterThan(0);
    }

    /// <summary>The hierarchy shape writes the topology table with rows.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task TopologyTableHasRowsInTheHierarchyShape()
    {
        using TestDir dir = new("duckdb_topology_on");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithContent(ExportContents.Hierarchy())
                .Build())
        {
            Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
            foreach (Packet packet in packets)
            {
                _Offer(exporter, packet);
            }
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();

        long topologyRows = _CountRows(connection, "topology");

        await Assert.That(topologyRows).IsGreaterThan(0);
    }

    /// <summary>A string field is written as plain text in the value column, one row per packet in packet order.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StringFieldWritesPlainValueColumn()
    {
        using TestDir dir = new("duckdb_string_value");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build())
        {
            Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com", "example.com", "other.example.org");
            foreach (Packet packet in packets)
            {
                _Offer(exporter, packet);
            }
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();

        int dnsNameFieldId = _FindFieldIdByName(connection, "dns.qry.name");

        await Assert.That(dnsNameFieldId).IsGreaterThanOrEqualTo(0)
            .Because("the DNS query packets must produce a dns.qry.name string field");

        string fieldTableName = FormattableString.Invariant($"field_{dnsNameFieldId}");
        string[] values = _ReadStringColumn(connection, fieldTableName, "value");

        await Assert.That(values.Length).IsEqualTo(3);
        await Assert.That(values[0]).IsEqualTo("example.com");
        await Assert.That(values[1]).IsEqualTo("example.com");
        await Assert.That(values[2]).IsEqualTo("other.example.org");
    }

    /// <summary>The exporter accepts packets up to the configured target count and refuses the rest, and only the accepted ones are written.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TargetPacketCountLimitsExport()
    {
        using TestDir dir = new("duckdb_target_count");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithTargetPacketCount(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        int accepted = 0;
        foreach (Packet packet in packets)
        {
            // The exporter refuses every packet after the target count, which ends the feed in a real pipeline.
            if (exporter.OnPacket(packet))
            {
                accepted++;
            }
        }
        exporter.OnFinish();

        await Assert.That(accepted).IsEqualTo(3);
        await Assert.That(exporter.PacketCount).IsEqualTo(3);

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        long packetRows = _CountRows(connection, "packets");

        await Assert.That(packetRows).IsEqualTo(3);
    }

    /// <summary>Cancelling the token makes the exporter refuse the next packet and finish, and the packet written before stays.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task CancellationStopsExport()
    {
        using TestDir dir = new("duckdb_cancel");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using CancellationTokenSource cts = new();

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithCancellationToken(cts.Token)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(20);
        _Offer(exporter, packets[0]);
        await cts.CancelAsync().ConfigureAwait(false);
        bool continued = exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(continued).IsFalse();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    /// <summary>An export that needs several batches writes every packet.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task MultipleFlushesAllRowsPersisted()
    {
        using TestDir dir = new("duckdb_multi_flush");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithMaxPacketsPerBlock(3)
                .Build())
        {
            Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
            foreach (Packet packet in packets)
            {
                _Offer(exporter, packet);
            }
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(10);
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        long packetRows = _CountRows(connection, "packets");

        await Assert.That(packetRows).IsEqualTo(10);
    }

    /// <summary>The background and the synchronous write mode write the same packet rows in the same order and report no errors.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task BackgroundAndSynchronousModesWriteIdenticalTables()
    {
        using TestDir dir = new("duckdb_write_modes");
        string backgroundPath = Path.Combine(dir.DirectoryPath, "background.duckdb");
        string synchronousPath = Path.Combine(dir.DirectoryPath, "synchronous.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(25);

        bool backgroundErrors = _Export(DuckDbExporter.CreateBuilder().ToFile(backgroundPath).WithMaxPacketsPerBlock(10), packets);
        bool synchronousErrors = _Export(
            DuckDbExporter.CreateBuilder().ToFile(synchronousPath).WithMaxPacketsPerBlock(10).WithWriteMode(ExportWriteMode.Synchronous),
            packets);

        using DuckDBConnection background = new(Invariant($"Data Source={backgroundPath}"));
        using DuckDBConnection synchronous = new(Invariant($"Data Source={synchronousPath}"));
        background.Open();
        synchronous.Open();

        // Same rows in the same order, whichever thread wrote them: the packets table is the one that carries every packet.
        string backgroundRows = DuckDbReadBack.Text(
            background, "SELECT string_agg(CAST(packet_id AS VARCHAR) || ':' || CAST(timestamp_ns AS VARCHAR), ',' ORDER BY rowid) FROM packets");
        string synchronousRows = DuckDbReadBack.Text(
            synchronous, "SELECT string_agg(CAST(packet_id AS VARCHAR) || ':' || CAST(timestamp_ns AS VARCHAR), ',' ORDER BY rowid) FROM packets");

        await Assert.That(backgroundErrors).IsFalse();
        await Assert.That(synchronousErrors).IsFalse();
        await Assert.That(synchronousRows).IsEqualTo(backgroundRows);
        await Assert.That(_CountRows(synchronous, "packets")).IsEqualTo(25);
    }

    /// <summary>A batch size of zero or less is refused with <see cref="ArgumentOutOfRangeException"/> whose message names the allowed range.</summary>
    /// <param name="count">The refused value, used for the packet count and the byte size of a batch.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task BatchSizeOutOfRangeNamesTheRange(int count)
    {
        await Assert.That(() => DuckDbExporter.CreateBuilder().WithMaxPacketsPerBlock(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and 2147483647");
        await Assert.That(() => DuckDbExporter.CreateBuilder().WithMaxBlockSize(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and");
    }

    /// <summary>The smallest and the largest batch size, in packets and in bytes, are accepted and the exporter builds.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task BatchSizeAtTheLimitsIsAccepted()
    {
        using TestDir dir = new("duckdb_limits");
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder().ToFile(dir.FilePath("limits.duckdb"))
            .WithMaxPacketsPerBlock(1).WithMaxPacketsPerBlock(int.MaxValue);
        builder = builder.WithMaxBlockSize(1).WithMaxBlockSize(long.MaxValue);

        // Both limits at their ends are accepted and the exporter builds.
        await Assert.That(() => builder.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>A write mode that is not a defined member of the enum is refused with <see cref="ArgumentOutOfRangeException"/>.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task WriteModeMustBeDefined()
    {
        await Assert.That(() => DuckDbExporter.CreateBuilder().WithWriteMode((ExportWriteMode)9)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Calling <c>OnFinish</c> twice does not throw and leaves the exporter finished.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task DoubleFinishDoesNotThrow()
    {
        using TestDir dir = new("duckdb_double_finish");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        _Offer(exporter, packets[0]);

        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region Catalog merge across batches

    /// <summary>
    /// Background mode alternates two batches. One sees the field with a value, the other only as a node without one; the
    /// catalog row with a table must win, because <c>field_{id}</c> exists.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ACatalogRowWithATableIsNotReplacedByAStructureRowOfAnotherBatch()
    {
        using TestDir dir = new("duckdb_catalog_merge");
        string outputPath = dir.FilePath("merge.duckdb");
        using ValueOrStructureFieldCapture capture = ValueOrStructureFieldCapture.Create();

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(outputPath)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(ExportWriteMode.Background)
            .Build())
        {
            foreach (Packet packet in capture.Packets)
            {
                _Offer(exporter, packet);
            }

            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        int fieldId = _FindFieldIdByName(connection, ValueOrStructureFieldCapture.FieldName);
        string tableName = DuckDbReadBack.Text(
            connection, Invariant($"SELECT table_name FROM catalog WHERE name = '{ValueOrStructureFieldCapture.FieldName}'"));

        await Assert.That(fieldId).IsGreaterThanOrEqualTo(0);
        await Assert.That(tableName).IsEqualTo(Invariant($"field_{fieldId}"));
        await Assert.That(_TableExists(connection, tableName)).IsTrue();
        await Assert.That(_CountCatalogRows(connection, ValueOrStructureFieldCapture.FieldName)).IsEqualTo(1);
    }

    /// <summary>The other write order: the structure row comes first, then the batch with the value; the value row replaces it.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ACatalogRowWithATableReplacesAnEarlierStructureRow()
    {
        using TestDir dir = new("duckdb_catalog_merge_reverse");
        string outputPath = dir.FilePath("merge.duckdb");
        using ValueOrStructureFieldCapture capture = ValueOrStructureFieldCapture.Create();

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(outputPath)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(ExportWriteMode.Background)
            .Build())
        {
            _Offer(exporter, capture.Packets[1]);
            _Offer(exporter, capture.Packets[0]);
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        int fieldId = _FindFieldIdByName(connection, ValueOrStructureFieldCapture.FieldName);
        string tableName = DuckDbReadBack.Text(
            connection, Invariant($"SELECT table_name FROM catalog WHERE name = '{ValueOrStructureFieldCapture.FieldName}'"));

        await Assert.That(fieldId).IsGreaterThanOrEqualTo(0);
        await Assert.That(tableName).IsEqualTo(Invariant($"field_{fieldId}"));
        await Assert.That(_CountCatalogRows(connection, ValueOrStructureFieldCapture.FieldName)).IsEqualTo(1);
    }

    #endregion

    #region Late packets and metadata

    /// <summary>A packet after <c>OnFinish</c> is refused and changes neither the count nor the finished database.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketAfterFinishIsRefusedAndChangesNothing()
    {
        using TestDir dir = new("duckdb_late");
        string outputPath = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        bool accepted;

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(outputPath).Build())
        {
            _Offer(exporter, packets[0]);
            exporter.OnFinish();

            accepted = exporter.OnPacket(packets[1]);

            await Assert.That(exporter.WrittenCount).IsEqualTo(1);
            await Assert.That(exporter.HasErrors).IsFalse();
        }

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();

        await Assert.That(accepted).IsFalse();
        await Assert.That(_CountRows(connection, "packets")).IsEqualTo(1);
    }

    /// <summary>An exporter finished before its first packet creates no database, and a later packet does not create one either.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task APacketAfterAnEmptyFinishCreatesNoDatabase()
    {
        using TestDir dir = new("duckdb_late_empty");
        string outputPath = dir.FilePath("capture.duckdb");
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(outputPath).Build();
        exporter.OnFinish();

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(File.Exists(outputPath)).IsFalse();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    /// <summary>The display name and description given to the builder are the ones the exporter reports.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task UiNameAndDescriptionComeFromTheBuilder()
    {
        using TestDir dir = new("duckdb_meta");
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(dir.FilePath("capture.duckdb"))
            .WithUiName("Lab")
            .WithDescription("run 7")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("Lab");
        await Assert.That(exporter.Description).IsEqualTo("run 7");
    }

    /// <summary><see cref="DuckDbExporter.IsFinished"/> is false while the export runs and true once it was finished.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task IsFinishedTurnsTrueOnlyAfterFinish()
    {
        using TestDir dir = new("duckdb_finished");
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(dir.FilePath("capture.duckdb")).Build();
        bool before = exporter.IsFinished;

        exporter.OnFinish();

        await Assert.That(before).IsFalse();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region Field tree round trip

    /// <summary>
    /// The topology of a packet rebuilds its field tree: node ids follow the depth-first pre-order of the in-memory tree and
    /// every node names its parent (-1 at the top level).
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TopologyRebuildsThePacketFieldTreeInPreOrder()
    {
        using TestDir dir = new("duckdb_tree_round_trip");
        string outputPath = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com");

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), packets);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<(int FieldId, int ParentNodeId)> expected = _ExpectedTopology(packets[0]);
        List<(int FieldId, int ParentNodeId)> actual = _ReadTopology(connection, packets[0].Id.Value);

        await Assert.That(expected.Count).IsGreaterThan(5);
        await Assert.That(string.Join(';', actual)).IsEqualTo(string.Join(';', expected));
    }

    /// <summary>
    /// The recursive query of the README ("Rebuild the field tree") runs on a real export and returns every node of the packet
    /// with its catalog name, in node order, indented by depth.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task RecursiveQueryRebuildsTheNamedTree()
    {
        using TestDir dir = new("duckdb_tree_query");
        string outputPath = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("example.com");

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), packets);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<string> lines = _RunTreeQuery(connection, packets[0].Id.Value);
        int expectedNodes = _ExpectedTopology(packets[0]).Count;

        await Assert.That(lines.Count).IsEqualTo(expectedNodes);
        await Assert.That(lines.Count(static line => !line.StartsWith(' '))).IsGreaterThan(0);
        await Assert.That(lines.Exists(static line => line.TrimStart().StartsWith("dns", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.Exists(static line => line.StartsWith("  ", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Every row of a field table joins to the topology node of the same field (packet id and node id).</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryFieldRowPointsAtItsTopologyNode()
    {
        using TestDir dir = new("duckdb_node_join");
        string outputPath = dir.FilePath("capture.duckdb");

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), PacketGenerators.CreateDnsQueryPackets("a.example.com", "b.example.org"));
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<(int FieldId, int FieldType, string TableName)> tables = _ReadValueTables(connection);
        List<string> mismatched = [];
        foreach ((int fieldId, int _, string tableName) in tables)
        {
            long mismatches = DuckDbReadBack.Scalar(
                connection,
                Invariant($"SELECT COUNT(*) FROM {tableName} f LEFT JOIN topology t ON f.packet_id = t.packet_id AND f.node_id = t.node_id ")
                + Invariant($"WHERE t.field_id IS DISTINCT FROM {fieldId}"));
            if (mismatches != 0)
            {
                mismatched.Add(tableName);
            }
        }

        await Assert.That(tables.Count).IsGreaterThan(3);
        await Assert.That(mismatched.Count).IsEqualTo(0);
    }

    /// <summary>The value column of every field table has the DuckDB type the README documents for its field type.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task FieldTablesUseTheDocumentedValueTypes()
    {
        using TestDir dir = new("duckdb_value_types");
        string outputPath = dir.FilePath("capture.duckdb");

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), PacketGenerators.CreateDnsQueryPackets("example.com"));
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<string> wrong = [];
        HashSet<string> seenTypes = [];
        foreach ((int _, int fieldType, string tableName) in _ReadValueTables(connection))
        {
            string expected = _ExpectedColumnType((FieldType)fieldType);
            string actual = DuckDbReadBack.Text(
                connection,
                Invariant($"SELECT data_type FROM information_schema.columns WHERE table_name = '{tableName}' AND column_name = 'value'"));
            seenTypes.Add(actual);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                wrong.Add(FormattableString.Invariant($"{tableName}: expected {expected}, was {actual}"));
            }
        }

        await Assert.That(wrong.Count).IsEqualTo(0).Because(string.Join("; ", wrong));
        await Assert.That(seenTypes.Contains("BLOB")).IsTrue();
        await Assert.That(seenTypes.Contains("VARCHAR")).IsTrue();
        await Assert.That(seenTypes.Count).IsGreaterThanOrEqualTo(3);
    }

    /// <summary>With no optional members in a flat shape no optional column exists, and there is no topology table at all.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task FlatShapeWithoutOptionalMembersOmitsOptionalColumnsAndTopology()
    {
        using TestDir dir = new("duckdb_flags_none");
        string outputPath = dir.FilePath("capture.duckdb");

        _Export(
            DuckDbExporter.CreateBuilder().ToFile(outputPath).WithContent(ExportContents.AllFields()),
            PacketGenerators.CreateEthernetUdpPackets(2));
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<(int FieldId, int FieldType, string TableName)> tables = _ReadValueTables(connection);
        long structureRows = DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL");

        await Assert.That(_ReadColumnNames(connection, "packets")).IsEqualTo("packet_id,timestamp_ns");
        await Assert.That(_TableExists(connection, "topology")).IsFalse();
        await Assert.That(tables.Count).IsGreaterThan(0);
        await Assert.That(_ReadColumnNames(connection, tables[0].TableName)).IsEqualTo("packet_id,occurrence,value");
        await Assert.That(structureRows).IsEqualTo(0);
    }

    /// <summary>With all flags the optional columns exist, and info and frame bytes equal the packet's.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheHierarchyShapeWithInfoAndFramesAddsTheOptionalPacketColumns()
    {
        using TestDir dir = new("duckdb_flags_all");
        string outputPath = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath).WithContent(ExportContents.Full), packets);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        List<(int FieldId, int FieldType, string TableName)> tables = _ReadValueTables(connection);
        string info = DuckDbReadBack.Text(connection, "SELECT info FROM packets ORDER BY packet_id LIMIT 1");
        byte[] frameBytes = _ReadBlob(connection, "SELECT frame_bytes FROM packets ORDER BY packet_id LIMIT 1");

        await Assert.That(_ReadColumnNames(connection, "packets")).IsEqualTo("packet_id,timestamp_ns,info,frame_bytes");
        await Assert.That(_ReadColumnNames(connection, "topology")).IsEqualTo("packet_id,node_id,field_id,parent_node_id,custom_text");
        await Assert.That(tables.Count).IsGreaterThan(0);
        await Assert.That(_ReadColumnNames(connection, tables[0].TableName))
            .IsEqualTo("packet_id,node_id,value,custom_repr,custom_text");
        await Assert.That(info).IsEqualTo(packets[0].Info);
        await Assert.That(frameBytes).IsEquivalentTo(packets[0].Frame.Data.ToArray());
    }

    /// <summary>
    /// Structure nodes (protocols, groups) are named by the catalog without a table, so a reader can join every topology
    /// node to a name without the dissector assemblies.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StructureNodesHaveCatalogRowsWithoutTable()
    {
        using TestDir dir = new("duckdb_structure_catalog");
        string outputPath = dir.FilePath("capture.duckdb");

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath).WithContent(ExportContents.Hierarchy()),
            PacketGenerators.CreateEthernetUdpPackets(2));
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        long unnamed = DuckDbReadBack.Scalar(
            connection, "SELECT COUNT(*) FROM topology t LEFT JOIN catalog c USING (field_id) WHERE c.field_id IS NULL");
        long structureRows = DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL");
        long structureWithType = DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM catalog WHERE table_name IS NULL AND field_type <> 0");
        long unnamedTopLevel = DuckDbReadBack.Scalar(
            connection,
            "SELECT COUNT(*) FROM topology t JOIN catalog c USING (field_id) WHERE t.parent_node_id = -1 AND c.name = CAST(c.field_id AS VARCHAR)");

        await Assert.That(unnamed).IsEqualTo(0);
        await Assert.That(structureRows).IsGreaterThan(0);
        await Assert.That(structureWithType).IsEqualTo(0);
        await Assert.That(unnamedTopLevel).IsEqualTo(0);
    }

    /// <summary>The display line of a node without a value is in <c>topology.custom_text</c>; value nodes have NULL there.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StructureNodeCustomTextIsInTopology()
    {
        using TestDir dir = new("duckdb_structure_text");
        string outputPath = dir.FilePath("capture.duckdb");
        using CustomFieldPacketFactory factory = new();

        _Export(
            DuckDbExporter.CreateBuilder().ToFile(outputPath).WithContent(ExportContents.Hierarchy(customText: true)),
            [factory.Create(7, "abc", containerText: "Test container line")]);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        string text = DuckDbReadBack.Text(connection, "SELECT custom_text FROM topology WHERE custom_text IS NOT NULL");
        long textRows = DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM topology WHERE custom_text IS NOT NULL");
        long totalRows = _CountRows(connection, "topology");

        await Assert.That(text).IsEqualTo("Test container line");
        await Assert.That(textRows).IsEqualTo(1);
        await Assert.That(totalRows).IsGreaterThan(1);
    }

    /// <summary>A bytes value is stored as a BLOB with the exact bytes, and a MAC address as the integer of its six bytes.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task BytesAreStoredAsExactBlobsAndAMacAddressAsAnInteger()
    {
        using TestDir dir = new("duckdb_blobs");
        string outputPath = dir.FilePath("capture.duckdb");
        using CustomFieldPacketFactory factory = new();
        byte[] frameData = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
        Packet[] udpPackets = PacketGenerators.CreateEthernetUdpPackets(1);

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), [factory.Create(1, "x", frameData: frameData)]);
        string udpPath = dir.FilePath("udp.duckdb");
        _Export(DuckDbExporter.CreateBuilder().ToFile(udpPath), udpPackets);

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        byte[] stored = _ReadBlob(
            connection, FormattableString.Invariant($"SELECT value FROM field_{factory.BytesFieldId.Value}"));
        using DuckDBConnection udpConnection = new(Invariant($"Data Source={udpPath}"));
        udpConnection.Open();
        int sourceFieldId = _FindFieldIdByName(udpConnection, "eth.src");
        ulong mac = (ulong)_ScalarObject(udpConnection, FormattableString.Invariant($"SELECT value FROM field_{sourceFieldId}"));

        // The source address of an Ethernet frame is bytes 6 to 11 of the frame; the stored integer is those six bytes, most
        // significant first (a UBIGINT with the upper two bytes zero).
        ulong expectedMac = BinaryPrimitives.ReadUInt64BigEndian([0, 0, .. udpPackets[0].Frame.Data.ToArray()[6..12]]);

        await Assert.That(stored).IsEquivalentTo(frameData);
        await Assert.That(mac).IsEqualTo(expectedMac);
    }

    /// <summary>Two occurrences of one field in a packet are two rows with different node ids.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task RepeatedFieldInOnePacketKeepsDistinctNodeIds()
    {
        using TestDir dir = new("duckdb_repeated");
        string outputPath = dir.FilePath("capture.duckdb");
        using CustomFieldPacketFactory factory = new();

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), [factory.Create(10, null, numberCount: 2)]);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        string table = FormattableString.Invariant($"field_{factory.NumberFieldId.Value}");
        long rows = _CountRows(connection, table);
        long distinctNodes = DuckDbReadBack.Scalar(connection, FormattableString.Invariant($"SELECT COUNT(DISTINCT node_id) FROM {table}"));
        long values = DuckDbReadBack.Scalar(connection, FormattableString.Invariant($"SELECT CAST(SUM(value) AS BIGINT) FROM {table}"));

        await Assert.That(rows).IsEqualTo(2);
        await Assert.That(distinctNodes).IsEqualTo(2);
        await Assert.That(values).IsEqualTo(21);
    }

    #endregion

    #region Catalog description and protocols

    /// <summary>
    /// The <c>catalog</c> table keeps its original column order and appends a nullable <c>description</c> column that
    /// carries <see cref="FieldInfo.Description"/> (text for a described field, NULL otherwise).
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task CatalogTableAppendsDescriptionAsLastColumn()
    {
        using TestDir dir = new("duckdb_catalog_description");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(2);

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), capture.Packets);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        string columns = _ReadColumnNames(connection, "catalog");
        string? described = _ReadDescription(connection, DescribedFieldCapture.DescribedFieldName);
        string? plain = _ReadDescription(connection, DescribedFieldCapture.PlainFieldName);
        long plainRows = _CountCatalogRows(connection, DescribedFieldCapture.PlainFieldName);

        await Assert.That(columns).IsEqualTo("field_id,name,ui_name,field_type,protocol_id,table_name,description");
        await Assert.That(described).IsEqualTo(DescribedFieldCapture.FieldDescription);
        await Assert.That(plainRows).IsEqualTo(1);
        await Assert.That(plain).IsNull();
    }

    /// <summary>The <c>protocols</c> table holds one row per protocol of the parsing stack: id, name and display name.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ProtocolsTableListsStackProtocols()
    {
        using TestDir dir = new("duckdb_protocols");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        ReadOnlyMemory<ProtocolInfo> protocols = capture.Stack.Protocols;

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), capture.Packets);
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        string columns = _ReadColumnNames(connection, "protocols");
        List<(int Id, string Name, string UiName)> rows = _ReadProtocols(connection);

        await Assert.That(columns).IsEqualTo("protocol_id,name,ui_name");
        await Assert.That(rows.Count).IsEqualTo(protocols.Length);
        for (int i = 0; i < rows.Count; i++)
        {
            ProtocolInfo info = protocols.Span[i];
            await Assert.That(rows[i].Id).IsEqualTo(info.Id.Value);
            await Assert.That(rows[i].Name).IsEqualTo(info.Name);
            await Assert.That(rows[i].UiName).IsEqualTo(info.UiName);
        }
        await Assert.That(rows.Exists(static row => row.Name == DescribedFieldCapture.ProtocolName)).IsTrue();
    }

    #endregion

    #region Resource settings

    /// <summary>With neither option set, no <c>SET</c> statement is produced, so DuckDB's own defaults apply.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task SettingsSqlIsEmptyByDefault()
    {
        string sql = DuckDbBulkWriter.BuildSettingsSql(memoryLimitBytes: 0, threads: 0);

        await Assert.That(sql).IsEmpty();
    }

    /// <summary>Each configured value produces exactly its own <c>SET</c> statement, formatted invariantly.</summary>
    /// <param name="memoryLimitBytes">Memory limit in bytes, or 0.</param>
    /// <param name="threads">Thread count, or 0.</param>
    /// <param name="expected">Expected SQL.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(1_073_741_824L, 0, "SET memory_limit = '1073741824B';")]
    [Arguments(0L, 2, "SET threads = 2;")]
    [Arguments(536_870_912L, 3, "SET memory_limit = '536870912B';SET threads = 3;")]
    [Arguments(long.MaxValue, int.MaxValue, "SET memory_limit = '9223372036854775807B';SET threads = 2147483647;")]
    public async Task SettingsSqlEmitsOnlyConfiguredValues(long memoryLimitBytes, int threads, string expected)
    {
        string sql = DuckDbBulkWriter.BuildSettingsSql(memoryLimitBytes, threads);

        await Assert.That(sql).IsEqualTo(expected);
    }

    /// <summary>Negative values never reach SQL.</summary>
    /// <param name="memoryLimitBytes">Memory limit in bytes.</param>
    /// <param name="threads">Thread count.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(-1L, 0)]
    [Arguments(0L, -1)]
    public async Task SettingsSqlRejectsNegativeValues(long memoryLimitBytes, int threads)
    {
        await Assert.That(() => DuckDbBulkWriter.BuildSettingsSql(memoryLimitBytes, threads)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary><see cref="DuckDbExporter.Builder.WithMemoryLimit"/> accepts only values greater than zero.</summary>
    /// <param name="bytes">Rejected value.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(long.MinValue)]
    public async Task WithMemoryLimitRejectsNonPositive(long bytes)
    {
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder();

        await Assert.That(() => builder.WithMemoryLimit(bytes)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary><see cref="DuckDbExporter.Builder.WithThreads"/> accepts only values greater than zero.</summary>
    /// <param name="threads">Rejected value.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task WithThreadsRejectsNonPositive(int threads)
    {
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder();

        await Assert.That(() => builder.WithThreads(threads)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>An export with explicit resource settings succeeds: DuckDB accepts the emitted <c>SET</c> statements.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ExportWithExplicitResourceSettingsSucceeds()
    {
        using TestDir dir = new("duckdb_settings");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder()
            .ToFile(outputPath)
            .WithMemoryLimit(256L * 1024 * 1024)
            .WithThreads(1);

        bool hasErrors = _Export(builder, PacketGenerators.CreateEthernetUdpPackets(3));
        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();

        await Assert.That(hasErrors).IsFalse();
        await Assert.That(_CountRows(connection, "packets")).IsEqualTo(3);
    }

    #endregion

    #region Error tolerance

    /// <summary>
    /// Tolerant mode: when the database file cannot be opened (parent directory missing), the failure is reported
    /// through <see cref="DuckDbExporter.ItemSkipped"/> as an I/O error and the export stops cleanly.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TolerantInvalidPathRaisesItemSkipped()
    {
        using TestDir dir = new("duckdb_tolerant_invalid");
        string outputPath = Path.Combine(dir.DirectoryPath, "missing", "capture.duckdb");
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(outputPath).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorKind> kinds = [];
        exporter.ItemSkipped += (_, args) => kinds.Add(args.Kind);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);
        exporter.OnFinish();

        await Assert.That(accepted).IsFalse();
        await Assert.That(kinds.Count).IsEqualTo(1);
        await Assert.That(kinds[0]).IsEqualTo(ExportErrorKind.IoError);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(File.Exists(outputPath)).IsFalse();
    }

    /// <summary>Strict mode: an unopenable database path aborts the export (<c>false</c>, errors set, finished).</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task StrictInvalidPathAbortsExport()
    {
        using TestDir dir = new("duckdb_strict_invalid");
        string outputPath = Path.Combine(dir.DirectoryPath, "missing", "capture.duckdb");
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(outputPath).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);

        bool first = exporter.OnPacket(packets[0]);
        bool second = exporter.OnPacket(packets[1]);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    #endregion

    #region Exporting and reading back

    /// <summary>Builds the exporter from <paramref name="builder"/>, exports <paramref name="packets"/> and finishes.</summary>
    /// <param name="builder">Configured builder (output file set).</param>
    /// <param name="packets">Packets to export.</param>
    /// <returns>The exporter's <see cref="DuckDbExporter.HasErrors"/> after finishing.</returns>
    private static bool _Export(DuckDbExporter.Builder builder, Packet[] packets)
    {
        using DuckDbExporter exporter = builder.Build();
        foreach (Packet packet in packets)
        {
            // The result is not checked here: the callers also export into an invalid path, where a strict exporter refuses.
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();
        return exporter.HasErrors;
    }

    /// <summary>Returns the column names of <paramref name="tableName"/> in ordinal order, comma-separated.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="tableName">Table name.</param>
    /// <returns>Comma-separated column names.</returns>
    private static string _ReadColumnNames(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT column_name FROM information_schema.columns WHERE table_name = $name ORDER BY ordinal_position;";
        command.Parameters.Add(new DuckDBParameter("name", tableName));
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return string.Join(',', names);
    }

    /// <summary>Returns the catalog description of the field named <paramref name="fieldName"/>, or null.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="fieldName">Field name.</param>
    /// <returns>The description, or <see langword="null"/> when NULL or absent.</returns>
    private static string? _ReadDescription(DuckDBConnection connection, string fieldName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT description FROM catalog WHERE name = $name LIMIT 1;";
        command.Parameters.Add(new DuckDBParameter("name", fieldName));
        object? result = command.ExecuteScalar();
        return result as string;
    }

    /// <summary>Counts catalog rows for one field name, so a NULL description is told apart from a missing row.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="fieldName">Field name.</param>
    /// <returns>Number of rows.</returns>
    private static long _CountCatalogRows(DuckDBConnection connection, string fieldName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM catalog WHERE name = $name;";
        command.Parameters.Add(new DuckDBParameter("name", fieldName));
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the <c>protocols</c> table ordered by <c>protocol_id</c>.</summary>
    /// <param name="connection">Open connection.</param>
    /// <returns>All rows.</returns>
    private static List<(int Id, string Name, string UiName)> _ReadProtocols(DuckDBConnection connection)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT protocol_id, name, ui_name FROM protocols ORDER BY protocol_id;";
        using DuckDBDataReader reader = command.ExecuteReader();
        List<(int Id, string Name, string UiName)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    /// <summary>Returns the first column of the first row of <paramref name="sql"/> as the value DuckDB.NET reads for its type.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">A query that returns one value.</param>
    /// <returns>The value; a UBIGINT is a <see cref="ulong"/>, a UINTEGER a <see cref="uint"/>.</returns>
    private static object _ScalarObject(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!; // Invariant: the callers query a table that holds a row.
    }

    /// <summary>Reads the BLOB in the first column of the first row of <paramref name="sql"/>.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">A query that returns one BLOB.</param>
    /// <returns>The bytes.</returns>
    private static byte[] _ReadBlob(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        reader.Read();
        using Stream stream = reader.GetStream(0);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Lists the field tables the catalog names (rows with a table name), ordered by field id.</summary>
    /// <param name="connection">Open connection.</param>
    /// <returns>Field id, field type, and table name of each.</returns>
    private static List<(int FieldId, int FieldType, string TableName)> _ReadValueTables(DuckDBConnection connection)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT field_id, field_type, table_name FROM catalog WHERE table_name IS NOT NULL ORDER BY field_id;";
        using DuckDBDataReader reader = command.ExecuteReader();
        List<(int FieldId, int FieldType, string TableName)> tables = [];
        while (reader.Read())
        {
            tables.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        }
        return tables;
    }

    /// <summary>Runs the recursive "Rebuild the field tree" query of the README for one packet.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="packetId">Packet id.</param>
    /// <returns>One indented field name per node, in node order.</returns>
    /// <remarks>Sync: NetworkInspector.Exporters.DuckDb/README.md § Rebuild the field tree (the recursive query is the one the README shows).</remarks>
    private static List<string> _RunTreeQuery(DuckDBConnection connection, int packetId)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant(
            $"""
            WITH RECURSIVE tree AS (
                SELECT t.packet_id, t.node_id, t.field_id, 0 AS depth
                FROM topology t WHERE t.packet_id = {packetId} AND t.parent_node_id = -1
                UNION ALL
                SELECT c.packet_id, c.node_id, c.field_id, p.depth + 1
                FROM topology c JOIN tree p ON c.packet_id = p.packet_id AND c.parent_node_id = p.node_id
            )
            SELECT repeat('  ', depth) || c.name AS field, tree.node_id
            FROM tree JOIN catalog c USING (field_id)
            ORDER BY tree.node_id;
            """);
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> lines = [];
        while (reader.Read())
        {
            lines.Add(reader.GetString(0));
        }
        return lines;
    }

    /// <summary>Reads the topology of one packet in node order: the field id and the parent node id of each node.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="packetId">Packet id.</param>
    /// <returns>One entry per node, ordered by node id.</returns>
    private static List<(int FieldId, int ParentNodeId)> _ReadTopology(DuckDBConnection connection, int packetId)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT field_id, parent_node_id FROM topology WHERE packet_id = {packetId} ORDER BY node_id;");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<(int FieldId, int ParentNodeId)> nodes = [];
        while (reader.Read())
        {
            nodes.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }
        return nodes;
    }

    /// <summary>
    /// Walks the packet's field tree in depth-first pre-order, the way the exporter numbers its nodes, and lists the field id
    /// and the parent node id of each node (-1 at the top level).
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>One entry per node, in node-id order.</returns>
    private static List<(int FieldId, int ParentNodeId)> _ExpectedTopology(Packet packet)
    {
        List<(int FieldId, int ParentNodeId)> rows = [];
        int nodeCounter = 0;
        _CollectPreOrder(packet.RootField(), -1, ref nodeCounter, rows);
        return rows;
    }

    /// <summary>Appends the children of a field to the list in depth-first pre-order, numbering the nodes the way the exporter does.</summary>
    /// <param name="parent">The field whose children are visited.</param>
    /// <param name="parentNodeId">The node id of <paramref name="parent"/>, or -1 for the root of the packet.</param>
    /// <param name="nodeCounter">The next free node id; it advances for every visited child.</param>
    /// <param name="rows">The list that receives the field id and the parent node id of each visited node.</param>
    private static void _CollectPreOrder(
        Field parent, int parentNodeId, ref int nodeCounter, List<(int FieldId, int ParentNodeId)> rows)
    {
        foreach (Field child in parent.Children(materialize: true))
        {
            int nodeId = nodeCounter++;
            rows.Add((child.FieldId.Value, parentNodeId));
            _CollectPreOrder(child, nodeId, ref nodeCounter, rows);
        }
    }

    /// <summary>The DuckDB type the README documents for the value column of a field type.</summary>
    /// <param name="fieldType">The catalog's field type.</param>
    /// <returns>The <c>information_schema</c> data type name.</returns>
    /// <remarks>Sync: NetworkInspector.Exporters.DuckDb/README.md § Table Layout (the value column types of the field tables).</remarks>
    private static string _ExpectedColumnType(FieldType fieldType) => fieldType switch
    {
        FieldType.Bool => "BOOLEAN",
        FieldType.I64 => "BIGINT",
        FieldType.U64 => "UBIGINT",
        FieldType.F64 => "DOUBLE",
        FieldType.Timestamp => "BIGINT",
        FieldType.String => "VARCHAR",
        FieldType.IPv4Address => "UINTEGER",
        FieldType.MacAddress => "UBIGINT",
        FieldType.Eui64 => "UBIGINT",
        FieldType.IPv6Address => "UHUGEINT",
        FieldType.Uuid => "UUID",
        _ => "BLOB",
    };

    /// <summary>Whether the database has a table of the name.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="tableName">Table name.</param>
    /// <returns><see langword="true"/> when the table exists.</returns>
    private static bool _TableExists(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = $name;";
        command.Parameters.Add(new DuckDBParameter("name", tableName));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Counts the rows of a table.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="tableName">Table name.</param>
    /// <returns>The number of rows.</returns>
    private static long _CountRows(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"SELECT COUNT(*) FROM {tableName};");
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Looks up the id of a field in the catalog by its registered name.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="fieldName">Field name, for example <c>dns.qry.name</c>.</param>
    /// <returns>The field id, or -1 when the catalog has no row of that name.</returns>
    private static int _FindFieldIdByName(DuckDBConnection connection, string fieldName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT field_id FROM catalog WHERE name = $name LIMIT 1;";
        command.Parameters.Add(new DuckDBParameter("name", fieldName));
        object? result = command.ExecuteScalar();
        if (result is null)
        {
            return -1;
        }
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one text column of a field table in row-key order, leaving out NULL values.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="tableName">The field table name.</param>
    /// <param name="columnName">The column to read.</param>
    /// <returns>The non-NULL values ordered by packet id and node id.</returns>
    private static string[] _ReadStringColumn(DuckDBConnection connection, string tableName, string columnName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        // Row order is not defined by SQL without ORDER BY, so the read is ordered by the row key.
        command.CommandText = FormattableString.Invariant($"SELECT {columnName} FROM {tableName} ORDER BY packet_id, node_id;");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }
        return values.ToArray();
    }

    #endregion
}
