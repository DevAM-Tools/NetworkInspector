// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// The write modes of the PBF exporter: a writer thread serializes, compresses and writes the blocks in background mode, the
/// caller's thread does it in synchronous mode. Both must produce the same file, the caller's stream must hold the complete file
/// when <c>OnFinish</c> returns, and a stream that fails must be reported as a lost block.
/// </summary>
[Category("Slow")]
internal sealed class PbfWriterThreadTests
{
    private const int _MagicLength = 44;

    /// <summary>
    /// Skips magic and header: the header carries the creation time (milliseconds), which differs between two runs; everything
    /// after it (blocks and trailer) must be identical.
    /// </summary>
    private static byte[] _AfterHeader(byte[] file)
    {
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(_MagicLength, 4));
        return file[(_MagicLength + 4 + headerLength)..];
    }

    private static byte[] _Export(PbfExportFormat format, bool compressed, ExportWriteMode mode, Packet[] packets)
    {
        using MemoryStream stream = new();
        using (PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(format)
            .WithCompressed(compressed)
            .WithMaxPacketsPerBlock(1000)
            .WithWriteMode(mode)
            .Build())
        {
            foreach (Packet packet in packets)
            {
                exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        return stream.ToArray();
    }

    [Test]
    [Arguments(PbfExportFormat.Standard, true)]
    [Arguments(PbfExportFormat.Standard, false)]
    [Arguments(PbfExportFormat.Columnar, true)]
    [Arguments(PbfExportFormat.Columnar, false)]
    public async Task BackgroundAndSynchronousModesWriteIdenticalBytes(PbfExportFormat format, bool compressed)
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3000);

        byte[] background = _Export(format, compressed, ExportWriteMode.Background, packets);
        byte[] synchronous = _Export(format, compressed, ExportWriteMode.Synchronous, packets);

        await Assert.That(_AfterHeader(synchronous).AsSpan().SequenceEqual(_AfterHeader(background))).IsTrue();
        await Assert.That(background.Length).IsGreaterThan(_MagicLength * 2);
    }

    [Test]
    public async Task CallerStreamHoldsTheCompleteFileWhenOnFinishReturnsAndNoReferenceIsKept()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2500);
        MemoryStream stream = new();
        PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Columnar)
            .WithMaxPacketsPerBlock(1000)
            .Build();
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        // The closing magic is at the end of the stream: the trailer and everything before it were written before OnFinish returned.
        byte[] file = stream.ToArray();
        byte[] magic = "NETWORK-INSPECTOR-PBF-FORMAT-v1\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray();

        await Assert.That(file.AsSpan(file.Length - _MagicLength).SequenceEqual(magic)).IsTrue();
        await Assert.That(exporter.BlockCount).IsEqualTo(3);
        await Assert.That(exporter.HasErrors).IsFalse();
        GC.KeepAlive(exporter);
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task AFailingStreamIsReportedAsALostBlockAndTheTrailerCountsOnlyWrittenPackets(ExportWriteMode mode)
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3000);
        using TestStreams.FailingStream stream = new(failOnWriteNumber: 4);
        List<ExportErrorEventArgs> events = [];
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithMaxPacketsPerBlock(1000)
            .WithWriteMode(mode)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        exporter.ItemSkipped += (_, args) => events.Add(args);

        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        // Writes: 1 magic, 2 header length, 3 header data, 4 first block (fails); later blocks succeed. The lost block's 1000 packets are skipped.
        await Assert.That(exporter.SkippedCount).IsEqualTo(1000);
        await Assert.That(exporter.WrittenCount).IsEqualTo(2000);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Message).Contains("1000 packets lost");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task BatchSizeOutOfRangeNamesTheRange(int count)
    {
        await Assert.That(() => PbfExporter.CreateBuilder().WithMaxPacketsPerBlock(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and 2147483647");
        await Assert.That(() => PbfExporter.CreateBuilder().WithMaxBlockSize(count))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 1 and");
    }

    [Test]
    public async Task WriteModeMustBeDefined()
    {
        await Assert.That(() => PbfExporter.CreateBuilder().WithWriteMode((ExportWriteMode)9)).Throws<ArgumentOutOfRangeException>();
    }
}
