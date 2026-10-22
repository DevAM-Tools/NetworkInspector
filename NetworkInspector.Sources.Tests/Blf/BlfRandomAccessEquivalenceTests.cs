// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// <see cref="BlfSource.FrameById"/> re-reads each indexed object header with the scanner's parser: over a corpus of
/// object types, container layouts, and split objects, for lazy and full scans, preloaded and memory-mapped, with and
/// without the container cache, every frame by id equals the sequential frame.
/// </summary>
internal sealed class BlfRandomAccessEquivalenceTests
{
    #region Corpus

    private const int _FileHeaderSize = 144;
    private static readonly byte[] _Dst = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] _Src = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Corpus cases: name and BLF bytes.</summary>
    public static IEnumerable<Func<(string Name, byte[] Data)>> Corpus()
    {
        yield return () => ("raw-all-types", _RawAllTypes());
        yield return () => ("containers-none-0-1-64-65", _Containers(0, [0, 1, 64, 65]));
        yield return () => ("containers-zlib-1-64-65", _Containers(2, [1, 64, 65]));
        yield return () => ("containers-lz4-64-65", _Containers(1, [64, 65]));
        yield return () => ("raw-and-containers-with-apptext", _RawMixedWithContainers());
        yield return () => ("split-object", _SplitObjects(objects: 6, splitEvery: 2));
        yield return () => ("broken-container-in-the-middle", _BrokenContainerInTheMiddle());
    }

    #endregion

    #region Identical

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task FrameByIdEqualsSequentialReadForEveryBackendScanModeAndCache(string name, byte[] data)
    {
        foreach (ScanMode mode in new[] { ScanMode.Lazy, ScanMode.Full })
        {
            foreach (int cache in new[] { 0, new BlfSourceOptions().CacheBudget })
            {
                // In-memory data.
                BlfSourceOptions memoryOptions = new() { ScanMode = mode, CacheBudget = cache };
                using (BlfSource memory = BlfSource.FromData(data, name, memoryOptions))
                {
                    List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(memory);
                    await Assert.That(RandomAccessEquivalence.FindMismatch(memory, frames, $"{name}/data/{mode}/{cache}")).IsNull();
                }

                // File backends: preloaded and memory-mapped.
                using TempCaptureFile file = new(data, ".blf");
                foreach (long? budget in new long?[] { long.MaxValue, null })
                {
                    BlfSourceOptions options = new() { ScanMode = mode, CacheBudget = cache, PreloadBudget = budget };
                    using BlfSource source = BlfSource.Open(file.Path, options);
                    List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
                    string label = $"{name}/{(budget is null ? "mapped" : "preload")}/{mode}/{cache}";
                    await Assert.That(RandomAccessEquivalence.FindMismatch(source, frames, label)).IsNull();
                }
            }
        }
    }

    [Test]
    public async Task LazySequentialReadOverSeveralContainersReturnsEveryFrame()
    {
        // Every container of a lazily scanned file appears while frames of the previous one are being read.
        byte[] data = _Containers(2, [1, 64, 65]);
        using BlfSource source = BlfSource.FromData(data, "lazy", new BlfSourceOptions { ScanMode = ScanMode.Lazy });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(130);
        await Assert.That(frames.Select(static f => f.Id.Value)).IsEquivalentTo(Enumerable.Range(0, 130));
    }

    [Test]
    public async Task FramesAfterABrokenContainerKeepTheirIds()
    {
        using BlfSource source = BlfSource.FromData(_BrokenContainerInTheMiddle(), "broken", new BlfSourceOptions { ScanMode = ScanMode.Full });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);

        // 5 frames before and 5 after the broken container; ids stay dense because only accepted objects are indexed.
        await Assert.That(frames.Count).IsEqualTo(10);
        await Assert.That(frames.Select(static f => f.Id.Value)).IsEquivalentTo(Enumerable.Range(0, 10));
    }

    #endregion

    #region Fail closed

    [Test]
    public async Task AlteredObjectHeaderReturnsNoFrame()
    {
        // Arrange: uncompressed containers and no cache, so FrameById re-reads the container bytes from the data.
        byte[] data = _Containers(0, [4, 4, 4]);
        using BlfSource source = BlfSource.FromData(data, "fail-closed", new BlfSourceOptions { ScanMode = ScanMode.Full, CacheBudget = 0 });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        byte[][] originals = [.. frames.Select(static f => f.Data.ToArray())];

        // Act: break the LOBJ signature of the object of frame 7 (the 8th inner CAN object in file order).
        int signature = _NthInnerObjectSignature(data, 7);
        data[signature] = (byte)'X';

        // Assert
        await Assert.That(source.FrameById(new FrameId(7))).IsNull();
        for (int id = 0; id < frames.Count; id++)
        {
            if (id == 7)
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
    [Arguments(0)]
    [Arguments(32 * 1024 * 1024)]
    public async Task FourReadersDuringALazyMappedScanNeverSeeAWrongFrame(int cacheBudget)
    {
        // Arrange: 50 000 CAN frames in zlib containers of 500, memory-mapped, lazy scan.
        const int count = 50_000;
        using TempCaptureFile file = new(_Containers(2, [.. Enumerable.Repeat(500, count / 500)]), ".blf");
        using BlfSource source = BlfSource.Open(
            file.Path, new BlfSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Lazy, CacheBudget = cacheBudget });
        _ = SourceTestFixture.InitializeAndStartSource(source);

        Frame[] expected = new Frame[count];
        int published = 0;
        int done = 0;
        int failures = 0;
        int successes = 0;

        // Act
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
            { Name = $"BLF FrameById reader {r}", IsBackground = true };
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

        // Assert
        await Assert.That(read).IsEqualTo(count);
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(successes).IsGreaterThan(0);
        await Assert.That(RandomAccessEquivalence.FindMismatch(source, [.. expected], "lazy-mapped")).IsNull();
    }

    #endregion

    #region Memory

    [Test]
    public async Task ConstantCanObjectsCostUnderHalfAByteAndHostileSplitsStayUnderThirtyTwo()
    {
        // Constant-size CAN objects: positions step by 48 bytes, so the index is about 0.4 B per frame.
        BlfFrameIndex can = _ScanIndex(_Containers(2, [.. Enumerable.Repeat(1_000, 64)]));
        await Assert.That(can.Count).IsEqualTo(64_000);
        await Assert.That(can.UsedBytes / (double)can.Count).IsLessThanOrEqualTo(0.5);

        // Hostile: one object per container, every object split across two containers.
        byte[] hostile = _SplitObjects(objects: 2_000, splitEvery: 1);
        BlfFrameIndex index = _ScanIndex(hostile);
        await Assert.That(index.Count).IsEqualTo(2_000);
        await Assert.That(index.UsedBytes / (double)index.Count).IsLessThanOrEqualTo(32.0);

        using BlfSource source = BlfSource.FromData(hostile, "hostile", new BlfSourceOptions { ScanMode = ScanMode.Full });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        await Assert.That(frames.Count).IsEqualTo(2_000);
        await Assert.That(RandomAccessEquivalence.FindMismatch(source, frames, "hostile")).IsNull();
    }

    #endregion

    #region Builders

    private static long _Time(int index) => 1_000_000L * (index + 1);

    private static byte[] _Can(int index)
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data, index);
        return TestFrames.BuildSocketCanClassic(0x100u + (uint)(index % 0x400), data);
    }

    private static byte[] _RawAllTypes()
    {
        byte[] ethernet = TestFrames.BuildEthernetFrame(_Dst, _Src, 0x0800, new byte[46]);
        return new BlfCaptureBuilder()
            .AddCanFrame(1, _Can(0), _Time(0))
            .AddCanFdFrame(2, TestFrames.BuildSocketCanFd(0x200, new byte[16], brs: true), _Time(1))
            .AddEthernetFrame(3, ethernet, _Time(2))
            .AddEthernetFrameEx(120, 4, ethernet, _Time(3))
            .AddLinFrame(5, 0x21, [1, 2, 3, 4], _Time(4))
            .AddLinMessage2(6, 0x22, [5, 6], 0x7E, _Time(5))
            .AddFlexRayFrame(7, 0x0A, 3, 0x1234, [1, 2, 3, 4, 5, 6, 7, 8], _Time(6))
            .AddCanXlChannelFrame(8, TestFrames.BuildSocketCanXl(0x123, [9, 8, 7]), _Time(7))
            .Build();
    }

    private static byte[] _Containers(ushort method, int[] perContainer)
    {
        BlfCaptureBuilder outer = new();
        int index = 0;
        foreach (int count in perContainer)
        {
            BlfCaptureBuilder inner = new();
            for (int k = 0; k < count; k++)
            {
                _ = inner.AddCanFrame(1, _Can(index), _Time(index));
                index++;
            }

            _ = outer.AddLogContainer(method, inner);
        }

        return outer.Build();
    }

    private static byte[] _RawMixedWithContainers()
    {
        BlfCaptureBuilder outer = new BlfCaptureBuilder().AddAppTextChannel(1, 1, "Powertrain", _Time(0));
        int index = 0;
        for (int block = 0; block < 4; block++)
        {
            _ = outer.AddCanFrame(2, _Can(index), _Time(index));
            index++;
            BlfCaptureBuilder inner = new BlfCaptureBuilder().AddAppTextChannel(2, 1, "Body", _Time(index));
            for (int k = 0; k < 10; k++)
            {
                _ = inner.AddCanFrame(1, _Can(index), _Time(index));
                index++;
            }

            _ = outer.AddLogContainer(2, inner);
        }

        return outer.Build();
    }

    /// <summary>
    /// CAN objects whose bytes are cut into uncompressed containers so that every <paramref name="splitEvery"/>-th
    /// object boundary falls inside an object: each container holds the tail of one object and the head of the next.
    /// </summary>
    private static byte[] _SplitObjects(int objects, int splitEvery)
    {
        BlfCaptureBuilder inner = new();
        for (int i = 0; i < objects; i++)
        {
            _ = inner.AddCanFrame(1, _Can(i), _Time(i));
        }

        byte[] objectBytes = inner.Build().AsSpan(_FileHeaderSize).ToArray();
        int objectSize = objectBytes.Length / objects;

        // Cut points in the middle of every splitEvery-th object.
        BlfCaptureBuilder outer = new();
        int start = 0;
        for (int i = 0; i < objects; i += splitEvery)
        {
            int cut = (i * objectSize) + (objectSize / 2);
            byte[] part = objectBytes.AsSpan(start, cut - start).ToArray();
            _ = outer.AddCorruptLogContainer(0, (uint)part.Length, part);
            start = cut;
        }

        byte[] last = objectBytes.AsSpan(start).ToArray();
        _ = outer.AddCorruptLogContainer(0, (uint)last.Length, last);
        return outer.Build();
    }

    private static byte[] _BrokenContainerInTheMiddle()
    {
        BlfCaptureBuilder first = new();
        BlfCaptureBuilder second = new();
        for (int i = 0; i < 5; i++)
        {
            _ = first.AddCanFrame(1, _Can(i), _Time(i));
            _ = second.AddCanFrame(1, _Can(i + 5), _Time(i + 5));
        }

        return new BlfCaptureBuilder()
            .AddLogContainer(2, first)
            .AddCorruptLogContainer(2, 4_096, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02])
            .AddLogContainer(2, second)
            .Build();
    }

    /// <summary>Scans <paramref name="data"/> completely and returns the index.</summary>
    private static BlfFrameIndex _ScanIndex(byte[] data)
    {
        using BlfDataBackend backend = BlfDataBackend.FromMemory(data);
        _ = BlfFileInfo.TryParse(data.AsSpan(0, _FileHeaderSize), TimeZoneInfo.Utc, out BlfFileInfo? info);
        BlfFrameIndex index = new();
        BlfIncrementalScanner scanner = new(backend, info!, index);
        while (scanner.ScanNext())
        {
            // Index the whole file.
        }

        return index;
    }

    /// <summary>
    /// File offset of the <c>LOBJ</c> signature of the <paramref name="n"/>-th inner CAN object, in a file of
    /// uncompressed containers (inner objects are stored verbatim after each container header).
    /// </summary>
    private static int _NthInnerObjectSignature(byte[] data, int n)
    {
        int found = -1;
        for (int offset = _FileHeaderSize; offset + 16 <= data.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != 0x4A42_4F4C
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12)) != 1)
            {
                continue;
            }

            found++;
            if (found == n)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("Inner object not found.");
    }

    #endregion
}
