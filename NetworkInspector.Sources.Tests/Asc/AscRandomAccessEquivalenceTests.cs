// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// <see cref="AscSource.FrameById"/> re-derives each indexed line from its byte offset: for the in-memory and the disk
/// backend, every frame by id equals the sequential frame, including the clock of relative files.
/// </summary>
internal sealed class AscRandomAccessEquivalenceTests
{
    #region Corpus

    private static readonly byte[] _Eight = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Corpus cases: name and ASC text.</summary>
    public static IEnumerable<Func<(string Name, string Text)>> Corpus()
    {
        yield return () => ("zero", _CanLines(0, relative: false, crlf: false));
        yield return () => ("one", _CanLines(1, relative: false, crlf: false));
        yield return () => ("64", _CanLines(64, relative: false, crlf: false));
        yield return () => ("65-crlf", _CanLines(65, relative: false, crlf: true));
        yield return () => ("65-relative", _CanLines(65, relative: true, crlf: false));
        yield return () => ("all-buses-lf", _AllBuses(crlf: false));
        yield return () => ("all-buses-crlf", _AllBuses(crlf: true));
        yield return () => ("long-line", _LongLine());
        foreach (Func<(string Name, string Text)> windowCase in WindowCorpus())
        {
            yield return windowCase;
        }
    }

    /// <summary>
    /// Cases for the disk read windows: long single-line Ethernet, wrapped Ethernet ended by every kind of line, groups
    /// that mix CAN and Ethernet, a packet open at the end of the file, and a header split by a line break. On these the
    /// disk backend must return exactly the in-memory frames.
    /// </summary>
    public static IEnumerable<Func<(string Name, string Text)>> WindowCorpus()
    {
        yield return () => ("ethernet-imix-lf", _EthernetImix(crlf: false));
        yield return () => ("ethernet-imix-crlf", _EthernetImix(crlf: true));
        yield return () => ("ethernet-terminators-lf", _EthernetTerminators(crlf: false));
        yield return () => ("ethernet-terminators-crlf", _EthernetTerminators(crlf: true));
        yield return () => ("mixed-groups", _MixedGroups(frames: 200, crlf: false));
        yield return () => ("ethernet-open-at-eof", _EthernetOpenAtEndOfFile());
        yield return () => ("afdx-split-header", _AfdxSplitHeader());
    }

    #endregion

