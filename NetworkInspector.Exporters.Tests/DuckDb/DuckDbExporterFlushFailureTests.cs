// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for a batch whose write fails inside the database: its transaction is rolled back, the batch is dropped, and the
/// counters say which packets were lost. A second connection creates the table of one field with a primary key and the row
/// of one chosen packet, so a batch that contains this packet fails on a duplicate key while every other batch is written.
/// The writer thread reports a failure through the caller's thread, so each test reads the counters at a point where the
/// failure is certain to be reported (a later packet, or the end of the export).
/// </summary>
[Category("Slow")]
internal sealed class DuckDbExporterFlushFailureTests
{
    private static string _ConnectionString(string path)
    {
        DuckDBConnectionStringBuilder builder = new() { DataSource = path };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Creates the table of one numeric field with the layout the exporter expects (no optional columns), a primary key on
    /// (packet id, node id), and the row of <paramref name="rejected"/>. Writing a batch that contains this packet fails
    /// inside the batch's transaction on the duplicate key, after its packet rows were appended.
    /// </summary>
    /// <param name="connection">Second connection to the database file.</param>
    /// <param name="rejected">The packet whose batch must fail; a packet of the same kind as the exported ones.</param>
    private static void _BlockPacket(DuckDBConnection connection, Packet rejected)
    {
        using ColumnarPacketBatch probe = new(ExportContents.Hierarchy(), 10, long.MaxValue, true);
        probe.AddPacket(rejected);
        foreach (KeyValuePair<int, FieldColumnBag> bag in probe.FieldBags)
        {
            if (bag.Value.FieldType is FieldType.U64 or FieldType.I64)
            {
                string valueType = bag.Value.FieldType == FieldType.U64
                    ? "UBIGINT"
                    : "BIGINT";
                DuckDbReadBack.Execute(
                    connection,
                    Invariant($"CREATE TABLE field_{bag.Key}(packet_id INTEGER, node_id INTEGER, value {valueType}, PRIMARY KEY (packet_id, node_id))"));
                DuckDbReadBack.Execute(connection, Invariant($"INSERT INTO field_{bag.Key} VALUES ({rejected.Id.Value}, {bag.Value.Positions[0]}, 0)"));
                return;
            }
        }

        throw new InvalidOperationException("The packet has no numeric field to block.");
    }

    private static DuckDbExporter.Builder _Builder(string path, int maxPacketsPerBlock, ExportWriteMode mode) =>
        DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithMaxPacketsPerBlock(maxPacketsPerBlock)
            .WithWriteMode(mode)
            .WithContent(ExportContents.Hierarchy());

    /// <summary>
    /// Three packets per batch: the first batch contains the blocked packet and fails, the second does not. The failed batch is
    /// dropped and counted as skipped, the export goes on, and the second batch is written.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task FailedBatchIsDroppedCountedAsSkippedAndTheExportContinues(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_flush_failure");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(6);
        int written;
        int skipped;
        int errors;
        int events = 0;
        List<string> messages = [];

        using (DuckDbExporter exporter = _Builder(path, 3, mode).Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            exporter.ItemSkipped += (_, args) =>
            {
                events++;
                messages.Add(args.Message);
            };

            // The first packet creates the database; then the second packet is blocked from a second connection, before the
            // first batch is handed to the writer.
            exporter.OnPacket(packets[0]);
            using DuckDBConnection other = new(_ConnectionString(path));
            other.Open();
            _BlockPacket(other, packets[1]);

            for (int i = 1; i < packets.Length; i++)
            {
                exporter.OnPacket(packets[i]);
            }

            exporter.OnFinish();
            written = exporter.WrittenCount;
            skipped = exporter.SkippedCount;
            errors = exporter.ErrorCount;
        }

        using DuckDBConnection check = new(_ConnectionString(path));
        check.Open();
        long packetRows = DuckDbReadBack.Count(check, "packets");

        // Only the second batch reached the database; the failed first batch left no rows and was not written again.
        await Assert.That(written).IsEqualTo(3).Because(string.Join(" | ", messages));
        await Assert.That(skipped).IsEqualTo(3);
        await Assert.That(errors).IsEqualTo(1);
        await Assert.That(events).IsEqualTo(1);
        await Assert.That(packetRows).IsEqualTo(3L);
        await Assert.That(messages[0]).Contains("3 packets lost");
    }

    /// <summary>
    /// Two packets per batch: the first batch fails. The hand-off of the second batch waits until the writer returned the
    /// first, so the failure is certainly queued when the fourth packet after the blocked one returns. In tolerant mode the
    /// packet after that (or an earlier one, when the writer was faster) reports it: the packets leave the written count and
    /// exactly one event is raised.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task TolerantModeReportsTheFailedBatchAtALaterPacket(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_flush_failure_tolerant");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        int events = 0;
        using DuckDbExporter exporter = _Builder(path, 2, mode).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        exporter.ItemSkipped += (_, _) => events++;
        exporter.OnPacket(packets[0]);
        using DuckDBConnection other = new(_ConnectionString(path));
        other.Open();
        _BlockPacket(other, packets[1]);

