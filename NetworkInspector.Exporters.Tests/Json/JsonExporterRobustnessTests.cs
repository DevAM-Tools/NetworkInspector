// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// Robustness tests for <see cref="JsonExporter"/> and its helpers: same-as-previous state after a failed write, and
/// configuration validation.
/// </summary>
internal sealed class JsonExporterRobustnessTests
{
    [Test]
    public async Task CompactWriterDoesNotReferenceAPacketThatFailedToWrite()
    {
        // Write calls: 1 = opening bracket, 2 = packet one, 3 = packet two (fails), 4 = packet three.
        using TestStreams.FailOnceStream stream = new() { FailingWriteCall = 3 };
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(JsonExportFormat.Compact)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("first.example.com", "second.example.com", "second.example.com");

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        exporter.OnPacket(packets[2]);
        exporter.OnFinish();

        string written = Encoding.UTF8.GetString(stream.WrittenBytes);

        // Packet three equals packet two. If packet two had left its values in the same-as-previous state, packet three would
        // leave them out ("same as before") and the file would never contain the name at all.
        await Assert.That(exporter.PacketCount).IsEqualTo(2);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(written).Contains("second.example.com");
    }

    [Test]
    public async Task CompactOutputAfterAFailedWriteIsStillValidJson()
    {
        using TestStreams.FailOnceStream stream = new() { FailingWriteCall = 3 };
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(JsonExportFormat.Compact)
            .Build();
        Packet[] packets = PacketGenerators.CreateDnsQueryPackets("first.example.com", "second.example.com", "third.example.com");

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        exporter.OnPacket(packets[2]);
        exporter.OnFinish();

        using JsonDocument document = JsonDocument.Parse(stream.WrittenBytes);

        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task BuilderRejectsAnUndefinedFormat()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();

        await Assert.That(() => builder.WithFormat((JsonExportFormat)99)).Throws<ArgumentOutOfRangeException>();
    }
}
