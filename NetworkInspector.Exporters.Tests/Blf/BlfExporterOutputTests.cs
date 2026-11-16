// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Output-handling tests for <see cref="BlfExporter"/>: where the header patch lands in a caller-supplied stream, how start
/// failures are reported, and the per-frame allocation of custom interface names.
/// </summary>
internal sealed class BlfExporterOutputTests
{
    /// <summary>Offset of the <c>obj_count</c> field inside the 144-byte BLF file header.</summary>
    private const int _ObjectCountOffset = 32;

    [Test]
    public async Task HeaderUpdateKeepsBytesBeforeTheStartPositionOfAStream()
    {
        // The caller already wrote 10 marker bytes; the BLF file starts at offset 10.
        using MemoryStream stream = new();
        byte[] marker = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        stream.Write(marker);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02]);
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, canData, LinkType.CanSocketcan));
            exporter.OnFinish();
        }

        byte[] bytes = stream.ToArray();
        uint objectCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(marker.Length + _ObjectCountOffset));

        await Assert.That(bytes[..marker.Length]).IsEquivalentTo(marker);
        await Assert.That(bytes.AsSpan(marker.Length, 4).ToArray()).IsEquivalentTo("LOGG"u8.ToArray());
        await Assert.That(objectCount).IsGreaterThan(0u);
    }

    [Test]
    public async Task HeaderUpdateStillPatchesTheHeaderOfAStreamStartingAtZero()
    {
        using MemoryStream stream = new();

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02]);
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, canData, LinkType.CanSocketcan));
            exporter.OnFinish();
        }

        uint objectCount = BinaryPrimitives.ReadUInt32LittleEndian(stream.ToArray().AsSpan(_ObjectCountOffset));

        await Assert.That(objectCount).IsGreaterThan(0u);
    }

    [Test]
    public async Task FileOpenFailureIsReportedAsExportErrorInsteadOfEscaping()
    {
        using TestDir dir = new("blf_open_failure");
        string path = Path.Combine(dir.DirectoryPath, "missing_directory", "output.blf");
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
    }

    [Test]
    public async Task RefusedOutputEntryIsReportedAsExportError()
    {
        // A target that refuses its main entry models an output that cannot be opened.
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToTarget(new MemoryExportTarget(refuse: static _ => "offline"))
            .Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);
        bool acceptedAgain = exporter.OnFrame(PacketGenerators.CreateEthernetFrames(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(acceptedAgain).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CustomInterfaceNameAllocatesNothingPerFrameAfterTheFirst()
    {
        using MemoryStream stream = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build();
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02]);
        Frame[] frames = new Frame[2000];
        Frame template = InterfaceFrameFactory.Create(
            "Powertrain",
            new Dictionary<string, object> { [BlfInterfacePropertyKeys.Channel] = 3L },
            LinkType.CanSocketcan,
            canData);
        for (int index = 0; index < frames.Length; index++)
        {
            frames[index] = template;
        }

        // Warm-up: registers the interface, writes the AppText name, and grows the pooled buffers once.
        exporter.OnFrame(template);
        exporter.OnFrame(template);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < frames.Length; index++)
        {
            exporter.OnFrame(frames[index]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The default-name string of a frame is about 32 bytes: 2000 frames would allocate 64 KB if it were built per frame.
        await Assert.That(allocated).IsLessThan(8_000L);
        exporter.OnFinish();
    }
}
