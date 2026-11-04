// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Asc;

/// <summary>
/// Robustness tests for <see cref="AscExporter"/> and its writer: invalid channels, start failures, out-of-range DLC codes,
/// corrupt and cut CAN payload counts and LIN data lengths, and the classification of write failures.
/// </summary>
internal sealed class AscExporterRobustnessTests
{
    private static Dictionary<string, object> _ChannelProperties(long channel) =>
        new() { [BlfInterfacePropertyKeys.Channel] = channel };

    /// <summary>Builds a SocketCAN frame buffer with an explicit byte-4 count, flag byte, and the given number of data bytes.</summary>
    private static byte[] _CanBuffer(byte declaredCount, byte flags, int presentBytes)
    {
        byte[] data = new byte[8 + presentBytes];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x123);
        data[4] = declaredCount;
        data[5] = flags;
        for (int index = 0; index < presentBytes; index++)
        {
            data[8 + index] = (byte)(index + 1);
        }

        return data;
    }

    /// <summary>
    /// Builds a DLT_LIN frame buffer with an explicit declared length (byte 4, bits 7-4), the classic checksum model, and the given
    /// number of data bytes (A0, A1, ...), independent of the declared length.
    /// </summary>
    private static byte[] _LinBuffer(int declaredLength, int presentBytes)
    {
        byte[] data = new byte[8 + presentBytes];
        data[0] = 1;
        data[4] = (byte)((declaredLength << 4) | 0x01);
        data[5] = 0x15;
        for (int index = 0; index < presentBytes; index++)
        {
            data[8 + index] = (byte)(0xA0 + index);
        }

        return data;
    }

    /// <summary>Memory stream whose write throws an exception that is not an I/O failure.</summary>
    private sealed class NonIoFailingStream : MemoryStream
    {
        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Simulated formatting failure");
    }

    [Test]
    public async Task AnFdFrameThatDeclaresMoreThan64BytesIsSkippedAsMalformedAndNamesTheLimit()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _CanBuffer(65, 0x04, 65), LinkType.CanSocketcan));
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].Message).Contains("ASC CAN FD frame declares 65 data bytes");
        await Assert.That(events[0].Message).Contains("at most 64");
        await Assert.That(Encoding.ASCII.GetString(stream.ToArray())).DoesNotContain("CANFD");
    }

    [Test]
    [Arguments(9, false)]
    [Arguments(15, false)]
    [Arguments(127, false)]
    [Arguments(9, true)]
    public async Task AClassicFrameThatDeclaresMoreThan8BytesIsSkippedAsMalformedAndNamesTheLimit(int declared, bool remote)
    {
        byte[] data = _CanBuffer((byte)declared, 0, 8);
        if (remote)
        {
            data[0] |= 0x40;
        }

        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, data, LinkType.CanSocketcan));
        exporter.OnFinish();

        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].Message).Contains(declared.ToString(CultureInfo.InvariantCulture) + " data bytes");
        await Assert.That(events[0].Message).Contains("at most 8");
        await Assert.That(events[0].Message).Contains("len8_dlc");
    }

    [Test]
    public async Task AnAboveMaximumCountStopsAStrictExport()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _CanBuffer(65, 0x04, 65), LinkType.CanSocketcan));

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task WellFormedCanFramesAtTheirMaximumAreWrittenWithoutAnEvent()
    {
        byte[] classicWithLongDlc = _CanBuffer(8, 0, 8);
        classicWithLongDlc[7] = 15;
        byte[] fdMaximum = _CanBuffer(64, 0x04, 64);
        byte[] remote = _CanBuffer(8, 0, 0);
        remote[0] |= 0x40;
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, classicWithLongDlc, LinkType.CanSocketcan));
        exporter.OnFrame(TestHarness.CreateFrame(1, 1_000_000_001L, fdMaximum, LinkType.CanSocketcan));
        exporter.OnFrame(TestHarness.CreateFrame(2, 1_000_000_002L, remote, LinkType.CanSocketcan));
        exporter.OnFinish();

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(exporter.FrameCount).IsEqualTo(3);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(content).Contains("Rx d F 01 02 03 04 05 06 07 08");
        await Assert.That(content).Contains(" 0 0 F 64 01 02 03");
        await Assert.That(content).Contains("Rx r 8");
    }

    [Test]
    public async Task CutCanFramesAreWrittenWithTheBytesPresentAndReportedOnce()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool fdAccepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _CanBuffer(16, 0x04, 4), LinkType.CanSocketcan));
        bool classicAccepted = exporter.OnFrame(TestHarness.CreateFrame(1, 1_000_000_001L, _CanBuffer(8, 0, 3), LinkType.CanSocketcan));
        bool fdAgainAccepted = exporter.OnFrame(TestHarness.CreateFrame(2, 1_000_000_002L, _CanBuffer(64, 0x04, 0), LinkType.CanSocketcan));
        exporter.OnFinish();

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(fdAccepted && classicAccepted && fdAgainAccepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(3);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(content).Contains("0 0 4 4 01 02 03 04");
        await Assert.That(content).Contains("123 Rx d 3 01 02 03");
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].ItemIndex).IsEqualTo(0);
        await Assert.That(events[0].Message).Contains("declares 16 data bytes but carries 4");
        await Assert.That(events[0].Message).Contains("reported once per export");
    }

    [Test]
    public async Task ACutCanFrameDoesNotStopAStrictExport()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _CanBuffer(8, 0, 3), LinkType.CanSocketcan));

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.HasErrors).IsFalse();
        await Assert.That(exporter.FrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task AFailingOutputStreamIsReportedAsIoError()
    {
        using TestStreams.FailingStream stream = new() { ThrowAfterByte = 0 };
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        _OfferEthernetFramesUntilTheBufferFlushes(exporter);

        await Assert.That(events.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task AWriteFailureThatIsNotAnIoFailureIsReportedAsSerializationError()
    {
        using NonIoFailingStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        _OfferEthernetFramesUntilTheBufferFlushes(exporter);

        await Assert.That(events.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).Contains("Simulated formatting failure");
    }

    /// <summary>
    /// Offers maximum-size Ethernet frames until the writer's 64 KiB output buffer must have been handed to the stream (each
    /// line is about 3 KB), so a failing stream raises its exception inside one of the frame handlers.
    /// </summary>
    /// <param name="exporter">The exporter whose output stream fails.</param>
    private static void _OfferEthernetFramesUntilTheBufferFlushes(AscExporter exporter)
    {
        byte[] ethernet = new byte[1518];
        for (int index = 0; index < 30; index++)
        {
            exporter.OnFrame(TestHarness.CreateFrame(index, 1_000_000_000L + index, ethernet, LinkType.Ethernet));
        }
    }

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
    [Arguments(9)]
    [Arguments(15)]
    public async Task ALinFrameThatDeclaresMoreThan8BytesIsSkippedAsMalformedAndNamesTheLimit(int declared)
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _LinBuffer(declared, 12), LinkType.Lin));
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].Message).Contains(declared.ToString(CultureInfo.InvariantCulture) + " data bytes");
        await Assert.That(events[0].Message).Contains("at most 8");
        await Assert.That(Encoding.ASCII.GetString(stream.ToArray())).DoesNotContain(" Li ");
    }

    [Test]
    public async Task ALinLengthAboveTheMaximumStopsAStrictExport()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _LinBuffer(15, 12), LinkType.Lin));

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task WellFormedLinFramesAreWrittenUnchangedWithoutAnEvent()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        // Maximum length with exactly the declared bytes, a shorter frame, no data at all, and bytes after the declared length
        // (a padded buffer): each is complete, and the bytes after the declared length are never written.
        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _LinBuffer(8, 8), LinkType.Lin));
        exporter.OnFrame(TestHarness.CreateFrame(1, 1_000_000_001L, _LinBuffer(3, 3), LinkType.Lin));
        exporter.OnFrame(TestHarness.CreateFrame(2, 1_000_000_002L, _LinBuffer(0, 0), LinkType.Lin));
        exporter.OnFrame(TestHarness.CreateFrame(3, 1_000_000_003L, _LinBuffer(2, 6), LinkType.Lin));
        exporter.OnFinish();

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(exporter.FrameCount).IsEqualTo(4);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(content).Contains("Rx 8 A0 A1 A2 A3 A4 A5 A6 A7 checksum");
        await Assert.That(content).Contains("Rx 3 A0 A1 A2 checksum");
        await Assert.That(content).Contains("Rx 0 checksum");
        await Assert.That(content).Contains("Rx 2 A0 A1 checksum");
        await Assert.That(content).DoesNotContain("A2 A3 checksum");
    }

    [Test]
    public async Task CutLinFramesAreWrittenWithTheBytesPresentAndReportedOnce()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool first = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _LinBuffer(8, 3), LinkType.Lin));
        bool second = exporter.OnFrame(TestHarness.CreateFrame(1, 1_000_000_001L, _LinBuffer(4, 0), LinkType.Lin));
        bool third = exporter.OnFrame(TestHarness.CreateFrame(2, 1_000_000_002L, _LinBuffer(8, 5), LinkType.Lin));
        exporter.OnFinish();

        string content = Encoding.ASCII.GetString(stream.ToArray());

        await Assert.That(first && second && third).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(3);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(content).Contains("Rx 3 A0 A1 A2 checksum");
        await Assert.That(content).Contains("Rx 0 checksum");
        await Assert.That(content).Contains("Rx 5 A0 A1 A2 A3 A4 checksum");
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].ItemIndex).IsEqualTo(0);
        await Assert.That(events[0].Message).Contains("LIN frame declares 8 data bytes but carries 3");
        await Assert.That(events[0].Message).Contains("reported once per export");
    }

    [Test]
    public async Task ACutLinFrameDoesNotStopAStrictExport()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _LinBuffer(8, 3), LinkType.Lin));

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.HasErrors).IsFalse();
        await Assert.That(exporter.FrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task TheCutCanAndCutLinAdvisoriesAreReportedOnceEach()
    {
        using MemoryStream stream = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, _CanBuffer(8, 0, 3), LinkType.CanSocketcan));
        exporter.OnFrame(TestHarness.CreateFrame(1, 1_000_000_001L, _LinBuffer(8, 3), LinkType.Lin));
        exporter.OnFrame(TestHarness.CreateFrame(2, 1_000_000_002L, _CanBuffer(8, 0, 2), LinkType.CanSocketcan));
        exporter.OnFrame(TestHarness.CreateFrame(3, 1_000_000_003L, _LinBuffer(8, 2), LinkType.Lin));
        exporter.OnFinish();

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0].Message).Contains("a CAN frame declares 8 data bytes but carries 3");
        await Assert.That(events[1].Message).Contains("a LIN frame declares 8 data bytes but carries 3");
        await Assert.That(exporter.FrameCount).IsEqualTo(4);
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
