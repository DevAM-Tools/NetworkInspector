// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Robustness tests for the BLF readers: Ethernet object types 121 to 123, classic CAN DLC 9 to 15, skip events for
/// truncated and unparsable objects (file and stream), saturating absolute timestamps, oversized container headers,
/// one decompression per container, and buffered reads from unbuffered streams.
/// </summary>
internal sealed class BlfRobustnessTests
{
    #region Fixtures

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    #endregion

    #region Ethernet types 121 to 123

    [Test]
    public async Task EthernetForwardedAndErrorObjectsAreRead()
    {
        uint[] types =
        [
            BlfConstants.ObjTypeEthernetFrameForwarded,
            BlfConstants.ObjTypeEthernetErrorEx,
            BlfConstants.ObjTypeEthernetErrorForwarded,
        ];
        BlfTestGenerator gen = new();
        List<byte[]> expected = [];
        for (int i = 0; i < types.Length; i++)
        {
            byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, 0x42]);
            expected.Add(eth);
            _ = gen.AddEthernetFrameEx(types[i], 2, eth, (i + 1) * 1_000_000L);
        }

        byte[] data = gen.Build();
        using BlfSource file = BlfSource.FromData(data, "eth.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        using BlfStreamSource stream = BlfStreamSource.FromStream(new MemoryStream(data));

        foreach (IFrameSource source in new IFrameSource[] { file, stream })
        {
            List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);
            await Assert.That(skips.Count).IsEqualTo(0);
            await Assert.That(frames.Count).IsEqualTo(types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                await Assert.That(frames[i].LinkType).IsEqualTo(LinkType.Ethernet);
                await Assert.That(frames[i].Data.Span.SequenceEqual(expected[i])).IsTrue();
            }
        }
    }

    #endregion

    #region Classic CAN DLC 9 to 15

    [Test]
    public async Task CanMessageWithDlc15KeepsTheBusDlcInLen8Dlc()
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        payload[3] = 15;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x123);
        payload.AsSpan(8, 8).Fill(0xA5);

        bool ok = CanParser.TryParseCanMessage(payload, out byte[] frame, out _);

        // Byte 4 is the payload count (8), byte 7 keeps the bus DLC so writers can restore it.
        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[BlfConstants.SocketCanLenOffset]).IsEqualTo((byte)8);
        await Assert.That(frame[BlfConstants.SocketCanLen8DlcOffset]).IsEqualTo((byte)15);
        await Assert.That(BlfConstants.ClassicCanDlc(frame)).IsEqualTo((byte)15);
    }

    [Test]
    public async Task CanMessageWithDlc8LeavesLen8DlcZero()
    {
        byte[] payload = new byte[16];
        payload[3] = 8;

        bool ok = CanParser.TryParseCanMessage(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[BlfConstants.SocketCanLenOffset]).IsEqualTo((byte)8);
        await Assert.That(frame[BlfConstants.SocketCanLen8DlcOffset]).IsEqualTo((byte)0);
        await Assert.That(BlfConstants.ClassicCanDlc(frame)).IsEqualTo((byte)8);
    }

    #endregion

    #region Skip events for truncated and unparsable objects

    [Test]
    public async Task ObjectCutByTheEndOfTheFileIsReported()
    {
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x100, [1, 2, 3, 4]);
        byte[] full = new BlfTestGenerator()
            .AddCanFrame(1, can, 1_000_000)
            .AddCanFrame(1, can, 2_000_000)
            .Build();
        byte[] cut = full.AsSpan(0, full.Length - 6).ToArray();

        using BlfSource file = BlfSource.FromData(cut, "cut.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        using BlfStreamSource stream = BlfStreamSource.FromStream(new MemoryStream(cut));

        foreach (IFrameSource source in new IFrameSource[] { file, stream })
        {
            List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);
            await Assert.That(frames.Count).IsEqualTo(1);
            await Assert.That(skips.Count).IsEqualTo(1);
            await Assert.That(skips[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);
        }
    }

    [Test]
    public async Task ShortRawCanObjectIsReportedAndReadingContinues()
    {
        // A CAN message object whose payload is too short to hold the id and data must raise a skip event, not vanish.
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x200, [9]);
        byte[] data = new BlfTestGenerator()
            .AddRawObject(BlfConstants.ObjTypeCanMessage, 1_000_000, [1, 0, 0, 8])
            .AddCanFrame(1, can, 2_000_000)
            .Build();

        using BlfSource file = BlfSource.FromData(data, "short.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        using BlfStreamSource stream = BlfStreamSource.FromStream(new MemoryStream(data));

        foreach (IFrameSource source in new IFrameSource[] { file, stream })
        {
            List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);
            await Assert.That(frames.Count).IsEqualTo(1);
            await Assert.That(skips.Count).IsEqualTo(1);
            await Assert.That(skips[0].Kind).IsEqualTo(FrameReadErrorKind.MalformedHeader);
        }
    }

    [Test]
    [Arguments(ScanMode.Full)]
    [Arguments(ScanMode.Lazy)]
    public async Task StrictModeYieldsTheFramesBeforeTheFirstError(ScanMode scanMode)
    {
        // The full scan finds the bad object at open; strict mode must still stop at it in file order, like the lazy scan.
        byte[] data = new BlfTestGenerator()
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x101, [1]), 1_000_000)
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x102, [2]), 2_000_000)
            .AddRawObject(BlfConstants.ObjTypeCanMessage, 3_000_000, [1, 0, 0, 8])
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x104, [4]), 4_000_000)
            .Build();

        using BlfSource source = BlfSource.FromData(
            data, "strict.blf", new BlfSourceOptions { ScanMode = scanMode, ErrorTolerance = ErrorToleranceMode.Strict });
        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(skips.Count).IsEqualTo(1);
        await Assert.That(source.NextFrame()).IsNull();
    }

    [Test]
    [Arguments(ScanMode.Full)]
    [Arguments(ScanMode.Lazy)]
    public async Task StrictModeStopsAtAFailureInsideAContainer(ScanMode scanMode)
    {
        // One container indexes all its frames in one scan step; the failure in the middle must still be reported
        // after the two frames in front of it, not before the container's first frame.
        BlfTestGenerator inner = new BlfTestGenerator()
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x101, [1]), 1_000_000)
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x102, [2]), 2_000_000)
            .AddRawObject(BlfConstants.ObjTypeCanMessage, 3_000_000, [1])
            .AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x104, [4]), 4_000_000);
        byte[] data = new BlfTestGenerator().AddLogContainer(BlfConstants.CompressionNone, inner).Build();

        using BlfSource strict = BlfSource.FromData(
            data, "strict.blf", new BlfSourceOptions { ScanMode = scanMode, ErrorTolerance = ErrorToleranceMode.Strict });
        using BlfSource tolerant = BlfSource.FromData(data, "tolerant.blf", new BlfSourceOptions { ScanMode = scanMode });
        List<Frame> strictFrames = _Drain(strict, out List<FrameReadErrorEventArgs> strictSkips);
        List<Frame> tolerantFrames = _Drain(tolerant, out List<FrameReadErrorEventArgs> tolerantSkips);

        await Assert.That(strictFrames.Count).IsEqualTo(2);
        await Assert.That(strictSkips.Count).IsEqualTo(1);
        await Assert.That(tolerantFrames.Count).IsEqualTo(3);
        await Assert.That(tolerantSkips.Count).IsEqualTo(1);
    }

    #endregion

    #region Timestamps and container limits

    [Test]
    public async Task AbsoluteTimestampSaturatesInsteadOfWrapping()
    {
        await Assert.That(BlfTimestamp.ToAbsolute(5, 10)).IsEqualTo(15L);
        await Assert.That(BlfTimestamp.ToAbsolute(0, long.MaxValue)).IsEqualTo(long.MaxValue);
        await Assert.That(BlfTimestamp.ToAbsolute(long.MaxValue - 5, 10)).IsEqualTo(long.MaxValue);
        await Assert.That(BlfTimestamp.ToAbsolute(1_000, long.MaxValue)).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task ContainerAboveTheLargestArrayIsAFormatError()
    {
        // With the size limit disabled, a header above Array.MaxLength must fail as BlfException (which the readers
        // turn into a skip event), not as OverflowException or OutOfMemoryException.
        await Assert.That(() => BlfContainer.Decompress([0x78, 0x9C], BlfConstants.CompressionZlib, uint.MaxValue))
            .Throws<BlfException>();
        await Assert.That(() => BlfContainer.Decompress([], BlfConstants.CompressionNone, (uint)Array.MaxLength + 1))
            .Throws<BlfException>();
    }

    #endregion

    #region One decompression per container

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task SequentialReadReusesTheScannerContainer(ScanMode scanMode)
    {
        const int frameCount = 40;
        BlfTestGenerator inner = new();
        for (int i = 0; i < frameCount; i++)
        {
            _ = inner.AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x300u + (uint)i, [(byte)i]), (i + 1) * 1_000L);
        }

        byte[] data = new BlfTestGenerator().AddLogContainer(BlfConstants.CompressionLz4, inner).Build();
        using BlfSource source = BlfSource.FromData(data, "lz4.blf", new BlfSourceOptions { ScanMode = scanMode });

        List<Frame> frames = _Drain(source, out _);
        Frame? random = source.FrameById(new FrameId(frameCount / 2));

        // The scanner inflated the container once; the source neither inflated it again for the sequential read nor
        // for the random access served from the cache.
        await Assert.That(frames.Count).IsEqualTo(frameCount);
        await Assert.That(random).IsNotNull();
        await Assert.That(random!.Value.Data.Span.SequenceEqual(frames[frameCount / 2].Data.Span)).IsTrue();
        await Assert.That(source.SourceDecompressionCount).IsEqualTo(0L);
    }

    #endregion

    #region Options and buffering on streams

    [Test]
    public async Task FromStreamAppliesTheOptions()
    {
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x100, [1]);
        byte[] data = new BlfTestGenerator()
            .AddRawObject(BlfConstants.ObjTypeCanMessage, 1_000_000, [1, 0, 0, 8])
            .AddCanFrame(1, can, 2_000_000)
            .Build();

        using BlfStreamSource source = BlfStreamSource.FromStream(
            new MemoryStream(data), options: new BlfSourceOptions { ErrorTolerance = ErrorToleranceMode.Strict });
        List<Frame> frames = _Drain(source, out List<FrameReadErrorEventArgs> skips);

        await Assert.That(source.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Strict);
        await Assert.That(frames.Count).IsEqualTo(0);
        await Assert.That(skips.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnbufferedStreamIsReadInLargeChunks()
    {
        const int frameCount = 200;
        BlfTestGenerator gen = new();
        for (int i = 0; i < frameCount; i++)
        {
            _ = gen.AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x100u + (uint)i, [(byte)i]), (i + 1) * 1_000L);
        }

        byte[] data = gen.Build();
        using ReadCountingStream stream = new(data);
        using BlfStreamSource source = BlfStreamSource.FromStream(stream, leaveOpen: true);
        List<Frame> frames = _Drain(source, out _);

        // Every object costs at least two reads (header, body) without the internal buffer.
        await Assert.That(frames.Count).IsEqualTo(frameCount);
        await Assert.That(stream.ReadCalls).IsLessThanOrEqualTo(8);
    }

    #endregion

    #region Helpers

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
