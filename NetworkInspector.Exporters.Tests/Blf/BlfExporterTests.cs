// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Tests for the <see cref="BlfExporter"/> — validates file header, Ethernet and CAN frames,
/// timestamps, and structural integrity.
/// </summary>
internal sealed class BlfExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        BlfExporter.Builder builder = BlfExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SingleEthernetFrame_ProducesValidBlf()
    {
        using TestDir dir = new("blf_single");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task MultipleFrames_AllWritten()
    {
        using TestDir dir = new("blf_multi");
        string path = dir.FilePath("output.blf");

        const int count = 20;
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        // Each frame produces at least one BLF object, plus potentially containers
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task CanClassicFrame_ProducesValidBlf()
    {
        using TestDir dir = new("blf_can");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02, 0x03, 0x04]);
        Frame frame = TestHarness.CreateFrame(
            new FrameId(0), 1_000_000_000, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task CanFdFrame_ProducesValidBlf()
    {
        using TestDir dir = new("blf_canfd");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        byte[] canData = SocketCanGenerators.BuildCanFd(
            0x456, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], brs: true);
        Frame frame = TestHarness.CreateFrame(
            new FrameId(0), 1_000_000_000, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
    }

    [Test]
    public async Task FrameCount_TracksCorrectly()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

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
    public async Task FlexRayFrame_ProducesValidBlf()
    {
        using TestDir dir = new("blf_flexray");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, frData, LinkType.Flexray);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task FlexRayFrame_FrameCountTracked()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 5;
        for (int i = 0; i < count; i++)
        {
            byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
                (byte)(i % 2), (ushort)(i + 1), (byte)i, 0x1234, [0x01, 0x02]);
            Frame frame = TestHarness.CreateFrame(
                new FrameId(i), (long)(i + 1) * 1_000_000_000, frData, LinkType.Flexray);
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task LinFrame_ProducesValidBlf()
    {
        using TestDir dir = new("blf_lin");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, linData, LinkType.Lin);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task LinFrame_FrameCountTracked()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 8;
        for (int i = 0; i < count; i++)
        {
            byte[] linData = LinGenerators.BuildLinFrame((byte)(i % 64), [0xAA, 0xBB]);
            Frame frame = TestHarness.CreateFrame(
                new FrameId(i), (long)(i + 1) * 1_000_000_000, linData, LinkType.Lin);
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task MixedFrameTypes_AllWritten()
    {
        using TestDir dir = new("blf_mixed");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();

        // Ethernet frame
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 1_000_000_000, ethData));

        // CAN frame
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x100, [0x01, 0x02]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(1), 2_000_000_000, canData, LinkType.CanSocketcan));

        // FlexRay frame
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(2), 3_000_000_000, frData, LinkType.Flexray));

        // LIN frame
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(3), 4_000_000_000, linData, LinkType.Lin));

        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        await Assert.That(verifier.HasValidHeader).IsTrue();
        // ObjectCount counts top-level BLF objects (containers), not individual frames
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.FrameCount).IsEqualTo(4);
    }

    // ========================================================================
    // Cancellation
    // ========================================================================

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
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
        using BlfExporter exporter = BlfExporter.CreateBuilder()
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
        using BlfExporter exporter = BlfExporter.CreateBuilder()
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
        using BlfExporter exporter = BlfExporter.CreateBuilder()
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
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnFrame_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(1);
        bool accepted = exporter.OnFrame(frames[0]);

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task DoubleFinish_IsIdempotent()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

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
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.UiName).IsEqualTo("BLF Exporter");
    }

    [Test]
    public async Task UiName_ReturnsCustomValue()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("Vehicle Bus Exporter")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("Vehicle Bus Exporter");
    }

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("Binary Logging Format export")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("Binary Logging Format export");
    }

    [Test]
    public async Task EthernetPayloadLongerThanUInt16_IsSkippedAsMalformed()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        // dst(6)+src(6)+ethertype(2) + payload larger than ushort.MaxValue
        byte[] oversized = new byte[14 + ushort.MaxValue + 1];
        oversized.AsSpan(12, 2).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(12), 0x0800);

        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, oversized, LinkType.Ethernet);
        bool cont = exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(cont).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
    }

    [Test]
    public async Task CanXlFrame_TolerantMode_Skipped()
    {
        // CAN XL shares LinkType.CanSocketcan with classic/FD but sets XLF (byte 4, bit 7).
        // BLF has no CAN XL object type in this exporter — must skip, not write classic CAN.
        using TestDir dir = new("blf_canxl");
        string path = dir.FilePath("output.blf");

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        ExportErrorKind? reportedKind = null;
        exporter.ItemSkipped += (_, e) => reportedKind = e.Kind;

        byte[] xlData = SocketCanGenerators.BuildCanXl(0x01, [0xAA, 0xBB, 0xCC, 0xDD]);
        bool accepted = exporter.OnFrame(
            TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, xlData, LinkType.CanSocketcan));
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(reportedKind).IsEqualTo(ExportErrorKind.UnsupportedType);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.HasErrors).IsTrue();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.Open(path);
        // File header only — no classic/FD CAN log objects from the XL frame.
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsEqualTo(0);
    }

    [Test]
    public async Task CanXlFrame_StrictMode_AbortsExport()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        byte[] xlData = SocketCanGenerators.BuildCanXl(0x01, [0xAA, 0xBB, 0xCC, 0xDD]);
        bool accepted = exporter.OnFrame(
            TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, xlData, LinkType.CanSocketcan));
        exporter.OnFinish();

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
    }
}
