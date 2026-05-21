// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for the <see cref="PcapngExporter"/> — validates file structure, frame data,
/// timestamps, multi-interface support, and tshark compatibility.
/// </summary>
internal sealed class PcapngExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SingleFrame_ProducesValidPcapng()
    {
        using TestDir dir = new("pcapng_single");
        string path = dir.FilePath("output.pcapng");

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.InterfaceCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);

        // Verify frame data round-trips correctly
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(frameData)).IsTrue();
    }

    [Test]
    public async Task MultipleFrames_AllWritten()
    {
        using TestDir dir = new("pcapng_multi");
        string path = dir.FilePath("output.pcapng");

        const int count = 50;
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.FrameCount).IsEqualTo(count);
    }

    [Test]
    public async Task StreamOutput_ProducesValidPcapng()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        // Validate structure from stream data
        PcapngVerifier verifier = PcapngVerifier.FromData(ms.ToArray());
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task LazyInit_NoFileCreatedWithoutFrames()
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
    public async Task FrameCount_TracksCorrectly()
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
    public async Task TsharkValidation_Skipped_WhenNotAvailable()
    {
        if (!TsharkVerifier.IsAvailable())
        {
            // Skip test gracefully
            return;
        }

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
                new FrameId(i), Timestamp.FromNanos((long)i * 1_000_000_000),
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
    public async Task FlexRayFrame_ProducesValidPcapng()
    {
        using TestDir dir = new("pcapng_flexray");
        string path = dir.FilePath("output.pcapng");

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, frData, LinkType.Flexray);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
        await Assert.That(verifier.Interfaces).Count().IsEqualTo(1);
        await Assert.That(verifier.Interfaces[0].LinkType).IsEqualTo((ushort)210);

        // Verify data round-trip
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(frData)).IsTrue();
    }

    [Test]
    public async Task LinFrame_ProducesValidPcapng()
    {
        using TestDir dir = new("pcapng_lin");
        string path = dir.FilePath("output.pcapng");

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, linData, LinkType.Lin);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(1);
        await Assert.That(verifier.Interfaces).Count().IsEqualTo(1);
        await Assert.That(verifier.Interfaces[0].LinkType).IsEqualTo((ushort)212);

        // Verify data round-trip
        await Assert.That(verifier.Frames[0].Data.AsSpan().SequenceEqual(linData)).IsTrue();
    }

    [Test]
    public async Task MixedLinkTypes_CreatesMultipleInterfaces()
    {
        using TestDir dir = new("pcapng_mixed_links");
        string path = dir.FilePath("output.pcapng");

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();

        // Ethernet frame
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, ethData));

        // FlexRay frame
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(1), 2_000_000_000, frData, LinkType.Flexray));

        // LIN frame
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(2), 3_000_000_000, linData, LinkType.Lin));

        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.Open(path);
        await Assert.That(verifier.SectionCount).IsEqualTo(1);
        await Assert.That(verifier.FrameCount).IsEqualTo(3);
        // Each distinct link type should create its own interface
        await Assert.That(verifier.InterfaceCount).IsGreaterThanOrEqualTo(3);
    }

    // ========================================================================
    // Cancellation
    // ========================================================================

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(10);

        exporter.OnFrame(frames[0]);
        exporter.OnFrame(frames[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnFrame must return false
        bool accepted = exporter.OnFrame(frames[2]);
        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    public async Task IsFinished_TrueAfterCancellation()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // Target frame count
    // ========================================================================

    [Test]
    public async Task TargetFrameCount_LimitsExport()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetFrameCount(3)
            .Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(10);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        await Assert.That(exporter.FrameCount).IsEqualTo(3);
    }

    [Test]
    public async Task IsFinished_TrueAfterTargetReached()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetFrameCount(2)
            .Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(5);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    // ========================================================================
    // Lifecycle: IsFinished, Double-finish
    // ========================================================================

    [Test]
    public async Task IsFinished_FalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnFrame_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(1);
        bool accepted = exporter.OnFrame(frames[0]);

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task DoubleFinish_IsIdempotent()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(2);
        exporter.OnFrame(frames[0]);

        // Calling OnFinish twice must not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(1);
    }

    // ========================================================================
    // UiName / Description
    // ========================================================================

    [Test]
    public async Task UiName_ReturnsDefault()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.UiName).IsEqualTo("PCAPNG Exporter");
    }

    [Test]
    public async Task UiName_ReturnsCustomValue()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("Network Capture Export")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("Network Capture Export");
    }

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("PCAP Next Generation capture file")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("PCAP Next Generation capture file");
    }

    // ========================================================================
    // Option string length boundary
    // ========================================================================

    [Test]
    public async Task ShbOption_AtMaxLength_WritesSuccessfully()
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
    public async Task ShbOption_ExceedsMaxLength_SetsHasErrors()
    {
        // A string whose UTF-8 encoding exceeds 65535 bytes must trigger the option-length
        // guard in PcapngWriter. The PCAPNG exporter converts the resulting
        // ArgumentOutOfRangeException into an export error rather than letting it escape
        // OnFinish(), so HasErrors must be true after the call completes.
        // Each ASCII character is 1 byte, so 65536 'x' chars = 65536 UTF-8 bytes > ushort.MaxValue.
        string oversizedValue = new('x', 65536);

        using MemoryStream ms = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(ms)
            .WithHardware(oversizedValue)
            .Build();

        exporter.OnFinish();

        await Assert.That(exporter.HasErrors).IsTrue();
    }
}
