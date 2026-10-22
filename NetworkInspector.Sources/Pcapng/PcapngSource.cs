// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Frame source for PCAPNG and legacy PCAP files.
/// Implements <see cref="IRandomAccessFrameSource"/> for sequential streaming
/// and random-access re-reading of captured frames.
/// </summary>
/// <remarks>
/// Supports two scan modes:
/// <list type="bullet">
/// <item><b>Full:</b> Scans the entire file upfront, building a complete frame index.</item>
/// <item><b>Lazy:</b> Scans frames on demand as <see cref="NextFrame"/> is called.</item>
/// </list>
/// Both modes return the same frames and raise <see cref="FrameSkipped"/> at the same positions, also in strict mode.
/// Packet payload on <see cref="Frame"/> is the captured length. PCAPNG/PCAP <c>orig_len</c> is not kept. A link type
/// that is not a named <see cref="LinkType"/> member is passed through with its number: the frame keeps its bytes,
/// dissectors leave it raw, and the PCAPNG exporter writes the same DLT back.
/// <para>
/// <b>Design.</b> The frame index stores only the start offset of every accepted block (about 0.4–1.6 B per frame,
/// <see cref="FrameIndex"/>). <see cref="FrameById"/> and the index-driven part of <see cref="NextFrame"/> re-read that
/// block with the scanner's own parser instead of reading stored fields, so a capture of hundreds of millions of frames
/// needs well under 2 B of RAM per frame, and a block that changed or became corrupt yields <c>null</c> instead of a
/// wrong frame. An unchanged file is therefore a correctness precondition: <see cref="CaptureFileGuard"/> keeps the file
/// locked (Windows) or detects changes (Linux, macOS). Random reads on many threads enter a <see cref="ReaderGate"/>
/// section so they share no written cache line; <see cref="NextFrame"/> holds a view lease instead, so a
/// <see cref="FrameSkipped"/> handler may dispose the source. <see cref="FramesById"/> reads a range forward and
/// requests the cache lines of the next blocks early. Details: SOURCE_GUIDE §8.5, §11.6, §13.4.
/// </para>
/// <para>
/// Thread-safety: <see cref="NextFrame"/> is not thread-safe (one reading thread). <see cref="FrameById"/> is
/// thread-safe and may run on any number of threads, also while <see cref="NextFrame"/> is still scanning lazily: it
/// returns every frame the index already holds and <c>null</c> for ids not indexed yet. It never waits for the scan.
/// </para>
/// </remarks>
public sealed class PcapngSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Constants

    /// <summary>Ids per gate section of <see cref="FramesById"/>: bounds the wait of a concurrent <see cref="Dispose"/>.</summary>
    private const int _BatchChunk = 64;

    /// <summary>
    /// Blocks <see cref="FramesById"/> requests from memory ahead of the one it parses, so the cache misses of the next
    /// blocks overlap the parse of the current one. At most 8 × <see cref="CachePrefetch.MaxBytes"/> = 32 KiB are in
    /// flight. Constant (tuning value, not configurable).
    /// </summary>
    private const int _BatchPrefetchDistance = 8;

    #endregion

    #region Fields

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    /// <summary>Optional description.</summary>
    private readonly string? _Description;

    /// <summary>Data backend (in-memory or mmap).</summary>
    private readonly DataBackend _Backend;

    /// <summary>
    /// Keeps the capture file open with <see cref="FileShare.Read"/> for the source's lifetime (writers denied on
    /// Windows, changes detected on Linux/macOS). Null for in-memory data (<see cref="FromData"/>).
    /// </summary>
    private readonly CaptureFileGuard? _Guard;

    /// <summary>
    /// Frame index built during scanning: the start offset of every accepted block. In lazy mode this is the scanner's
    /// index, so entries the scanner appends are visible to <see cref="FrameById"/> as soon as the index publishes its
    /// count.
    /// </summary>
    private readonly FrameIndex _Index;

    /// <summary>
    /// Format-specific metadata. Sections and their interfaces are single-writer / multi-reader stores, so
    /// <see cref="FrameById"/> resolves the section of a block from any thread; the scan cursor lives in the scanner.
    /// </summary>
    private readonly ScannerFormat _Format;

    /// <summary><see cref="_Format"/> as PCAPNG state, or null for legacy PCAP (typed once for the re-read path).</summary>
    private readonly PcapNgFormat? _PcapNgFormat;

    /// <summary><see cref="_Format"/> as legacy PCAP state, or null for PCAPNG.</summary>
    private readonly LegacyPcapFormat? _LegacyFormat;

    /// <summary>Scanner for lazy mode (null after full scan or when lazy scan completes).</summary>
    private volatile IncrementalScanner? _Scanner;

    /// <summary>Registered interfaces; the only interface lookup <see cref="FrameById"/> uses.</summary>
    private readonly PcapInterfaceTable _Interfaces = new();

    /// <summary>Sequential frame counter for NextFrame().</summary>
    private int _CurrentFrame;

    /// <summary>Source ID assigned during Start().</summary>
    private FrameSourceId _SourceId;

    /// <summary>Registry reference for lazy interface registration.</summary>
    private volatile FrameInterfaceRegistry? _Registry;

    /// <summary><see cref="PcapngSourceOptions.FrameSlabSize"/> of the options this source was opened with.</summary>
    private readonly int _FrameSlabSize;

    /// <summary>Frame slab allocator of the sequential path; null when slabs are off. NextFrame thread only.</summary>
    private FrameSlabAllocator? _Slabs;

    /// <summary>Whether the source has been started.</summary>
    private volatile bool _Started;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>
    /// Protects the mapped view against <see cref="Dispose"/> during <see cref="FrameById"/>: readers mark only their own
    /// thread, and <see cref="Dispose"/> waits for them before it unmaps. Null for in-memory data, which stays valid after
    /// dispose. <see cref="NextFrame"/> keeps the view lease of the backend instead, so a dispose from inside it (an error
    /// handler) never waits for its own thread.
    /// </summary>
    private readonly ReaderGate? _Gate;

    #endregion

    #region Error Tolerance Fields

    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();

    /// <summary>
    /// Malformed packet skips collected during a full open-time scan, in file order. Each skip carries the index of
    /// the frame that follows it, so <see cref="_NextFrameFromIndex"/> replays it exactly where the lazy scan would
    /// have raised it.
    /// </summary>
    private List<FrameReadErrorEventArgs>? _PendingScanSkips;

    /// <summary>Index of the next pending open-time skip to replay (full-scan mode only).</summary>
    private int _PendingScanSkipCursor;

    /// <summary>Set in strict mode after a skip, or after a file change, so later <see cref="NextFrame"/> calls return null.</summary>
    private volatile bool _Aborted;

    #endregion

    #region Constructors

    /// <summary>
    /// Private constructor — use <see cref="Open"/> or <see cref="FromData"/> factory methods.
    /// </summary>
    private PcapngSource(
        string uiName,
        string? description,
        DataBackend backend,
        CaptureFileGuard? guard,
        IncrementalScanner scanner,
        bool lazy,
        PcapngSourceOptions options,
        List<FrameReadErrorEventArgs>? pendingScanSkips)
    {
        UiName = uiName;
        _Description = description;
        _Backend = backend;
        _Gate = backend.IsInMemory
            ? null
            : new ReaderGate();
        _Guard = guard;
        _Index = scanner.Index;
        _Format = scanner.Format;
        _PcapNgFormat = scanner.Format as PcapNgFormat;
        _LegacyFormat = scanner.Format as LegacyPcapFormat;
        _Scanner = lazy ? scanner : null;
        _FrameSlabSize = options.FrameSlabSize;
        _PendingScanSkips = pendingScanSkips;

        // Construction-time tolerance from the options. The property stays settable so a caller may still flip the
        // mode while reading (IErrorTolerantFrameSource contract).
        ErrorTolerance = options.ErrorTolerance;

        if (lazy)
        {
            scanner.OnSkip = _HandleSkip;
            scanner.OnInterfaceAdded = _OnInterfaceAdded;
        }
    }

    #endregion

    #region Factory methods

    /// <summary>
    /// Opens a capture file from disk.
    /// </summary>
    /// <param name="path">Path to the PCAPNG or legacy PCAP file.</param>
    /// <param name="options">Reader options, or null for defaults. <see cref="PcapngSourceOptions.ErrorTolerance"/>
    /// becomes the initial <see cref="ErrorTolerance"/>.</param>
    /// <returns>A configured PcapngSource ready for <see cref="IFrameSource.Start"/>.</returns>
    /// <exception cref="PcapException">The file format is unrecognized or corrupt.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
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
    public static PcapngSource Open(string path, PcapngSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();

        string uiName = options.UiName ?? Path.GetFileName(path);
        string? description = null;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Capture file not found.", path);
        }

        // The guard denies writers (Windows) and detects changes (Linux/macOS) for the whole lifetime of the source.
        CaptureFileGuard guard = CaptureFileGuard.Open(path);
        DataBackend? backend = null;
        try
        {
            // Decide backend: in-memory (bounded by the budget and Array.MaxLength) or one read-only mapping.
            backend = PreloadDecision.ShouldPreload(guard.Length, options.PreloadBudget)
                ? DataBackend.FromMemory(guard.ReadAll())
                : DataBackend.FromMmap(path);

            PcapngSource source = options.ScanMode == ScanMode.Full
                ? _OpenFullScan(uiName, description, backend, guard, options)
                : _OpenLazy(uiName, description, backend, guard, options);

            // A preloaded copy is never read from the file again. Once it is verified unchanged, the monitor can stop; a
            // changed file keeps its latch, so the source fails closed as before.
            if (backend.IsInMemory && guard.IsUnchanged())
            {
                guard.StopMonitoring();
            }

            return source;
        }
        catch
        {
            // The source never took ownership: release the mapping and the guard before the error propagates.
            backend?.Dispose();
            guard.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a PcapngSource from in-memory data (e.g. for testing or WASM).
    /// Always performs a full scan.
    /// </summary>
    /// <param name="data">Raw capture file bytes.</param>
    /// <param name="uiName">Display name for this source.</param>
    /// <param name="options">Reader options, or null for defaults. <see cref="PcapngSourceOptions.ScanMode"/> is
    /// ignored — in-memory data is always fully scanned; <see cref="PcapngSourceOptions.ErrorTolerance"/> behaves as in
    /// <see cref="Open"/>; the display name is <paramref name="uiName"/>.</param>
    /// <returns>A configured PcapngSource ready for <see cref="IFrameSource.Start"/>.</returns>
    /// <exception cref="PcapException">The data format is unrecognized or corrupt.</exception>
    public static PcapngSource FromData(byte[] data, string uiName = "In-Memory Capture", PcapngSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(uiName);
        options ??= new();

        // byte[] in .NET is always bounded by int.MaxValue, so no size check is needed here.
        DataBackend backend = DataBackend.FromMemory(data);
        return _OpenFullScan(uiName, null, backend, guard: null, options);
    }

    /// <summary>Full scan: scans entire file upfront.</summary>
    /// <exception cref="IOException">The guarded file changed while it was being scanned.</exception>
    private static PcapngSource _OpenFullScan(
        string uiName,
        string? description,
        DataBackend backend,
        CaptureFileGuard? guard,
        PcapngSourceOptions options)
    {
        IncrementalScanner scanner = new(backend, backend.FileSize);
        List<FrameReadErrorEventArgs> skips = [];
        scanner.OnSkip = skips.Add;

        // Packet skips return false without exhausting; keep scanning until EOF.
        while (true)
        {
            if (scanner.NextFrame(out _))
            {
                continue;
            }

            if (scanner.IsExhausted)
            {
                break;
            }
        }

        // An index built from bytes that changed meanwhile could point at the wrong blocks: refuse it.
        if (guard is not null && !guard.IsUnchanged())
        {
            throw new IOException("Capture file changed while it was being scanned.");
        }

        return new PcapngSource(uiName, description, backend, guard, scanner, lazy: false, options, skips.Count == 0 ? null : skips);
    }

    /// <summary>Lazy scan: only parses the first header, scans frames on demand.</summary>
    private static PcapngSource _OpenLazy(
        string uiName,
        string? description,
        DataBackend backend,
        CaptureFileGuard? guard,
        PcapngSourceOptions options)
    {
        IncrementalScanner scanner = new(backend, backend.FileSize);
        return new PcapngSource(uiName, description, backend, guard, scanner, lazy: true, options, pendingScanSkips: null);
    }

    #endregion

    #region IFrameSource implementation

    /// <summary>The file guard (lock and change detection) of a file-backed source; <see langword="null"/> in memory. Tests, diagnostics.</summary>
    internal CaptureFileGuard? FileGuard => _Guard;

    /// <inheritdoc />
    public string? Description => _Description;

    /// <inheritdoc />
    public int? EstimatedFrameCount
    {
        get
        {
            if (_Scanner is null)
            {
                return _Index.Count;
            }

            return null;
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc/>
    /// <remarks>Initialised from <see cref="PcapngSourceOptions.ErrorTolerance"/>; may be changed at any time.</remarks>
    public ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _CurrentFrame = 0;
        _Slabs = FrameSlabAllocator.CreateOrNull(_FrameSlabSize);

        // Register every interface known so far: all of them after a full scan, the legacy interface or none yet in
        // lazy mode. Later IDBs arrive through the scanner's OnInterfaceAdded callback before their first packet.
        _RegisterKnownInterfaces(registry);
        _Started = true;
    }

    /// <inheritdoc />
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
            throw new InvalidOperationException($"{UiName} has not been started.");
        }

        if (_Aborted)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The mapped backend is scanned and copied through the view pointer without a per-copy reference. One lease
        // per call keeps the view mapped while this call reads, also when Dispose runs concurrently (a session shutdown
        // with a timeout disposes sources whose read loop may still be inside NextFrame). Disposed before the lease:
        // nothing more is read.
        if (!_Backend.TryAcquireScanLease())
        {
            return null;
        }

        try
        {
            // Read _Scanner once: Dispose() nulls the field from another thread, and the scan step must use the snapshot.
            IncrementalScanner? scanner = _Scanner;
            if (scanner is not null)
            {
                return _NextFrameFromScanner(scanner);
            }

            return _NextFrameFromIndex(cancellationToken);
        }
        finally
        {
            _Backend.ReleaseScanLease();
        }
    }

    #endregion

    #region IRandomAccessFrameSource implementation

    /// <inheritdoc />
    /// <remarks>
    /// Thread-safe, also while a lazy scan runs on the <see cref="NextFrame"/> thread: the frame index, the sections,
    /// and the interface table are single-writer / multi-reader stores, and every section and interface is published
    /// before the index covers its first frame. The index holds only the start offset of each accepted block; this
    /// method re-reads that block with the scanner's own parser, so the frame equals the one the first sequential read
    /// returned. Returns <c>null</c> for ids not indexed yet, before <see cref="Start"/>, after a concurrent
    /// <see cref="Dispose"/>, when the bytes at the indexed block no longer form that packet, and after the capture file
    /// changed (Linux/macOS). Allocates one array per call on the mapped backend. A concurrent <see cref="Dispose"/>
    /// waits until this call left the mapped view.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The source was disposed before the call started reading.</exception>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        int frameId = id.Value;
        if (!_Index.TryGetBlockSpan(frameId, out long blockOffset, out long nextBlockOffset))
        {
            return null;
        }

        // Snapshot _Registry so a concurrent Dispose() nulling the field cannot
        // produce a NullReferenceException between the disposed check and Frame.Create.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return null;
        }

        // The view gate keeps the mapping alive while the block is parsed in place (Dispose waits for it); a reader
        // writes only its own thread's slot. Disposal started: no frame.
        if (!_TryEnterView(out ReaderGateToken token))
        {
            return null;
        }

        IndexedPacket packet;
        ReadOnlyMemory<byte> data;
        try
        {
            // Hint: request the whole block (header, packet bytes, trailing length) at once instead of line after line;
            // the next block's start bounds it, the file end bounds the newest frame. It never changes what is read.
            _Backend.PrefetchBlock(blockOffset, _PrefetchBound(blockOffset, nextBlockOffset));
            if (!_TryReadIndexedPacketData(blockOffset, out packet, out data))
            {
                return null;
            }
        }
        finally
        {
            _ExitView(in token);
        }

        return _CreateIndexedFrame(frameId, in packet, data, registry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thread-safe like <see cref="FrameById"/>, also during a lazy scan. Reads the range forward in chunks of 64 ids:
    /// first the block offsets of the chunk (ascending ids decode each index group once, and the next id's offset bounds
    /// a block), then, inside one view-gate section, every block while the blocks 8 ids ahead are already requested from
    /// memory. A concurrent <see cref="Dispose"/> waits for at most one chunk; later chunks throw
    /// <see cref="ObjectDisposedException"/>. Allocates one array per frame on the mapped backend.
    /// </remarks>
    public int FramesById(FrameId first, Span<Frame?> frames, CancellationToken cancellationToken = default)
    {
        Span<long> starts = stackalloc long[_BatchChunk];
        Span<long> bounds = stackalloc long[_BatchChunk];
        int found = 0;
        for (int chunkStart = 0; chunkStart < frames.Length; chunkStart += _BatchChunk)
        {
            ObjectDisposedException.ThrowIf(_Disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            // The id sum is formed in long: a range that runs past the valid ids reads null entries, it does not wrap.
            int length = Math.Min(_BatchChunk, frames.Length - chunkStart);
            found += _ReadChunk((long)first.Value + chunkStart, frames.Slice(chunkStart, length), starts, bounds);
        }

        return found;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent. Waits for <see cref="FrameById"/> calls running on other threads, then releases the scanner, the
    /// registry reference, the backend, and then the file lock (other processes may write the file again). A failure to
    /// release the memory mapping is an OS error, not a frame error: it propagates as
    /// <see cref="AggregateException"/> after the source's own state is released (the same rule as
    /// <see cref="Blf.BlfSource"/> and <see cref="Asc.AscSource"/>).
    /// </remarks>
    /// <exception cref="AggregateException">Releasing the memory mapping failed.</exception>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // _Scanner is read by FrameById and other public surface from any
        // thread; publish the null write with a release barrier (SOURCE_GUIDE §13.3).
        _Scanner = null;
        // Clear the registry reference so the session can be GC'd after Dispose().
        _Registry = null;
        // _Slabs stays: it belongs to the NextFrame thread, which may still be finishing a frame; it dies with the source.
        // GC.SuppressFinalize is called before the backend disposal so it executes
        // even if _Backend.Dispose() throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        try
        {
            // Random reads on other threads finish their frame first (at most one frame each); then nothing reads the
            // view through the gate any more. NextFrame keeps its own lease, so a dispose from its error handler unmaps
            // when that call ends (SafeHandle reference count).
            _Gate?.Close();
            _Backend.Dispose();
        }
        finally
        {
            // The file stays locked until the mapping is gone; then writers may open it again.
            _Guard?.Dispose();
        }
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Reads ids <paramref name="firstId"/> … <paramref name="firstId"/> + <paramref name="frames"/>.Length − 1 (at most
    /// <see cref="_BatchChunk"/>; see <see cref="FramesById"/>).
    /// </summary>
    private int _ReadChunk(long firstId, Span<Frame?> frames, Span<long> starts, Span<long> bounds)
    {
        // Offsets first, outside the gate: an id that is not indexed (or outside the id range) gets −1 and a null entry.
        int length = frames.Length;
        for (int i = 0; i < length; i++)
        {
            long id = firstId + i;
            if (id >= 0 && id <= int.MaxValue && _Index.TryGetBlockSpan((int)id, out long start, out long next))
            {
                starts[i] = start;
                bounds[i] = _PrefetchBound(start, next);
            }
            else
            {
                starts[i] = -1;
            }
        }

        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null || !_TryEnterView(out ReaderGateToken token))
        {
            frames.Clear();
            return 0;
        }

        int found = 0;
        try
        {
            // Software pipeline: blocks i + 1 … i + 8 are on their way while block i is parsed and copied.
            int ahead = Math.Min(_BatchPrefetchDistance, length);
            for (int i = 0; i < ahead; i++)
            {
                _PrefetchEntry(starts[i], bounds[i]);
            }

            for (int i = 0; i < length; i++)
            {
                if (i + _BatchPrefetchDistance < length)
                {
                    _PrefetchEntry(starts[i + _BatchPrefetchDistance], bounds[i + _BatchPrefetchDistance]);
                }

                // An indexed id lies in 0 … int.MaxValue (checked above), so the cast is exact.
                Frame? frame = starts[i] >= 0 && _TryReadIndexedPacketData(starts[i], out IndexedPacket packet, out ReadOnlyMemory<byte> data)
                    ? _CreateIndexedFrame((int)(firstId + i), in packet, data, registry)
                    : null;
                frames[i] = frame;
                if (frame is not null)
                {
                    found++;
                }
            }
        }
        finally
        {
            _ExitView(in token);
        }

        return found;
    }

    /// <summary>Prefetches one chunk entry; −1 marks an id that is not indexed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PrefetchEntry(long start, long bound)
    {
        if (start >= 0)
        {
            _Backend.PrefetchBlock(start, bound);
        }
    }

    /// <summary>The prefetch bound of a block: the next block's start, or the file end when it is unknown.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long _PrefetchBound(long blockOffset, long nextBlockOffset) =>
        nextBlockOffset > blockOffset
            ? nextBlockOffset
            : long.MaxValue;

    /// <summary>
    /// Re-reads the indexed block with the scanner's parser and copies (mapped) or slices (in-memory) the packet bytes.
    /// Caller is inside the view gate (or the data is in memory). False when the bytes no longer form that packet.
    /// </summary>
    private bool _TryReadIndexedPacketData(long blockOffset, out IndexedPacket packet, out ReadOnlyMemory<byte> data)
    {
        // Same parser as the scan; acceptance is not re-decided, only the fields are re-read. Any mismatch fails closed.
        if (!_TryReadIndexedPacket(blockOffset, out packet))
        {
            data = default;
            return false;
        }

        // Preloaded: zero-copy slice of the file array. Mapped: one exact-size copy of the packet bytes.
        data = _Backend.ReadFrameDataLeased(packet.DataOffset, packet.CapturedLength);
        return true;
    }

    /// <summary>Builds the frame of a re-read packet; null when its interface is unknown or the file changed.</summary>
    private Frame? _CreateIndexedFrame(int frameId, in IndexedPacket packet, ReadOnlyMemory<byte> data, FrameInterfaceRegistry registry)
    {
        // A change found by a sequential check fails closed; the latch is one volatile read, no system call.
        if (!_TryResolveInterface(in packet, out PcapInterfaceEntry? iface)
            || (_Guard is not null && _Guard.ChangeDetected))
        {
            return null;
        }

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(packet.TimestampNanos),
            data,
            iface.LinkType,
            iface.Id,
            registry);

        return result.IsSuccess
            ? result.Value
            : null;
    }

    /// <summary>
    /// The registered interface of a re-read packet: PCAPNG packets carry it from their <see cref="InterfaceInfo"/> (one
    /// field read); legacy PCAP and a not yet published entry fall back to the table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryResolveInterface(in IndexedPacket packet, [NotNullWhen(true)] out PcapInterfaceEntry? iface)
    {
        iface = packet.Registered;
        return iface is not null || _Interfaces.TryGet(packet.SectionIndex, packet.InterfaceId, out iface);
    }

    /// <summary>Enters the view gate of a mapped backend; in-memory data needs none. False once disposal started.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryEnterView(out ReaderGateToken token)
    {
        token = default;
        return _Gate is null || _Gate.TryEnter(out token);
    }

    /// <summary>Ends a section started by <see cref="_TryEnterView"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ExitView(in ReaderGateToken token) => _Gate?.Exit(in token);

    /// <summary>Reads the next frame from the pre-built index (full scan mode, or after the lazy scan finished).</summary>
    private Frame? _NextFrameFromIndex(CancellationToken cancellationToken = default)
    {
        // Snapshot _Registry once for the entire scan pass so a concurrent Dispose()
        // cannot null the field between the per-iteration disposed check and Frame.Create.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return null;
        }

        while (_CurrentFrame < _Index.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Abort early if another thread disposed the source mid-iteration; the
            // backend may have already released its mmap pointer.
            if (_Disposed != 0 || _Aborted)
            {
                return null;
            }

            // Replay open-time skips recorded before the frame at _CurrentFrame was indexed. A skip stores the index of
            // the frame that follows it, so skips with FrameIndex <= _CurrentFrame belong in front of this frame. Strict
            // mode aborts at that position, exactly as lazy mode does.
            if (!_ReplayPendingSkipsBefore(_CurrentFrame))
            {
                return null;
            }

            int frameId = _CurrentFrame++;
            if (!_CheckFileUnchanged(frameId))
            {
                return null;
            }

            // The caller (NextFrame) holds the view lease, so the block is parsed in place.
            if (!_Index.TryGetBlockOffset(frameId, out long blockOffset)
                || !_TryReadIndexedPacket(blockOffset, out IndexedPacket packet))
            {
                // An indexed block that no longer parses means the bytes changed under us: report, never guess.
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameId,
                    FileOffset = blockOffset,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = Invariant($"Indexed block at offset {blockOffset} no longer parses."),
                });
                continue;
            }

            Frame? frame = _CreateSequentialFrame(
                registry, frameId, packet.DataOffset, packet.CapturedLength, packet.TimestampNanos,
                packet.SectionIndex, packet.InterfaceId);
            if (frame is not null)
            {
                return frame;
            }
        }

        // End of the index: one last change check, then skips recorded after the last frame (trailing corruption).
        if (!_CheckFileUnchanged(0))
        {
            return null;
        }

        _ = _ReplayPendingSkipsBefore(int.MaxValue);
        return null;
    }

    /// <summary>
    /// Sequential change check (see <see cref="CaptureFileGuard.HasChangedAt"/> for when the file is asked and when only
    /// the latch is read). <see cref="FrameById"/> reads only that latch. A change raises
    /// <see cref="FrameReadErrorKind.SourceFileChanged"/> once and stops sequential reading in every tolerance mode.
    /// Returns false when reading must stop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _CheckFileUnchanged(int frameId)
    {
        if (_Guard is null || !_Guard.HasChangedAt(frameId))
        {
            return true;
        }

        _ReportFileChanged(frameId);
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

    /// <summary>Fields of one indexed packet re-read from its block.</summary>
    /// <param name="DataOffset">File offset of the first packet byte.</param>
    /// <param name="CapturedLength">Captured packet length in bytes.</param>
    /// <param name="TimestampNanos">Timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="SectionIndex">Section of the block (0 for legacy PCAP).</param>
    /// <param name="InterfaceId">Interface id within the section (0 for legacy PCAP).</param>
    /// <param name="Registered">The interface's published registry entry; null for legacy PCAP and before registration.</param>
    private readonly record struct IndexedPacket(
        long DataOffset, int CapturedLength, long TimestampNanos, ushort SectionIndex, ushort InterfaceId, PcapInterfaceEntry? Registered);

    /// <summary>
    /// Re-reads the accepted block at <paramref name="blockOffset"/> with the scanner's rules and parser: the block length
    /// checks of the scan loop, the section that was current at that offset, and <see cref="PcapngPacketBlocks"/> (PCAPNG)
    /// or <see cref="PcapPacketHeader"/> (legacy PCAP). False when the bytes no longer form an accepted packet (fail
    /// closed). The caller holds a view lease (sequential thread) or is inside the view gate (<see cref="FrameById"/>,
    /// <see cref="FramesById"/>), so spans stay mapped.
    /// </summary>
    private bool _TryReadIndexedPacket(long blockOffset, out IndexedPacket packet)
    {
        packet = default;
        long fileSize = _Backend.FileSize;
        PcapNgFormat? format = _PcapNgFormat;
        if (format is null)
        {
            return _TryReadLegacyRecord(_LegacyFormat!, blockOffset, fileSize, out packet);
        }

        if (!format.TryFindSection(blockOffset, out ushort sectionIndex, out SectionInfo? section)
            || blockOffset + 8 > fileSize)
        {
            return false;
        }

        // Block type and length in the section's byte order; the same length rules as the scan loop.
        bool swap = section.ByteSwapped;
        EndianReader reader = new(swap);
        ReadOnlySpan<byte> prefix = _Backend.GetScanSpan(blockOffset, 8);
        if (prefix.Length < 8)
        {
            return false;
        }

        uint blockType = reader.ReadU32(prefix);
        if (!PcapPadding.TryRoundBlockLength(reader.ReadU32(prefix[4..]), out uint blockLength)
            || blockLength < PcapConstants.MinBlockSize
            || blockLength > PcapConstants.MaxBlockSize
            || blockOffset + blockLength > fileSize)
        {
            return false;
        }

        ReadOnlySpan<byte> block = _Backend.GetScanSpan(blockOffset, (int)blockLength);
        if (block.Length < (int)blockLength
            || !PcapngBlockLayout.TryValidatePcapngBlockLength(blockLength, block[^4..], swap, out _))
        {
            return false;
        }

        // The parser the scanner ran for this block type, with the same validated length and section.
        ScanPacketResult result;
        EpbFields fields;
        int bytesBeforeData;
        switch (blockType)
        {
            case PcapConstants.BlockTypeEPB:
                result = PcapngPacketBlocks.TryReadEpb(block, bodyOmits8ByteHeader: false, swap, blockLength, section, out fields);
                bytesBeforeData = PcapngBlockLayout.EpbBytesBeforePacketData;
                break;
            case PcapConstants.BlockTypeSPB:
                result = PcapngPacketBlocks.TryReadSpb(block, bodyOmits8ByteHeader: false, swap, blockLength, section, out fields);
                bytesBeforeData = PcapngBlockLayout.SpbBytesBeforePacketData;
                break;
            case PcapConstants.BlockTypePB:
                result = PcapngPacketBlocks.TryReadPb(block, bodyOmits8ByteHeader: false, swap, blockLength, section, out fields);
                bytesBeforeData = PcapngBlockLayout.PbBytesBeforePacketData;
                break;
            default:
                return false;
        }

        if (result != ScanPacketResult.Frame)
        {
            return false;
        }

        packet = new IndexedPacket(
            blockOffset + bytesBeforeData, fields.CapturedLength, fields.TimestampNanos, sectionIndex, (ushort)fields.InterfaceId,
            fields.Interface?.Registered);
        return true;
    }

    /// <summary>Re-reads a legacy PCAP record header with the scanner's rules (see <see cref="_TryReadIndexedPacket"/>).</summary>
    private bool _TryReadLegacyRecord(LegacyPcapFormat legacy, long recordOffset, long fileSize, out IndexedPacket packet)
    {
        packet = default;
        if (recordOffset + PcapConstants.PcapPacketHeaderSize > fileSize)
        {
            return false;
        }

        ReadOnlySpan<byte> header = _Backend.GetScanSpan(recordOffset, PcapConstants.PcapPacketHeaderSize);
        if (header.Length < PcapConstants.PcapPacketHeaderSize
            || !PcapPacketHeader.TryParse(header, out PcapPacketHeader record, out _))
        {
            return false;
        }

        EndianReader reader = new(legacy.Info.ByteSwapped);
        uint inclLen = reader.Swap(record.InclLen.Value);
        long dataOffset = recordOffset + PcapConstants.PcapPacketHeaderSize;
        if (!PcapngBlockLayout.TryValidateLegacyInclLen(inclLen, legacy.Info.DeclaredSnapLength, out _)
            || dataOffset + inclLen > fileSize)
        {
            return false;
        }

        long timestampNanos = legacy.Info.TimestampToNanos(reader.Swap(record.TsSec.Value), reader.Swap(record.TsFrac.Value));
        packet = new IndexedPacket(dataOffset, (int)inclLen, timestampNanos, 0, 0, Registered: null);
        return true;
    }

    /// <summary>Scans and returns the next frame in lazy mode.</summary>
    /// <param name="scanner">The scanner snapshot NextFrame read once; a concurrent Dispose may null the field meanwhile.</param>
    private Frame? _NextFrameFromScanner(IncrementalScanner scanner)
    {
        // Snapshot _Registry for the same reason: Dispose() can null it between the
        // disposed check in NextFrame() and the Frame.Create call below.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return null;
        }

        while (true)
        {
            // Abort early if Dispose() was called from another thread mid-scan; the
            // backend pointer might already be released.
            if (_Disposed != 0 || _Aborted)
            {
                return null;
            }

            if (!scanner.NextFrame(out ScannedFrame scanned))
            {
                if (!scanner.IsExhausted)
                {
                    // Packet skip already raised via OnSkip → _HandleSkip.
                    if (_Aborted)
                    {
                        return null;
                    }

                    continue;
                }

                // Scanning complete. The index and the format already belong to this source; dropping the scanner
                // switches NextFrame to the index path and makes EstimatedFrameCount available. A file that changed
                // during the scan is reported before the end of the stream.
                _Scanner = null;
                _ = _CheckFileUnchanged(0);
                return null;
            }

            if (!_CheckFileUnchanged(scanned.FrameIndex))
            {
                return null;
            }

            Frame? frame = _CreateSequentialFrame(
                registry, scanned.FrameIndex, scanned.FileOffset, scanned.CapturedLength, scanned.TimestampNanos,
                scanned.SectionIndex, scanned.InterfaceId);
            if (frame is not null)
            {
                // Track sequential progress so a subsequent NextFrame() after lazy-scan completion (which switches to
                // _NextFrameFromIndex) resumes from the next frame instead of replaying the index from the start.
                _CurrentFrame = scanned.FrameIndex + 1;
                return frame;
            }
        }
    }

    /// <summary>
    /// Builds one frame of the sequential path: resolves the interface, copies the bytes (into a slab when slabs are on),
    /// and counts it. Reports a skip and returns null when the interface is unknown or frame creation fails.
    /// </summary>
    private Frame? _CreateSequentialFrame(
        FrameInterfaceRegistry registry,
        int frameId,
        long fileOffset,
        int capturedLength,
        long timestampNanos,
        ushort sectionIndex,
        ushort interfaceId)
    {
        if (!_Interfaces.TryGet(sectionIndex, interfaceId, out PcapInterfaceEntry? iface))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameId,
                FileOffset = fileOffset,
                Kind = FrameReadErrorKind.UnresolvedInterface,
                Message = $"Unresolved interface: section={sectionIndex}, interface={interfaceId}.",
            });
            return null;
        }

        // Zero-copy for in-memory captures; the mapped backend copies once, into the slab when slabs are on.
        ReadOnlyMemory<byte> data = _Backend.CopyFrameDataSequential(fileOffset, capturedLength, _Slabs);
        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(timestampNanos),
            data,
            iface.LinkType,
            iface.Id,
            registry);

        if (result.IsSuccess)
        {
            Interlocked.Increment(ref _ReadFrameCount);
            return result.Value;
        }

        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = frameId,
            FileOffset = fileOffset,
            Kind = FrameReadErrorKind.Other,
            Message = $"Frame creation failed at offset {fileOffset}.",
        });
        return null;
    }

    /// <summary>Registers every interface the format state holds and that is not registered yet.</summary>
    private void _RegisterKnownInterfaces(FrameInterfaceRegistry registry)
    {
        if (_Format is PcapNgFormat pcapng)
        {
            for (int s = 0; s < pcapng.SectionCount; s++)
            {
                if (!pcapng.TryGetSection(s, out SectionInfo? section))
                {
                    continue;
                }

                for (int i = 0; i < section.InterfaceCount; i++)
                {
                    _RegisterPcapngInterface(registry, (ushort)s, (ushort)i, section, section.Interface(i)!);
                }
            }
        }
        else if (_Format is LegacyPcapFormat legacy && !_Interfaces.Contains(0, 0))
        {
            FrameInterfaceId id = registry.Register(
                _SourceId, "Default Interface", null, legacy.Info.LinkType, PcapInterfaceProperties.ForLegacy(legacy.Info));
            _ = _Interfaces.TryAdd(0, 0, new PcapInterfaceEntry(id, legacy.Info.LinkType));
        }
    }

    /// <summary>Scanner callback (lazy mode, scan thread): registers an IDB before any of its packets is indexed.</summary>
    private void _OnInterfaceAdded(ushort sectionIndex, ushort interfaceIndex, SectionInfo section, InterfaceInfo info)
    {
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return;
        }

        _RegisterPcapngInterface(registry, sectionIndex, interfaceIndex, section, info);
    }

    /// <summary>Registers a single PCAPNG interface with the registry and publishes it in the interface table.</summary>
    private void _RegisterPcapngInterface(
        FrameInterfaceRegistry registry, ushort sectionIndex, ushort interfaceIndex, SectionInfo section, InterfaceInfo info)
    {
        if (_Interfaces.Contains(sectionIndex, interfaceIndex))
        {
            return;
        }

        // if_name comes from the file: a name the registry would reject (empty, control characters) falls back to the
        // positional name instead of failing the whole read.
        string name = info.Name is { } ifName && NameValidation.IsValidUiName(ifName) ? ifName : $"Interface {interfaceIndex}";
        FrameInterfaceId id = registry.Register(
            _SourceId, name, info.Description, info.LinkType, PcapInterfaceProperties.ForPcapng(info, section));

        // Table first (sequential path, legacy fallback), then the InterfaceInfo the re-read parser hands back.
        PcapInterfaceEntry entry = new(id, info.LinkType);
        _ = _Interfaces.TryAdd(sectionIndex, interfaceIndex, entry);
        info.SetRegistered(entry);
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Handles a skipped frame by updating statistics and raising the event.
    /// In strict mode, sets the abort flag so subsequent NextFrame calls return null.
    /// </summary>
    private void _HandleSkip(FrameReadErrorEventArgs error)
    {
        _SkippedFrameCount.Increment();
        _ErrorCount.Increment();

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // sets _Aborted so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _Aborted = true;
        }
    }

    /// <summary>Replays pending skips whose FrameIndex is at or before <paramref name="frameIndex"/>. Returns false when aborted.</summary>
    private bool _ReplayPendingSkipsBefore(int frameIndex)
    {
        List<FrameReadErrorEventArgs>? skips = _PendingScanSkips;
        if (skips is null)
        {
            return true;
        }

        while (_PendingScanSkipCursor < skips.Count && skips[_PendingScanSkipCursor].FrameIndex <= frameIndex)
        {
            _HandleSkip(skips[_PendingScanSkipCursor++]);
            if (_Aborted)
            {
                return false;
            }
        }

        if (_PendingScanSkipCursor == skips.Count)
        {
            _PendingScanSkips = null;
        }

        return true;
    }

    #endregion
}
