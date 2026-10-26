// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for <see cref="DuckDbBulkWriter"/>: how the database path reaches the connection string.
/// </summary>
internal sealed class DuckDbBulkWriterTests
{
    [Test]
    [Arguments("a;b=c.duckdb")]
    [Arguments("it's.duckdb")]
    [Arguments("x;access_mode=READ_ONLY.duckdb")]
    public async Task PathWithConnectionStringCharactersIsOpenedAsOneFile(string fileName)
    {
        using TestDir dir = new("duckdb_path");
        string path = Path.Combine(dir.DirectoryPath, fileName);

        using (DuckDbBulkWriter writer = new(path, ColumnarDetailFlags.All, [], 0, 0))
        {
            writer.Complete();
        }

        // A path that was cut at ';' would have created a different file and applied the rest as connection options.
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(Directory.GetFiles(dir.DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Count(file => !file.EndsWith(".wal", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task FieldTablesOfARolledBackBatchAreCreatedAgainByTheNextBatch()
    {
        using TestDir dir = new("duckdb_rollback");
        string path = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using ColumnarPacketBatch batch = new(ColumnarDetailFlags.All, 100, 1024 * 1024, true);
        foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(2))
        {
            batch.AddPacket(packet);
        }

        // The last field of the batch gets a table with the wrong shape, so the batch fails after the tables of the
        // earlier fields were created inside its transaction.
        int lastFieldId = batch.FieldBags.Keys.Last();
        using DuckDbBulkWriter writer = new(path, ColumnarDetailFlags.All, [], 0, 0);
        DuckDBConnectionStringBuilder connectionString = new() { DataSource = path };
        using DuckDBConnection other = new(connectionString.ConnectionString);
        other.Open();
        _Execute(other, $"CREATE TABLE field_{lastFieldId}(unrelated INTEGER)");

        Exception? firstFailure = null;
        try
        {
            writer.WriteBatch(batch);
        }
        catch (Exception ex)
        {
            firstFailure = ex;
        }

        _Execute(other, $"DROP TABLE field_{lastFieldId}");
        writer.WriteBatch(batch);

        await Assert.That(firstFailure).IsNotNull();
        await Assert.That(_Count(other, "packets")).IsEqualTo(2L);
    }

    private static void _Execute(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long _Count(DuckDBConnection connection, string table)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
