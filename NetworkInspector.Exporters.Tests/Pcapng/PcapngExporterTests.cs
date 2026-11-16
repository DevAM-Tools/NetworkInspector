// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for the <see cref="PcapngExporter"/> — validates file structure, frame data,
/// timestamps, multi-interface support, and tshark compatibility.
/// </summary>
internal sealed class PcapngExporterTests
{
    [Test]
    public async Task BuilderRejectsBinaryOrOutOfRangeTimestampResolution()
    {
        PcapngExporter.Builder binary = PcapngExporter.CreateBuilder().ToStream(new MemoryStream());
        PcapngExporter.Builder tooFine = PcapngExporter.CreateBuilder().ToStream(new MemoryStream());

        await Assert.That(() => binary.WithTimestampResolution(0x80 | 9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => tooFine.WithTimestampResolution(20)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SingleFrameProducesValidPcapng()
    {
        using MemoryStream stream = new();

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.InterfaceCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);

        // Verify frame data round-trips correctly
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(frameData)).IsTrue();
    }

    [Test]
    public async Task MultipleFramesAllWritten()
    {
        using MemoryStream stream = new();

        const int count = 50;
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(verifier.FrameCount).IsEqualTo(count);
    }

    [Test]
    public async Task StreamOutputProducesValidPcapng()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        // Validate structure from stream data
        PcapngVerifier verifier = PcapngVerifier.FromData(ms.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task FinishWithoutFramesWritesAFileWithOnlyTheSectionHeaderBlock()
    {
        using TestDir dir = new("pcapng_lazy");
        string path = dir.FilePath("output.pcapng");

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();
        exporter.OnFinish();

        // OnFinish triggers lazy init even when no frames arrived, so a file
        // with a valid SHB (but zero EPBs) is always produced.
        await Assert.That(File.Exists(path)).IsTrue();
        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.FrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task FrameCountTracksCorrectly()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 10;
        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    [Category("Slow")]
    public async Task TsharkCountsTheFramesOfTheWrittenFileLikeTheVerifier()
    {
        TsharkVerifier.RequireAvailable();
        using TestDir dir = new("pcapng_tshark");
        string path = dir.FilePath("output.pcapng");

        const int count = 5;
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        // Create frames sharing a single interface to avoid multiple IDB blocks
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        if (registry.SourceCount == 0)
        {
            registry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameSourceId sourceId = new(0);
        FrameInterfaceId ifId = registry.Register(sourceId, "tshark_test", null, LinkType.Ethernet);

        for (int i = 0; i < count; i++)
        {
            byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
            Frame frame = Frame.Create(
                i, Timestamp.FromNanos((long)i * 1_000_000_000),
                frameData, LinkType.Ethernet, ifId, registry).Value;
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        // Verify our verifier agrees with the expected count
        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.FrameCount).IsEqualTo(count);

        // Verify tshark agrees
        int tsharkCount = TsharkVerifier.GetPacketCount(path);
        await Assert.That(tsharkCount).IsEqualTo(count);
    }

    [Test]
    public async Task FlexRayFrameProducesValidPcapng()
    {
        using MemoryStream stream = new();

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();

        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, frData, LinkType.Flexray);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
        await Assert.That(verifier.Interfaces).Count().IsEqualTo(1);
        await Assert.That(verifier.Interfaces[0].LinkType).IsEqualTo((ushort)210);

        // Verify data round-trip
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(frData)).IsTrue();
    }

    [Test]
    public async Task LinFrameProducesValidPcapng()
    {
        using MemoryStream stream = new();

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();

        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, linData, LinkType.Lin);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
        await Assert.That(verifier.Interfaces).Count().IsEqualTo(1);
        await Assert.That(verifier.Interfaces[0].LinkType).IsEqualTo((ushort)212);

        // Verify data round-trip
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(linData)).IsTrue();
    }

    [Test]
    public async Task MixedLinkTypesCreatesMultipleInterfaces()
    {
        using MemoryStream stream = new();

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();

        // Ethernet frame
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000, ethData));

        // FlexRay frame
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF]);
        exporter.OnFrame(TestHarness.CreateFrame(
            1, 2_000_000_000, frData, LinkType.Flexray));

        // LIN frame
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        exporter.OnFrame(TestHarness.CreateFrame(
            2, 3_000_000_000, linData, LinkType.Lin));

        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(3);
        // Each distinct link type should create its own interface
        await Assert.That(verifier.InterfaceCount).IsGreaterThanOrEqualTo(3);
    }

    // ========================================================================
    // Option string length boundary
    // ========================================================================

    [Test]
    public async Task ShbOptionAtMaxLengthWritesSuccessfully()
    {
        // A string whose UTF-8 encoding is exactly 65535 bytes must succeed.
        string maxValue = new('a', 65535);

        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithHardware(maxValue)
            .Build();

        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task AnOverlongShbOptionIsRefusedByTheBuilder()
    {
        // Each ASCII character is 1 byte, so 65536 'x' chars = 65536 UTF-8 bytes > ushort.MaxValue.
        string oversizedValue = new('x', 65536);
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder().ToStream(new MemoryStream());

        await Assert.That(() => builder.WithHardware(oversizedValue)).Throws<ArgumentException>();
        await Assert.That(() => builder.WithOs(oversizedValue)).Throws<ArgumentException>();
        await Assert.That(() => builder.WithApplication(oversizedValue)).Throws<ArgumentException>();
        await Assert.That(() => builder.WithComment(oversizedValue)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AnShbOptionIsMeasuredInUtf8BytesNotCharacters()
    {
        // 21846 three-byte characters are 65538 bytes but only 21846 chars; 21845 of them are 65535 bytes and fit.
        string tooLong = new('€', 21846);
        string fits = new('€', 21845);
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder().ToStream(new MemoryStream());

        await Assert.That(() => builder.WithComment(tooLong)).Throws<ArgumentException>();
        await Assert.That(() => builder.WithComment(fits)).ThrowsNothing();
    }

    [Test]
    public async Task ANullShbOptionIsRefusedByTheBuilder()
    {
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder().ToStream(new MemoryStream());

        await Assert.That(() => builder.WithComment(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ATruncatedFrameRaisesOneAdvisoryEvent()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).WithSnapLength(64).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        // Two frames above the snap length, one exactly at it (not cut), and one more above it.
        foreach (int length in new[] { 100, 101, 64, 200 })
        {
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000, new byte[length]));
        }

        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(exporter.FrameCount).IsEqualTo(4);
        await Assert.That(exporter.TruncatedFrameCount).IsEqualTo(3);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).Contains("64");
        await Assert.That(events[0].Message).Contains("WithSnapLength");
        await Assert.That(verifier.Frames[0].CapturedLength).IsEqualTo(64u);
        await Assert.That(verifier.Frames[0].OriginalLength).IsEqualTo(100u);
        await Assert.That(verifier.Frames[2].CapturedLength).IsEqualTo(64u);
        await Assert.That(verifier.Frames[2].OriginalLength).IsEqualTo(64u);
    }

    [Test]
    public async Task FramesWithinTheSnapLengthRaiseNoEventAndAreNotCounted()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).WithSnapLength(64).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000, new byte[64]));
        exporter.OnFinish();

        await Assert.That(exporter.TruncatedFrameCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ShbOptionEmptyHardwareWritesWithoutErrors()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithHardware("")
            .Build();

        exporter.OnFinish();

        await Assert.That(exporter.HasErrors).IsFalse();
        await Assert.That(ms.Length).IsGreaterThan(0);
        PcapngVerifier verifier = PcapngVerifier.FromData(ms.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
    }
}
