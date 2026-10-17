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
/// Thread-safety: <see cref="NextFrame"/> is not thread-safe (one reading thread). <see cref="FrameById"/> is
/// thread-safe and may run on any number of threads, also while <see cref="NextFrame"/> is still scanning lazily: it
/// returns every frame the index already holds and <c>null</c> for ids not indexed yet. It never waits for the scan.
/// </para>
/// </remarks>
public sealed class PcapngSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    /// <summary>Optional description.</summary>
    private readonly string? _Description;

    /// <summary>Data backend (in-memory or mmap).</summary>
    private readonly DataBackend _Backend;

    /// <summary>
    /// Frame index built during scanning. In lazy mode this is the scanner's index, so entries the scanner appends are
    /// visible to <see cref="FrameById"/> as soon as the index publishes its count.
    /// </summary>
    private readonly FrameIndex _Index;

    /// <summary>Format-specific metadata (sections and interfaces). Mutated by the scanner; read on the scan thread only.</summary>
    private readonly ScannerFormat _Format;

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

    /// <summary>Set in strict mode after a skip so later <see cref="NextFrame"/> calls return null.</summary>
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
        IncrementalScanner scanner,
        bool lazy,
        PcapngSourceOptions options,
        List<FrameReadErrorEventArgs>? pendingScanSkips)
    {
        UiName = uiName;
        _Description = description;
        _Backend = backend;
        _Index = scanner.Index;
        _Format = scanner.Format;
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
    /// <exception cref="IOException">The file could not be read.</exception>
    public static PcapngSource Open(string path, PcapngSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();

        string uiName = options.UiName ?? Path.GetFileName(path);
        string? description = null;

        FileInfo fileInfo = new(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Capture file not found.", path);
        }

        // Decide backend: in-memory (bounded by the budget and Array.MaxLength) or one read-only mapping.
        DataBackend backend = PreloadDecision.ShouldPreload(fileInfo.Length, options.PreloadBudget)
            ? DataBackend.FromMemory(File.ReadAllBytes(path))
            : DataBackend.FromMmap(path);

        try
        {
            return options.ScanMode == ScanMode.Full
                ? _OpenFullScan(uiName, description, backend, options)
                : _OpenLazy(uiName, description, backend, options);
        }
        catch
        {
            // The source never took ownership: release the mapping before the format error propagates.
            backend.Dispose();
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
        return _OpenFullScan(uiName, null, backend, options);
    }

    /// <summary>Full scan: scans entire file upfront.</summary>
    private static PcapngSource _OpenFullScan(string uiName, string? description, DataBackend backend, PcapngSourceOptions options)
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

        return new PcapngSource(uiName, description, backend, scanner, lazy: false, options, skips.Count == 0 ? null : skips);
    }

    /// <summary>Lazy scan: only parses the first header, scans frames on demand.</summary>
    private static PcapngSource _OpenLazy(string uiName, string? description, DataBackend backend, PcapngSourceOptions options)
    {
        IncrementalScanner scanner = new(backend, backend.FileSize);
        return new PcapngSource(uiName, description, backend, scanner, lazy: true, options, pendingScanSkips: null);
    }

    #endregion

    #region IFrameSource implementation

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
    /// Thread-safe, also while a lazy scan runs on the <see cref="NextFrame"/> thread: the frame index and the
    /// interface table are single-writer / multi-reader stores, and every interface is registered before the index
    /// covers its first frame. Returns <c>null</c> for ids not indexed yet, before <see cref="Start"/>, and after a
    /// concurrent <see cref="Dispose"/> released the registry. Allocates one array per call on the mapped backend.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The source was disposed before the call started reading.</exception>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        int frameId = id.Value;
        if (frameId < 0 || frameId >= _Index.Count)
        {
            return null;
        }

        FrameOffset offset = _Index.GetOffset(frameId);
        if (!_Interfaces.TryGet(offset.SectionIndex, offset.InterfaceId, out PcapInterfaceEntry iface))
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

        ReadOnlyMemory<byte> data = _Backend.ReadFrameData(offset.FileOffset, offset.CapturedLength);
        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(_Index.GetTimestamp(frameId)),
            data,
            iface.LinkType,
            iface.Id,
            registry);

        if (!result.IsSuccess)
        {
            return null;
        }

        return result.Value;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent. Releases the scanner, the registry reference, and the backend. A failure to release the memory
    /// mapping is an OS error, not a frame error: it propagates as <see cref="AggregateException"/> after the source's
    /// own state is released (the same rule as <see cref="Blf.BlfSource"/> and <see cref="Asc.AscSource"/>).
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
        _Backend.Dispose();
    }

    #endregion

    #region Private helpers

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
            FrameOffset offset = _Index.GetOffset(frameId);
            Frame? frame = _CreateSequentialFrame(
                registry, frameId, offset.FileOffset, offset.CapturedLength, _Index.GetTimestamp(frameId),
                offset.SectionIndex, offset.InterfaceId);
            if (frame is not null)
            {
                return frame;
            }
        }

        // Skips recorded after the last frame (trailing corruption) are replayed at end-of-stream.
        _ = _ReplayPendingSkipsBefore(int.MaxValue);
        return null;
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
                // switches NextFrame to the index path and makes EstimatedFrameCount available.
                _Scanner = null;
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
        if (!_Interfaces.TryGet(sectionIndex, interfaceId, out PcapInterfaceEntry iface))
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
            for (int s = 0; s < pcapng.Sections.Count; s++)
            {
                SectionInfo section = pcapng.Sections[s];
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
        _ = _Interfaces.TryAdd(sectionIndex, interfaceIndex, new PcapInterfaceEntry(id, info.LinkType));
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
