// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Asc;

/// <summary>
/// Robustness tests for <see cref="AscExporter"/> and its writer: invalid channels, start failures, LIN length limits, and
/// out-of-range DLC codes.
/// </summary>
internal sealed class AscExporterRobustnessTests
{
    private static Dictionary<string, object> _ChannelProperties(long channel) =>
        new() { [BlfInterfacePropertyKeys.Channel] = channel };

    [Test]
    public async Task CanFrameOnNegativeChannelIsSkippedAsMalformed()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        Frame frame = InterfaceFrameFactory.Create(
            "bad-channel",
            _ChannelProperties(-3),
            LinkType.CanSocketcan,
            SocketCanGenerators.BuildCanClassic(0x123, [0x01]));

        bool accepted = exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Message).Contains("-3");
        await Assert.That(Encoding.ASCII.GetString(stream.ToArray())).DoesNotContain(" -3 ");
    }

    [Test]
    public async Task LinFrameOnNegativeChannelIsSkippedAsMalformed()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        Frame frame = InterfaceFrameFactory.Create(
            "bad-lin-channel", _ChannelProperties(-1), LinkType.Lin, LinGenerators.BuildLinFrame(0x15, [0x01, 0x02]));

        bool accepted = exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    [Test]
    public async Task EthernetFrameOnNegativeChannelIsSkippedAsMalformed()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        Frame frame = InterfaceFrameFactory.Create(
            "bad-eth-channel", _ChannelProperties(-7), LinkType.Ethernet, FrameGenerators.BuildEthernetIpv4UdpFrame(16));

        bool accepted = exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    [Test]
    public async Task CanFrameOnNegativeChannelStopsTheExportInStrictMode()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        Frame frame = InterfaceFrameFactory.Create(
            "strict-bad-channel",
            _ChannelProperties(-3),
            LinkType.CanSocketcan,
            SocketCanGenerators.BuildCanClassic(0x123, [0x01]));

        bool accepted = exporter.OnFrame(frame);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task FileOpenFailureIsReportedAsExportErrorInsteadOfEscaping()
    {
        using TestDir dir = new("asc_open_failure");
        string path = Path.Combine(dir.DirectoryPath, "missing_directory", "output.asc");
        using AscExporter exporter = AscExporter.CreateBuilder().ToFile(path).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task MissingOutputStreamIsReportedAsExportError()
    {
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(null!).Build(); // null! : deliberately invalid stream
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DisposedExporterDoesNotCountAnErrorWhenFramesArrive()
    {
        using MemoryStream stream = new();
        AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.OnFinish();

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task LinFrameWithDlcAboveEightWritesAtMostEightDataBytes()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        byte[] lin = new byte[8 + 12];
        lin[4] = (byte)((15 << 4) | 0x01);
        lin[5] = 0x15;
        for (int index = 0; index < 12; index++)
        {
            lin[8 + index] = (byte)(0xA0 + index);
        }

        exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, lin, LinkType.Lin));
        exporter.OnFinish();

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(content).Contains("Rx 8 A0 A1 A2 A3 A4 A5 A6 A7 checksum");
    }

    [Test]
    public async Task CanFdWriterMasksADlcAboveFifteenLikeTheClassicPath()
    {
        using MemoryStream stream = new();
        AscWriter writer = new(stream, 0);
        try
        {
            writer.WriteCanFdMessage(0, 1, 0x123, false, false, false, 0x13, [0x01, 0x02, 0x03]);
            writer.Finish();
        }
        finally
        {
            writer.Return();
        }

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(content).Contains("0 0 3 3 01 02 03");
    }

    [Test]
    public async Task WriterConstructorFailureDoesNotLeavePooledBuffersRented()
    {
        using MemoryStream stream = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                _ = new AscWriter(stream, 0, 5); // 5 : decimals outside 6..9 make the constructor throw
            }
            catch (ArgumentOutOfRangeException)
            {
                // Expected: the constructor rejects the argument.
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Each failed construction used to rent a 64 KiB output buffer and never return it; 50 attempts would allocate over 3 MB.
        await Assert.That(allocated).IsLessThan(500_000L);
    }
}