    #region Identical

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task FrameByIdEqualsSequentialReadForBothBackends(string name, string text)
    {
        // In-memory backend.
        using (AscSource memory = AscSource.FromText(text, name))
        {
            List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(memory);
            await Assert.That(RandomAccessEquivalence.FindMismatch(memory, frames, $"{name}/memory")).IsNull();
        }

        // Disk backend.
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        List<Frame> diskFrames = RandomAccessEquivalence.StartAndReadAll(disk);
        await Assert.That(RandomAccessEquivalence.FindMismatch(disk, diskFrames, $"{name}/disk")).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(WindowCorpus))]
    public async Task DiskReadWindowsReturnTheInMemoryFrames(string name, string text)
    {
        // Arrange: the in-memory backend parses from its whole buffer, so it is the reference for the disk windows.
        using AscSource memory = AscSource.FromText(text, name);
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(memory);
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });

        // Act
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(disk);

        // Assert: same frames in the same order, and by id in reverse order too.
        await Assert.That(_FirstDifference(expected, sequential)).IsNull();
        await Assert.That(RandomAccessEquivalence.FindMismatch(disk, sequential, $"{name}/disk")).IsNull();
    }

    #endregion

    #region Read-ahead

    [Test]
    [Arguments("forward")]
    [Arguments("backward")]
    [Arguments("every-second")]
    [Arguments("random")]
    [Arguments("forward-then-backward")]
    public async Task FrameByIdInEveryOrderEqualsTheSequentialRead(string order)
    {
        // Arrange: about 4 read-ahead windows of CAN and Ethernet records, so walks refill several times.
        string text = _MixedGroups(frames: 3_000, crlf: false);
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(disk);
        int[] ids = _Order(order, sequential.Count);

        // Act
        string? mismatch = _FirstByIdMismatch(disk, sequential, ids, order);

        // Assert
        await Assert.That(sequential.Count).IsEqualTo(3_000);
        await Assert.That(mismatch).IsNull();
    }

    [Test]
    public async Task InterleavedWalksOverMoreSourcesThanWaysStayExact()
    {
        // Arrange: more sources than read-ahead ways, read round-robin on one thread, so ways are evicted constantly.
        const int sourceCount = AscReadAhead.Ways + 2;
        List<TempCaptureFile> files = [];
        List<AscSource> sources = [];
        List<List<Frame>> expected = [];
        try
        {
            for (int s = 0; s < sourceCount; s++)
            {
                files.Add(new TempCaptureFile(Encoding.Latin1.GetBytes(_MixedGroups(frames: 500 + s, crlf: s % 2 == 1)), ".asc"));
                sources.Add(AscSource.Open(files[s].Path, new AscSourceOptions { PreloadBudget = null }));
                expected.Add(RandomAccessEquivalence.StartAndReadAll(sources[s]));
            }

            // Act
            string? mismatch = null;
            for (int id = 0; id < 500 && mismatch is null; id++)
            {
                for (int s = 0; s < sourceCount && mismatch is null; s++)
                {
                    Frame? actual = sources[s].FrameById(new FrameId(id));
                    if (actual is null || !RandomAccessEquivalence.SameFrame(expected[s][id], actual.Value))
                    {
                        mismatch = Invariant($"source {s}, id {id}");
                    }
                }
            }

            // Assert
            await Assert.That(mismatch).IsNull();
        }
        finally
        {
            sources.ForEach(static source => source.Dispose());
            files.ForEach(static file => file.Dispose());
        }
    }

    [Test]
    public async Task ConcurrentWalksOnFourThreadsStayExact()
    {
        // Arrange
        string text = _MixedGroups(frames: 3_000, crlf: false);
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(disk);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using Barrier start = new(4);

        // Act: thread t walks forward (even t) or backward (odd t) from its own quarter, wrapping around.
        Task<string?>[] walkers = new Task<string?>[4];
        for (int t = 0; t < walkers.Length; t++)
        {
            int thread = t;
            walkers[t] = Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait(timeout.Token);
                    int count = sequential.Count;
                    int first = thread * count / 4;
                    int[] ids = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        ids[i] = thread % 2 == 0
                            ? (first + i) % count
                            : (first - i + count) % count;
                    }

                    return _FirstByIdMismatch(disk, sequential, ids, Invariant($"thread {thread}"));
                },
                timeout.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        string?[] results = await Task.WhenAll(walkers);

        // Assert
        await Assert.That(results.All(static result => result is null)).IsTrue();
    }

    #endregion

    #region Read windows

    [Test]
    public async Task ReadWindowIsThePowerOfTwoOfTheLongestRecordInItsGroup()
    {
        // Arrange: three groups; group 0 holds spans 1 and 65, group 1 a span of exactly 64, group 2 a clamped span.
        AscFrameIndex index = new(relativeClock: false, trackReadWindows: true);
        for (int i = 0; i < 130; i++)
        {
            index.Add(i * 100L, new AscTimestamp(i, 0), folded: true);
        }

        // Act
        index.RequireReadBytes(0, 1);
        index.RequireReadBytes(63, 65);
        index.RequireReadBytes(64, 64);
        index.RequireReadBytes(129, 1L << 40);
        index.Complete();

        // Assert
        await Assert.That(index.ReadWindow(0)).IsEqualTo(128);
        await Assert.That(index.ReadWindow(63)).IsEqualTo(128);
        await Assert.That(index.ReadWindow(64)).IsEqualTo(64);
        await Assert.That(index.ReadWindow(127)).IsEqualTo(64);
        await Assert.That(index.ReadWindow(128)).IsEqualTo(1 << AscFrameIndex.MaxWindowShift);
    }

    [Test]
    public async Task UntrackedIndexReportsTheLargestWindowAndNoWindowBytes()
    {
        // Arrange
        AscFrameIndex tracked = new(relativeClock: false, trackReadWindows: true);
        AscFrameIndex untracked = new(relativeClock: false);
        for (int i = 0; i < 200; i++)
        {
            tracked.Add(i * 50L, new AscTimestamp(i, 0), folded: true);
            tracked.RequireReadBytes(i, 50);
            untracked.Add(i * 50L, new AscTimestamp(i, 0), folded: true);
            untracked.RequireReadBytes(i, 50);
        }

        // Act
        tracked.Complete();
        untracked.Complete();

        // Assert: one window byte per group of 64 (4 groups), none without tracking.
        await Assert.That(untracked.ReadWindow(0)).IsEqualTo(1 << AscFrameIndex.MaxWindowShift);
        await Assert.That(tracked.ReadWindow(199)).IsEqualTo(64);
        await Assert.That(tracked.UsedBytes - untracked.UsedBytes).IsEqualTo(4L);
    }

    #endregion

    #region Clock

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ClocksMovedByEventsNegativeTimesAndExtremesMatchTheSequentialRead(bool relative)
    {
        // Arrange: statistic lines move a relative clock; a negative step; a line whose time does not parse; a time
        // that does not fit long nanoseconds.
        string text = new AscCaptureBuilder().Header(relative)
            .Can("0.100000", 1, 0x100, _Eight)
            .Statistic("0.200000", 1)
            .Can("0.300000", 1, 0x101, _Eight)
            .Can("-0.000100", 1, 0x102, _Eight)
            .Raw("x.y 1 103 Rx d 8 01 02 03 04 05 06 07 08")
            .Can("9300000000.000000", 1, 0x104, _Eight)
            .Can("0.400000", 1, 0x105, _Eight)
            .Statistic("1.000000", 1)
            .Can("0.500000", 1, 0x106, _Eight)
            .Build();

        // Act
        using AscSource memory = AscSource.FromText(text, "clock");
        List<Frame> memoryFrames = RandomAccessEquivalence.StartAndReadAll(memory);
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        List<Frame> diskFrames = RandomAccessEquivalence.StartAndReadAll(disk);

        // Assert: both backends agree with each other and with FrameById, also on the skipped ids.
        await Assert.That(RandomAccessEquivalence.FindMismatch(memory, memoryFrames, "clock/memory")).IsNull();
        await Assert.That(RandomAccessEquivalence.FindMismatch(disk, diskFrames, "clock/disk")).IsNull();
        await Assert.That(diskFrames.Select(static f => f.Timestamp.AsNanos)).IsEquivalentTo(memoryFrames.Select(static f => f.Timestamp.AsNanos));
    }

    [Test]
    public async Task RelativeIndexKeepsNegativeAndOverflowingClocksExactly()
    {
        // Arrange
        AscFrameIndex index = new(relativeClock: true);
        AscTimestamp[] clocks = [new(5, 0), new(-1, 500_000_000), new(9_300_000_000, 1), new(-9_300_000_000, 7), new(0, 999_999_999)];

        // Act
        for (int i = 0; i < clocks.Length; i++)
        {
            index.Add(i * 10L, clocks[i], folded: true);
        }

        index.Complete();

        // Assert
        for (int i = 0; i < clocks.Length; i++)
        {
            await Assert.That(index.TryGetClock(i, [], out AscTimestamp clock) && clock == clocks[i]).IsTrue();
        }
    }

    [Test]
    public async Task AbsoluteIndexStoresOnlyClocksThatDidNotFold()
    {
        // Arrange: frame 1 did not fold, so its clock is the running clock of an earlier line.
        AscFrameIndex index = new(relativeClock: false);
        index.Add(0, new AscTimestamp(1, 0), folded: true);
        index.Add(40, new AscTimestamp(1, 0), folded: false);
        index.Complete();

        // Act
        bool firstFolded = index.TryGetClock(0, "2.500000 1 123 Rx d 0"u8, out AscTimestamp first);
        bool secondStored = index.TryGetClock(1, "garbage"u8, out AscTimestamp second);

        // Assert: frame 0 re-folds its own line; frame 1 returns the stored override.
        await Assert.That(firstFolded && first == new AscTimestamp(2, 500_000_000)).IsTrue();
        await Assert.That(secondStored && second == new AscTimestamp(1, 0)).IsTrue();
        await Assert.That(index.UsedBytes).IsLessThan(2_000L);
    }

    #endregion

    #region Builders

    private static string _CanLines(int count, bool relative, bool crlf)
    {
        AscCaptureBuilder builder = new AscCaptureBuilder().Header(relative).LineEnding(crlf);
        for (int i = 0; i < count; i++)
        {
            string time = relative
                ? Invariant($"0.000{731 + (i % 7):D3}")
                : Invariant($"{(i + 1) * 0.000731:F6}");
            _ = builder.Can(time, 1 + (i % 2), 0x100u + (uint)i, _Eight.AsSpan(0, i % 9));
            if (i % 10 == 9)
            {
                _ = builder.Comment(Invariant($"after frame {i}")).Raw(string.Empty);
            }
        }

        return builder.Build();
    }

    private static string _AllBuses(bool crlf)
    {
        byte[] ethernet = TestFrames.BuildEthernetFrame(
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x11, 0x22, 0x33, 0x44, 0x55], 0x0800, new byte[80]);
        return new AscCaptureBuilder().Header(relative: false).LineEnding(crlf)
            .Can("0.100000", 1, 0x123, _Eight)
            .CanFd("0.200000", 1, 0x200, new byte[16])
            .Raw("0.250000 CANXL 1 Rx XLFF 0 0 123 05 0 3 4 0 0 01 0000000A 11 22 33 44 0 00400000")
            .Raw("0.300000 Li 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0")
            .Raw("0.400000 Fr 1 V9 0A 4 0 0 1234 x 8 0102030405060708")
            .Ethernet("0.500000", 1, ethernet)
            .Raw("0.600000 1 ErrorFrame")
            .Raw("0.700000 1 OverloadFrame")
            .Comment("between buses")
            .Raw(string.Empty)
            .Can("0.800000", 2, 0x7FF, [])
            .Build();
    }

    private static string _LongLine() =>
        new AscCaptureBuilder().Header(relative: false)
            .Can("0.100000", 1, 0x123, _Eight)
            .Raw("0.200000 1 124 Rx d 8 01 02 03 04 05 06 07 08 " + new string('A', 70_000))
            .Can("0.300000", 1, 0x125, _Eight)
            .Build();

    /// <summary>Ethernet frames of 64, 594, and 1518 bytes, each on one physical line (as the ASC exporter writes them).</summary>
    private static string _EthernetImix(bool crlf)
    {
        AscCaptureBuilder builder = new AscCaptureBuilder().Header(relative: false).LineEnding(crlf);
        int[] sizes = [64, 594, 64, 1518, 64, 594];
        for (int i = 0; i < 70; i++)
        {
            _ = builder.Raw(_EthernetLine(Invariant($"{(i + 1) * 0.001:F6}"), 1 + (i % 2), _Payload(sizes[i % sizes.Length], i)));
        }

        return builder.Build();
    }

    /// <summary>
    /// Wrapped Ethernet packets of 0 to 140 bytes, each followed by a different line: a CAN frame, a comment, a line
    /// that looks like hex until its last token, a blank line, a statistic line, another packet, or End TriggerBlock
    /// text. The varying lengths move the end of each record across many window boundaries.
    /// </summary>
    private static string _EthernetTerminators(bool crlf)
    {
        AscCaptureBuilder builder = new AscCaptureBuilder().Header(relative: false).LineEnding(crlf);
        for (int i = 0; i < 71; i++)
        {
            string time = Invariant($"{(i + 1) * 0.001:F6}");
            _ = builder.Ethernet(time, 1, _Payload(i * 2, i));
            _ = (i % 7) switch
            {
                0 => builder.Can(time, 2, 0x100u + (uint)i, _Eight),
                1 => builder.Comment(Invariant($"after packet {i}")),
                2 => builder.Raw("ab cd ef 01 zz"),
                3 => builder.Raw(string.Empty).Raw("   ").Can(time, 2, 0x200u + (uint)i, _Eight),
                4 => builder.Statistic(time, 1),
                5 => builder,
                _ => builder.Raw("End of segment " + i.ToString(CultureInfo.InvariantCulture)),
            };
        }

        return builder.Build();
    }

    /// <summary>CAN frames where every 16th frame is a one-line Ethernet frame: every group of 64 mixes both lengths.</summary>
    private static string _MixedGroups(int frames, bool crlf)
    {
        AscCaptureBuilder builder = new AscCaptureBuilder().Header(relative: false).LineEnding(crlf);
        int[] sizes = [64, 594, 1518];
        for (int i = 0; i < frames; i++)
        {
            string time = Invariant($"{(i + 1) * 0.0001:F6}");
            _ = i % 16 == 15
                ? builder.Raw(_EthernetLine(time, 1, _Payload(sizes[(i / 16) % sizes.Length], i)))
                : builder.Can(time, 1 + (i % 3), 0x100u + (uint)(i % 0x400), _Eight.AsSpan(0, i % 9));
        }

        return builder.Build();
    }

    /// <summary>A file whose last record is a wrapped Ethernet packet without a final line end.</summary>
    private static string _EthernetOpenAtEndOfFile()
    {
        string text = new AscCaptureBuilder().Header(relative: false)
            .Can("0.100000", 1, 0x123, _Eight)
            .Ethernet("0.200000", 1, _Payload(100, 1))
            .Build();
        return text[..text.LastIndexOf("End TriggerBlock", StringComparison.Ordinal)].TrimEnd('\n');
    }

    /// <summary>
    /// An AFDX packet whose header token takes the line break (<c>4\n5</c>), so the header does not fit its first line
    /// and the packet keeps the parser's full window; followed by hex continuation lines and a CAN frame.
    /// </summary>
    private static string _AfdxSplitHeader() =>
        new AscCaptureBuilder().Header(relative: false)
            .Can("0.100000", 1, 0x123, _Eight)
            .Raw("0.200000 AFDX 1 Rx 2 3 4")
            .Raw("5 40:00112233445566778899AABBCCDDEEFF")
            .Raw("00112233445566778899AABBCCDDEEFF")
            .Raw("00112233445566778899AABBCCDDEEFF")
            .Raw("00112233445566778899AABBCCDDEEFF")
            .Can("0.300000", 1, 0x124, _Eight)
            .Build();

    /// <summary>One physical Ethernet line: <c>time ETH channel Rx len:hex</c>.</summary>
    private static string _EthernetLine(string time, int channel, byte[] frame) =>
        Invariant($"{time} ETH {channel} Rx {frame.Length:x}:{Convert.ToHexString(frame)}");

    /// <summary>A deterministic payload of <paramref name="length"/> bytes that differs per <paramref name="seed"/>.</summary>
    private static byte[] _Payload(int length, int seed)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)((i * 31) + (seed * 7));
        }

        return payload;
    }

    /// <summary>Id order of a read-ahead test.</summary>
    private static int[] _Order(string order, int count)
    {
        int[] ids = new int[count];
        System.Random random = new(17);
        int filled = 0;
        switch (order)
        {
            case "forward":
                for (int i = 0; i < count; i++)
                {
                    ids[filled++] = i;
                }

                break;
            case "backward":
                for (int i = count - 1; i >= 0; i--)
                {
                    ids[filled++] = i;
                }

                break;
            case "every-second":
                for (int i = 0; i < count; i += 2)
                {
                    ids[filled++] = i;
                }

                for (int i = 1; i < count; i += 2)
                {
                    ids[filled++] = i;
                }

                break;
            case "random":
                for (int i = 0; i < count; i++)
                {
                    ids[filled++] = random.Next(count);
                }

                break;
            default:
                // Forward through the first half, then backward from the end: the walk turns and jumps.
                for (int i = 0; i < count / 2; i++)
                {
                    ids[filled++] = i;
                }

                for (int i = count - 1; filled < count; i--)
                {
                    ids[filled++] = i;
                }

                break;
        }

        return ids;
    }

    /// <summary>Requests <paramref name="ids"/> by id; the first id whose frame differs from the sequential one, else null.</summary>
    private static string? _FirstByIdMismatch(AscSource source, List<Frame> sequential, int[] ids, string label)
    {
        foreach (int id in ids)
        {
            Frame? actual = source.FrameById(new FrameId(id));
            if (actual is null || !RandomAccessEquivalence.SameFrame(sequential[id], actual.Value))
            {
                return Invariant($"{label}: FrameById({id}) differs from the sequential frame.");
            }
        }

        return null;
    }

    /// <summary>The first index where the two frame lists differ (count included), else null.</summary>
    private static string? _FirstDifference(List<Frame> expected, List<Frame> actual)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (!RandomAccessEquivalence.SameFrame(expected[i], actual[i]))
            {
                return Invariant($"frame {i} differs");
            }
        }

        return expected.Count == actual.Count
            ? null
            : Invariant($"{actual.Count} frames instead of {expected.Count}");
    }

    #endregion
}
