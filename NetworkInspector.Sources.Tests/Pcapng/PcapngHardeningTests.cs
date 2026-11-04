// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Hostile or broken PCAPNG input and lifecycle edges: a malformed Interface Description Block keeps its index, an empty
/// file takes the mapped path without leaking, a disposed lazy source does not claim a total, and the factories reject
/// null names.
/// </summary>
internal sealed class PcapngHardeningTests
{
    #region Helpers

    private static readonly byte[] _EthernetFrame = TestFrames.BuildEthernetFrame(
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x11, 0x22, 0x33, 0x44, 0x55], 0x0800, [1, 2, 3, 4]);

    private static readonly byte[] _CanFrame = TestFrames.BuildSocketCanClassic(0x123, [1, 2, 3, 4, 5, 6, 7, 8]);

    /// <summary>An Interface Description Block that is only its 12-byte frame: too short to hold a link type.</summary>
    private static byte[] _MalformedIdb()
    {
        byte[] block = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x0000_0001);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 12);
        return block;
    }

    /// <summary>
    /// Interface 0: Ethernet. Interface 1: malformed IDB. Interface 2: SocketCAN. One packet on each of 1 and 2, then one on 0.
    /// </summary>
    private static byte[] _CaptureWithMalformedMiddleInterface()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet);
        writer.WriteRawBlock(_MalformedIdb());
        writer.AddInterface(LinkType.CanSocketcan);
        writer.WriteFrame(1, 1_000_000_000, _EthernetFrame);
        writer.WriteFrame(2, 2_000_000_000, _CanFrame);
        writer.WriteFrame(0, 3_000_000_000, _EthernetFrame);
        return writer.Build();
    }

    private static List<Frame> _Drain(IFrameSource source)
    {
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    #endregion

    #region Malformed Interface Description Block

    [Test]
    [Arguments(ScanMode.Full)]
    [Arguments(ScanMode.Lazy)]
    public async Task FileSourceKeepsTheIndexOfAMalformedInterfaceBlock(ScanMode scanMode)
    {
        byte[] data = _CaptureWithMalformedMiddleInterface();
        using TempCaptureFile file = new(data, ".pcapng");
        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { ScanMode = scanMode });
        List<FrameReadErrorEventArgs> skips = [];
        source.FrameSkipped += (_, error) => skips.Add(error);
        _ = SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _Drain(source);

        // The packet on the malformed interface is skipped; the packet on interface 2 keeps the SocketCAN interface of the
        // third IDB (it used to be unresolved or attributed to the wrong interface); interface 0 is untouched.
        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[0].Data.Span.SequenceEqual(_CanFrame)).IsTrue();
        await Assert.That(frames[1].LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(skips.Count(skip => skip.Kind == FrameReadErrorKind.MalformedHeader)).IsEqualTo(1);
        await Assert.That(skips.Count(skip => skip.Kind == FrameReadErrorKind.UnresolvedInterface)).IsEqualTo(1);
    }

    [Test]
    public async Task FileSourceRandomAccessSkipsThePacketOfAMalformedInterface()
    {
        byte[] data = _CaptureWithMalformedMiddleInterface();
        using PcapngSource source = PcapngSource.FromData(data, "malformed-idb.pcapng");
        _ = SourceTestFixture.InitializeAndStartSource(source);

        int indexed = source.EstimatedFrameCount ?? -1;

        // Only the packets of usable interfaces are indexed, in file order.
        await Assert.That(indexed).IsEqualTo(2);
        await Assert.That(source.FrameById(0)!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(source.FrameById(1)!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
    }

    [Test]
    public async Task StreamSourceKeepsTheIndexOfAMalformedInterfaceBlock()
    {
        byte[] data = _CaptureWithMalformedMiddleInterface();
        using PcapngStreamSource source = PcapngStreamSource.FromStream(new MemoryStream(data), "malformed-idb.pcapng");
        List<FrameReadErrorEventArgs> skips = [];
        source.FrameSkipped += (_, error) => skips.Add(error);
        _ = SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _Drain(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[1].LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(skips.Count(skip => skip.Kind == FrameReadErrorKind.MalformedHeader)).IsEqualTo(1);
        await Assert.That(skips.Count(skip => skip.Kind == FrameReadErrorKind.UnresolvedInterface)).IsEqualTo(1);
    }

    #endregion

    #region Empty file and the mapped backend

    [Test]
    public async Task MmapPoolRejectsAnEmptyFileAndReleasesTheFile()
    {
        using TempCaptureFile file = new([], ".pcapng");

        await Assert.That(() => new MmapPool(file.Path)).Throws<IOException>();

        // A FileStream that was left open (FileShare.Read) would make this delete fail on Windows.
        File.Delete(file.Path);
        await Assert.That(File.Exists(file.Path)).IsFalse();
    }

    [Test]
    public async Task MmapPoolStillMapsANonEmptyFile()
    {
        using TempCaptureFile file = new([1, 2, 3, 4], ".bin");
        using MmapPool pool = new(file.Path);

        byte[] bytes = pool.GetPrimarySpan(1, 2).ToArray();

        await Assert.That(pool.FileSize).IsEqualTo(4L);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 2, 3 });
    }

    [Test]
    public async Task EmptyFileOnTheMappedPathIsReportedLikeAPreloadedEmptyFile()
    {
        using TempCaptureFile file = new([], ".pcapng");
        PcapException? preloaded = await Assert.That(() => PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = 1024 }))
            .Throws<PcapException>();

        PcapException? mapped = await Assert.That(() => PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null }))
            .Throws<PcapException>();

        await Assert.That(mapped!.Message).IsEqualTo(preloaded!.Message);
    }

    [Test]
    public async Task EmptyBlfFileOnTheMappedPathIsReportedAsABlfError()
    {
        using TempCaptureFile file = new([], ".blf");

        await Assert.That(() => BlfSource.Open(file.Path, new BlfSourceOptions { PreloadBudget = null })).Throws<BlfException>();

        File.Delete(file.Path);
        await Assert.That(File.Exists(file.Path)).IsFalse();
    }

    #endregion

    #region Lifecycle

    [Test]
    public async Task DisposedHalfScannedLazySourceKeepsItsEstimateUnknown()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet);
        for (int i = 0; i < 20; i++)
        {
            writer.WriteFrame(0, (i + 1) * 1_000_000_000L, _EthernetFrame);
        }

        using TempCaptureFile file = new(writer.Build(), ".pcapng");
        PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { ScanMode = ScanMode.Lazy });
        _ = SourceTestFixture.InitializeAndStartSource(source);
        _ = source.NextFrame();
        int? whileRunning = source.EstimatedFrameCount;

        source.Dispose();
        int? afterDispose = source.EstimatedFrameCount;

        await Assert.That(whileRunning).IsNull();
        await Assert.That(afterDispose).IsNull();
    }

    [Test]
    public async Task DisposedFullyScannedSourceKeepsItsCount()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet);
        for (int i = 0; i < 5; i++)
        {
            writer.WriteFrame(0, (i + 1) * 1_000_000_000L, _EthernetFrame);
        }

        PcapngSource source = PcapngSource.FromData(writer.Build(), "full.pcapng");
        _ = SourceTestFixture.InitializeAndStartSource(source);

        source.Dispose();

        await Assert.That(source.EstimatedFrameCount).IsEqualTo(5);
    }

    [Test]
    public async Task StreamFactoryRejectsANullName()
    {
        await Assert.That(() => PcapngStreamSource.FromStream(new MemoryStream(), null!)).Throws<ArgumentNullException>();
    }

    #endregion
}
