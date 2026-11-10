// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The layout of the buffer records that the matrix tests cannot see in the rows alone: the column types of the DuckDB table and of
/// the Parquet file, what an export writes when no packet has an additional buffer, and the shape of the JSON records.
/// </summary>
internal sealed class BuffersLayoutTests
{
    #region Captures without buffers and column descriptions

    /// <summary>The frames of a capture whose packets have no additional buffer.</summary>
    /// <returns>Two Ethernet, IPv4, UDP frames.</returns>
    private static List<Frame> _PlainFrames() =>
    [
        TestHarness.CreateFrame(0, 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(18)),
        TestHarness.CreateFrame(1, 2_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(32)),
    ];

    /// <summary>The (name, type) pairs of the columns of a relation, in order.</summary>
    /// <param name="connection">The open DuckDB connection.</param>
    /// <param name="relation">The SQL table expression to describe.</param>
    /// <returns>The column name and the DuckDB type name of each column.</returns>
    private static List<(string Name, string Type)> _Describe(DuckDBConnection connection, string relation)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"DESCRIBE SELECT * FROM {relation}");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<(string, string)> columns = [];
        while (reader.Read())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    #endregion

    #region DuckDB and Parquet

    /// <summary>Proves that the DuckDB buffer table has exactly the columns packet id, buffer index, and bytes with their documented types.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheDuckDbBufferTableHasTheDocumentedColumnTypes()
    {
        using TestDir dir = new("buffers_layout_duckdb");
        BufferRun run = BufferExportScenarios.Run(BufferScenario.DuckDbHierarchy, BufferTrace.Create(), BufferMembers.Buffers, dir);

        using DuckDBConnection connection = new(Invariant($"Data Source={run.Path}"));
        connection.Open();
        List<(string Name, string Type)> columns = _Describe(connection, "packet_buffers");

        await Assert.That(columns.Count).IsEqualTo(3);
        await Assert.That(columns[0]).IsEqualTo(("packet_id", "INTEGER"));
        await Assert.That(columns[1]).IsEqualTo(("buffer_index", "UTINYINT"));
        await Assert.That(columns[2]).IsEqualTo(("bytes", "BLOB"));
    }

    /// <summary>Proves that a DuckDB export with the buffers member on creates the buffer table, empty, when no packet has a buffer.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheDuckDbBufferTableExistsButIsEmptyWhenNoPacketHasABuffer()
    {
        using TestDir dir = new("buffers_layout_duckdb_none");

        BufferRun run = BufferExportScenarios.Run(BufferScenario.DuckDbHierarchy, _PlainFrames(), BufferMembers.Buffers, dir);

        // The member is on, so the table is part of the layout (a reader sees "none were present", not a missing table).
        List<BufferRow>? rows = BufferReaders.ReadDuckDb(run.Path);
        await Assert.That(rows).IsNotNull();
        await Assert.That(rows!.Count).IsEqualTo(0);
    }

    /// <summary>Proves that the Parquet buffer file has exactly the columns packet id, buffer index, and bytes with their documented types.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheParquetBufferFileHasTheDocumentedColumnTypes()
    {
        using TestDir dir = new("buffers_layout_parquet");
        BufferRun run = BufferExportScenarios.Run(BufferScenario.ParquetHierarchy, BufferTrace.Create(), BufferMembers.Buffers, dir);

        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        List<(string Name, string Type)> columns = _Describe(connection, Invariant($"read_parquet('{Path.Combine(run.Path, "packet_buffers.parquet")}')"));

        await Assert.That(columns.Count).IsEqualTo(3);
        await Assert.That(columns[0]).IsEqualTo(("packet_id", "INTEGER"));
        await Assert.That(columns[1]).IsEqualTo(("buffer_index", "UTINYINT"));
        await Assert.That(columns[2]).IsEqualTo(("bytes", "BLOB"));
    }

    /// <summary>Proves that a Parquet export writes no buffer file when no packet has a buffer, while the packets file exists.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheParquetDatasetHasNoBufferFileWhenNoPacketHasABuffer()
    {
        using TestDir dir = new("buffers_layout_parquet_none");

        BufferRun run = BufferExportScenarios.Run(BufferScenario.ParquetHierarchy, _PlainFrames(), BufferMembers.Buffers, dir);

        await Assert.That(File.Exists(Path.Combine(run.Path, "packet_buffers.parquet"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(run.Path, "packets.parquet"))).IsTrue();
    }

    /// <summary>
    /// A re-export to the same directory removes a buffer file of the earlier export: a dataset of an export without buffers must
    /// not keep the old file next to the new tables.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Category("Slow")]
    public async Task ARepeatedParquetExportWithoutBuffersRemovesTheBufferFileOfTheEarlierExport()
    {
        using TestDir dir = new("buffers_layout_parquet_again");
        string path = dir.FilePath("dataset");
        for (int round = 0; round < 2; round++)
        {
            using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToDirectory(path).WithWriteMode(ExportWriteMode.Synchronous)
                .WithContent(new ExportContent { Buffers = round == 0 }).Build();
            _ = TreeFreeFeed.Run(exporter, BufferTrace.Create());
            exporter.OnFinish();

            await Assert.That(File.Exists(Path.Combine(path, "packet_buffers.parquet"))).IsEqualTo(round == 0);
        }
    }

    #endregion

    #region JSON

    /// <summary>
    /// Proves that the JSON buffers key is an array of index and Base64 records, and that a packet without buffers has no such key.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheJsonBuffersKeyIsAnArrayOfIndexAndBase64RecordsAndIsMissingForAPacketWithoutBuffers()
    {
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Array)
            .WithContent(new ExportContent { Buffers = true }).Build())
        {
            _ = exporter.OnPacket(factory.CreatePacket());
            _ = exporter.OnPacket(factory.CreatePacket([0xFB, 0xFF], []));
            exporter.OnFinish();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        JsonElement without = document.RootElement[0];
        JsonElement with = document.RootElement[1];

        await Assert.That(without.TryGetProperty("buffers", out _)).IsFalse();
        await Assert.That(with.GetProperty("buffers").GetArrayLength()).IsEqualTo(2);

        // Standard Base64 with padding ("+/" and "="): the same encoding as the frame bytes.
        await Assert.That(with.GetProperty("buffers")[0].GetProperty("index").GetInt32()).IsEqualTo(1);
        await Assert.That(with.GetProperty("buffers")[0].GetProperty("bytes").GetString()).IsEqualTo("+/8=");
        await Assert.That(with.GetProperty("buffers")[1].GetProperty("index").GetInt32()).IsEqualTo(2);
        await Assert.That(with.GetProperty("buffers")[1].GetProperty("bytes").GetString()).IsEqualTo(string.Empty);
    }

    #endregion
}
