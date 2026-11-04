// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// The pipeline sample of the guides as a test: a DuckDB exporter with two selected fields and a filter share one
/// <see cref="ParsePlan"/>, 100 frames are parsed once each with the plan's options, and no packet carries a field tree. The text
/// between the sample markers is the one the Core guide and the exporter guide show; it is copied here so that a guide that no
/// longer compiles, or a plan that builds trees, fails a test (no test reads Markdown).
/// </summary>
[Category("Slow")]
internal sealed class PipelineSampleTests
{
    [Test]
    public async Task GuidePipelineSampleExportsTwoFieldsWithoutAFieldTree()
    {
        using TestDir dir = new("pipeline_sample");
        string path = dir.FilePath("dns.duckdb");
        Stack stack = TestHarness.GetStack();

        // The sample reads `frames`, `stack` and `nextId`; the test supplies them. Every third frame is a DNS query (a longer datagram, which the filter selects).
        List<Frame> frames = [];
        for (int i = 0; i < 100; i++)
        {
            frames.Add(
                i % 3 == 0
                    ? TestHarness.CreateFrame(i, i * 1_000_000L, FrameGenerators.BuildDnsQueryFrame("example.com"))
                    : TestHarness.CreateFrame(i, i * 1_000_000L, FrameGenerators.BuildEthernetIpv4UdpFrame(8)));
        }

        // Packet ids are a dense sequence per stack and thread (a repeated id is a replay): take a fresh range of 100 from the
        // harness so that this test neither replays ids of earlier tests nor makes later ones do so.
        int nextId = TestHarness.NextPacketId().Value;
        for (int reserved = 1; reserved < 100; reserved++)
        {
            _ = TestHarness.NextPacketId();
        }

        List<bool> treeSeen = [];

        // --- sample start ---
        ExportContent content = ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"));
        DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithContent(content)                       // selected fields, see Export content
            .Build();
        FilterObserverOptions filterOptions = new();
        FilterObserver filter = PacketFilter.Compile("udp.length > 20", stack, in filterOptions).Value!;

        ParseDemand[] demands =
        [
            // The filter runs first, so its verdict is complete when the exporter's packet is committed.
            filter.ParseDemand,
            exporter.ParseDemand,
        ];
        if (!ParsePlan.TryCreate(stack, demands, out ParsePlan? plan, out ParsePlanError planError))
        {
            throw new InvalidOperationException(planError.Message);
        }

        ParseOptions options = plan.Options;   // a property: copy once, pass by `in`
        Packet? recycle = null;
        foreach (Frame frame in frames)
        {
            // One parse per frame for both consumers; no field tree because neither demanded one.
            Packet packet;
            if (recycle is not null && Packet.TryParse(recycle, nextId, stack, frame, in options, out _))
            {
                packet = recycle;
            }
            else if (Packet.TryParse(nextId, stack, frame, in options, out Packet? created, out _) && created is not null)
            {
                packet = created;
            }
            else
            {
                nextId++;
                continue;
            }

            nextId++;
            recycle = packet;
            treeSeen.Add(packet.HasFieldTree); // test only: not part of the guide sample
            if (filter.TryReadMatch(out bool matched, out _) && matched)
            {
                exporter.OnPacket(packet);   // commits the fields the exporter's observer staged during the parse
            }
        }

        exporter.OnFinish();
        // --- sample end ---

        exporter.Dispose();
        using DuckDBConnection connection = new($"Data Source={path}");
        connection.Open();

        await Assert.That(treeSeen.Count).IsEqualTo(100);
        await Assert.That(treeSeen.Contains(true)).IsFalse();
        await Assert.That(_Scalar(connection, "SELECT COUNT(*) FROM packets")).IsEqualTo(34L);
        await Assert.That(_Scalar(connection, $"SELECT COUNT(*) FROM field_{stack.GetFieldId("udp.dstport")!.Value.Value}")).IsEqualTo(34L);
        await Assert.That(_Scalar(connection, $"SELECT COUNT(*) FROM field_{stack.GetFieldId("ip.src")!.Value.Value}")).IsEqualTo(34L);
        await Assert.That(plan.ToString()).Contains("no tree");
    }

    private static long _Scalar(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
