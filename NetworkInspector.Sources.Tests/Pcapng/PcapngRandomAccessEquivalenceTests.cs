// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// <see cref="PcapngSource.FrameById"/> re-reads each indexed block with the scanner's parser: over a corpus of
/// PCAPNG and legacy PCAP shapes, in lazy and full scan mode, preloaded and memory-mapped, every frame by id equals the
/// sequential frame; altered bytes fail closed; concurrent readers during a lazy scan never see a wrong frame.
/// </summary>
internal sealed class PcapngRandomAccessEquivalenceTests
{
    #region Corpus

    private static readonly byte[] _Dst = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] _Src = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Corpus cases: name and bytes.</summary>
    public static IEnumerable<Func<(string Name, byte[] Data)>> Corpus()
    {
        yield return () => ("empty", _Capture(0));
        yield return () => ("one", _Capture(1));
        yield return () => ("63", _Capture(63));
        yield return () => ("64", _Capture(64));
        yield return () => ("65", _Capture(65));
        yield return () => ("129", _Capture(129));
        yield return () => ("imix", _Imix(300));
        yield return () => ("gro-frame", _GroFrame());
        yield return () => ("two-sections-mixed-byte-order", _TwoSectionsMixedByteOrder());
        yield return () => ("epb-before-idb", _EpbBeforeIdb());
        yield return () => ("unknown-blocks", _UnknownBlocksBetweenPackets());
        yield return () => ("unknown-1mib-block", _UnknownBigBlockBetweenPackets());
        yield return () => ("gro-frame-last", _GroFrameLast());
        yield return () => ("spb-snap-length", _SimplePackets());
        yield return () => ("obsolete-pb", _ObsoletePackets());
        yield return () => ("tsresol-ns-and-pow2", _TimestampResolutions());
        yield return () => ("tsoffset", _TimestampOffset());
        yield return () => ("legacy-le-us", _Legacy(bigEndian: false, nanoseconds: false));
        yield return () => ("legacy-be-us", _Legacy(bigEndian: true, nanoseconds: false));
        yield return () => ("legacy-le-ns", _Legacy(bigEndian: false, nanoseconds: true));
        yield return () => ("legacy-be-ns", _Legacy(bigEndian: true, nanoseconds: true));
        yield return () => ("truncated-last-block", _TruncatedLastBlock());
    }

    #endregion

    #region Identical

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task FrameByIdEqualsSequentialReadForEveryBackendAndScanMode(string name, byte[] data)
    {
        // In-memory data (always a full scan).
        using (PcapngSource fromData = PcapngSource.FromData(data, name))
        {
            List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(fromData);
            await Assert.That(RandomAccessEquivalence.FindMismatch(fromData, frames, $"{name}/data")).IsNull();
        }

        // File backends: preloaded and memory-mapped, lazy and full.
        using TempCaptureFile file = new(data, ".pcapng");
        foreach (long? budget in new long?[] { long.MaxValue, null })
        {
            foreach (ScanMode mode in new[] { ScanMode.Lazy, ScanMode.Full })
            {
                PcapngSourceOptions options = new() { PreloadBudget = budget, ScanMode = mode };
                using PcapngSource source = PcapngSource.Open(file.Path, options);
                List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
                string label = $"{name}/{(budget is null ? "mapped" : "preload")}/{mode}";
                await Assert.That(RandomAccessEquivalence.FindMismatch(source, frames, label)).IsNull();
            }
        }
    }

    [Test]
    public async Task FramesAtGroupEdgesAreIdenticalForEveryBackendAndScanMode()
    {
        // 129 frames: lane 63 of group 0 (id 63), the first lane of group 1 (id 64), lane 63 of group 1 (id 127), and the
        // single frame of the open last group (id 128). These are the ids where the block span reads the next group.
        byte[] data = _Capture(129);
        int[] ids = [62, 63, 64, 127, 128];
        List<string> mismatches = [];

        using TempCaptureFile file = new(data, ".pcapng");
        foreach (long? budget in new long?[] { long.MaxValue, null })
        {
            foreach (ScanMode mode in new[] { ScanMode.Lazy, ScanMode.Full })
            {
                PcapngSourceOptions options = new() { PreloadBudget = budget, ScanMode = mode };
                using PcapngSource source = PcapngSource.Open(file.Path, options);
                List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
                foreach (int id in ids)
                {
                    Frame? actual = source.FrameById(new FrameId(id));
                    if (actual is null || !RandomAccessEquivalence.SameFrame(frames[id], actual.Value))
                    {
                        mismatches.Add(Invariant($"id {id}, {(budget is null ? "mapped" : "preload")}, {mode}"));
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task FramesByIdWhileTheLastGroupIsStillOpenEqualTheSequentialFrames()
    {
        // A lazy scan stopped inside a group: the newest frames sit in the open staging group of the index, where the next
        // block start is not published yet (the prefetch then bounds by the file end).
        using TempCaptureFile file = new(_Capture(300), ".pcapng");
        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Lazy });
        _ = SourceTestFixture.InitializeAndStartSource(source);
        List<Frame> sequential = [];
        List<string> mismatches = [];

        for (int read = 0; read < 150; read++)
        {
            sequential.Add(source.NextFrame()!.Value);
            Frame? newest = source.FrameById(new FrameId(read));
            Frame? first = source.FrameById(new FrameId(0));
            if (newest is null || !RandomAccessEquivalence.SameFrame(sequential[read], newest.Value))
            {
                mismatches.Add(Invariant($"newest id {read}"));
            }

            if (first is null || !RandomAccessEquivalence.SameFrame(sequential[0], first.Value))
            {
                mismatches.Add(Invariant($"first id after {read} frames"));
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task InterfaceDeclaredAfterItsPacketStaysSkipped()
    {
        // The EPB that names interface 1 before its IDB is skipped by the scan and never gets an id.
        using PcapngSource source = PcapngSource.FromData(_EpbBeforeIdb(), "epb-before-idb");
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount).IsEqualTo(1);
        await Assert.That(source.FrameById(new FrameId(2))).IsNull();
    }

    #endregion

    #region Fail closed

    [Test]
    public async Task AlteredBytesAtAnIndexedBlockReturnNoFrame()
    {
        // Arrange: 100 frames from memory; keep a copy of every frame's bytes before the data changes.
        byte[] data = _Capture(100);
        using PcapngSource source = PcapngSource.FromData(data, "fail-closed");
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        byte[][] originals = [.. frames.Select(static f => f.Data.ToArray())];
        List<int> blocks = _EnhancedPacketBlockOffsets(data);

        // Act: frame 42 gets an unknown block type, frame 43 a cap_len larger than its block.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blocks[42]), 0x0BAD_0BAD);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blocks[43] + 20), 4096);

        // Assert
        await Assert.That(source.FrameById(new FrameId(42))).IsNull();
        await Assert.That(source.FrameById(new FrameId(43))).IsNull();
        for (int id = 0; id < frames.Count; id++)
        {
            if (id is 42 or 43)
            {
                continue;
            }

            Frame? frame = source.FrameById(new FrameId(id));
            await Assert.That(frame is not null && frame.Value.Data.Span.SequenceEqual(originals[id])).IsTrue();
        }
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task FourReadersDuringALazyMappedScanNeverSeeAWrongFrame()
    {
        // Arrange: 50 000 IMIX frames, memory-mapped, lazy scan.
        const int count = 50_000;
        using TempCaptureFile file = new(_Imix(count), ".pcapng");
        using PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Lazy });
        _ = SourceTestFixture.InitializeAndStartSource(source);

        Frame[] expected = new Frame[count];
        int published = 0;
        int done = 0;
        int failures = 0;
        int successes = 0;

        // Act: readers request ids the scan already returned; every one must come back and equal the sequential frame.
        Thread[] readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = new Thread(() =>
            {
                System.Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int limit = Volatile.Read(ref published);
                    if (limit == 0)
                    {
                        continue;
                    }

                    int id = random.Next(limit);
                    Frame? frame = source.FrameById(new FrameId(id));
                    if (frame is null || !RandomAccessEquivalence.SameFrame(expected[id], frame.Value))
                    {
                        Interlocked.Increment(ref failures);
                    }
                    else
                    {
                        Interlocked.Increment(ref successes);
                    }
                }
            })
            { Name = $"PCAPNG FrameById reader {r}", IsBackground = true };
            readers[r].Start();
        }

        int read = 0;
        while (source.NextFrame() is { } frame)
        {
            expected[frame.Id.Value] = frame;
            Volatile.Write(ref published, frame.Id.Value + 1);
            read++;
        }

        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert: no wrong or missing frame during the scan; afterwards every id resolves.
        await Assert.That(read).IsEqualTo(count);
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(successes).IsGreaterThan(0);
        await Assert.That(RandomAccessEquivalence.FindMismatch(source, [.. expected], "lazy-mapped")).IsNull();
    }

    #endregion

    #region Memory

    [Test]
    public async Task MixedSizeOffsetsCostAboutOneAndAHalfBytesPerFrame()
    {
        // The index stores 4-aligned block starts of three block sizes: 0.375 B + 9 residual bits per frame.
        using DataBackend backend = DataBackend.FromMemory(_Imix(65_536));
        IncrementalScanner scanner = new(backend, backend.FileSize);
        while (scanner.NextFrame(out _) || !scanner.IsExhausted)
        {
            // Scan the whole file; the scanner fills the index.
        }

        await Assert.That(scanner.Index.Count).IsEqualTo(65_536);
        await Assert.That(scanner.Index.UsedBytes / (double)scanner.Index.Count).IsLessThanOrEqualTo(1.55);
    }

    #endregion

    #region Builders

    /// <summary>Ethernet frame of <paramref name="size"/> bytes (at least 18) whose payload starts with <paramref name="index"/>.</summary>
    private static byte[] _Frame(int index, int size)
    {
        byte[] payload = new byte[size - 14];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        return TestFrames.BuildEthernetFrame(_Dst, _Src, 0x0800, payload);
    }

    private static byte[] _Capture(int count)
    {
        using PcapngCaptureBuilder writer = new();
        if (count == 0)
        {
            return writer.Build();
        }

        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < count; i++)
        {
            writer.WriteFrame(0, 1_700_000_000_000_000_000L + (i * 731_017L), _Frame(i, 60 + (i % 3)));
        }

        return writer.Build();
    }

    private static byte[] _Imix(int count)
    {
        int[] sizes = [64, 64, 594, 64, 64, 594, 64, 1518, 64, 594, 64, 594];
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < count; i++)
        {
            writer.WriteFrame(0, 1_700_000_000_000_000_000L + (i * 731_000L), _Frame(i, sizes[i % sizes.Length]));
        }

        return writer.Build();
    }

    private static byte[] _GroFrame()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, snapLen: 262_144);
        writer.WriteFrame(0, 1_000, _Frame(0, 64));
        writer.WriteFrame(0, 2_000, _Frame(1, 262_144));
        writer.WriteFrame(0, 3_000, _Frame(2, 64));
        return writer.Build();
    }

    private static byte[] _TwoSectionsMixedByteOrder()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false);
        for (int i = 0; i < 70; i++)
        {
            writer.WriteFrame(0, 1_000_000_000L + (i * 1_000L), _Frame(i, 70));
        }

        byte[] little = writer.Build();
        byte[] big = _BigEndianSection(70);
        return [.. little, .. big];
    }

    private static byte[] _EpbBeforeIdb()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000, _Frame(0, 64));
        writer.WriteRawBlock(PcapngBlocks.EnhancedPacket(1, _Frame(1, 64)));
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(1, 3_000, _Frame(2, 64));
        return writer.Build();
    }

    private static byte[] _UnknownBlocksBetweenPackets()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < 10; i++)
        {
            writer.WriteFrame(0, 1_000 + i, _Frame(i, 64));
            writer.WriteRawBlock(PcapngBlocks.EmptyDsb());
        }

        return writer.Build();
    }

    /// <summary>A 1 MiB block of an unknown type between frame 5 and frame 6: the next-block bound is far, the 4 KiB cap applies.</summary>
    private static byte[] _UnknownBigBlockBetweenPackets()
    {
        const int blockLength = 1024 * 1024;
        byte[] unknown = new byte[blockLength];
        BinaryPrimitives.WriteUInt32LittleEndian(unknown, 0x0BAD_B10C);
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(4), blockLength);
        unknown.AsSpan(8, blockLength - 12).Fill(0xA5);
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(blockLength - 4), blockLength);

        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < 12; i++)
        {
            writer.WriteFrame(0, 1_000 + i, _Frame(i, 64 + i));
            if (i == 5)
            {
                writer.WriteRawBlock(unknown);
            }
        }

        return writer.Build();
    }

    /// <summary>A 262 144-byte GRO frame as the last frame: the prefetch bound is the file end.</summary>
    private static byte[] _GroFrameLast()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, snapLen: 262_144);
        writer.WriteFrame(0, 1_000, _Frame(0, 64));
        writer.WriteFrame(0, 2_000, _Frame(1, 64));
        writer.WriteFrame(0, 3_000, _Frame(2, 262_144));
        return writer.Build();
    }

    private static byte[] _SimplePackets()
    {
        // SPB: stored length is min(orig_len, snap length); the second packet is cut by snap length 80.
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, snapLen: 80);
        writer.WriteRawBlock(_SimplePacketBlock(_Frame(0, 64), originalLength: 64));
        writer.WriteRawBlock(_SimplePacketBlock(_Frame(1, 120).AsSpan(0, 80).ToArray(), originalLength: 120));
        writer.WriteFrame(0, 5_000, _Frame(2, 64));
        return writer.Build();
    }

    private static byte[] _ObsoletePackets()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteRawBlock(_ObsoletePacketBlock(_Frame(0, 64), timestamp: 1_234_567));
        writer.WriteFrame(0, 2_000_000, _Frame(1, 64));
        writer.WriteRawBlock(_ObsoletePacketBlock(_Frame(2, 100), timestamp: 3_000_000));
        return writer.Build();
    }

    private static byte[] _TimestampResolutions()
    {
        // Interface 0: 10^-9; interface 1: 2^-20 (if_tsresol 0x94).
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.AddInterface(LinkType.Ethernet, rawTimestampResolution: 0x94);
        for (int i = 0; i < 20; i++)
        {
            writer.WriteFrame((uint)(i % 2), 1_700_000_000_000_000_000L + (i * 1_000_003L), _Frame(i, 64));
        }

        return writer.Build();
    }

    private static byte[] _TimestampOffset()
    {
        using PcapngCaptureBuilder writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false, timestampOffsetSeconds: 3_600);
        for (int i = 0; i < 5; i++)
        {
            writer.WriteFrame(0, 1_000_000_000L + (i * 1_000L), _Frame(i, 64));
        }

        return writer.Build();
    }

    private static byte[] _TruncatedLastBlock()
    {
        byte[] full = _Capture(20);
        return full.AsSpan(0, full.Length - 10).ToArray();
    }

    private static byte[] _SimplePacketBlock(byte[] stored, uint originalLength)
    {
        int padded = (stored.Length + 3) & ~3;
        int length = 16 + padded;
        byte[] block = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x0000_0003);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), originalLength);
        stored.CopyTo(block.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(length - 4), (uint)length);
        return block;
    }

    private static byte[] _ObsoletePacketBlock(byte[] data, ulong timestamp)
    {
        int padded = (data.Length + 3) & ~3;
        int length = 32 + padded;
        byte[] block = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x0000_0002);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)(timestamp >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), (uint)data.Length);
        data.CopyTo(block.AsSpan(28));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(length - 4), (uint)length);
        return block;
    }

    /// <summary>A big-endian section: SHB, one Ethernet IDB (if_tsresol 6), and <paramref name="count"/> EPBs.</summary>
    private static byte[] _BigEndianSection(int count)
    {
        List<byte> bytes = [];
        byte[] shb = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(shb, 0x0A0D_0D0A);
        BinaryPrimitives.WriteUInt32BigEndian(shb.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(shb.AsSpan(8), 0x1A2B_3C4D);
        BinaryPrimitives.WriteUInt16BigEndian(shb.AsSpan(12), 1);
        BinaryPrimitives.WriteInt64BigEndian(shb.AsSpan(16), -1);
        BinaryPrimitives.WriteUInt32BigEndian(shb.AsSpan(24), 28);
        bytes.AddRange(shb);

        byte[] idb = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(idb, 0x0000_0001);
        BinaryPrimitives.WriteUInt32BigEndian(idb.AsSpan(4), 32);
        BinaryPrimitives.WriteUInt16BigEndian(idb.AsSpan(8), (ushort)LinkType.Ethernet);
        BinaryPrimitives.WriteUInt32BigEndian(idb.AsSpan(12), 65_535);
        BinaryPrimitives.WriteUInt16BigEndian(idb.AsSpan(16), 9);
        BinaryPrimitives.WriteUInt16BigEndian(idb.AsSpan(18), 1);
        idb[20] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(idb.AsSpan(28), 32);
        bytes.AddRange(idb);

        for (int i = 0; i < count; i++)
        {
            byte[] data = _Frame(1_000 + i, 66 + (i % 5));
            int padded = (data.Length + 3) & ~3;
            int length = 32 + padded;
            byte[] epb = new byte[length];
            ulong timestamp = 2_000_000_000UL + (ulong)(i * 17);
            BinaryPrimitives.WriteUInt32BigEndian(epb, 0x0000_0006);
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(4), (uint)length);
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(12), (uint)(timestamp >> 32));
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(16), (uint)timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(20), (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(24), (uint)data.Length);
            data.CopyTo(epb.AsSpan(28));
            BinaryPrimitives.WriteUInt32BigEndian(epb.AsSpan(length - 4), (uint)length);
            bytes.AddRange(epb);
        }

        return [.. bytes];
    }

    /// <summary>A legacy PCAP file with 70 Ethernet records.</summary>
    private static byte[] _Legacy(bool bigEndian, bool nanoseconds)
    {
        List<byte> bytes = [];
        byte[] header = new byte[24];
        _Write32(header, 0, nanoseconds ? 0xA1B2_3C4Du : 0xA1B2_C3D4u, bigEndian);
        _Write16(header, 4, 2, bigEndian);
        _Write16(header, 6, 4, bigEndian);
        _Write32(header, 16, 65_535, bigEndian);
        _Write32(header, 20, 1, bigEndian);
        bytes.AddRange(header);

        for (int i = 0; i < 70; i++)
        {
            byte[] data = _Frame(i, 60 + (i % 7));
            byte[] record = new byte[16 + data.Length];
            _Write32(record, 0, (uint)(1_700_000_000 + i), bigEndian);
            _Write32(record, 4, (uint)(i * 12_345), bigEndian);
            _Write32(record, 8, (uint)data.Length, bigEndian);
            _Write32(record, 12, (uint)data.Length, bigEndian);
            data.CopyTo(record.AsSpan(16));
            bytes.AddRange(record);
        }

        return [.. bytes];
    }

    private static void _Write32(byte[] target, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
        }
    }

    private static void _Write16(byte[] target, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);
        }
    }

    /// <summary>Start offsets of the EPBs of a little-endian single-section capture, in file order.</summary>
    private static List<int> _EnhancedPacketBlockOffsets(byte[] data)
    {
        List<int> offsets = [];
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (type == 6)
            {
                offsets.Add(offset);
            }

            offset += length;
        }

        return offsets;
    }

    #endregion
}
