// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// End-to-end tests for the <c>FrameSlabSize</c> option: with slabs on, sequentially read frames share backing arrays;
/// with slabs off (the default) every frame owns its bytes; random access never uses slabs. Covers every source that
/// copies frame bytes (PCAPNG mapped and stream, BLF file and stream, ASC file and stream).
/// </summary>
internal sealed class FrameSlabSharingTests
{
    #region Constants and fixtures

    private const int _SlabSize = 4096;
    private const int _FrameCount = 6;

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private const string _AscText =
        "base hex timestamps absolute\n" +
        "Begin Triggerblock\n" +
        "0.100000 1 100 Rx d 2 01 02\n" +
        "0.200000 1 200 Rx d 2 03 04\n" +
        "0.300000 1 300 Rx d 2 05 06\n" +
        "0.400000 1 400 Rx d 2 07 08\n" +
        "0.500000 1 500 Rx d 2 09 0A\n" +
        "0.600000 1 600 Rx d 2 0B 0C\n" +
        "End TriggerBlock\n";

    #endregion

    #region PCAPNG

    [Test]
    public async Task PcapngMappedSlabsShareArraysAndRandomAccessDoesNot()
    {
        using TempCaptureFile file = new(_BuildPcapng(), ".pcapng");
        PcapngSourceOptions options = new() { PreloadBudget = null, FrameSlabSize = _SlabSize };

        using PcapngSource source = PcapngSource.Open(file.Path, options);
        List<Frame> frames = _Drain(source);

        await _AssertSharedArrays(frames, expectedShared: true);

        // FrameById runs on any thread and must not touch the sequential reader's allocator.
        Frame? random = source.FrameById(new FrameId(1));
        await Assert.That(random).IsNotNull();
        await Assert.That(_ArrayOf(random!.Value)).IsNotSameReferenceAs(_ArrayOf(frames[0]));
        await Assert.That(random.Value.Data.Span.SequenceEqual(frames[1].Data.Span)).IsTrue();
    }

    [Test]
    public async Task PcapngMappedDefaultGivesEveryFrameItsOwnArray()
    {
        using TempCaptureFile file = new(_BuildPcapng(), ".pcapng");

        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null });
        await _AssertSharedArrays(_Drain(source), expectedShared: false);
    }

    [Test]
    public async Task PcapngStreamSlabsShareArrays()
    {
        byte[] data = _BuildPcapng();

        using PcapngStreamSource withSlabs = PcapngStreamSource.FromStream(
            new MemoryStream(data), options: new PcapngSourceOptions { FrameSlabSize = _SlabSize });
        using PcapngStreamSource withoutSlabs = PcapngStreamSource.FromStream(new MemoryStream(data));

        await _AssertSharedArrays(_Drain(withSlabs), expectedShared: true);
        await _AssertSharedArrays(_Drain(withoutSlabs), expectedShared: false);
    }

    #endregion

    #region BLF

    [Test]
    public async Task BlfFileAndStreamSlabsShareArrays()
    {
        byte[] data = _BuildBlfCan();
        BlfSourceOptions options = new() { ScanMode = ScanMode.Full, FrameSlabSize = _SlabSize };

        using BlfSource file = BlfSource.FromData(data, "slab.blf", options);
        using BlfStreamSource stream = BlfStreamSource.FromStream(new MemoryStream(data), options: options);
        using BlfSource fileDefault = BlfSource.FromData(data, "plain.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });

        await _AssertSharedArrays(_Drain(file), expectedShared: true);
        await _AssertSharedArrays(_Drain(stream), expectedShared: true);
        await _AssertSharedArrays(_Drain(fileDefault), expectedShared: false);
    }

    [Test]
    public async Task BlfType120WithSlabsCopiesInsteadOfAliasingTheFileBuffer()
    {
        // Type 120 frames alias the in-memory file without slabs. With slabs they are copied, so a held frame pins
        // one slab instead of the whole file buffer.
        BlfTestGenerator gen = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, 0xEE]);
            _ = gen.AddEthernetFrameEx(BlfConstants.ObjTypeEthernetFrameEx, 1, eth, (i + 1) * 1_000_000L);
        }

        byte[] data = gen.Build();
        using BlfSource source = BlfSource.FromData(
            data, "eth.blf", new BlfSourceOptions { ScanMode = ScanMode.Full, FrameSlabSize = _SlabSize });

        List<Frame> frames = _Drain(source);
        await _AssertSharedArrays(frames, expectedShared: true);
        await Assert.That(_ArrayOf(frames[0])).IsNotSameReferenceAs(data);
    }

    #endregion

    #region ASC

    [Test]
    public async Task AscFileAndStreamSlabsShareArrays()
    {
        AscSourceOptions options = new() { FrameSlabSize = _SlabSize };

        using AscSource file = AscSource.FromText(_AscText, "slab.asc", options);
        using AscStreamSource stream = AscStreamSource.FromStream(
            new MemoryStream(Encoding.Latin1.GetBytes(_AscText)), options: options);
        using AscSource fileDefault = AscSource.FromText(_AscText, "plain.asc");

        await _AssertSharedArrays(_Drain(file), expectedShared: true);
        await _AssertSharedArrays(_Drain(stream), expectedShared: true);
        await _AssertSharedArrays(_Drain(fileDefault), expectedShared: false);
    }

    #endregion

    #region Helpers

    /// <summary>Asserts the frame count and that all frames share one array (slabs) or no two frames do (default).</summary>
    private static async Task _AssertSharedArrays(List<Frame> frames, bool expectedShared)
    {
        await Assert.That(frames.Count).IsEqualTo(_FrameCount);

        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        foreach (Frame frame in frames)
        {
            _ = arrays.Add(_ArrayOf(frame));
        }

        int expectedArrays = expectedShared ? 1 : _FrameCount;
        await Assert.That(arrays.Count).IsEqualTo(expectedArrays);
    }

    /// <summary>The array behind a frame's bytes; every source hands out array-backed memory.</summary>
    private static byte[] _ArrayOf(Frame frame)
    {
        bool isArray = MemoryMarshal.TryGetArray(frame.Data, out ArraySegment<byte> segment);
        return isArray
            ? segment.Array!
            : throw new InvalidOperationException("Frame data is not array-backed.");
    }

    private static List<Frame> _Drain(IFrameSource source)
    {
        SourceTestFixture.InitializeAndStartSource(source);
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static byte[] _BuildPcapng()
    {
        using PcapNgTestWriter writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < _FrameCount; i++)
        {
            byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, 0xAB, 0xCD]);
            writer.WriteFrame(0, (i + 1) * 1_000_000L, eth);
        }

        return writer.Build();
    }

    private static byte[] _BuildBlfCan()
    {
        BlfTestGenerator gen = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            byte[] can = FrameBuilders.BuildSocketCanClassic(0x100u + (uint)i, [(byte)i, 2, 3, 4]);
            _ = gen.AddCanFrame(1, can, (i + 1) * 1_000_000L);
        }

        return gen.Build();
    }

    #endregion
}
