// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for the Interface Description Block: the 16-bit link type field and the all-or-nothing write of the block.
/// </summary>
internal sealed class PcapngInterfaceBlockTests
{
    [Test]
    public async Task LinkTypeAboveSixteenBitsIsRejectedAndNothingIsWritten()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            await Assert.That(() => writer.WriteInterfaceDescription((LinkType)0x0001_0001, 65535, PcapngWriter.TsResolNanoseconds, "eth0"))
                .Throws<ArgumentOutOfRangeException>();
            await Assert.That(stream.Length).IsEqualTo(0L);
        }
        finally
        {
            writer.ReturnBuffers();
        }
    }

    [Test]
    public async Task NegativeLinkTypeIsRejected()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            await Assert.That(() => writer.WriteInterfaceDescription((LinkType)(-1), 65535, PcapngWriter.TsResolNanoseconds, "eth0"))
                .Throws<ArgumentOutOfRangeException>();
        }
        finally
        {
            writer.ReturnBuffers();
        }
    }

    [Test]
    public async Task LargestSixteenBitLinkTypeIsWrittenUnchanged()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            writer.WriteInterfaceDescription((LinkType)ushort.MaxValue, 65535, PcapngWriter.TsResolNanoseconds, "eth0");
        }
        finally
        {
            writer.ReturnBuffers();
        }

        ushort linkType = BinaryPrimitives.ReadUInt16LittleEndian(stream.ToArray().AsSpan(8));

        await Assert.That(linkType).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task FrameWithAnUnrepresentableLinkTypeIsSkippedAsSerializationError()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        Frame frame = TestHarness.CreateFrame(
            new FrameId(0), 1_000_000_000L, new byte[20], (LinkType)0x0001_0001);

        bool accepted = exporter.OnFrame(frame);

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
    }
}
