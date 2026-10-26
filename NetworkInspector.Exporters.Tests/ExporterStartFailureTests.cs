// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Start-up failure tests shared by the exporters that open their output lazily on the first item: a file that cannot be
/// created, or an output without a stream, must end in the normal error path (counter and event) and must never throw out of
/// <c>OnFrame</c> or <c>OnPacket</c>.
/// </summary>
internal sealed class ExporterStartFailureTests
{
    [Test]
    public async Task PbfFileOpenFailureIsReportedAsExportErrorInsteadOfEscaping()
    {
        using TestDir dir = new("pbf_open_failure");
        string path = Path.Combine(dir.DirectoryPath, "missing_directory", "output.pbf");
        using PbfExporter exporter = PbfExporter.CreateBuilder().ToFile(path).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task PbfMissingOutputStreamIsReportedAsExportError()
    {
        using PbfExporter exporter = PbfExporter.CreateBuilder().ToStream(null!).Build(); // null! : deliberately invalid stream
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PcapngFileOpenFailureIsReportedAsExportErrorInsteadOfEscaping()
    {
        using TestDir dir = new("pcapng_open_failure");
        string path = Path.Combine(dir.DirectoryPath, "missing_directory", "output.pcapng");
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task PcapngMissingOutputStreamIsReportedAsExportError()
    {
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(null!).Build(); // null! : deliberately invalid stream
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsonFileOpenFailureIsReportedAsExportErrorInsteadOfEscaping()
    {
        using TestDir dir = new("json_open_failure");
        string path = Path.Combine(dir.DirectoryPath, "missing_directory", "output.json");
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToFile(path).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task JsonMissingOutputStreamIsReportedAsExportError()
    {
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(null!).Build(); // null! : deliberately invalid stream
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }
}
