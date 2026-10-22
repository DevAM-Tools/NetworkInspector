// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// BLF (Binary Logging Format) frame source with random access support.
/// Supports CAN, CAN FD, LIN, FlexRay, and Ethernet object types.
///
/// Key design decisions:
/// - Two-level iteration: outer file blocks → inner decompressed container objects
/// - 2Q scan-resistant cache for decompressed containers (avoids LRU scan pollution)
/// - Skip is <c>max(max(16, object_length), header_size)</c>; 0–3 trailing zeros are consumed
///   by a 1-byte <c>LOBJ</c> scan, not by adding a computed pad to the skip
/// - Incomplete inner objects at a container tail are carried into the next decompressed blob
/// - Absolute timestamps = file start offset (from BlfDate) + relative timestamp
/// - Compact frame index (<see cref="BlfFrameIndex"/>): only where each frame object starts (a virtual position inside the
///   decompressed containers, or a file offset for raw objects). Type, channel, length, and timestamp are re-read from
///   the object header with the scanner's parser, so the index costs about 0.7 B per CAN frame and a changed file fails
///   closed (<see cref="CaptureFileGuard"/>).
/// - Random reads (<see cref="FrameById"/>, <see cref="FramesById"/>) run inside a <see cref="ReaderGate"/> section; the
///   sequential <see cref="NextFrame"/> holds a view lease so a <see cref="FrameSkipped"/> handler may dispose.
/// - A reading thread remembers the last container it used (see the container memo), so ids read one after another
///   skip the row search and the cache lock.
/// - Details: SOURCE_GUIDE §8.5, §11.6, §13.4.
/// </summary>
public sealed partial class BlfSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    private readonly BlfDataBackend _Backend;

    /// <summary>
    /// Keeps the BLF file open with <see cref="FileShare.Read"/> for the source's lifetime (writers denied on Windows,
    /// changes detected on Linux/macOS). Null for in-memory data (<see cref="FromData"/>).
    /// </summary>
    private readonly CaptureFileGuard? _Guard;

    private readonly BlfFileInfo _FileInfo;

    /// <summary>Where every accepted frame object starts; everything else is re-read from the object header.</summary>
    private readonly BlfFrameIndex _Index;

    private readonly BlfSourceOptions _Options;

    /// <inheritdoc />
    public string UiName { get; }

    // Container cache for random access (2Q algorithm from Stack project).
    // The cache itself is not thread-safe; all accesses are guarded by _ContainerCacheLock.
    private readonly TwoQueueCache<long, byte[]> _ContainerCache;

    /// <summary>
    /// Synchronises every <see cref="_ContainerCache"/> access. The cache implementation
    /// is documented as non-thread-safe; concurrent <see cref="FrameById"/> calls would
    /// otherwise corrupt its internal LRU lists. Decompression is performed outside the
    /// lock to keep the critical section O(1).
    /// </summary>
    private readonly Lock _ContainerCacheLock = new();

    // Scanner for lazy/incremental scanning. Declared volatile so a null write from
    // Dispose() is observed promptly by sequential / random-access paths.
    private volatile BlfIncrementalScanner? _Scanner;
    private volatile bool _FullyScanned;

    // Interface registration
    private FrameSourceId _SourceId;
    private volatile FrameInterfaceRegistry? _Registry;
    private readonly Dictionary<(uint ObjectType, ushort Channel, bool HasHardwareChannel, ushort HardwareChannel), FrameInterfaceId> _InterfaceMap = new();

    /// <summary>
    /// Immutable copy of <see cref="_InterfaceMap"/>, replaced (never mutated) under <see cref="_InterfaceLock"/> after
    /// every registration. Readers on any thread look an interface up here without the lock; only a miss takes it.
    /// Interfaces are few, so the copy on registration costs nothing measurable.
    /// </summary>
    private volatile Dictionary<(uint ObjectType, ushort Channel, bool HasHardwareChannel, ushort HardwareChannel), FrameInterfaceId>
        _InterfaceSnapshot = new();

    /// <summary>
    /// Synchronises registrations of <see cref="_GetOrRegisterInterface"/> that arrive via the thread-safe
    /// <see cref="FrameById"/> path. All reads and writes of <see cref="_InterfaceMap"/> are guarded by this lock; lookups
    /// that hit read <see cref="_InterfaceSnapshot"/> without it.
    /// </summary>
    private readonly Lock _InterfaceLock = new();

    /// <summary>
    /// Channel names from AppText objects (busType + channel → name) once the scan has finished. While a lazy scan
    /// runs, the scanner's published snapshot is read instead (<see cref="_CurrentChannelNames"/>).
    /// </summary>
    private volatile IReadOnlyDictionary<(byte BusType, byte Channel), string>? _ChannelNames;

    /// <summary>
    /// The container most recently decoded by the scanner, held strongly so sequential <see cref="NextFrame"/> never
    /// depends on the cache budget and never inflates a container twice. Written and read on the NextFrame thread only;
    /// <see cref="FrameById"/> does not read it (it uses the cache).
    /// </summary>
    private long _SequentialContainerOffset = -1;

    /// <summary>Decompressed bytes of the container at <see cref="_SequentialContainerOffset"/>.</summary>
    private byte[]? _SequentialContainer;

    /// <summary>
    /// Container row of the last frame the sequential path resolved
    /// (<see cref="BlfFrameIndex.TryGetLocation(int, ref BlfRowCursor, out BlfFrameLocation)"/>). NextFrame thread only.
    /// </summary>
    private BlfRowCursor _SequentialRow = BlfRowCursor.Empty;

    /// <summary>
    /// Interface key and id of the last frame the sequential path built: consecutive frames of one channel reuse the id
    /// without the interface lock. Reset by <see cref="Start"/>. NextFrame thread only.
    /// </summary>
    private (uint ObjectType, ushort Channel, ushort? HardwareChannel) _SequentialInterfaceKey;

    /// <summary>Registry id that belongs to <see cref="_SequentialInterfaceKey"/>; invalid when nothing is cached.</summary>
    private FrameInterfaceId _SequentialInterfaceId = FrameInterfaceId.Invalid;

    /// <summary>Frame slab allocator of the sequential path; null when <see cref="BlfSourceOptions.FrameSlabSize"/> is 0.</summary>
    private FrameSlabAllocator? _Slabs;

    // Sequential read state
    private int _NextFrameIndex;
    private volatile bool _Started;
    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    // Error tolerance statistics
    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();
    private volatile bool _Aborted;

    /// <summary>Tracks scanner decompression failures already reported via HandleSkip.</summary>
    private long _LastReportedDecompressionFailures;

    /// <summary>Tracks scanner corrupted-container-header errors already reported via HandleSkip.</summary>
    private long _LastReportedCorruptedContainerCount;

    /// <summary>Tracks scanner tail-truncation events already reported via HandleSkip.</summary>
    private long _LastReportedTruncatedObjectCount;

    /// <summary>Tracks scanner unparsable frame objects already reported via HandleSkip.</summary>
    private long _LastReportedUnparsableObjectCount;

    /// <summary>Tracks scanner corrupt inner objects already reported via HandleSkip.</summary>
    private long _LastReportedCorruptedInnerObjectCount;

    /// <summary>Tracks scanner objects cut by end of file already reported via HandleSkip.</summary>
    private long _LastReportedTruncatedFileObjectCount;

    /// <summary>
    /// The scanner's failure marks in file order (<see cref="BlfIncrementalScanner.Marks"/>), each with the number of frames
    /// indexed before it; <see cref="NextFrame"/> replays them before the frame they precede
    /// (<see cref="_ReplayPendingDiagnosticsBefore"/>). Set once after a full scan, re-read after every lazy scan step
    /// (the list only grows; the cursor stays valid). Null when nothing failed or everything was replayed so far.
    /// NextFrame thread only.
    /// </summary>
    private List<BlfScanDiagnosticsMark>? _PendingScanDiagnostics;

    /// <summary>Next mark of <see cref="_PendingScanDiagnostics"/> to replay. NextFrame thread only.</summary>
    private int _PendingScanDiagnosticsCursor;

    /// <summary>
    /// Guards the backend, the container cache, and the registry against a Dispose/read race on the random-access
    /// path: <see cref="FrameById"/> runs inside a section, and <see cref="Dispose"/> closes the gate (waiting for
    /// running sections on other threads) before it releases anything. Readers write only their own thread's slot, so
    /// concurrent readers share no written cache line. <see cref="NextFrame"/> keeps the view lease of the backend.
    /// </summary>
    private readonly ReaderGate _Gate = new();

    /// <summary>Last issued source id for <see cref="ContainerMemo"/> keys; ids start at 1.</summary>
    private static long _LastMemoOwner;

    /// <summary>Process-unique key of this source in the per-thread <see cref="ContainerMemo"/>.</summary>
    private readonly long _MemoOwner = Interlocked.Increment(ref _LastMemoOwner);

    /// <summary>The calling thread's memo of the last container it read by id (see <see cref="_GetContainerDataMemoized"/>).</summary>
    [ThreadStatic]
    private static ContainerMemo? _Memo;

    /// <summary>Counts silent random-access failures (decompression errors, OOM) that would otherwise be invisible to callers.</summary>
    private long _RandomAccessFailureCount;

    /// <summary>
    /// Containers this source inflated itself (sequential fallback and random access), not counting the scanner.
    /// Written with <see cref="Interlocked.Increment(ref long)"/> from any reading thread.
    /// </summary>
    private long _SourceDecompressionCount;

    /// <summary>
    /// Tracks containers that are currently being decompressed by exactly one "winner" thread.
    /// Other threads needing the same container offset wait on the entry's
    /// <see cref="ContainerDecompressionWork.Ready"/> signal outside <see cref="_ContainerCacheLock"/>,
    /// then read the result from <see cref="_ContainerCache"/> (or check
    /// <see cref="ContainerDecompressionWork.Error"/>) after waking.
    /// All accesses are guarded by <see cref="_ContainerCacheLock"/>.
    /// </summary>
    private readonly Dictionary<long, ContainerDecompressionWork> _PendingDecompressions = new();

    /// <summary>
    /// Limits the number of simultaneously executing decompression operations to bound
    /// transient peak memory. At most <see cref="BlfSourceOptions.MaxDecompressionConcurrency"/>
    /// threads may decompress in parallel; additional threads block until a slot is released.
    /// The semaphore is acquired outside <see cref="_ContainerCacheLock"/> to avoid lock inversion.
    /// Threads that share the result of another thread's decompression by waiting on
    /// <see cref="ContainerDecompressionWork.Ready"/> never acquire this semaphore.
    /// </summary>
    private readonly SemaphoreSlim _DecompressionSemaphore;

    #endregion

    #region Constructors

    private BlfSource(BlfDataBackend backend, CaptureFileGuard? guard, BlfFileInfo fileInfo, BlfSourceOptions options, string uiName)
    {
        _Backend = backend;
        _Guard = guard;
        _FileInfo = fileInfo;
        _Index = new();
        _Options = options;
        UiName = uiName;
        ErrorTolerance = options.ErrorTolerance;

        // A1in gets the whole budget: with the standard 25 % share a single container larger than a quarter of the
        // budget is evicted by its own insertion. Sequential reads do not depend on this cache at all (they hold the
        // current container themselves); it serves FrameById and the first frames after a full scan.
        _ContainerCache = options.CacheBudget == 0
            ? TwoQueueCache.Create2Q<long, byte[]>(0, ContainerWeigher.Instance)
            : TwoQueueCache.Create2QCustom<long, byte[]>(
                options.CacheBudget,
                a1InMaxWeight: options.CacheBudget,
                ghostMaxWeight: options.CacheBudget / 2,
                ContainerWeigher.Instance);
        _DecompressionSemaphore = new SemaphoreSlim(
            options.MaxDecompressionConcurrency,
            options.MaxDecompressionConcurrency);
    }

    #endregion

    #region Properties

    /// <summary>The file guard (lock and change detection) of a file-backed source; <see langword="null"/> in memory. Tests, diagnostics.</summary>
    internal CaptureFileGuard? FileGuard => _Guard;

    /// <inheritdoc/>
    public string? Description => null;

    /// <inheritdoc/>
    public int? EstimatedFrameCount
    {
        get
        {
            if (_FullyScanned)
            {
                return _Index.Count;
            }

            return null;
        }
    }

    /// <inheritdoc/>
    public bool IsRunning => _Started && _Disposed == 0;

    // ── IErrorTolerantFrameSource / IFrameSourceStatistics ────────────────────

    /// <inheritdoc/>
    public int ReadFrameCount => _ReadFrameCount;

    /// <inheritdoc/>
    public int SkippedFrameCount => _SkippedFrameCount.Value;

    /// <inheritdoc/>
    public int ErrorCount => _ErrorCount.Value;

    /// <inheritdoc/>
    public bool HasErrors => _ErrorCount.Value > 0;

    /// <summary>
    /// Number of silent random-access read failures accumulated by <see cref="FrameById"/> calls.
    /// Incremented when decompression or OOM errors occur in the read-only random-access path;
    /// these failures are intentionally not reflected in <see cref="ErrorCount"/> or
    /// <see cref="SkippedFrameCount"/> to prevent poisoning sequential consumption state.
    /// Callers (e.g. UI diagnostics) can poll this counter to detect intermittent data issues.
    /// </summary>
    public long RandomAccessFailureCount => Volatile.Read(ref _RandomAccessFailureCount);

    /// <summary>
    /// Containers the source decompressed itself, outside the scanner. Diagnostic for tests: a sequential read reuses the
    /// scanner's blob, so this stays 0 until a cache miss forces a second inflate.
    /// </summary>
    internal long SourceDecompressionCount => Interlocked.Read(ref _SourceDecompressionCount);

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

    #endregion

    #region Events

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    #endregion

    #region Factory Methods

    /// <summary>
    /// Opens a BLF file from disk.
    /// Files whose size is within <see cref="BlfSourceOptions.PreloadBudget"/> (and within
    /// <see cref="Array.MaxLength"/>) are fully loaded into memory for zero-copy container access.
    /// Larger files are memory-mapped with one read-only view, which protects the heap from
    /// unbounded LOH pressure on large captures.
    /// </summary>
    /// <param name="path">Path to the BLF file.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new BlfSource ready to be started.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="BlfException">Thrown if the file cannot be parsed as a valid BLF file.</exception>
    /// <exception cref="IOException">
    /// The file could not be read, another process holds it open for writing (Windows), or it changed during a full
    /// scan (Linux/macOS).
    /// </exception>
    /// <remarks>
    /// The source keeps the file open with <see cref="FileShare.Read"/> until it is disposed: on Windows no other
    /// process can write to it meanwhile; on Linux and macOS a change is detected and reported as
    /// <see cref="FrameReadErrorKind.SourceFileChanged"/>. A preloaded file is served from its copy, so no change is
    /// detected after <c>Open</c> returns.
    /// </remarks>
    public static BlfSource Open(string path, BlfSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();
        string uiName = options.UiName ?? Path.GetFileName(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("BLF file not found.", path);
        }

        // The guard denies writers (Windows) and detects changes (Linux/macOS) for the whole lifetime of the source.
        CaptureFileGuard guard = CaptureFileGuard.Open(path);
        BlfDataBackend backend;
        try
        {
            // Choose backend: in-memory when the file fits within the preload budget and a .NET array;
            // memory-mapped otherwise to avoid pinning large LOH allocations.
            backend = PreloadDecision.ShouldPreload(guard.Length, options.PreloadBudget)
                ? BlfDataBackend.FromMemory(guard.ReadAll())
                : BlfDataBackend.FromMmap(path);
        }
        catch
        {
            guard.Dispose();
            throw;
        }

        return _OpenFromBackend(backend, guard, uiName, options);
    }

    /// <summary>
    /// Creates a BlfSource from in-memory data.
    /// </summary>
    /// <param name="data">Complete BLF file data.</param>
    /// <param name="uiName">Display name for this source.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new BlfSource ready to be started.</returns>
    /// <exception cref="BlfException">Thrown if the data cannot be parsed as a valid BLF file.</exception>
    public static BlfSource FromData(byte[] data, string uiName, BlfSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(uiName);
        options ??= new();
        BlfDataBackend backend = BlfDataBackend.FromMemory(data);
        return _OpenFromBackend(backend, guard: null, uiName, options);
    }

    /// <summary>
    /// Internal factory: validates the file header and creates a <see cref="BlfSource"/>
    /// from a pre-constructed <see cref="BlfDataBackend"/>.
    /// Takes ownership of <paramref name="backend"/> and <paramref name="guard"/> — disposes both on failure, so a
    /// failed open never leaves the file locked.
    /// </summary>
    /// <exception cref="BlfException">The file header is missing or corrupt.</exception>
    /// <exception cref="IOException">The guarded file changed during a full scan.</exception>
    private static BlfSource _OpenFromBackend(BlfDataBackend backend, CaptureFileGuard? guard, string uiName, BlfSourceOptions options)
    {
        // TryParse reads the 144-byte minimum file header only; the rest of a longer header is skipped by the scanner.
        ReadOnlySpan<byte> header = backend.GetSpan(0, BlfConstants.FileHeaderMinSize);

        if (!BlfFileInfo.TryParse(header, options.TimestampTimeZone, out BlfFileInfo? fileInfo))
        {
            backend.Dispose();
            guard?.Dispose();
            throw new BlfException("Invalid BLF file: missing or corrupt LOGG file header.");
        }

        BlfSource source = new(backend, guard, fileInfo, options, uiName);
        try
        {
            if (options.ScanMode == ScanMode.Full)
            {
                source._ScanFull();

                // An index built from bytes that changed meanwhile could point at the wrong objects: refuse it.
                if (guard is not null && !guard.IsUnchanged())
                {
                    throw new IOException("Capture file changed while it was being scanned.");
                }
            }
            else
            {
                source._InitializeLazyScanner();
            }

            // A preloaded copy is never read from the file again. Once it is verified unchanged, the monitor can stop; a
            // changed file keeps its latch, so the source fails closed as before.
            if (guard is not null && backend.IsInMemory && guard.IsUnchanged())
            {
                guard.StopMonitoring();
            }
        }
        catch
        {
            // Releases the backend and the guard; the caller never sees a half-built source.
            source.Dispose();
            throw;
        }

        return source;
    }

    #endregion

    #region IFrameSource Implementation

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _NextFrameIndex = 0;
        _Slabs = FrameSlabAllocator.CreateOrNull(_Options.FrameSlabSize);
        _SequentialInterfaceId = FrameInterfaceId.Invalid;

        // Channel names: a full scan stored them in _ChannelNames already; a lazy scan publishes snapshots that
        // _CurrentChannelNames reads while the scan runs.
        _Started = true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// For thread-safe random access, use <see cref="FrameById"/> instead.
    /// A <see cref="Dispose"/> on another thread is safe: a call already reading finishes its frame on the still-mapped
    /// view; a call that starts after disposal began returns <c>null</c> or throws <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException("BlfSource has not been started.");
        }

        if (_Aborted)
        {
            return null;
        }

        // The sequential path reads the mapped view without the reader gate (FrameSkipped handlers run inside this
        // call and may call Dispose, which would wait for a gate section of its own thread). One view lease per call keeps
        // the pages mapped while this call reads, also when Dispose runs concurrently (a session shutdown with a timeout
        // disposes sources whose read loop may still be inside NextFrame). Disposed before the lease: nothing more is read.
        if (!_Backend.TryAcquireScanLease())
        {
            return null;
        }

        try
        {
            return _NextFrameCore(cancellationToken);
        }
        finally
        {
            _Backend.ReleaseScanLease();
        }
    }

    /// <summary>The body of <see cref="NextFrame"/>, run while the caller holds a view lease.</summary>
    private Frame? _NextFrameCore(CancellationToken cancellationToken)
    {
        // A lazy scan continues after every indexed frame failed to build: the stream ends only at the end of the file,
        // so lazy and full scans return the same frames.
        while (true)
        {
            Frame? frame = _NextIndexedFrame(cancellationToken, out bool stop);
            if (frame is not null || stop)
            {
                return frame;
            }

            BlfIncrementalScanner? scanner = _Scanner;
            if (_FullyScanned || scanner is null || scanner.IsExhausted)
            {
                break;
            }
        }

        // End of the frames: one last change check, then failures behind the last frame (a truncated tail, a broken
        // last container).
        if (_FullyScanned && !_CheckFileUnchanged(0))
        {
            return null;
        }

        _ = _ReplayPendingDiagnosticsBefore(int.MaxValue);
        return null;
    }

    /// <summary>
    /// Scans more of a lazy file when every indexed frame was consumed, then returns the next frame that builds.
    /// Null with <paramref name="stop"/> = true when reading must stop (strict abort, file change); null with
    /// <paramref name="stop"/> = false when every indexed frame was consumed.
    /// </summary>
    private Frame? _NextIndexedFrame(CancellationToken cancellationToken, out bool stop)
    {
        stop = false;

        // If not fully scanned, try to scan more. Snapshot _Scanner
        // so a concurrent Dispose() does not null out the reference between checks.
        // NextFrame() is single-threaded by contract (SOURCE_GUIDE §13.1) but Dispose()
        // is not; the snapshot makes the read symmetric with Dispose's null write.
        BlfIncrementalScanner? scanner = _Scanner;
        if (!_FullyScanned && scanner is not null)
        {
            while (_NextFrameIndex >= _Index.Count && !scanner.IsExhausted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanner.ScanNext(cancellationToken);

                // The scanner notes each failure with its frame position (also inside a container); the frame loop
                // below reports it before the frame it precedes, so strict mode stops exactly at the first failure.
                _PendingScanDiagnostics = scanner.Marks;
            }

            if (scanner.IsExhausted && !_FullyScanned)
            {
                // Snapshot the names before the scanner is released; FrameById reads _ChannelNames from now on.
                _ChannelNames = scanner.ChannelNames;
                _FullyScanned = true;
                _Scanner = null;
            }
        }

        // Loop to skip over errored frames in tolerant mode
        while (_NextFrameIndex < _Index.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Full scan: failures the open-time scan found in front of this frame are reported first (file order).
            if (!_ReplayPendingDiagnosticsBefore(_NextFrameIndex))
            {
                stop = true;
                return null;
            }

            int frameIndex = _NextFrameIndex++;
            if (!_CheckFileUnchanged(frameIndex))
            {
                stop = true;
                return null;
            }

            Frame? frame = _BuildFrame(frameIndex, cancellationToken);
            if (frame is not null)
            {
                Interlocked.Increment(ref _ReadFrameCount);
                return frame;
            }

            // BuildFrame returned null — frame could not be constructed.
            // HandleSkip was already called inside BuildFrame for specific error causes.
            // If we're aborted (strict mode), stop here.
            if (_Aborted)
            {
                stop = true;
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Sequential change check (see <see cref="CaptureFileGuard.HasChangedAt"/> for when the file is asked and when only
    /// the latch is read). <see cref="FrameById"/> reads only that latch. A change raises
    /// <see cref="FrameReadErrorKind.SourceFileChanged"/> once and stops sequential reading in every tolerance mode.
    /// Returns false when reading must stop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _CheckFileUnchanged(int frameIndex)
    {
        if (_Guard is null || !_Guard.HasChangedAt(frameIndex))
        {
            return true;
        }

        _ReportFileChanged(frameIndex);
        return false;
    }

    /// <summary>Raises <see cref="FrameReadErrorKind.SourceFileChanged"/> once and stops sequential reading regardless of the tolerance mode.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ReportFileChanged(int frameIndex)
    {
        if (_Aborted)
        {
            return;
        }

        _Aborted = true;
        _ErrorCount.Increment();
        FrameSkipped?.Invoke(this, new FrameReadErrorEventArgs
        {
            FrameIndex = frameIndex,
            FileOffset = -1,
            Kind = FrameReadErrorKind.SourceFileChanged,
            Message = "The capture file changed while it was being read.",
        });
    }

    /// <summary>
    /// Reports the full-scan failures that lie in front of frame <paramref name="frameIndex"/> (marks with
    /// <c>FrameIndex &lt;= frameIndex</c>), in file order. Returns false when strict mode aborted.
    /// </summary>
    private bool _ReplayPendingDiagnosticsBefore(int frameIndex)
    {
        List<BlfScanDiagnosticsMark>? marks = _PendingScanDiagnostics;
        if (marks is null)
        {
            return true;
        }

        while (_PendingScanDiagnosticsCursor < marks.Count && marks[_PendingScanDiagnosticsCursor].FrameIndex <= frameIndex)
        {
            BlfScanDiagnosticsMark mark = marks[_PendingScanDiagnosticsCursor++];
            if (!_ReportScannerDiagnostics(mark.Counts))
            {
                return false;
            }
        }

        if (_PendingScanDiagnosticsCursor == marks.Count)
        {
            _PendingScanDiagnostics = null;
        }

        return true;
    }

    #endregion

    #region IRandomAccessFrameSource Implementation

    /// <inheritdoc/>
    /// <remarks>
    /// Thread-safe, also while a lazy scan runs on the <see cref="NextFrame"/> thread: the frame index is a
    /// single-writer / multi-reader store, channel names are published as immutable snapshots, and interface
    /// registration is locked. Returns the frame for every id the index already holds and <c>null</c> for ids not
    /// indexed yet; it never waits for the scan. Frame construction failures are reported via the return value only —
    /// this method never increments error counters, raises <see cref="FrameSkipped"/>, or sets the abort flag, so
    /// callers (e.g. UI threads) cannot poison sequential consumption. Allocates one array per call (no frame slabs).
    /// <para>
    /// Thread-safety: the call runs inside a <see cref="ReaderGate"/> section for the entire duration of
    /// <see cref="_TryBuildFrame"/> so that the mmap primary view pointer cannot be released by a concurrent
    /// <see cref="Dispose"/> call while a span read is in progress; <see cref="Dispose"/> waits for the section.
    /// </para>
    /// </remarks>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        // Disposal started: the gate refuses the section, and the call reports the disposed source.
        if (!_Gate.TryEnter(out ReaderGateToken token))
        {
            ObjectDisposedException.ThrowIf(true, this);
        }

        try
        {
            if (_Disposed != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
            }

            // Reject before Start(): _Registry is null and BuildFrame would NRE.
            if (!_Started)
            {
                throw new InvalidOperationException("BlfSource has not been started.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            int index = id.Value;
            if (index < 0 || index >= _Index.Count)
            {
                return null;
            }

            BlfReadState state = BlfReadState.Empty;
            Frame? frame = _TryBuildFrame(index, ref state, useCursor: false, cancellationToken);

            // A change found by a sequential check fails closed; the latch is one volatile read, no system call.
            return _Guard is not null && _Guard.ChangeDetected
                ? null
                : frame;
        }
        finally
        {
            _Gate.Exit(in token);
        }
    }
    /// <inheritdoc/>
    /// <remarks>
    /// Thread-safe like <see cref="FrameById"/>. Reads the range forward in chunks of 64 ids, each inside one gate section
    /// (a concurrent <see cref="Dispose"/> waits for at most one chunk; later chunks throw
    /// <see cref="ObjectDisposedException"/>). Consecutive ids walk the container rows with one cursor, and the ids of one
    /// container share its bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The source has not been started.</exception>
    public int FramesById(FrameId first, Span<Frame?> frames, CancellationToken cancellationToken = default)
    {
        if (!_Started)
        {
            throw new InvalidOperationException("BlfSource has not been started.");
        }

        BlfReadState state = BlfReadState.Empty;
        int found = 0;
        for (int chunkStart = 0; chunkStart < frames.Length; chunkStart += _BatchChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_Gate.TryEnter(out ReaderGateToken token))
            {
                ObjectDisposedException.ThrowIf(true, this);
            }

            int end = Math.Min(frames.Length, chunkStart + _BatchChunk);
            try
            {
                ObjectDisposedException.ThrowIf(_Disposed != 0, this);
                for (int i = chunkStart; i < end; i++)
                {
                    // The id sum is formed in long: a range that runs past the indexed ids reads null entries.
                    long index = (long)first.Value + i;
                    frames[i] = index >= 0 && index < _Index.Count
                        ? _TryBuildFrame((int)index, ref state, useCursor: true, cancellationToken)
                        : null;
                }
            }
            finally
            {
                _Gate.Exit(in token);
            }

            // A change found by a sequential check fails closed for the whole chunk, as FrameById does per frame.
            for (int i = chunkStart; i < end; i++)
            {
                if (_Guard is not null && _Guard.ChangeDetected)
                {
                    frames[i] = null;
                }
                else if (frames[i] is not null)
                {
                    found++;
                }
            }
        }

        return found;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent. Waits for running <see cref="FrameById"/> calls, releases the cache, scanner and registry
    /// reference, then the backend. A failure to release the memory mapping is an OS error, not a frame read error:
    /// it propagates as <see cref="AggregateException"/> (the same rule as <see cref="Pcapng.PcapngSource"/> and
    /// <see cref="Asc.AscSource"/>) after the state of the source has been released.
    /// </remarks>
    /// <exception cref="AggregateException">Releasing the memory mapping failed.</exception>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // Running FrameById sections on other threads finish first (they consume mmap spans); none starts afterwards.
            _Gate.Close();

            // Release this thread's memo of a container of this source; other threads drop theirs at their next lookup.
            if (_Memo is { } memo && memo.Owner == _MemoOwner)
            {
                _Memo = null;
            }

            // _Started intentionally left set: it is a one-shot "has Start been called"
            // latch (see SOURCE_GUIDE §13.3). IsRunning combines it with _Disposed == 0.
            // Clear the registry reference so the session can be GC'd after Dispose().
            // The volatile store on _Registry must not be reordered before the
            // _Disposed latch store on weakly-ordered architectures (ARM/Apple Silicon).
            // TryBuildFrame reads _Registry; the write must be equivalently fenced so a
            // concurrent reader cannot observe null without also observing _Disposed != 0.
            _Registry = null;
            lock (_ContainerCacheLock)
            {
                _ContainerCache.Clear();
            }
            _Scanner = null;
            _SequentialContainer = null;
            _Slabs = null;

            // Backend disposal failures are OS errors, not frame read errors: propagate like PcapngSource and
            // AscSource do. The gate is closed for good, so a throw here cannot block later readers; the semaphore and
            // finalizer suppression run in the finally.
            _Backend.Dispose();
        }
        finally
        {
            _DecompressionSemaphore.Dispose();
            GC.SuppressFinalize(this);

            // The file stays locked until the mapping is gone; then writers may open it again.
            _Guard?.Dispose();
        }
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// Sentinel for an in-flight container decompression operation.
    /// <para>
    /// Lifecycle:
    /// <list type="number">
    ///   <item>Created by the "winner" thread under <see cref="_ContainerCacheLock"/> and stored
    ///   in <see cref="_PendingDecompressions"/>.</item>
    ///   <item>The winner decompresses outside the lock, acquires <see cref="_DecompressionSemaphore"/>
    ///   for the duration of the allocation, then re-acquires <see cref="_ContainerCacheLock"/>
    ///   to publish the result: on success the bytes are put in <see cref="_ContainerCache"/>;
    ///   on failure <see cref="Error"/> is set. In either case the entry is removed from
    ///   <see cref="_PendingDecompressions"/> and <see cref="Ready"/> is signalled, all while
    ///   still holding the lock.</item>
    ///   <item>Waiting threads hold a reference to this object and call <see cref="Ready"/>.Wait()
    ///   outside the lock. After the wait returns they re-acquire the lock and read the result
    ///   from the cache, or fall back to <see cref="Error"/> on failure.</item>
    ///   <item>After signalling, the winner drops its reference. Waiting threads may still be
    ///   about to call <c>Wait()</c>, so the underlying <see cref="ManualResetEventSlim"/> must
    ///   never be disposed explicitly — it is released by the GC once all references are gone.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>
    /// Not thread-safe. All field accesses must be performed under <see cref="_ContainerCacheLock"/>,
    /// except <see cref="Ready"/>.Wait() (called outside the lock by waiters).
    /// </remarks>
    // CA1001 is suppressed because eager disposal of Ready is unsafe: a winner thread may call
    // Ready.Set() after BlfSource.Dispose() has already cleared _PendingDecompressions under
    // _ContainerCacheLock, which would cause an ObjectDisposedException. ManualResetEventSlim
    // uses only managed resources (spin + Monitor wait) under normal concurrency; a kernel
    // handle is allocated only under extreme thread contention, and the GC finalizer reclaims it.
    [SuppressMessage(
        "Reliability",
        "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
        Justification = "Id: CA1001. Why: Disposing ManualResetEventSlim while a waiter may still call Ready.Wait after the winner removed the pending-decompression entry causes ObjectDisposedException; the event is managed-only and GC-collected. User approved.")]
    private sealed class ContainerDecompressionWork
    {
        /// <summary>
        /// Signalled by the winner once <see cref="Error"/> and the cache entry are committed.
        /// <see cref="ManualResetEventSlim"/> is used instead of <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>
        /// to avoid blocking thread-pool threads and to support cooperative cancellation via
        /// <see cref="ManualResetEventSlim.Wait(CancellationToken)"/>.
        /// <para>
        /// <see cref="ManualResetEventSlim"/> uses only managed resources (spin + Monitor wait) under
        /// normal concurrency. The GC finalizer reclaims any kernel handle allocated under extreme
        /// contention. Eager disposal is intentionally avoided — see the CA1001 suppression above.
        /// </para>
        /// </summary>
        internal readonly ManualResetEventSlim Ready = new(false);

        /// <summary>
        /// Populated by the winner with the decompression exception when the operation fails;
        /// <c>null</c> on success. Read by waiters only after <see cref="Ready"/>.Wait() returns,
        /// which guarantees visibility (the signal and this write happen under the same lock).
        /// </summary>
        internal Exception? Error;

        /// <summary>
        /// The decompressed bytes on success, handed to the waiters directly: the cache may not keep them (budget 0, or
        /// a container larger than the budget). Same visibility rule as <see cref="Error"/>.
        /// </summary>
        internal byte[]? Result;
    }

    #endregion
}
