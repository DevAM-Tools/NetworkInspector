// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for how <see cref="PbfExporter"/> accounts for a block whose field tree was cut at the maximum nesting depth when
/// the error tolerance is Strict: the block is dropped, and the counters and the trailer must say so.
/// </summary>
internal sealed class PbfExporterTruncationTests
{
    /// <summary>Length of the closing magic of a PBF file (44 bytes).</summary>
    private const int _MagicLength = 44;

    /// <summary>Reads the packet count that the trailer of a PBF file states (trailer field 1, a varint).</summary>
    private static ulong _ReadTrailerPacketCount(byte[] file)
    {
        int sizeOffset = file.Length - _MagicLength - 4;
        int trailerSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(sizeOffset));
        ReadOnlySpan<byte> trailer = file.AsSpan(sizeOffset - trailerSize, trailerSize);

        // The first trailer field is the packet count: tag 0x08, then a varint.
        ulong value = 0;
        int shift = 0;
        for (int index = 1; index < trailer.Length; index++)
        {
            byte current = trailer[index];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return value;
    }

    [Test]
    public async Task StrictTruncationDropsTheBlockAndTheTrailerDoesNotCountItsPackets()
    {
        Packet deep = PbfInternalTests.CreateDeepPacket(18);
        using MemoryStream stream = new();
        bool accepted;
        using (PbfExporter exporter = PbfExporter.CreateBuilder().ToStream(stream).WithCompressed(false).Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Strict;
            accepted = exporter.OnPacket(deep);
            exporter.OnFinish();

            await Assert.That(exporter.WrittenCount).IsEqualTo(0);
            await Assert.That(exporter.SkippedCount).IsEqualTo(1);
            await Assert.That(exporter.HasErrors).IsTrue();
        }

        // The cut is only noticed when the block is flushed (here at finish), so the packet itself was accepted.
        await Assert.That(accepted).IsTrue();
        await Assert.That(_ReadTrailerPacketCount(stream.ToArray())).IsEqualTo(0ul);
    }

    [Test]
    public async Task StrictTruncationCountsEveryPacketOfTheDroppedBlockAsSkipped()
    {
        Packet first = PbfInternalTests.CreateDeepPacket(18);
        Packet second = PbfInternalTests.CreateDeepPacket(18);
        using MemoryStream stream = new();
        bool firstAccepted;
        bool secondAccepted;
        using (PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(2)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Strict;
            firstAccepted = exporter.OnPacket(first);
            secondAccepted = exporter.OnPacket(second);
            exporter.OnFinish();

            await Assert.That(exporter.WrittenCount).IsEqualTo(0);
            await Assert.That(exporter.SkippedCount).IsEqualTo(2);
        }

        await Assert.That(firstAccepted).IsTrue();
        await Assert.That(secondAccepted).IsFalse();
        await Assert.That(_ReadTrailerPacketCount(stream.ToArray())).IsEqualTo(0ul);
    }
}
