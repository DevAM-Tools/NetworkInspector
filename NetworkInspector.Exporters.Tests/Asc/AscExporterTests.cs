// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Asc;

/// <summary>
/// Tests for <see cref="AscExporter"/> — validates line content, hex/decimal encoding,
/// frame statistics, lifecycle transitions, error tolerance, and file output.
/// </summary>
internal sealed class AscExporterTests
{
    // ========================================================================
    // Builder
    // ========================================================================

    [Test]
    public async Task Builder_RequiresOutput()
    {
        AscExporter.Builder builder = AscExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Builder_TargetFrameCount_NegativeThrows()
    {
        AscExporter.Builder builder = AscExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(-1)).Throws<ArgumentOutOfRangeException>();
    }

    // ========================================================================
    // CAN classic
    // ========================================================================

    [Test]
    public async Task SingleCanFrame_WrittenCorrectly()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02, 0x03, 0x04]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // Data frame indicator
        await Assert.That(content).Contains("Rx d");
        // Standard CAN ID in 3-char uppercase hex
        await Assert.That(content).Contains("123 Rx d");
        // Data bytes
        await Assert.That(content).Contains("01 02 03 04");
    }

    [Test]
    public async Task CanExtendedId_HasXSuffix()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x1FFFFFFF, [0xDE, 0xAD], extended: true);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // Extended CAN ID must have 8-char hex + trailing 'x' (no space before x)
        await Assert.That(content).Contains("1FFFFFFFx");
    }

    [Test]
    public async Task CanRemoteFrame_HasRToken()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        // RTR flag is set by BuildCanClassic when canId has bit 30 set — use raw helper
        // SocketCanGenerators.BuildCanClassic with an RTR extended frame:
        // set bit 30 (RTR) in the SocketCAN ID
        const uint rtfCanId = 0x456u; // standard frame
        byte[] canData = SocketCanGenerators.BuildCanClassic(rtfCanId, ReadOnlySpan<byte>.Empty);
        // Manually set the RTR flag (bit 30 in Big-Endian SocketCAN ID at byte 1)
        // SocketCAN ID word in big-endian: byte[0] high, ... byte[3] low.
        // bit30 → 0x40000000 → byte[0] = 0x40
        canData[0] |= 0x40;

        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // RTR frames use 'r' instead of 'd'
        await Assert.That(content).Contains("Rx r");
    }

    // ========================================================================
    // CAN FD
    // ========================================================================

    [Test]
    public async Task SingleCanFdFrame_WrittenCorrectly()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanFd(
            0x456, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], brs: true);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // CAN FD header token
        await Assert.That(content).Contains("CANFD");
        // Standard ID in 3-char uppercase hex (no 'x' suffix)
        await Assert.That(content).Contains("456");
        // Data bytes
        await Assert.That(content).Contains("01 02 03 04 05 06 07 08");
    }

    [Test]
    public async Task CanFdFlags_BrsEsiPreserved()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanFd(0x100, [0xAA, 0xBB], brs: true);
        // ESI flag is bit 1 of fd_flags (byte 5); set manually
        canData[5] |= 0x02;

        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // BRS=1, ESI=1 appear as decimal 0/1 tokens in the CANFD line
        await Assert.That(content).Contains("CANFD");
        // BRS=1 (brs:true was set) and ESI=1 (manually set above)
        // Format: "CANFD {ch} Rx {id} {brs} {esi} ..."
        await Assert.That(content).Contains("1 1");
    }

    // ========================================================================
    // LIN
    // ========================================================================

    [Test]
    public async Task SingleLinFrame_WrittenCorrectly()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] linData = LinGenerators.BuildLinFrame(0x3F, [0x11, 0x22, 0x33], checksum: 0x7A);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, linData, LinkType.Lin);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // LIN channel prefix
        await Assert.That(content).Contains("L1");
        // Frame ID in 2-char uppercase hex (0x3F = 63 decimal → upper 6 bits = 0x3F)
        await Assert.That(content).Contains("3F");
        // Data bytes
        await Assert.That(content).Contains("11 22 33");
        // Checksum field
        await Assert.That(content).Contains("checksum =");
        await Assert.That(content).Contains("7A");
        // Enhanced checksum type
        await Assert.That(content).Contains("CSM = enhanced");
    }

    // ========================================================================
    // FlexRay
    // ========================================================================

    [Test]
    public async Task SingleFlexRayFrame_WrittenCorrectly()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            channel: 0, frameId: 0x000A, cycle: 3, headerCrc: 0x05A3,
            data: [0xDE, 0xAD, 0xBE, 0xEF]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, frData, LinkType.Flexray);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // FlexRay marker and version token
        await Assert.That(content).Contains("Fr");
        await Assert.That(content).Contains("V9");
        // Frame ID: 4-char uppercase hex
        await Assert.That(content).Contains("000A");
        // Header CRC: 11-bit value, 4-char uppercase hex
        await Assert.That(content).Contains("05A3");
        // Data bytes
        await Assert.That(content).Contains("DE AD BE EF");
    }

    // ========================================================================
    // Multiple frames
    // ========================================================================

    [Test]
    public async Task MultipleFrames_AllWritten()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 5;
        for (int i = 0; i < count; i++)
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic((uint)(0x100 + i), [0xAA]);
            Frame frame = TestHarness.CreateFrame(
                new FrameId(i), (long)(i + 1) * 1_000_000L, canData, LinkType.CanSocketcan);
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
    }

    // ========================================================================
    // Empty export
    // ========================================================================

    [Test]
    public async Task EmptyExport_ProducesValidFile()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // Must contain both the opening and closing block markers
        await Assert.That(content).Contains("Begin Triggerblock");
        await Assert.That(content).Contains("End TriggerBlock");
    }

    // ========================================================================
    // File output
    // ========================================================================

    [Test]
    public async Task WriteToFile_CreatesFile()
    {
        using TestDir dir = new("asc_file");
        string path = dir.FilePath("output.asc");

        using AscExporter exporter = AscExporter.CreateBuilder().ToFile(path).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 1_000_000_000L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(File.Exists(path)).IsTrue();
        long size = new FileInfo(path).Length;
        await Assert.That(size).IsGreaterThan(0L);
    }

    [Test]
    public async Task WriteToStream_VerifyContent()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x7FF, [0xAB, 0xCD]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // Header lines
        await Assert.That(content).Contains("base hex  timestamps absolute");
        await Assert.That(content).Contains("no internal events logged");
        await Assert.That(content).Contains("Begin Triggerblock");
        await Assert.That(content).Contains("End TriggerBlock");
        // At least one data line
        await Assert.That(content).Contains("7FF Rx d");
    }

    // ========================================================================
    // Cancellation
    // ========================================================================

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x100, [0x01]);
        Frame frame0 = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        Frame frame1 = TestHarness.CreateFrame(new FrameId(1), 1_000_000L, canData, LinkType.CanSocketcan);
        Frame frame2 = TestHarness.CreateFrame(new FrameId(2), 2_000_000L, canData, LinkType.CanSocketcan);

        exporter.OnFrame(frame0);
        exporter.OnFrame(frame1);
        await cts.CancelAsync().ConfigureAwait(false);

        bool accepted = exporter.OnFrame(frame2);
        await Assert.That(accepted).IsFalse();
        exporter.OnFinish();
    }

    [Test]
    public async Task IsFinished_TrueAfterCancellation()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder()
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
    public async Task TargetCount_StopsAtN()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetFrameCount(3)
            .Build();

        for (int i = 0; i < 10; i++)
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic((uint)(0x100 + i), [0xAA]);
            Frame frame = TestHarness.CreateFrame(
                new FrameId(i), (long)i * 1_000_000L, canData, LinkType.CanSocketcan);
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        await Assert.That(exporter.WrittenCount).IsEqualTo(3L);
    }

    [Test]
    public async Task IsFinished_TrueAfterTargetReached()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetFrameCount(2)
            .Build();

        for (int i = 0; i < 5; i++)
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic((uint)(0x100 + i), [0xBB]);
            Frame frame = TestHarness.CreateFrame(
                new FrameId(i), (long)i * 1_000_000L, canData, LinkType.CanSocketcan);
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    // ========================================================================
    // Lifecycle
    // ========================================================================

    [Test]
    public async Task IsFinished_FalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnFrame_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x200, [0x01]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        bool accepted = exporter.OnFrame(frame);

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task DoubleFinish_IsIdempotent()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x300, [0x02]);
        Frame frame = TestHarness.CreateFrame(new FrameId(0), 0L, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);

        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(1L);
    }

    // ========================================================================
    // Statistics
    // ========================================================================

    [Test]
    public async Task Statistics_Correct()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        // 3 valid CAN frames
        for (int i = 0; i < 3; i++)
        {
            byte[] canData = SocketCanGenerators.BuildCanClassic((uint)(0x100 + i), [0x01]);
            exporter.OnFrame(TestHarness.CreateFrame(
                new FrameId(i), (long)i * 1_000_000L, canData, LinkType.CanSocketcan));
        }

        // 2 unsupported (Ethernet) frames — produce skips
        for (int i = 0; i < 2; i++)
        {
            byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
            exporter.OnFrame(TestHarness.CreateFrame(
                new FrameId(100 + i), (long)(10 + i) * 1_000_000L, ethData));
        }

        exporter.OnFinish();

        await Assert.That(exporter.WrittenCount).IsEqualTo(3L);
        await Assert.That(exporter.SkippedCount).IsEqualTo(2L);
        await Assert.That(exporter.ErrorCount).IsEqualTo(2L);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    // ========================================================================
    // UiName / Description
    // ========================================================================

    [Test]
    public async Task UiName_ReturnsDefault()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.UiName).IsEqualTo("ASC Exporter");
    }

    [Test]
    public async Task UiName_Description_Preserved()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My ASC Export")
            .WithDescription("Test capture")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My ASC Export");
        await Assert.That(exporter.Description).IsEqualTo("Test capture");
    }

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.Description).IsNull();
    }

    // ========================================================================
    // Unsupported link type
    // ========================================================================

    [Test]
    public async Task UnsupportedLinkType_Skipped()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, e) =>
        {
            if (e.Kind == ExportErrorKind.UnsupportedType)
            {
                Interlocked.Increment(ref skippedRaised);
            }
        };

        // Ethernet frame — not supported by ASC exporter
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 0L, ethData));
        exporter.OnFinish();

        await Assert.That(exporter.SkippedCount).IsEqualTo(1L);
        await Assert.That(skippedRaised).IsEqualTo(1);
    }

    [Test]
    public async Task CanXlFrame_TolerantMode_Skipped()
    {
        // CAN XL shares LinkType.CanSocketcan with classic/FD but sets XLF (byte 4, bit 7).
        // The ASC format has no CAN XL representation; the exporter must count it as skipped.
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        ExportErrorKind? reportedKind = null;
        exporter.ItemSkipped += (_, e) => reportedKind = e.Kind;

        byte[] xlData = SocketCanGenerators.BuildCanXl(0x01, [0xAA, 0xBB, 0xCC, 0xDD]);
        bool accepted = exporter.OnFrame(
            TestHarness.CreateFrame(new FrameId(0), 1_000_000L, xlData, LinkType.CanSocketcan));
        exporter.OnFinish();

        // Tolerant mode: caller is told to continue, frame is counted as skipped, not written.
        await Assert.That(accepted).IsTrue();
        await Assert.That(reportedKind).IsEqualTo(ExportErrorKind.UnsupportedType);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1L);
        await Assert.That(exporter.WrittenCount).IsEqualTo(0L);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task CanXlFrame_StrictMode_AbortsExport()
    {
        // In Strict mode the exporter must immediately abort when a CAN XL frame arrives.
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        byte[] xlData = SocketCanGenerators.BuildCanXl(0x01, [0xAA, 0xBB, 0xCC, 0xDD]);
        bool accepted = exporter.OnFrame(
            TestHarness.CreateFrame(new FrameId(0), 1_000_000L, xlData, LinkType.CanSocketcan));
        exporter.OnFinish();

        // Strict mode: OnFrame returns false, export is finished in error state.
        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // Error tolerance
    // ========================================================================

    [Test]
    public async Task ErrorTolerance_TolerantFiresEvent()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        // Ethernet frame is unsupported → skipped + event raised
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 0L, ethData));

        // Tolerant mode: OnFrame still returns true so the loop can continue
        await Assert.That(accepted).IsTrue();
        await Assert.That(skippedRaised).IsEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
        exporter.OnFinish();
    }

    [Test]
    public async Task ErrorTolerance_StrictAbortsOnError()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        // Ethernet frame is unsupported → strict mode sets error and aborts
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        bool accepted = exporter.OnFrame(TestHarness.CreateFrame(new FrameId(0), 0L, ethData));

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    // ========================================================================
    // Mixed frame types
    // ========================================================================

    [Test]
    public async Task MixedFrameTypes_AllWritten()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        // CAN classic
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x100, [0x01, 0x02]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(0), 1_000_000L, canData, LinkType.CanSocketcan));

        // CAN FD
        byte[] fdData = SocketCanGenerators.BuildCanFd(0x200, [0xAA, 0xBB, 0xCC, 0xDD]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(1), 2_000_000L, fdData, LinkType.CanSocketcan));

        // LIN
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(2), 3_000_000L, linData, LinkType.Lin));

        // FlexRay
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 5, 0x1234, [0xDE, 0xAD, 0xBE, 0xEF]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(3), 4_000_000L, frData, LinkType.Flexray));

        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        await Assert.That(exporter.WrittenCount).IsEqualTo(4L);
        await Assert.That(content).Contains("Rx d");
        await Assert.That(content).Contains("CANFD");
        await Assert.That(content).Contains("CSM = enhanced");
        await Assert.That(content).Contains("Fr");
    }

    // ========================================================================
    // Line endings
    // ========================================================================

    [Test]
    public async Task LineEndings_AreCrLf()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();
        exporter.OnFinish();

        byte[] bytes = ms.ToArray();

        // Every line must end with \r\n
        bool hasCrLf = false;
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A)
            {
                hasCrLf = true;
                break;
            }
        }

        await Assert.That(hasCrLf).IsTrue();
    }

    // ========================================================================
    // Timestamp
    // ========================================================================

    [Test]
    public async Task Timestamp_RelativeToFirstFrame()
    {
        using MemoryStream ms = new();
        using AscExporter exporter = AscExporter.CreateBuilder().ToStream(ms).Build();

        // First frame at t=2s, second at t=3s → second frame should show 1.000000
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x111, [0x01]);
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(0), 2_000_000_000L, canData, LinkType.CanSocketcan));
        exporter.OnFrame(TestHarness.CreateFrame(
            new FrameId(1), 3_000_000_000L, canData, LinkType.CanSocketcan));
        exporter.OnFinish();

        string content = Encoding.UTF8.GetString(ms.ToArray());

        // First frame: timestamp = 0.000000
        await Assert.That(content).Contains("0.000000");
        // Second frame: timestamp = 1.000000
        await Assert.That(content).Contains("1.000000");
    }
}
