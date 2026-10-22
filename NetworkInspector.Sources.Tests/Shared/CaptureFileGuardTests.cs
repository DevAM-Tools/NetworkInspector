// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// File sources keep their capture locked against writers (Windows) or detect a change and fail closed
/// (Linux/macOS); in-memory sources have no guard.
/// </summary>
internal sealed class CaptureFileGuardTests
{
    #region Corpus

    /// <summary>Format, file extension, capture bytes, and whether the file is opened with the preload backend.</summary>
    public static IEnumerable<Func<(string Format, bool Preload)>> SourceKinds()
    {
        foreach (string format in new[] { "pcapng", "blf", "asc" })
        {
            yield return () => (format, true);
            yield return () => (format, false);
        }
    }

    #endregion

    #region Windows: writers denied

    [Test]
    [RunOn(OS.Windows)]
    [MethodDataSource(nameof(SourceKinds))]
    public async Task WritersAreDeniedWhileTheSourceIsOpen(string format, bool preload)
    {
        // Arrange
        using TempCaptureFile file = new(_Capture(format, 20), "." + format);
        bool writerDeniedWhileOpen;
        bool readerAllowedWhileOpen;

        // Act
        using (IRandomAccessFrameSource source = _Open(format, file.Path, preload))
        {
            _ = SourceTestFixture.InitializeAndStartSource(source);
            writerDeniedWhileOpen = !_CanOpen(file.Path, FileAccess.Write, FileShare.ReadWrite);
            readerAllowedWhileOpen = _CanOpen(file.Path, FileAccess.Read, FileShare.ReadWrite);
        }

        bool writerAllowedAfterDispose = _CanOpen(file.Path, FileAccess.Write, FileShare.ReadWrite);

        // Assert
        await Assert.That(writerDeniedWhileOpen).IsTrue();
        await Assert.That(readerAllowedWhileOpen).IsTrue();
        await Assert.That(writerAllowedAfterDispose).IsTrue();
    }

    #endregion

    #region Linux and macOS: changes detected

