// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Stress;

/// <summary>
/// Random reads by id run through a reader gate, and <c>Dispose</c> waits for them before it unmaps the file. On the
/// mapped backends a read after the unmap would be an access violation (a process crash, not an exception). These
/// tests dispose a source from another thread while four threads read it by id; a reader may only see the correct frame,
/// <c>null</c> (only once disposal started), or <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class DisposeDuringRandomReadStressTests
{
    #region Constants

    private const int _Rounds = 24;
    private const int _FrameCount = 4_000;
    private const int _ReaderCount = 4;

    /// <summary>Ids per <c>FramesById</c> call of a batch reader: more than three chunks of 64.</summary>
    private const int _BatchLength = 200;
    private const int _ReadsBeforeDispose = 200;

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    #endregion

    #region Tests

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task PcapngMappedDisposeDuringFrameByIdIsSafe(ScanMode scanMode)
    {
        byte[] bytes = _BuildPcapng();
        using TempCaptureFile file = new(bytes, ".pcapng");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(PcapngSource.FromData(bytes));
        PcapngSourceOptions options = new() { PreloadBudget = null, ScanMode = scanMode };

        List<string> problems = await _DisposeWhileReadingById(() => PcapngSource.Open(file.Path, options), expected, file.Path, batch: false);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task BlfMappedDisposeDuringFrameByIdIsSafe()
    {
        byte[] bytes = _BuildBlf();
        using TempCaptureFile file = new(bytes, ".blf");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(BlfSource.FromData(bytes, "reference.blf"));
        BlfSourceOptions options = new() { PreloadBudget = null };

        List<string> problems = await _DisposeWhileReadingById(() => BlfSource.Open(file.Path, options), expected, file.Path, batch: false);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task BlfPreloadedDisposeDuringFrameByIdIsSafe()
    {
        byte[] bytes = _BuildBlf();
        using TempCaptureFile file = new(bytes, ".blf");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(BlfSource.FromData(bytes, "reference.blf"));
        BlfSourceOptions options = new() { PreloadBudget = long.MaxValue };

        List<string> problems = await _DisposeWhileReadingById(() => BlfSource.Open(file.Path, options), expected, file.Path, batch: false);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task AscMappedDisposeDuringFrameByIdIsSafe()
    {
        byte[] bytes = _BuildAsc();
        using TempCaptureFile file = new(bytes, ".asc");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(AscSource.FromData(bytes, "reference.asc"));
        AscSourceOptions options = new() { PreloadBudget = null };

        List<string> problems = await _DisposeWhileReadingById(() => AscSource.Open(file.Path, options), expected, file.Path, batch: false);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task AscInMemoryDisposeDuringFrameByIdIsSafe()
    {
        byte[] bytes = _BuildAsc();
        using TempCaptureFile file = new(bytes, ".asc");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(AscSource.FromData(bytes, "reference.asc"));
        AscSourceOptions options = new() { PreloadBudget = long.MaxValue };

        List<string> problems = await _DisposeWhileReadingById(() => AscSource.Open(file.Path, options), expected, file.Path, batch: false);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task PcapngMappedDisposeDuringFramesByIdIsSafe(ScanMode scanMode)
    {
        byte[] bytes = _BuildPcapng();
        using TempCaptureFile file = new(bytes, ".pcapng");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(PcapngSource.FromData(bytes));
        PcapngSourceOptions options = new() { PreloadBudget = null, ScanMode = scanMode };

        List<string> problems = await _DisposeWhileReadingById(() => PcapngSource.Open(file.Path, options), expected, file.Path, batch: true);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments(long.MaxValue)]
    public async Task BlfDisposeDuringFramesByIdIsSafe(long? preloadBudget)
    {
        byte[] bytes = _BuildBlf();
        using TempCaptureFile file = new(bytes, ".blf");
        List<Frame> expected = RandomAccessEquivalence.StartAndReadAll(BlfSource.FromData(bytes, "reference.blf"));
        BlfSourceOptions options = new() { PreloadBudget = preloadBudget };

        List<string> problems = await _DisposeWhileReadingById(() => BlfSource.Open(file.Path, options), expected, file.Path, batch: true);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task BlfFrameByIdAfterDisposeThrows()
    {
        using TempCaptureFile file = new(_BuildBlf(), ".blf");
        BlfSource source = BlfSource.Open(file.Path, new BlfSourceOptions { PreloadBudget = null });
        _ = RandomAccessEquivalence.StartAndReadAll(source);
        source.Dispose();

        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task PcapngFrameByIdAfterDisposeThrows()
    {
        using TempCaptureFile file = new(_BuildPcapng(), ".pcapng");
        PcapngSource source = PcapngSource.Open(file.Path, new PcapngSourceOptions { PreloadBudget = null });
        _ = RandomAccessEquivalence.StartAndReadAll(source);
        source.Dispose();

        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<ObjectDisposedException>();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Per round: opens a source, drains it (so every id is indexed), starts four readers that loop on random ids, waits
    /// until each has read <see cref="_ReadsBeforeDispose"/> frames, and disposes the source. Returns the problems found:
    /// a wrong frame, a null before disposal started, an exception other than <see cref="ObjectDisposedException"/>, and
    /// (Windows) a file that cannot be opened for writing after <c>Dispose</c> returned.
    /// </summary>
    private static async Task<List<string>> _DisposeWhileReadingById(
        Func<IRandomAccessFrameSource> open, List<Frame> expected, string path, bool batch)
    {
        List<string> problems = [];
        for (int round = 0; round < _Rounds; round++)
        {
            IRandomAccessFrameSource source = open();
            _ = SourceTestFixture.InitializeAndStartSource(source);
            while (source.NextFrame() is not null)
            {
            }

            RoundState state = new();
            using CountdownEvent ready = new(_ReaderCount);
            Task[] readers = new Task[_ReaderCount];
            for (int r = 0; r < _ReaderCount; r++)
            {
                int seed = (round * _ReaderCount) + r;
                readers[r] = Task.Factory.StartNew(
                    () => _ReadLoop(source, expected, seed, state, ready, batch), TaskCreationOptions.LongRunning);
            }

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                problems.Add("Readers did not reach the read count.");
            }

            Volatile.Write(ref state.DisposeStarted, 1);
            source.Dispose();
            Volatile.Write(ref state.Stop, 1);
            await Task.WhenAll(readers);

            problems.AddRange(state.Problems);
            if (OperatingSystem.IsWindows() && !_CanOpenForWriting(path))
            {
                problems.Add("The file is still locked after Dispose returned.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Reads random ids until the source is disposed or the round stops; records anything unexpected. A batch reader asks
    /// for <see cref="_BatchLength"/> ids per call and counts every entry as one read.
    /// </summary>
    private static void _ReadLoop(
        IRandomAccessFrameSource source, List<Frame> expected, int seed, RoundState state, CountdownEvent ready, bool batch)
    {
        System.Random random = new(seed);
        Frame?[] frames = new Frame?[batch ? _BatchLength : 1];
        int reads = 0;
        bool signalled = false;
        try
        {
            while (Volatile.Read(ref state.Stop) == 0)
            {
                // A batch reader reads a range of 200 ids (more than three chunks of 64) that starts at a random id and
                // stays inside the source; a single reader reads one random id.
                int first = batch
                    ? random.Next(Math.Max(1, expected.Count - _BatchLength))
                    : random.Next(expected.Count);
                if (batch)
                {
                    _ = source.FramesById(new FrameId(first), frames);
                }
                else
                {
                    frames[0] = source.FrameById(new FrameId(first));
                }

                for (int i = 0; i < frames.Length; i++)
                {
                    _Check(first + i, frames[i], expected, state);
                }

                reads += frames.Length;
                if (!signalled && reads >= _ReadsBeforeDispose)
                {
                    signalled = true;
                    _ = ready.Signal();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected once Dispose won the race.
        }
        catch (Exception exception)
        {
            state.Add(Invariant($"Unexpected {exception.GetType().Name}: {exception.Message}"));
        }
        finally
        {
            // A reader that ended early must not leave the test thread waiting.
            if (!signalled)
            {
                _ = ready.Signal();
            }
        }
    }

    private static void _Check(int id, Frame? frame, List<Frame> expected, RoundState state)
    {
        if (frame is null)
        {
            // Null is a disposal outcome; before disposal started it would be a lost frame.
            if (Volatile.Read(ref state.DisposeStarted) == 0)
            {
                state.Add(Invariant($"Read of id {id} returned null before disposal started."));
            }
        }
        else if (!RandomAccessEquivalence.SameFrame(expected[id], frame.Value))
        {
            state.Add(Invariant($"Read of id {id} returned a frame that differs from the sequential frame."));
        }
    }

    private static bool _CanOpenForWriting(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static byte[] _BuildPcapng()
    {
        using PcapngCaptureBuilder writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < _FrameCount; i++)
        {
            writer.WriteFrame(0, (i + 1) * 1_000L, TestFrames.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, (byte)(i >> 8)]));
        }

        return writer.Build();
    }

    private static byte[] _BuildBlf()
    {
        BlfCaptureBuilder inner = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            _ = inner.AddCanFrame(1, TestFrames.BuildSocketCanClassic(0x100u + (uint)(i & 0xFF), [(byte)i]), (i + 1) * 1_000L);
        }

        // Raw objects on top level plus one compressed container: both random-access read paths touch the mapping.
        BlfCaptureBuilder outer = new();
        _ = outer.AddLogContainer(BlfConstants.CompressionLz4, inner);
        for (int i = 0; i < _FrameCount; i++)
        {
            _ = outer.AddCanFrame(2, TestFrames.BuildSocketCanClassic(0x200u + (uint)(i & 0xFF), [(byte)i]), (_FrameCount + i + 1) * 1_000L);
        }

        return outer.Build();
    }

    /// <summary>Synthetic ASC capture of <see cref="_FrameCount"/> classic CAN frames with absolute timestamps.</summary>
    private static byte[] _BuildAsc()
    {
        AscCaptureBuilder builder = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            _ = builder.Can(Invariant($"{(i + 1) * 0.001:F6}"), 1, 0x100u + (uint)(i & 0xFF), [(byte)i, (byte)(i >> 8)]);
        }

        return Encoding.Latin1.GetBytes(builder.Build());
    }

    /// <summary>Flags and problems shared by the readers of one round; the flags are fields so they can be passed by reference.</summary>
    private sealed class RoundState
    {
        public int DisposeStarted;
        public int Stop;
        private readonly Lock _Gate = new();
        private readonly List<string> _Problems = [];

        public List<string> Problems
        {
            get
            {
                lock (_Gate)
                {
                    return [.. _Problems];
                }
            }
        }

        public void Add(string problem)
        {
            lock (_Gate)
            {
                _Problems.Add(problem);
            }
        }
    }

    #endregion
}
