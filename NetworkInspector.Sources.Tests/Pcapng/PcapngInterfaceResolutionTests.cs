// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// <see cref="PcapngSource.FrameById"/> resolves the registered interface of a re-read packet from the packet's
/// <c>InterfaceInfo</c> (one field read) and falls back to the interface table for legacy PCAP. These tests prove that
/// the interface id and link type always equal the sequential frame's, also for interfaces whose IDB arrives in the middle
/// of a lazy scan, and that a second <c>Start</c> keeps the first registration.
/// </summary>
internal sealed class PcapngInterfaceResolutionTests
{
    #region Constants

    private const int _FramesPerInterface = 40;
    private static readonly byte[] _Dst = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] _Src = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly LinkType[] _LinkTypes = [LinkType.Ethernet, LinkType.Raw, LinkType.Null];

    #endregion

    #region Tests

    [Test]
    public async Task InterfacesDeclaredInTheMiddleOfALazyScanResolveForConcurrentReaders()
    {
        // Two sections of three interfaces; each IDB follows the frames of the interfaces before it, so the scanner
        // registers it only when it reaches it.
        using TempCaptureFile file = new(_TwoSectionsWithLateInterfaces(), ".pcapng");
        List<string> problems = [];
        for (int round = 0; round < 20; round++)
        {
            using PcapngSource source = PcapngSource.Open(
                file.Path, new PcapngSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Lazy });
            _ = SourceTestFixture.InitializeAndStartSource(source);
            problems.AddRange(_ReadWhileScanning(source, round));
        }

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task FrameByIdBeforeStartReturnsNull()
    {
        using PcapngSource source = PcapngSource.FromData(_TwoSectionsWithLateInterfaces(), "before-start");

        Frame? frame = source.FrameById(new FrameId(0));

        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task RepeatedStartWithTheSameRegistryKeepsTheInterfaceIds()
    {
        // Arrange: a fully scanned capture read once.
        using PcapngSource source = PcapngSource.FromData(_TwoSectionsWithLateInterfaces(), "repeated-start");
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        List<Frame> first = _ReadAll(source);

        // Act: Start again; the interfaces are already registered, so nothing is registered twice.
        source.Start(sourceId, registry);
        List<Frame> second = _ReadAll(source);
        List<string> problems = [];
        for (int i = 0; i < first.Count; i++)
        {
            Frame? byId = source.FrameById(first[i].Id);
            bool sequentialSame = i < second.Count && RandomAccessEquivalence.SameFrame(first[i], second[i]);
            bool byIdSame = byId is not null && RandomAccessEquivalence.SameFrame(first[i], byId.Value);
            if (!sequentialSame || !byIdSame)
            {
                problems.Add(Invariant($"frame {i}: sequential {sequentialSame}, by id {byIdSame}"));
            }
        }

        // Assert
        await Assert.That(second.Count).IsEqualTo(first.Count);
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task StartWithAnotherRegistryKeepsTheFirstRegistrationInFrameByIdAndNextFrameAlike()
    {
        // Arrange: read once under registry 1.
        using PcapngSource source = PcapngSource.FromData(_TwoSectionsWithLateInterfaces(), "second-registry");
        List<Frame> first = RandomAccessEquivalence.StartAndReadAll(source);

        // Act: Start under registry 2. The interface table keeps the entries of registry 1 and registers nothing new, so
        // the ids no longer belong to the registry in use: both read paths report no frame, and they agree.
        List<Frame> second = RandomAccessEquivalence.StartAndReadAll(source);
        int byIdFrames = 0;
        for (int i = 0; i < first.Count; i++)
        {
            byIdFrames += source.FrameById(first[i].Id) is null ? 0 : 1;
        }

        // Assert
        await Assert.That(first.Count).IsEqualTo(240);
        await Assert.That(second.Count).IsEqualTo(byIdFrames);
    }

    [Test]
    public async Task LegacyPcapStillResolvesItsInterfaceThroughTheTable()
    {
        using PcapngSource source = PcapngSource.FromData(_LegacyCapture(), "legacy");
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);

        string? mismatch = RandomAccessEquivalence.FindMismatch(source, frames, "legacy");

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(mismatch).IsNull();
        await Assert.That(source.FrameById(new FrameId(1))!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
    }

    #endregion

    #region Helpers

    /// <summary>Reads every frame of an already started source with <c>NextFrame</c>.</summary>
    private static List<Frame> _ReadAll(PcapngSource source)
    {
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// Scans <paramref name="source"/> on this thread while one reader thread requests frames the scan already returned.
    /// Returns every wrong or missing frame (empty when all are right) and checks all ids after the scan.
    /// </summary>
    private static List<string> _ReadWhileScanning(PcapngSource source, int seed)
    {
        const int capacity = 2 * 3 * _FramesPerInterface;
        Frame[] expected = new Frame[capacity];
        int published = 0;
        int done = 0;
        int successes = 0;
        List<string> problems = [];
        Lock problemLock = new();

        Thread reader = new(() =>
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
                    lock (problemLock)
                    {
                        problems.Add(Invariant($"FrameById({id}) differs from the sequential frame or is missing."));
                    }
                }
                else
                {
                    _ = Interlocked.Increment(ref successes);
                }
            }
        })
        { Name = "PCAPNG interface reader", IsBackground = true };
        reader.Start();

        int read = 0;
        while (source.NextFrame() is { } next)
        {
            expected[next.Id.Value] = next;
            Volatile.Write(ref published, next.Id.Value + 1);
            read++;
        }

        Volatile.Write(ref done, 1);
        reader.Join();

        if (read != capacity)
        {
            problems.Add(Invariant($"read {read} frames, expected {capacity}."));
        }

        // The link type of every frame follows its interface, in both sections.
        for (int i = 0; i < read; i++)
        {
            LinkType wanted = _LinkTypes[(i / _FramesPerInterface) % _LinkTypes.Length];
            if (expected[i].LinkType != wanted)
            {
                problems.Add(Invariant($"sequential frame {i} has link type {expected[i].LinkType}, expected {wanted}."));
            }
        }

        string? afterScan = RandomAccessEquivalence.FindMismatch(source, [.. expected.Take(read)], "after the scan");
        if (afterScan is not null)
        {
            problems.Add(afterScan);
        }

        return problems;
    }

    /// <summary>
    /// Two sections. Each has interface 0 first; interface 1 is declared after the first 40 frames and interface 2 after 80,
    /// so the lazy scan meets the IDBs in the middle of the file. Frames 0-39 use interface 0, 40-79 interface 1, 80-119
    /// interface 2 of the first section; the second section repeats the shape.
    /// </summary>
    private static byte[] _TwoSectionsWithLateInterfaces()
    {
        List<byte> bytes = [];
        for (int section = 0; section < 2; section++)
        {
            using PcapngCaptureBuilder writer = new();
            for (uint interfaceId = 0; interfaceId < 3; interfaceId++)
            {
                _ = writer.AddInterface(_LinkTypes[interfaceId], nanosecondResolution: true, name: Invariant($"if{section}-{interfaceId}"));
                for (int i = 0; i < _FramesPerInterface; i++)
                {
                    int index = (section * 3 * _FramesPerInterface) + ((int)interfaceId * _FramesPerInterface) + i;
                    byte[] payload = new byte[24];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, index);
                    writer.WriteFrame(
                        interfaceId,
                        1_700_000_000_000_000_000L + (index * 1_000L),
                        TestFrames.BuildEthernetFrame(_Dst, _Src, 0x0800, payload));
                }
            }

            bytes.AddRange(writer.Build());
        }

        return [.. bytes];
    }

    /// <summary>A little-endian microsecond PCAP with an Ethernet header and three records.</summary>
    private static byte[] _LegacyCapture()
    {
        List<byte> bytes = [];
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA1B2_C3D4u);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 65_535);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        bytes.AddRange(header);
        for (int i = 0; i < 3; i++)
        {
            byte[] data = TestFrames.BuildEthernetFrame(_Dst, _Src, 0x0800, [(byte)i, 1, 2, 3]);
            byte[] record = new byte[16 + data.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(1_700_000_000 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)(i * 1_000));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), (uint)data.Length);
            data.CopyTo(record.AsSpan(16));
            bytes.AddRange(record);
        }

        return [.. bytes];
    }

    #endregion
}
