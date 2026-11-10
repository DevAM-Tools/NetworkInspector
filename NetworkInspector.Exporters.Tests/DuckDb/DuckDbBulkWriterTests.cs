// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for <see cref="DuckDbBulkWriter"/>: how the database path reaches the connection string.
/// </summary>
[Category("Slow")]
internal sealed class DuckDbBulkWriterTests
{
    #region Database file and rollback

    /// <summary>
    /// A database path that contains connection string characters (a semicolon or an apostrophe) is opened as exactly one file;
    /// no part of the path is read as a connection option.
    /// </summary>
    /// <param name="fileName">The file name, which contains connection string characters.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments("a;b=c.duckdb")]
    [Arguments("it's.duckdb")]
    [Arguments("x;access_mode=READ_ONLY.duckdb")]
    public async Task PathWithConnectionStringCharactersIsOpenedAsOneFile(string fileName)
    {
        using TestDir dir = new("duckdb_path");
        string path = Path.Combine(dir.DirectoryPath, fileName);

        using (DuckDbBulkWriter writer = new(path, ExportContents.Full, [], 0, 0, DuckDbStorageVersions.DefaultSettingValue))
        {
            writer.Complete();
        }

        // A path that was cut at ';' would have created a different file and applied the rest as connection options.
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(Directory.GetFiles(dir.DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Count(file => !file.EndsWith(".wal", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// A batch that fails after some field tables were created inside its transaction is rolled back completely, so the next
    /// batch creates those tables again and all packets of the batch arrive.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task FieldTablesOfARolledBackBatchAreCreatedAgainByTheNextBatch()
    {
        using TestDir dir = new("duckdb_rollback");
        string path = Path.Combine(dir.DirectoryPath, "capture.duckdb");
        using ColumnarPacketBatch batch = new(ExportContents.Full, 100, 1024 * 1024, true);
        foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(2))
        {
            batch.AddPacket(packet);
        }

        // The last field of the batch gets a table with the wrong shape, so the batch fails after the tables of the
        // earlier fields were created inside its transaction.
        int lastFieldId = batch.FieldBags.Keys.Last();
        using DuckDbBulkWriter writer = new(path, ExportContents.Full, [], 0, 0, DuckDbStorageVersions.DefaultSettingValue);
        DuckDBConnectionStringBuilder connectionString = new() { DataSource = path };
        using DuckDBConnection other = new(connectionString.ConnectionString);
        other.Open();
        DuckDbReadBack.Execute(other, Invariant($"CREATE TABLE field_{lastFieldId}(unrelated INTEGER)"));

        // The failure is the appender's refusal of the wrong table shape (one column instead of the writer's columns), not any other exception.
        IndexOutOfRangeException firstFailure = Assert.Throws<IndexOutOfRangeException>(() => writer.WriteBatch(batch));

        DuckDbReadBack.Execute(other, Invariant($"DROP TABLE field_{lastFieldId}"));
        writer.WriteBatch(batch);

        await Assert.That(firstFailure.Message).Contains(Invariant($"The table field_{lastFieldId} has 1 columns"));
        await Assert.That(DuckDbReadBack.Count(other, "packets")).IsEqualTo(2L);
    }

    #endregion

    #region Empty blob values

    /// <summary>
    /// DuckDB.NET appends an empty span as NULL but an empty array as an empty BLOB. The writer therefore appends an empty
    /// payload as an empty array, so an empty bytes value is not confused with an absent one. If DuckDB.NET ever fixes the
    /// span overload, this test fails and the workaround in <c>DuckDbBulkWriter</c> can go.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task DuckDbNetAppendsAnEmptySpanAsNullAndAnEmptyArrayAsEmptyBlob()
    {
        using TestDir dir = new("duckdb_empty_blob");
        DuckDBConnectionStringBuilder connectionString = new() { DataSource = Path.Combine(dir.DirectoryPath, "blob.duckdb") };
        using DuckDBConnection connection = new(connectionString.ConnectionString);
        connection.Open();
        DuckDbReadBack.Execute(connection, "CREATE TABLE blobs(b BLOB)");

        _AppendBlobs(connection);

        await Assert.That(DuckDbReadBack.Count(connection, "blobs")).IsEqualTo(3L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COUNT(*) FROM blobs WHERE b IS NULL")).IsEqualTo(1L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT COALESCE(MIN(rowid), -1) FROM blobs WHERE b IS NULL")).IsEqualTo(0L);
        await Assert.That(DuckDbReadBack.Scalar(connection, "SELECT CAST(SUM(octet_length(b)) AS BIGINT) FROM blobs")).IsEqualTo(3L);
    }

    /// <summary>Appends an empty span, an empty array and a three-byte span; spans cannot be used in an async test method.</summary>
    /// <param name="connection">Open connection with the table <c>blobs(b BLOB)</c>.</param>
    private static void _AppendBlobs(DuckDBConnection connection)
    {
        using DuckDBAppender appender = connection.CreateAppender("blobs");
        appender.CreateRow().AppendValue(Span<byte>.Empty).EndRow();
        appender.CreateRow().AppendValue(Array.Empty<byte>()).EndRow();
        appender.CreateRow().AppendValue(new byte[] { 1, 2, 3 }.AsSpan()).EndRow();
    }

    #endregion
}
