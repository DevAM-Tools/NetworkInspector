// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// BLF (Binary Logging Format) frame source with random access support.
/// Supports CAN, CAN FD, LIN, FlexRay, and Ethernet object types.
///
/// Key design decisions:
/// - Two-level iteration: outer file blocks → inner decompressed container objects
/// - 2Q scan-resistant cache for decompressed containers (avoids LRU scan pollution)
/// - Objects do NOT align to any fixed boundary — LOBJ magic scanning for corruption recovery
/// - skip_distance = max(max(16, object_length), header_size) ensures forward progress
/// - Absolute timestamps = file start offset (from BlfDate) + relative timestamp
/// </summary>
public sealed partial class BlfSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    private readonly BlfDataBackend _Backend;
    private readonly BlfFileInfo _FileInfo;
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
    private readonly Dictionary<(uint ObjectType, ushort Channel), FrameInterfaceId> _InterfaceMap = new();

    /// <summary>
    /// Synchronises concurrent <see cref="_GetOrRegisterInterface"/> calls that arrive via
    /// the thread-safe <see cref="FrameById"/> path. All writes to <see cref="_InterfaceMap"/>
    /// are guarded by this lock; reads are performed under the lock too to avoid torn reads
    /// on the dictionary's internal state.
    /// </summary>
    private readonly Lock _InterfaceLock = new();

    /// <summary>Discovered channel names from AppText objects (busType + channel → name).</summary>
    private IReadOnlyDictionary<(byte BusType, byte Channel), string>? _ChannelNames;

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

    /// <summary>
    /// Guards the lifetime of the mmap-backed <see cref="_Backend"/> against a Dispose/read race.
    /// <see cref="FrameById"/> acquires the read lock for the duration of <see cref="_TryBuildFrame"/>
    /// so that the <see cref="ReadOnlySpan{Byte}"/> returned by the mmap primary view remains valid.
    /// <see cref="Dispose"/> acquires the write lock before disposing the backend, guaranteeing that
    /// all in-flight random-access reads have completed before the pointer is released.
    /// Not disposed explicitly — <see cref="ReaderWriterLockSlim"/> holds only managed state that
    /// the GC will collect once <see cref="BlfSource"/> is unreachable.
    /// </summary>
    [SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
        Justification = "Intentionally not disposed. Disposing the lock while a concurrent FrameById caller " +
                        "may still be entering the read lock (they observe _Disposed != 0 and exit, " +
                        "but the window is not zero) would cause SynchronizationLockException. " +
                        "The lock holds only managed state that the GC collects once BlfSource is unreachable.")]
    private readonly ReaderWriterLockSlim _LifetimeLock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Counts silent random-access failures (decompression errors, OOM) that would otherwise be invisible to callers.</summary>
    private long _RandomAccessFailureCount;

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

    private BlfSource(BlfDataBackend backend, BlfFileInfo fileInfo, BlfSourceOptions options, string uiName)
    {
        _Backend = backend;
        _FileInfo = fileInfo;
        _Index = new();
        _Options = options;
        UiName = uiName;
        ErrorTolerance = options.ErrorTolerance;
        _ContainerCache = TwoQueueCache.Create2Q<long, byte[]>(
            options.CacheBudget,
            ContainerWeigher.Instance);
        _DecompressionSemaphore = new SemaphoreSlim(
            options.MaxDecompressionConcurrency,
            options.MaxDecompressionConcurrency);
    }

    #endregion

    #region Properties

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
    /// Files whose size is within <see cref="BlfSourceOptions.PreloadBudget"/> are fully
    /// loaded into memory for zero-copy container access. Larger files are memory-mapped
    /// using a pool of <see cref="BlfSourceOptions.MmapSlotCount"/> view accessors, which
    /// protects the heap from unbounded LOH pressure on large captures.
    /// </summary>
    /// <param name="path">Path to the BLF file.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new BlfSource ready to be started.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="BlfException">Thrown if the file cannot be parsed as a valid BLF file.</exception>
    public static BlfSource Open(string path, BlfSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();
        string uiName = options.UiName ?? Path.GetFileName(path);

        FileInfo fileInfo = new(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("BLF file not found.", path);
        }

        long fileSize = fileInfo.Length;

        // Choose backend: in-memory when the file fits within the preload budget;
        // memory-mapped otherwise to avoid pinning large LOH allocations.
        BlfDataBackend backend = options.PreloadBudget.HasValue && fileSize <= options.PreloadBudget.Value
            ? BlfDataBackend.FromMemory(File.ReadAllBytes(path))
            : BlfDataBackend.FromMmap(path, options.MmapSlotCount);

        return _OpenFromBackend(backend, uiName, options);
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
        return _OpenFromBackend(backend, uiName, options);
    }

    /// <summary>
    /// Internal factory: validates the file header and creates a <see cref="BlfSource"/>
    /// from a pre-constructed <see cref="BlfDataBackend"/>.
    /// Takes ownership of <paramref name="backend"/> — disposes it on failure.
    /// </summary>
    private static BlfSource _OpenFromBackend(BlfDataBackend backend, string uiName, BlfSourceOptions options)
    {
        // Parse file header from the first bytes of the backend
        ReadOnlySpan<byte> header = backend.GetSpan(0, Format.BlfConstants.FileHeaderMinSize + 128);

        if (!BlfFileInfo.TryParse(header, options.TimestampTimeZone, out BlfFileInfo? fileInfo))
        {
            backend.Dispose();
            throw new BlfException("Invalid BLF file: missing or corrupt LOGG file header.");
        }

        BlfSource source = new(backend, fileInfo, options, uiName);

        if (options.ScanMode == ScanMode.Full)
        {
            source._ScanFull();
        }
        else
        {
            source._InitializeLazyScanner();
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
        _Started = true;

        // If fully scanned, store discovered channel names for interface naming.
        // Read _Scanner so a parallel Dispose() (allowed by SOURCE_GUIDE
        // §13.1) can never present us with a torn null reference between the
        // null-check and the dereference.
        BlfIncrementalScanner? scanner = _Scanner;
        if (_FullyScanned && scanner is not null)
        {
            _ChannelNames = scanner.ChannelNames;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// For thread-safe random access, use <see cref="FrameById"/> instead.
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

                // Report any new decompression failures from the scanner
                long failures = scanner.DecompressionFailures;
                while (_LastReportedDecompressionFailures < failures)
                {
                    _LastReportedDecompressionFailures++;
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _NextFrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.Other,
                        Message = $"Container decompression failed (failure #{_LastReportedDecompressionFailures})."
                    });

                    if (_Aborted)
                    {
                        return null;
                    }
                }

                // Report any new corrupted-container-header errors from the scanner.
                long corrupted = scanner.CorruptedContainerCount;
                while (_LastReportedCorruptedContainerCount < corrupted)
                {
                    _LastReportedCorruptedContainerCount++;
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _NextFrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.CorruptedBlock,
                        Message = $"Container header offset out of bounds (corrupted container #{_LastReportedCorruptedContainerCount})."
                    });

                    if (_Aborted)
                    {
                        return null;
                    }
                }

                // Report any new tail-truncation events from the scanner.
                long truncated = scanner.TruncatedObjectCount;
                while (_LastReportedTruncatedObjectCount < truncated)
                {
                    _LastReportedTruncatedObjectCount++;
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _NextFrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.CorruptedBlock,
                        Message = $"Truncated BLF object at container boundary (dropped #{_LastReportedTruncatedObjectCount})."
                    });

                    if (_Aborted)
                    {
                        return null;
                    }
                }
            }

            if (scanner.IsExhausted && !_FullyScanned)
            {
                _FullyScanned = true;
                _Index.ShrinkToFit();
                _ChannelNames = scanner.ChannelNames;
            }
        }

        // Loop to skip over errored frames in tolerant mode
        while (_NextFrameIndex < _Index.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameIndex = _NextFrameIndex++;
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
                return null;
            }
        }

        return null;
    }

    #endregion

    #region IRandomAccessFrameSource Implementation

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <c>null</c> before <see cref="Start"/> has been called or while a lazy scan
    /// is still in progress, because the frame index, channel map and registry are mutated
    /// by the scanning thread and a concurrent random-access read would race with those
    /// mutations. Frame construction failures are reported via the return value only — this
    /// method never increments error counters, raises <see cref="FrameSkipped"/>, or sets
    /// the abort flag, so callers (e.g. UI threads) cannot poison sequential consumption.
    /// <para>
    /// Thread-safety: a <see cref="ReaderWriterLockSlim"/> read lock is held for the entire
    /// duration of <see cref="_TryBuildFrame"/> so that the mmap primary view pointer cannot be
    /// released by a concurrent <see cref="Dispose"/> call while a span read is in progress.
    /// </para>
    /// </remarks>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        _LifetimeLock.EnterReadLock();
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

            // While a lazy scan is still appending entries the index / channel map
            // are not stable; mirror PcapSource.FrameById and refuse the call.
            if (!_FullyScanned)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            int index = id.Value;
            if (index < 0 || index >= _Index.Count)
            {
                return null;
            }

            return _TryBuildFrame(index, cancellationToken);
        }
        finally
        {
            _LifetimeLock.ExitReadLock();
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // Acquire the write lock before touching the backend so that any concurrent
        // FrameById readers holding the read lock can finish consuming their mmap spans
        // before the primary pointer is released.
        _LifetimeLock.EnterWriteLock();
        try
        {
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
            // Wrapped so that a backend disposal failure does not prevent the write lock
            // from being released by the finally block.
            try
            {
                _Backend.Dispose();
            }
            catch (Exception) { _ErrorCount.Increment(); }
        }
        finally
        {
            _LifetimeLock.ExitWriteLock();
        }

        // _LifetimeLock is intentionally NOT disposed here. It holds only managed state
        // that the GC will collect once BlfSource is unreachable. Disposing it while
        // concurrent FrameById callers may still be entering the read lock (they observe
        // _Disposed != 0 and exit, but the window is not zero) would cause a
        // SynchronizationLockException. Leaving it undisposed is safe and correct.
        _DecompressionSemaphore.Dispose();
        GC.SuppressFinalize(this);
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
        Justification = "Eager disposal is unsafe; see comment above. ManualResetEventSlim lifetime is bounded by GC reachability.")]
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
    }

    #endregion
}
