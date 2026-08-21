// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for the <see cref="DuckDbExporter"/> — validates the builder contract, the base table
/// layout, and reads written data back with a <see cref="DuckDBConnection"/> to verify
/// packet/topology/catalog/field row content and string dictionary encoding.
/// </summary>
internal sealed class DuckDbExporterTests
{
    [Test]
    public async Task Export_ToExistingPath_OverwritesPreviousDatabase()
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

        using DuckDBConnection connection = new($"Data Source={outputPath}");
        connection.Open();
        long packetRows = _CountRows(connection, "packets");
        await Assert.That(packetRows).IsEqualTo(2);
    }

    [Test]
    public async Task Builder_RequiresOutput()
    {
        DuckDbExporter.Builder builder =
            DuckDbExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyExport_CreatesNoFile()
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

    [Test]
    public async Task Export_CreatesBaseTablesWithPacketRows()
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
                exporter.OnPacket(packet);
            }
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(4);
        }

        await Assert.That(File.Exists(outputPath)).IsTrue();

        using DuckDBConnection connection = new($"Data Source={outputPath}");
        connection.Open();

        long packetRows = _CountRows(connection, "packets");
        await Assert.That(packetRows).IsEqualTo(4);

        long catalogRows = _CountRows(connection, "catalog");
        await Assert.That(catalogRows).IsGreaterThan(0);
    }

    [Test]
    public async Task TopologyTable_HasRows_WhenIncludeTopologySet()
    {
        using TestDir dir = new("duckdb_topology_on");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using (DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithDetailFlags(ColumnarDetailFlags.IncludeTopology)
                .Build())
        {
            Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
            foreach (Packet packet in packets)
            {
                exporter.OnPacket(packet);
            }
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new($"Data Source={outputPath}");
        connection.Open();

        long topologyRows = _CountRows(connection, "topology");
        await Assert.That(topologyRows).IsGreaterThan(0);
    }

    [Test]
    public async Task StringField_WritesPlainValueColumn()
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
                exporter.OnPacket(packet);
            }
            exporter.OnFinish();
        }

        using DuckDBConnection connection = new($"Data Source={outputPath}");
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
        await Assert.That(_TableExists(connection, FormattableString.Invariant($"field_{dnsNameFieldId}_value_dict"))).IsFalse();
    }

    [Test]
    public async Task TargetPacketCount_LimitsExport()
    {
        using TestDir dir = new("duckdb_target_count");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .WithTargetPacketCount(3)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(3);

        using DuckDBConnection connection = new($"Data Source={outputPath}");
        connection.Open();
        long packetRows = _CountRows(connection, "packets");
        await Assert.That(packetRows).IsEqualTo(3);
    }

    [Test]
    public async Task Cancellation_StopsExport()
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
        exporter.OnPacket(packets[0]);
        await cts.CancelAsync().ConfigureAwait(false);
        bool continued = exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(continued).IsFalse();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleFlushes_AllRowsPersisted()
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
                exporter.OnPacket(packet);
            }
            exporter.OnFinish();

            await Assert.That(exporter.PacketCount).IsEqualTo(10);
        }

        using DuckDBConnection connection = new($"Data Source={outputPath}");
        connection.Open();
        long packetRows = _CountRows(connection, "packets");
        await Assert.That(packetRows).IsEqualTo(10);
    }

    [Test]
    public async Task DoubleFinish_DoesNotThrow()
    {
        using TestDir dir = new("duckdb_double_finish");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");

        using DuckDbExporter exporter =
            DuckDbExporter.CreateBuilder()
                .ToFile(outputPath)
                .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);

        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // DuckDB read-back helpers
    // ========================================================================

    private static long _CountRows(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"SELECT COUNT(*) FROM {tableName};");
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static bool _TableExists(DuckDBConnection connection, string tableName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = $name;";
        command.Parameters.Add(new DuckDBParameter("name", tableName));
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

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

    private static string[] _ReadStringColumn(DuckDBConnection connection, string tableName, string columnName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"SELECT {columnName} FROM {tableName};");
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

    private static int?[] _ReadNullableIntColumn(DuckDBConnection connection, string tableName, string columnName)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($"SELECT {columnName} FROM {tableName};");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<int?> values = [];
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        }
        return values.ToArray();
    }
}