        // Batches: [0, 1] fails, [2, 3] is written, and packet 4 starts the next one.
        exporter.OnPacket(packets[1]);
        exporter.OnPacket(packets[2]);
        exporter.OnPacket(packets[3]);
        bool accepted = exporter.OnPacket(packets[4]);

        await Assert.That(accepted).IsTrue();
        await Assert.That(events).IsEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsEqualTo(2);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.WrittenCount).IsEqualTo(3);
    }

    /// <summary>
    /// Strict mode: a packet after the failed batch is refused, and the error is latched without an event. The refusal comes at
    /// the third packet at the earliest and at the fifth at the latest (see the tolerant test), so the loop is bounded.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task StrictModeStopsAtALaterPacketAfterAFailedBatch(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_flush_failure_strict");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        int events = 0;
        using DuckDbExporter exporter = _Builder(path, 2, mode).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        exporter.ItemSkipped += (_, _) => events++;
        exporter.OnPacket(packets[0]);
        using DuckDBConnection other = new(_ConnectionString(path));
        other.Open();
        _BlockPacket(other, packets[1]);

        bool refused = false;
        for (int i = 1; i < packets.Length; i++)
        {
            if (!exporter.OnPacket(packets[i]))
            {
                refused = true;
                break;
            }
        }

        await Assert.That(refused).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.SkippedCount).IsEqualTo(2);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events).IsEqualTo(0);
    }

    /// <summary>
    /// The last batch fails on its own: the batches written before it still get their catalog, the counters match the
    /// database, and exactly one error is raised for the lost batch.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task FailedFinalBatchStillWritesTheCatalog(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_flush_failure_final");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        int events = 0;
        List<string> messages = [];
        int written;
        int skipped;
        int errors;
        bool hasErrors;

        using (DuckDbExporter exporter = _Builder(path, 3, mode).Build())
        {
            exporter.ItemSkipped += (_, args) =>
            {
                events++;
                messages.Add(args.Message);
            };
            exporter.OnPacket(packets[0]);
            using DuckDBConnection other = new(_ConnectionString(path));
            other.Open();
            _BlockPacket(other, packets[3]);

            // Packets 0 to 2 form a full batch that is written; packets 3 and 4 stay in the last, partial batch, which
            // OnFinish writes (and which fails).
            for (int i = 1; i < packets.Length; i++)
            {
                exporter.OnPacket(packets[i]);
            }

            exporter.OnFinish();
            written = exporter.WrittenCount;
            skipped = exporter.SkippedCount;
            errors = exporter.ErrorCount;
            hasErrors = exporter.HasErrors;
        }

        using DuckDBConnection check = new(_ConnectionString(path));
        check.Open();
        long packetRows = DuckDbReadBack.Count(check, "packets");
        long catalogRows = DuckDbReadBack.Count(check, "catalog");

        await Assert.That(catalogRows).IsGreaterThan(0).Because(string.Join(" | ", messages));
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(packetRows).IsEqualTo(3L);
        await Assert.That(skipped).IsEqualTo(2);
        await Assert.That(errors).IsEqualTo(1);
        await Assert.That(events).IsEqualTo(1);
        await Assert.That(hasErrors).IsTrue();
    }

    /// <summary>
    /// A long export with many small batches writes every packet exactly once and in packet-id order: the hand-off between the
    /// caller's thread and the writer thread loses and duplicates nothing.
    /// </summary>
    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task ManyBatchesAreWrittenExactlyOnce(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_many_batches");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(200);
        int written;

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path).WithMaxPacketsPerBlock(7).WithWriteMode(mode).Build())
        {
            foreach (Packet packet in packets)
            {
                exporter.OnPacket(packet);
            }

            exporter.OnFinish();
            written = exporter.WrittenCount;
        }

        using DuckDBConnection check = new(_ConnectionString(path));
        check.Open();
        long packetRows = DuckDbReadBack.Count(check, "packets");
        long distinctPackets = DuckDbReadBack.Scalar(check, "SELECT COUNT(DISTINCT packet_id) FROM packets");
        long outOfOrder = DuckDbReadBack.Scalar(
            check,
            "SELECT COUNT(*) FROM (SELECT packet_id, LAG(packet_id) OVER (ORDER BY rowid) AS previous FROM packets) t WHERE previous > packet_id");

        await Assert.That(written).IsEqualTo(200);
        await Assert.That(packetRows).IsEqualTo(200L);
        await Assert.That(distinctPackets).IsEqualTo(200L);
        await Assert.That(outOfOrder).IsEqualTo(0L);
    }
}