    [Test]
    [ExcludeOn(OS.Windows)]
    [MethodDataSource(nameof(SourceKinds))]
    public async Task AppendedByteStopsReadingAndFrameById(string format, bool preload)
    {
        // Arrange: a lazily scanned (or, for ASC, indexed) source that already returned 10 frames.
        using TempCaptureFile file = new(_Capture(format, 40), "." + format);
        using IRandomAccessFrameSource source = _Open(format, file.Path, preload);
        List<FrameReadErrorKind> kinds = [];
        ((IErrorTolerantFrameSource)source).FrameSkipped += (_, e) => kinds.Add(e.Kind);
        _ = SourceTestFixture.InitializeAndStartSource(source);
        for (int i = 0; i < 10; i++)
        {
            _ = source.NextFrame();
        }

        // Act: another handle appends one byte (advisory locks do not stop it).
        using (FileStream writer = new(file.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.WriteByte(0);
        }

        // FrameById makes no system call, so before a sequential check it still serves the unchanged bytes.
        Frame? byIdBeforeCheck = source.FrameById(new FrameId(0));

        // The end-of-read check of the sequential path finds the change and stops reading; a preloaded source has no
        // check left and reads its copy to the end.
        int readToEnd = 0;
        while (source.NextFrame() is not null)
        {
            readToEnd++;
        }

        Frame? later = source.NextFrame();
        Frame? byIdAfterCheck = source.FrameById(new FrameId(0));

        // Assert
        await Assert.That(byIdBeforeCheck).IsNotNull();
        await Assert.That(later).IsNull();
        if (preload)
        {
            await Assert.That(readToEnd).IsEqualTo(30);
            await Assert.That(byIdAfterCheck).IsNotNull();
            await Assert.That(kinds).DoesNotContain(FrameReadErrorKind.SourceFileChanged);
        }
        else
        {
            await Assert.That(byIdAfterCheck).IsNull();
            await Assert.That(kinds).Contains(FrameReadErrorKind.SourceFileChanged);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [MethodDataSource(nameof(SourceKinds))]
    public async Task ChangedModificationTimeIsDetected(string format, bool preload)
    {
        // Arrange
        using TempCaptureFile file = new(_Capture(format, 5), "." + format);
        using IRandomAccessFrameSource source = _Open(format, file.Path, preload);
        List<FrameReadErrorKind> kinds = [];
        ((IErrorTolerantFrameSource)source).FrameSkipped += (_, e) => kinds.Add(e.Kind);
        _ = SourceTestFixture.InitializeAndStartSource(source);

        // A lazy scan indexes frame 0 with the first read.
        _ = source.NextFrame();
        Frame? byIdBeforeChange = source.FrameById(new FrameId(0));

        // Act: only the modification time moves; the end-of-read check compares it.
        File.SetLastWriteTimeUtc(file.Path, DateTime.UtcNow.AddHours(1));
        while (source.NextFrame() is not null)
        {
        }

        Frame? byIdAfterCheck = source.FrameById(new FrameId(0));

        // Assert: a preloaded source serves its copy; a file-backed source fails closed.
        await Assert.That(byIdBeforeChange).IsNotNull();
        if (preload)
        {
            await Assert.That(byIdAfterCheck).IsNotNull();
            await Assert.That(kinds).DoesNotContain(FrameReadErrorKind.SourceFileChanged);
        }
        else
        {
            await Assert.That(byIdAfterCheck).IsNull();
            await Assert.That(kinds).Contains(FrameReadErrorKind.SourceFileChanged);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [MethodDataSource(nameof(SourceKinds))]
    public async Task BackgroundMonitorStopsFrameByIdWithoutSequentialReading(string format, bool preload)
    {
        // Arrange: a source that read one frame; from here on only FrameById runs.
        using TempCaptureFile file = new(_Capture(format, 40), "." + format);
        using IRandomAccessFrameSource source = _Open(format, file.Path, preload);
        List<FrameReadErrorKind> kinds = [];
        ((IErrorTolerantFrameSource)source).FrameSkipped += (_, e) => kinds.Add(e.Kind);
        _ = SourceTestFixture.InitializeAndStartSource(source);
        _ = source.NextFrame();
        CaptureFileGuard guard = _GuardOf(source);
        Frame? before = source.FrameById(new FrameId(0));

        // Act: another handle appends one byte. The monitor of a file-backed source (default interval) finds it without any
        // read call; a preloaded source runs no monitor and keeps serving its copy.
        using (FileStream writer = new(file.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.WriteByte(0);
        }

        if (!preload)
        {
            await guard.ChangeSignal!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Frame? after = source.FrameById(new FrameId(0));
        Frame? next = source.NextFrame();

        // Assert
        await Assert.That(guard.IsMonitored).IsEqualTo(!preload);
        await Assert.That(before).IsNotNull();
        if (preload)
        {
            await Assert.That(after).IsNotNull();
            await Assert.That(next).IsNotNull();
            await Assert.That(kinds).DoesNotContain(FrameReadErrorKind.SourceFileChanged);
        }
        else
        {
            // Random access fails closed and the next sequential read reports the change and ends.
            await Assert.That(after).IsNull();
            await Assert.That(next).IsNull();
            await Assert.That(kinds).Contains(FrameReadErrorKind.SourceFileChanged);
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task StopMonitoringEndsChangeDetection()
    {
        // Arrange: a monitored guard whose owner has verified its copy and stops the monitor.
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        using CaptureFileGuard guard = CaptureFileGuard.Open(file.Path, TimeSpan.FromMilliseconds(20));
        guard.StopMonitoring();
        guard.StopMonitoring();

        // Act: the modification time moves; without a monitor only the direct check could see it, and it is switched off.
        File.SetLastWriteTimeUtc(file.Path, DateTime.UtcNow.AddHours(1));

        // Assert
        await Assert.That(guard.IsMonitored).IsFalse();
        await Assert.That(guard.IsUnchanged()).IsTrue();
        await Assert.That(guard.ChangeDetected).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task MonitorDetectsAModificationTimeChange()
    {
        // Arrange
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        using CaptureFileGuard guard = CaptureFileGuard.Open(file.Path, TimeSpan.FromMilliseconds(20));
        bool unchangedBefore = guard.IsUnchanged();

        // Act
        File.SetLastWriteTimeUtc(file.Path, DateTime.UtcNow.AddHours(1));
        await guard.ChangeSignal!.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(unchangedBefore).IsTrue();
        await Assert.That(guard.ChangeDetected).IsTrue();
        await Assert.That(guard.IsUnchanged()).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task UndisposedMonitoredGuardStaysCollectable()
    {
        // Arrange / Act: a guard nobody disposes; its timer must not keep it alive.
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        WeakReference guard = _OpenAndForget(file.Path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        await Assert.That(guard.IsAlive).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task DisposedGuardReportsChangedWithoutThrowing()
    {
        // Arrange
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        CaptureFileGuard guard = CaptureFileGuard.Open(file.Path, TimeSpan.FromMilliseconds(5));

        // Act: dispose while the monitor is running; a late tick must find the latch and stop.
        guard.Dispose();
        guard.Dispose();

        // Assert
        await Assert.That(guard.IsUnchanged()).IsFalse();
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task WindowsGuardRunsNoMonitor()
    {
        // Arrange / Act
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        using CaptureFileGuard guard = CaptureFileGuard.Open(file.Path);

        // Assert: the OS denies writers, so no background check and no system call per check.
        await Assert.That(guard.IsMonitored).IsFalse();
        await Assert.That(guard.ChangeSignal is null).IsTrue();
        await Assert.That(guard.IsUnchanged()).IsTrue();
    }

    [Test]
    public async Task NonPositiveMonitorIntervalIsRejected()
    {
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");

        await Assert.That(() => CaptureFileGuard.Open(file.Path, TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task HasChangedAtReportsNothingForAnUnchangedFile()
    {
        // Arrange: no background tick can interfere with the one-hour period.
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        using CaptureFileGuard guard = CaptureFileGuard.Open(file.Path, TimeSpan.FromHours(1));

        // Act / Assert: the interval frames ask the file, the others read the latch; both say "unchanged".
        await Assert.That(guard.HasChangedAt(0)).IsFalse();
        await Assert.That(guard.HasChangedAt(1)).IsFalse();
        await Assert.That(guard.HasChangedAt(CaptureFileGuard.SequentialCheckInterval)).IsFalse();
        await Assert.That(guard.HasChangedAt(CaptureFileGuard.SequentialCheckInterval + 1)).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task HasChangedAtAsksTheFileOnlyOnIntervalFrames()
    {
        // Arrange: only the modification time moves, and the one-hour period keeps the monitor out of the way.
        using TempCaptureFile file = new(_Capture("pcapng", 5), ".pcapng");
        using CaptureFileGuard guard = CaptureFileGuard.Open(file.Path, TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(file.Path, DateTime.UtcNow.AddHours(1));

        // Act: a frame between two checks reads only the latch, which no check has set yet.
        bool betweenChecks = guard.HasChangedAt(1);
        bool atInterval = guard.HasChangedAt(CaptureFileGuard.SequentialCheckInterval);
        bool afterDetection = guard.HasChangedAt(1);

        // Assert
        await Assert.That(betweenChecks).IsFalse();
        await Assert.That(atInterval).IsTrue();
        await Assert.That(afterDetection).IsTrue();
    }

    #endregion

    #region In-memory sources

    [Test]
    [Arguments("pcapng")]
    [Arguments("blf")]
    [Arguments("asc")]
    public async Task InMemorySourcesReadAndServeFrameByIdWithoutAGuard(string format)
    {
        // Arrange
        byte[] data = _Capture(format, 30);
        using IRandomAccessFrameSource source = format switch
        {
            "pcapng" => PcapngSource.FromData(data, "memory"),
            "blf" => BlfSource.FromData(data, "memory"),
            _ => AscSource.FromText(Encoding.Latin1.GetString(data)),
        };
        List<FrameReadErrorKind> kinds = [];
        ((IErrorTolerantFrameSource)source).FrameSkipped += (_, e) => kinds.Add(e.Kind);

        // Act
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);

        // Assert
        await Assert.That(frames.Count).IsEqualTo(30);
        await Assert.That(RandomAccessEquivalence.FindMismatch(source, frames, format)).IsNull();
        await Assert.That(kinds).DoesNotContain(FrameReadErrorKind.SourceFileChanged);
    }

    #endregion

    #region Helpers

    private static IRandomAccessFrameSource _Open(string format, string path, bool preload)
    {
        long? budget = preload ? long.MaxValue : null;
        return format switch
        {
            "pcapng" => PcapngSource.Open(path, new PcapngSourceOptions { PreloadBudget = budget, ScanMode = ScanMode.Lazy }),
            "blf" => BlfSource.Open(path, new BlfSourceOptions { PreloadBudget = budget, ScanMode = ScanMode.Lazy }),
            _ => AscSource.Open(path, new AscSourceOptions { PreloadBudget = budget }),
        };
    }

    private static CaptureFileGuard _GuardOf(IRandomAccessFrameSource source) => source switch
    {
        PcapngSource pcapng => pcapng.FileGuard!,
        BlfSource blf => blf.FileGuard!,
        AscSource asc => asc.FileGuard!,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a file source."),
    };

    /// <summary>Opens a monitored guard in a separate frame, so no local of the test keeps it reachable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference _OpenAndForget(string path) => new(CaptureFileGuard.Open(path, TimeSpan.FromMilliseconds(10)));

    private static bool _CanOpen(string path, FileAccess access, FileShare share)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, access, share);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static byte[] _Capture(string format, int frames)
    {
        byte[] can = TestFrames.BuildSocketCanClassic(0x123, [1, 2, 3, 4, 5, 6, 7, 8]);
        switch (format)
        {
            case "pcapng":
                using (PcapngCaptureBuilder writer = new())
                {
                    writer.AddInterface(LinkType.CanSocketcan, nanosecondResolution: true);
                    for (int i = 0; i < frames; i++)
                    {
                        writer.WriteFrame(0, 1_000_000L * (i + 1), can);
                    }

                    return writer.Build();
                }

            case "blf":
                BlfCaptureBuilder blf = new();
                for (int i = 0; i < frames; i++)
                {
                    _ = blf.AddCanFrame(1, can, 1_000_000L * (i + 1));
                }

                return blf.Build();

            default:
                AscCaptureBuilder asc = new AscCaptureBuilder().Header(relative: false);
                for (int i = 0; i < frames; i++)
                {
                    _ = asc.Can(Invariant($"{(i + 1) * 0.001:F6}"), 1, 0x123, [1, 2, 3, 4, 5, 6, 7, 8]);
                }

                return Encoding.Latin1.GetBytes(asc.Build());
        }
    }

    #endregion
}
