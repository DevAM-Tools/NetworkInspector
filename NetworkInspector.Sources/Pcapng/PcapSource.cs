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
/// Thread-safety: <see cref="FrameById"/> is thread-safe with respect to itself only after
/// the lazy scan has completed (<see cref="NextFrame"/> has returned <c>null</c> at end-of-stream).
/// While the lazy scan is in progress, <see cref="FrameById"/> returns <c>null</c> for any id
/// because the underlying frame index, format metadata, and interface table are still being
/// mutated by the scanner thread. <see cref="NextFrame"/> is not thread-safe.
/// </remarks>
public sealed class PcapSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>User-friendly display name.</summary>
    private readonly string _UiName;

    /// <summary>Optional description.</summary>
    private readonly string? _Description;

    /// <summary>Data backend (in-memory or mmap).</summary>
    private readonly DataBackend _Backend;

    /// <summary>Frame index built during scanning.</summary>
    private FrameIndex _Index;

    /// <summary>Format-specific metadata for frame reconstruction.</summary>
    private ScannerFormat _Format;

    /// <summary>Scanner for lazy mode (null after full scan or when lazy scan completes).</summary>
    private IncrementalScanner? _Scanner;

    /// <summary>Sequential frame counter for NextFrame().</summary>
    private int _CurrentFrame;

    /// <summary>Number of interfaces already registered with the stack.</summary>
    private int _RegisteredInterfaceCount;

    /// <summary>Maps (sectionIndex, interfaceId) → FrameInterfaceId.</summary>
    private readonly Dictionary<(ushort, ushort), FrameInterfaceId> _Interfaces = [];

    /// <summary>Source ID assigned during Start().</summary>
    private FrameSourceId _SourceId;

    /// <summary>Registry reference for lazy interface registration.</summary>
    private FrameInterfaceRegistry? _Registry;

    /// <summary>Whether the source has been started.</summary>
    private bool _Started;

    /// <summary>Whether the source has been disposed.</summary>
    private bool _Disposed;

    #endregion

    #region Error Tolerance Fields

    private long _ReadFrameCount;
    private long _SkippedFrameCount;
    private long _ErrorCount;
    private bool _Aborted;

    #endregion

    #region Constructors

    /// <summary>
    /// Private constructor — use <see cref="Open"/> or <see cref="FromData"/> factory methods.
    /// </summary>
    private PcapSource(string uiName, string? description, DataBackend backend, FrameIndex index, ScannerFormat format, IncrementalScanner? scanner)
    {
        _UiName = uiName;
        _Description = description;
        _Backend = backend;
        _Index = index;
        _Format = format;
        _Scanner = scanner;
    }

    #endregion

    #region Factory methods

    /// <summary>
    /// Opens a capture file from disk.
    /// </summary>
    /// <param name="path">Path to the PCAPNG or legacy PCAP file.</param>
    /// <param name="options">Reader options, or null for defaults.</param>
    /// <returns>A configured PcapSource ready for <see cref="IFrameSource.Start"/>.</returns>
    /// <exception cref="PcapException">The file format is unrecognized or corrupt.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static PcapSource Open(string path, PcapSourceOptions? options = null)
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

        long fileSize = fileInfo.Length;

        // Decide backend: in-memory or mmap
        bool useInMemory = options.PreloadBudget.HasValue && fileSize <= options.PreloadBudget.Value;
        DataBackend backend;

        if (useInMemory)
        {
            byte[] data = File.ReadAllBytes(path);
            backend = DataBackend.FromMemory(data);
        }
        else
        {
            backend = DataBackend.FromMmap(path, options.MaxHandles);
        }

        if (options.ScanMode == ScanMode.Full)
        {
            return _OpenFullScan(uiName, description, backend);
        }

        return _OpenLazy(uiName, description, backend);
    }

    /// <summary>
    /// Creates a PcapSource from in-memory data (e.g. for testing or WASM).
    /// Always performs a full scan.
    /// </summary>
    /// <param name="data">Raw capture file bytes.</param>
    /// <param name="uiName">Display name for this source.</param>
    /// <param name="options">Reader options, or null for defaults.
    /// <see cref="PcapSourceOptions.ScanMode"/> is ignored — in-memory data is always fully scanned.</param>
    /// <returns>A configured PcapSource ready for <see cref="IFrameSource.Start"/>.</returns>
    /// <exception cref="PcapException">The data format is unrecognized or corrupt.</exception>
    public static PcapSource FromData(byte[] data, string uiName = "In-Memory Capture", PcapSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(uiName);
        // byte[] in .NET is always bounded by int.MaxValue, so no size check needed here.
        // PcapSourceOptions accepted for parity with Open(...) and to satisfy
        // SOURCE_GUIDE §7.3; UiName is taken from the explicit argument.
        _ = options;
        DataBackend backend = DataBackend.FromMemory(data);
        return _OpenFullScan(uiName, null, backend);
    }

    /// <summary>Full scan: scans entire file upfront.</summary>
    private static PcapSource _OpenFullScan(string uiName, string? description, DataBackend backend)
    {
        IncrementalScanner scanner = new(backend, backend.FileSize);

        // Scan to exhaustion
        while (scanner.NextFrame(out _))
        {
            // Frame is already indexed inside the scanner
        }

        FrameIndex index = scanner.Index;
        index.ShrinkToFit();

        return new PcapSource(uiName, description, backend, index, scanner.Format, null);
    }

    /// <summary>Lazy scan: only parses the first header, scans frames on demand.</summary>
    private static PcapSource _OpenLazy(string uiName, string? description, DataBackend backend)
    {
        IncrementalScanner scanner = new(backend, backend.FileSize);

        return new PcapSource(uiName, description, backend, scanner.Index, scanner.Format, scanner);
    }

    #endregion

    #region IFrameSource implementation

    /// <inheritdoc />
    public string UiName => _UiName;

    /// <inheritdoc />
    public string? Description => _Description;

    /// <inheritdoc />
    public int? EstimatedFrameCount
    {
        get
        {
            if (Volatile.Read(ref _Scanner) is null)
            {
                return _Index.Count;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public bool IsFrameCountTruncated
    {
        get
        {
            IncrementalScanner? scanner = Volatile.Read(ref _Scanner);
            if (scanner is not null)
            {
                return scanner.IsIndexFull;
            }

            return _Index.IsFull;
        }
    }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);

    // ── IErrorTolerantFrameSource / IFrameSourceStatistics ────────────────────

    /// <inheritdoc/>
    public long ReadFrameCount => Volatile.Read(ref _ReadFrameCount);

    /// <inheritdoc/>
    public long SkippedFrameCount => Volatile.Read(ref _SkippedFrameCount);

    /// <inheritdoc/>
    public long ErrorCount => Volatile.Read(ref _ErrorCount);

    /// <inheritdoc/>
    public bool HasErrors => Volatile.Read(ref _ErrorCount) > 0;

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _CurrentFrame = 0;
        Volatile.Write(ref _Started, true);

        // For full scan mode, register all interfaces now. Read _Scanner via Volatile
        // for symmetry with the Volatile.Write in Dispose().
        if (Volatile.Read(ref _Scanner) == null)
        {
            _RegisterAllInterfaces(registry);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// For thread-safe random access, use <see cref="FrameById"/> instead.
    /// </remarks>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
        {
            throw new InvalidOperationException($"{UiName} has not been started.");
        }

        if (Volatile.Read(ref _Aborted))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Read _Scanner with Volatile for symmetry with Dispose()'s Volatile.Write.
        if (Volatile.Read(ref _Scanner) is not null)
        {
            return _NextFrameFromScanner();
        }

        return _NextFrameFromIndex(cancellationToken);
    }

    #endregion

    #region IRandomAccessFrameSource implementation

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>null</c> while a lazy scan is still in progress, because the frame index,
    /// interface map, and format metadata are mutated by the scanning thread and a concurrent
    /// random-access read would race with those mutations. Once <see cref="NextFrame"/> has
    /// returned <c>null</c> at end-of-stream, the lazy scan has finalised and this method
    /// becomes thread-safe.
    /// </remarks>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        // While a lazy scan is still in progress, the frame index, _Interfaces, and _Format
        // are not yet stable. Returning null avoids a race with the single scanning thread.
        if (Volatile.Read(ref _Scanner) is not null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        int frameId = id.Value;
        if (frameId < 0 || frameId >= _Index.Count)
        {
            return null;
        }

        ref readonly FrameOffset offset = ref _Index.GetOffset(frameId);
        long timestampNanos = _Index.GetTimestamp(frameId);

        ReadOnlyMemory<byte> data = _Backend.ReadFrameData(frameId, offset.FileOffset, offset.CapturedLength);

        // Resolve link type and interface ID
        if (!_TryResolveLinkTypeAndInterface(offset.SectionIndex, offset.InterfaceId, out LinkType linkType, out FrameInterfaceId interfaceId))
        {
            return null;
        }

        // Snapshot _Registry so a concurrent Dispose() nulling the field cannot
        // produce a NullReferenceException between the disposed check and Frame.Create.
        FrameInterfaceRegistry? registry = Volatile.Read(ref _Registry);
        if (registry is null)
        {
            return null;
        }

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(timestampNanos),
            data,
            linkType,
            interfaceId,
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
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
        {
            return;
        }

        Volatile.Write(ref _Disposed, true);
        // _Scanner is read by FrameById and other public surface from any
        // thread; publish the null write with a release barrier (SOURCE_GUIDE §13.3).
        Volatile.Write(ref _Scanner, null);
        // Clear the registry reference so the session can be GC'd after Dispose().
        _Registry = null;
        // GC.SuppressFinalize is called before the backend disposal so it executes
        // even if _Backend.Dispose() throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        _Backend.Dispose();
    }

    #endregion

    #region Private helpers

    /// <summary>Reads the next frame from the pre-built index (full scan mode).</summary>
    private Frame? _NextFrameFromIndex(CancellationToken cancellationToken = default)
    {
        // Snapshot _Registry once for the entire scan pass so a concurrent Dispose()
        // cannot null the field between the per-iteration disposed check and Frame.Create.
        FrameInterfaceRegistry? registry = Volatile.Read(ref _Registry);
        if (registry is null)
        {
            return null;
        }

        while (_CurrentFrame < _Index.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Abort early if another thread disposed the source mid-iteration; the
            // backend may have already released its mmap pointer.
            if (Volatile.Read(ref _Disposed) || Volatile.Read(ref _Aborted))
            {
                return null;
            }

            int frameId = _CurrentFrame++;
            ref readonly FrameOffset offset = ref _Index.GetOffset(frameId);
            long timestampNanos = _Index.GetTimestamp(frameId);

            ReadOnlyMemory<byte> data = _Backend.ReadFrameData(frameId, offset.FileOffset, offset.CapturedLength);

            if (!_TryResolveLinkTypeAndInterface(offset.SectionIndex, offset.InterfaceId, out LinkType linkType, out FrameInterfaceId interfaceId))
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameId,
                    FileOffset = offset.FileOffset,
                    Kind = FrameReadErrorKind.UnresolvedInterface,
                    Message = $"Unresolved interface: section={offset.SectionIndex}, interface={offset.InterfaceId}."
                });
                continue;
            }

            ParseResult<Frame> result = Frame.Create(
                new FrameId(frameId),
                new Timestamp(timestampNanos),
                data,
                linkType,
                interfaceId,
                registry);

            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _ReadFrameCount);
                return result.Value;
            }

            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameId,
                FileOffset = offset.FileOffset,
                Kind = FrameReadErrorKind.Other,
                Message = $"Frame creation failed at offset {offset.FileOffset}."
            });
        }

        return null;
    }

    /// <summary>Scans and returns the next frame in lazy mode.</summary>
    private Frame? _NextFrameFromScanner()
    {
        // Snapshot _Scanner via Volatile.Read so the local strong reference survives
        // a concurrent Dispose() that would otherwise null out the field. The local
        // is non-null only because NextFrame() rejected the disposed/un-scanned case.
        IncrementalScanner scanner = Volatile.Read(ref _Scanner)!;

        // Snapshot _Registry for the same reason: Dispose() can null it between the
        // disposed check in NextFrame() and the Frame.Create call below.
        FrameInterfaceRegistry? registry = Volatile.Read(ref _Registry);
        if (registry is null)
        {
            return null;
        }

        while (true)
        {
            // Abort early if Dispose() was called from another thread mid-scan; the
            // backend pointer might already be released.
            if (Volatile.Read(ref _Disposed) || Volatile.Read(ref _Aborted))
            {
                return null;
            }

            if (!scanner.NextFrame(out ScannedFrame scanned))
            {
                // Scanning complete — finalize
                _FinishLazyScan(scanner);
                return null;
            }

            // Register interfaces that were discovered while scanning this frame.
            // A single call after scanning is sufficient; the helper checks which
            // interfaces are new and skips already-registered ones (O(1) guard).
            _RegisterNewInterfaces();

            // Prefer backend-backed memory: zero-copy for in-memory captures;
            // mmap path allocates once inside ReadFrameData (same as copying the scan span).
            ReadOnlyMemory<byte> frameData = _Backend.ReadFrameData(
                scanned.FrameIndex,
                scanned.FileOffset,
                scanned.CapturedLength);

            if (!_TryResolveLinkTypeAndInterface(scanned.SectionIndex, scanned.InterfaceId, out LinkType linkType, out FrameInterfaceId interfaceId))
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = scanned.FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.UnresolvedInterface,
                    Message = $"Unresolved interface: section={scanned.SectionIndex}, interface={scanned.InterfaceId}."
                });
                continue;
            }

            ParseResult<Frame> result = Frame.Create(
                new FrameId(scanned.FrameIndex),
                new Timestamp(scanned.TimestampNanos),
                frameData,
                linkType,
                interfaceId,
                registry);

            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _ReadFrameCount);
                // Track sequential progress so a subsequent NextFrame() after lazy-scan
                // completion (which switches to _NextFrameFromIndex) resumes from the next
                // frame instead of replaying the index from the start.
                _CurrentFrame = scanned.FrameIndex + 1;
                return result.Value;
            }

            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = scanned.FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = $"Frame creation failed for scanned frame {scanned.FrameIndex}."
            });
        }
    }

    /// <summary>
    /// Finalizes lazy scan: transfers scanner state to the source.
    /// Publishes <see cref="_Index"/>, <see cref="_Format"/> and clears <see cref="_Scanner"/>
    /// using <see cref="Volatile"/> writes so that subsequent reads from <see cref="FrameById"/>
    /// see a consistent post-scan snapshot (<see cref="FrameById"/> uses a <see cref="Volatile"/>
    /// read on <see cref="_Scanner"/> to detect this transition).
    /// </summary>
    private void _FinishLazyScan(IncrementalScanner scanner)
    {
        _Index = scanner.Index;
        _Index.ShrinkToFit();
        _Format = scanner.Format;
        // _Scanner is the publication marker observed by FrameById. Clearing it last
        // (with a release-style store) ensures _Index/_Format updates above are visible
        // to threads that subsequently observe _Scanner == null.
        Volatile.Write(ref _Scanner, null);
    }

    /// <summary>
    /// Registers all interfaces from all sections (full-scan mode).
    /// </summary>
    private void _RegisterAllInterfaces(FrameInterfaceRegistry registry)
    {
        if (_Format is PcapNgFormat pcapng)
        {
            for (ushort s = 0; s < pcapng.Sections.Count; s++)
            {
                SectionInfo section = pcapng.Sections[s];
                for (ushort i = 0; i < section.InterfaceCount; i++)
                {
                    _RegisterInterface(registry, section, s, i);
                }
            }
        }
        else if (_Format is LegacyPcapFormat legacy)
        {
            _RegisterLegacyInterface(registry, legacy.Info);
        }
    }

    /// <summary>
    /// Registers newly discovered interfaces during lazy scanning.
    /// </summary>
    private void _RegisterNewInterfaces()
    {
        if (_Registry == null)
        {
            return;
        }

        // Read _Scanner via Volatile so a parallel Dispose() can never present us with
        // a torn null reference between the null-check and the dereference.
        IncrementalScanner? scanner = Volatile.Read(ref _Scanner);
        ScannerFormat format = scanner?.Format ?? _Format;

        if (format is PcapNgFormat pcapng)
        {
            // Count total interfaces across all sections
            int totalInterfaces = 0;
            foreach (SectionInfo section in pcapng.Sections)
            {
                totalInterfaces += section.InterfaceCount;
            }

            if (totalInterfaces <= _RegisteredInterfaceCount)
            {
                return;
            }

            // Register the new ones
            int current = 0;
            for (ushort s = 0; s < pcapng.Sections.Count; s++)
            {
                SectionInfo section = pcapng.Sections[s];
                for (ushort i = 0; i < section.InterfaceCount; i++)
                {
                    if (current >= _RegisteredInterfaceCount)
                    {
                        _RegisterInterface(_Registry, section, s, i);
                    }
                    current++;
                }
            }

            _RegisteredInterfaceCount = totalInterfaces;
        }
        else if (format is LegacyPcapFormat legacy && _RegisteredInterfaceCount == 0)
        {
            _RegisterLegacyInterface(_Registry, legacy.Info);
            _RegisteredInterfaceCount = 1;
        }
    }

    /// <summary>Registers a single PCAPNG interface with the stack.</summary>
    private void _RegisterInterface(FrameInterfaceRegistry registry, SectionInfo section, ushort sectionIndex, ushort interfaceId)
    {
        InterfaceInfo? info = section.Interface(interfaceId);
        if (info == null)
        {
            return;
        }

        (ushort, ushort) key = (sectionIndex, interfaceId);
        if (_Interfaces.ContainsKey(key))
        {
            return;
        }

        string name = info.Name ?? $"Interface {interfaceId}";
        Dictionary<string, object>? props = _BuildPcapNgProperties(info, section);
        FrameInterfaceId id = registry.Register(_SourceId, name, info.Description, info.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>Registers a legacy PCAP interface with the stack.</summary>
    private void _RegisterLegacyInterface(FrameInterfaceRegistry registry, LegacyPcapInfo info)
    {
        (ushort, ushort) key = (0, 0);
        if (_Interfaces.ContainsKey(key))
        {
            return;
        }

        Dictionary<string, object>? props = _BuildLegacyPcapProperties(info);
        FrameInterfaceId id = registry.Register(_SourceId, "Default Interface", null, info.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>
    /// Builds a properties dictionary from PCAPNG interface and section metadata.
    /// Returns null when no properties are available (avoids empty dictionary allocation).
    /// </summary>
    private static Dictionary<string, object>? _BuildPcapNgProperties(InterfaceInfo info, SectionInfo section)
    {
        // RawLinkType and SnapLength are always available — initialize with them
        Dictionary<string, object> props = new()
        {
            [FrameInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [FrameInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        // Interface-level metadata (IDB options)
        if (info.Speed.HasValue)
        {
            props[FrameInterfacePropertyKeys.Speed] = info.Speed.Value;
        }
        if (info.FcsLength.HasValue)
        {
            props[FrameInterfacePropertyKeys.FcsLength] = info.FcsLength.Value;
        }
        if (info.Filter is not null)
        {
            props[FrameInterfacePropertyKeys.Filter] = info.Filter;
        }
        if (info.Os is not null)
        {
            props[FrameInterfacePropertyKeys.Os] = info.Os;
        }

        // Section-level metadata (SHB options) — shared across all interfaces in the section
        if (section.Hardware is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureHardware] = section.Hardware;
        }
        if (section.Os is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureOs] = section.Os;
        }
        if (section.UserApplication is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureApplication] = section.UserApplication;
        }

        return props;
    }

    /// <summary>
    /// Builds a properties dictionary from legacy PCAP global header metadata.
    /// Returns null when no properties are available.
    /// </summary>
    private static Dictionary<string, object>? _BuildLegacyPcapProperties(LegacyPcapInfo info)
    {
        // Legacy PCAP has limited metadata — snap length and raw link type
        Dictionary<string, object> props = new()
        {
            [FrameInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [FrameInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        return props;
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Handles a skipped frame by updating statistics and raising the event.
    /// In strict mode, sets the abort flag so subsequent NextFrame calls return null.
    /// </summary>
    private void _HandleSkip(FrameReadErrorEventArgs error)
    {
        Interlocked.Increment(ref _SkippedFrameCount);
        Interlocked.Increment(ref _ErrorCount);

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // sets _Aborted so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Aborted, true);
        }
    }

    /// <summary>Resolves the link type and registered interface ID for a frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryResolveLinkTypeAndInterface(ushort sectionIndex, ushort interfaceId, out LinkType linkType, out FrameInterfaceId frameInterfaceId)
    {
        if (_Format is PcapNgFormat pcapng)
        {
            if (sectionIndex < pcapng.Sections.Count)
            {
                InterfaceInfo? info = pcapng.Sections[sectionIndex].Interface(interfaceId);
                if (info?.LinkType != null && _Interfaces.TryGetValue((sectionIndex, interfaceId), out frameInterfaceId))
                {
                    linkType = info.LinkType.Value;
                    return true;
                }
            }
        }
        else if (_Format is LegacyPcapFormat legacy)
        {
            if (legacy.Info.LinkType.HasValue && _Interfaces.TryGetValue((0, 0), out frameInterfaceId))
            {
                linkType = legacy.Info.LinkType.Value;
                return true;
            }
        }

        linkType = default;
        frameInterfaceId = FrameInterfaceId.Invalid;
        return false;
    }
    #endregion
}
