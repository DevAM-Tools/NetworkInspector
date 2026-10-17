// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using DescribedFieldCapture = NetworkInspector.Exporters.Tests.Columnar.DescribedFieldCapture;

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

    #region Catalog description and protocols

    /// <summary>
    /// The <c>catalog</c> table keeps its original column order and appends a nullable <c>description</c> column that
    /// carries <see cref="FieldInfo.Description"/> (text for a described field, NULL otherwise).
    /// </summary>
    [Test]
    public async Task CatalogTableAppendsDescriptionAsLastColumn()
    {
        using TestDir dir = new("duckdb_catalog_description");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(2);

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), capture.Packets);
        using DuckDBConnection connection = new($"Data Source={outputPath}");
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
    [Test]
    public async Task ProtocolsTableListsStackProtocols()
    {
        using TestDir dir = new("duckdb_protocols");
        string outputPath = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        ReadOnlyMemory<ProtocolInfo> protocols = capture.Stack.Protocols;

        _Export(DuckDbExporter.CreateBuilder().ToFile(outputPath), capture.Packets);
        using DuckDBConnection connection = new($"Data Source={outputPath}");
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
    [Test]
    [Arguments(-1L, 0)]
    [Arguments(0L, -1)]
    public async Task SettingsSqlRejectsNegativeValues(long memoryLimitBytes, int threads)
    {
        await Assert.That(() => DuckDbBulkWriter.BuildSettingsSql(memoryLimitBytes, threads)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary><see cref="DuckDbExporter.Builder.WithMemoryLimit"/> accepts only values greater than zero.</summary>
    /// <param name="bytes">Rejected value.</param>
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
        using DuckDBConnection connection = new($"Data Source={outputPath}");
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

    // ========================================================================
    // DuckDB read-back helpers
    // ========================================================================

    /// <summary>Builds the exporter from <paramref name="builder"/>, exports <paramref name="packets"/> and finishes.</summary>
    /// <param name="builder">Configured builder (output file set).</param>
    /// <param name="packets">Packets to export.</param>
    /// <returns>The exporter's <see cref="DuckDbExporter.HasErrors"/> after finishing.</returns>
    private static bool _Export(DuckDbExporter.Builder builder, Packet[] packets)
    {
        using DuckDbExporter exporter = builder.Build();
        foreach (Packet packet in packets)
        {
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
