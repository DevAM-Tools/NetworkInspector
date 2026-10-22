// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Robustness tests for the PCAPNG readers: options reach the source on every factory, strict mode stops at the first
/// error in file order, legacy PCAP records are bounded and truncation is reported, the interface count is capped, and
/// unbuffered streams are read in large chunks.
/// </summary>
internal sealed class PcapngSourceRobustnessTests
{
    #region Fixtures

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    #endregion

    #region Options on every factory

    [Test]
    public async Task OpenAndFromDataApplyTheErrorToleranceOption()
    {
        byte[] data = _BuildCaptureWithUndeclaredInterface();
        PcapngSourceOptions strict = new() { ErrorTolerance = ErrorToleranceMode.Strict };
        using TempCaptureFile file = new(data, ".pcapng");

        using PcapngSource opened = PcapngSource.Open(file.Path, strict);
        using PcapngSource fromData = PcapngSource.FromData(data, "strict.pcapng", strict);
        using PcapngSource defaults = PcapngSource.Open(file.Path);

        await Assert.That(opened.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Strict);
        await Assert.That(fromData.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Strict);
        await Assert.That(defaults.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Tolerant);
    }

    [Test]
    public async Task FromStreamAppliesTheErrorToleranceOption()
    {
        byte[] data = _BuildCaptureWithUndeclaredInterface();

        using PcapngStreamSource source = PcapngStreamSource.FromStream(
            new MemoryStream(data), options: new PcapngSourceOptions { ErrorTolerance = ErrorToleranceMode.Strict });
        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(source.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Strict);
        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(skips.Count).IsEqualTo(1);
    }

    #endregion

    #region Strict mode in file order

    [Test]
    [Arguments(ScanMode.Full)]
    [Arguments(ScanMode.Lazy)]
    public async Task StrictModeYieldsTheFramesBeforeTheFirstError(ScanMode scanMode)
    {
        // A full scan finds the error at open; strict mode must still deliver the frames in front of it, like the
        // lazy scan and the stream reader do.
        using TempCaptureFile file = new(_BuildCaptureWithUndeclaredInterface(), ".pcapng");
        using PcapngSource source = PcapngSource.Open(
            file.Path, new PcapngSourceOptions { ScanMode = scanMode, ErrorTolerance = ErrorToleranceMode.Strict });

        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(source.NextFrame()).IsNull();
    }

    [Test]
    public async Task TolerantModeSkipsTheBadBlockAndKeepsReading()
    {
        using PcapngSource source = PcapngSource.FromData(_BuildCaptureWithUndeclaredInterface(), "tolerant.pcapng");

        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(source.SkippedFrameCount).IsEqualTo(1);
    }

    #endregion

    #region Legacy PCAP records

    [Test]
    public async Task LegacyInclLenAboveSnapLenIsAMalformedHeader()
    {
        byte[] ok = TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [1, 2]);
        byte[] tooLong = new byte[100];
        byte[] data = _BuildLegacyPcap(snapLen: 64, ok, tooLong);

        using PcapngSource source = PcapngSource.FromData(data, "legacy.pcap");
        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(skips[0].Kind).IsEqualTo(FrameReadErrorKind.MalformedHeader);
    }

    [Test]
    public async Task LegacyRecordCutByTheEndOfTheFileIsReportedAsTruncated()
    {
        byte[] ok = TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [1, 2]);
        byte[] full = _BuildLegacyPcap(snapLen: 65535, ok, new byte[50]);
        byte[] cut = full.AsSpan(0, full.Length - 40).ToArray();

        using TempCaptureFile file = new(cut, ".pcap");
        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null });
        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        // The cut record is reported, never returned as a shortened frame.
        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(skips[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);
    }

    #endregion

    #region Interface count cap

    [Test]
    public async Task SectionRejectsAnInterfaceBeyondTheIndexRange()
    {
        // Frame index entries store the interface index as ushort; interface 65 536 must not wrap onto interface 0.
        SectionInfo section = new(byteSwapped: false, sectionLength: -1, startOffset: 0);
        int last = -1;
        for (int i = 0; i < SectionInfo.MaxInterfaceCount; i++)
        {
            last = section.AddInterface(new InterfaceInfo((ushort)LinkType.Ethernet, 0));
        }

        int overflow = section.AddInterface(new InterfaceInfo((ushort)LinkType.Ethernet, 0));

        await Assert.That(last).IsEqualTo(ushort.MaxValue);
        await Assert.That(overflow).IsEqualTo(-1);
        await Assert.That(section.Interface(ushort.MaxValue)).IsNotNull();
    }

    #endregion

    #region Unbuffered streams

    [Test]
    public async Task UnbufferedStreamIsReadInLargeChunks()
    {
        const int frameCount = 200;
        using PcapngCaptureBuilder writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < frameCount; i++)
        {
            writer.WriteFrame(0, (i + 1) * 1_000L, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i]));
        }

        byte[] data = writer.Build();
        using ReadCountingStream stream = new(data);
        using PcapngStreamSource source = PcapngStreamSource.FromStream(stream, leaveOpen: true);
        List<Frame> frames = _Drain(source, out _);

        // Without the internal buffer every block costs several small reads (header, body, trailer).
        int chunkBound = (data.Length / PcapngStreamSource.StreamBufferSize) + 4;
        await Assert.That(frames.Count).IsEqualTo(frameCount);
        await Assert.That(stream.ReadCalls).IsLessThanOrEqualTo(chunkBound);
    }

    #endregion

    #region Helpers

    /// <summary>Frames 0 and 1 on interface 0, one EPB on undeclared interface 7, then frame 2 on interface 0.</summary>
    private static byte[] _BuildCaptureWithUndeclaredInterface()
    {
        using PcapngCaptureBuilder writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0]));
        writer.WriteFrame(0, 2_000, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [1]));
        writer.WriteFrame(7, 3_000, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xBA, 0xD0]));
        writer.WriteFrame(0, 4_000, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [2]));
        return writer.Build();
    }

    /// <summary>Legacy little-endian microsecond PCAP with Ethernet link type and one record per payload.</summary>
    private static byte[] _BuildLegacyPcap(uint snapLen, params byte[][] records)
    {
        using MemoryStream ms = new();
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA1B2_C3D4u);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], snapLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
        ms.Write(header);

        Span<byte> record = stackalloc byte[16];
        for (int i = 0; i < records.Length; i++)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)records[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)records[i].Length);
            ms.Write(record);
            ms.Write(records[i]);
        }

        return ms.ToArray();
    }

    private static List<Frame> _Drain(IFrameSource source, out List<FrameReadErrorEventArgs> skips)
    {
        List<FrameReadErrorEventArgs> collected = [];
        if (source is IErrorTolerantFrameSource tolerant)
        {
            tolerant.FrameSkipped += (_, e) => collected.Add(e);
        }

        SourceTestFixture.InitializeAndStartSource(source);
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        skips = collected;
        return frames;
    }

    #endregion
}
