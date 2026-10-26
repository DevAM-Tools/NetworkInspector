// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// Tests for a batch whose write fails inside the database: its transaction is rolled back, the batch is dropped, and the
/// counters say which packets were lost. A second connection creates a table of the wrong shape for every field, which
/// makes the field-table write of the batch fail after the packet rows were appended.
/// </summary>
internal sealed class DuckDbExporterFlushFailureTests
{
    private static string _ConnectionString(string path)
    {
        DuckDBConnectionStringBuilder builder = new() { DataSource = path };
        return builder.ConnectionString;
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

    private static int[] _FieldIds(Packet[] packets)
    {
        using ColumnarPacketBatch probe = new(ColumnarDetailFlags.All, 1000, 1024 * 1024, true);
        foreach (Packet packet in packets)
        {
            probe.AddPacket(packet);
        }

        return probe.FieldBags.Keys.ToArray();
    }

    [Test]
    public async Task FailedBatchIsDroppedCountedAsSkippedAndTheExportContinues()
    {
        using TestDir dir = new("duckdb_flush_failure");
        string path = dir.FilePath("capture.duckdb");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(6);
        int[] fieldIds = _FieldIds(packets);
        long packetRows;
        int written;
        int skipped;
        int errors;
        int events = 0;

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithMaxPacketsPerBlock(3)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            exporter.ItemSkipped += (_, _) => events++;

            // The first packet creates the database; then the field tables are blocked from a second connection.
            exporter.OnPacket(packets[0]);
            using (DuckDBConnection other = new(_ConnectionString(path)))
            {
                other.Open();
                foreach (int fieldId in fieldIds)
                {
                    _Execute(other, $"CREATE TABLE field_{fieldId}(unrelated INTEGER)");
                }

                exporter.OnPacket(packets[1]);
                exporter.OnPacket(packets[2]);

                // The batch of three packets failed and was dropped: nothing counts as written.
                await Assert.That(exporter.WrittenCount).IsEqualTo(0);
                await Assert.That(exporter.SkippedCount).IsEqualTo(3);
                await Assert.That(exporter.ErrorCount).IsEqualTo(1);
                await Assert.That(events).IsEqualTo(1);

                foreach (int fieldId in fieldIds)
                {
                    _Execute(other, $"DROP TABLE field_{fieldId}");
                }
            }

            exporter.OnPacket(packets[3]);
            exporter.OnPacket(packets[4]);
            exporter.OnPacket(packets[5]);
            exporter.OnFinish();
            written = exporter.WrittenCount;
            skipped = exporter.SkippedCount;
            errors = exporter.ErrorCount;
        }

        using DuckDBConnection check = new(_ConnectionString(path));
        check.Open();
        packetRows = _Count(check, "packets");

        // Only the second batch reached the database; the failed first batch left no rows and was not written again.
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(skipped).IsEqualTo(3);
        await Assert.That(errors).IsEqualTo(1);
        await Assert.That(packetRows).IsEqualTo(3L);
    }
}
