// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Random-access frame source for Vector ASC (ASCII trace) files.
/// Implements <see cref="IRandomAccessFrameSource"/> and <see cref="IErrorTolerantFrameSource"/>.
/// <para>
/// On open, the file is either loaded into memory (if its size is within <see cref="AscSourceOptions.PreloadBudget"/>)
/// or read from disk through the guarded file handle. In both cases the file is scanned once. The index
/// (<see cref="AscFrameIndex"/>) keeps only the byte offset of every frame line, the relative clock where the line cannot
/// reproduce it, and, for the disk backend, one read-window size per group of 64 frames. A frame is re-read from its offset
/// and parsed again, so the index needs well under 2 B per frame. The window size is fixed at scan time so that one
/// positional read always holds everything the parser looks at (the line and, for Ethernet, the continuation lines and
/// the line that ends the packet); a thread that reads ids in order is served from a per-thread 64 KiB window
/// (<see cref="AscReadAhead"/>). Details: SOURCE_GUIDE §11.1 and §11.6.
/// </para>
/// <para>
/// All frame interfaces discovered during scanning are pre-registered in <see cref="Start"/>, so <see cref="FrameById"/>
/// normally registers nothing; an interface the scan missed is registered lazily under a lock. <see cref="FrameById"/> is
/// safe to call from multiple threads concurrently.
/// </para>
/// </summary>
public sealed class AscSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>
    /// In-memory backend: the whole file (one buffer, no per-line slices); frame lines are found by byte offset.
    /// Empty for the disk backend.
    /// </summary>
    private readonly ReadOnlyMemory<byte> _InMemoryData;

    /// <summary>True for the in-memory backend, false for the disk backend.</summary>
    private readonly bool _IsInMemory;

    /// <summary>
    /// Disk backend (and preloaded files): keeps the ASC file open with <see cref="FileShare.Read"/> for the source's
    /// lifetime — writers denied on Windows, changes detected on Linux/macOS — and serves the disk re-reads through
    /// <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>, which is thread-safe and stateless. A preloaded
    /// file keeps the lock but its change detection stops after Open, since it is served from its copy. Null for
    /// <see cref="FromText"/> / <see cref="FromData"/>.
    /// </summary>
    private readonly CaptureFileGuard? _Guard;

    /// <summary>Parsed ASC file header.</summary>
    private readonly AscHeader _Header;

    /// <summary>Byte offset of every frame-producing line plus the clocks that cannot be re-derived from the line.</summary>
    private readonly AscFrameIndex _FrameIndex;

    /// <summary>
    /// Owner id of this source in the per-thread <see cref="AscReadAhead"/> windows (disk backend). Process-unique, so a
    /// later source never reads an earlier source's window.
    /// </summary>
    private readonly long _ReadAheadOwner = AscReadAhead.NewOwner();

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    // ── Interface registration (pre-populated in Start(), read-only afterwards) ──
    private FrameSourceId _SourceId;

    // Cleared by Dispose from any thread. _BuildFrame snapshots it once per call, so every helper of one frame sees the same
    // registry or none (a null snapshot means a concurrent Dispose).
    private volatile FrameInterfaceRegistry? _Registry;

    /// <summary>
    /// Maps (busType, channel) → FrameInterfaceId. Pre-populated in <see cref="Start"/>
    /// from the scanned interface set. A lazy fallback path in
    /// <see cref="_RegisterInterface"/> may add entries after Start returns;
    /// all mutations are guarded by <see cref="_InterfaceLock"/> to preserve
    /// the thread-safety guarantee of <see cref="FrameById"/>.
    /// </summary>
    private readonly Dictionary<(AscBusType, int), FrameInterfaceId> _InterfaceMap = [];

    /// <summary>
    /// Synchronises concurrent <see cref="_RegisterInterface"/> calls that arrive via
    /// the thread-safe <see cref="FrameById"/> path. All writes to <see cref="_InterfaceMap"/>
    /// are guarded by this lock; reads before the lock are a fast-path optimisation
    /// because .NET Dictionary read operations are safe when no concurrent write is
    /// in progress — the lock ensures writes are serialised.
    /// </summary>
    private readonly Lock _InterfaceLock = new();

    /// <summary>
    /// Set of all (busType, channel, linkType) combinations discovered during index building.
    /// Used in <see cref="Start"/> to pre-register all interfaces.
    /// </summary>
    private readonly HashSet<(AscBusType BusType, int Channel, LinkType LinkType)> _DiscoveredInterfaces;

    // ── Sequential read state (single-threaded NextFrame, no locking needed) ──
    private int _NextFrameIndex;

    // ── Cross-thread observable state (use Volatile) ──
    private volatile bool _Started;

    /// <summary><see cref="AscSourceOptions.FrameSlabSize"/> of the options this source was opened with.</summary>
    private readonly int _FrameSlabSize;

    /// <summary>Frame slab allocator of the sequential path; null when slabs are off. NextFrame thread only.</summary>
    private FrameSlabAllocator? _Slabs;
    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;
    private volatile bool _Aborted;

    // ── Error tolerance statistics (use Interlocked / saturating volatile counters) ──
    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();

    #endregion

    #region Construction

    private AscSource(
        ReadOnlyMemory<byte> inMemoryData,
        bool isInMemory,
        CaptureFileGuard? guard,
        AscHeader header,
        AscFrameIndex frameIndex,
        HashSet<(AscBusType, int, LinkType)> discoveredInterfaces,
        ErrorToleranceMode errorTolerance,
        int frameSlabSize,
        string uiName)
    {
        _InMemoryData = inMemoryData;
        _IsInMemory = isInMemory;
        _Guard = guard;
        _Header = header;
        _FrameIndex = frameIndex;
        _DiscoveredInterfaces = discoveredInterfaces;
        UiName = uiName;
        ErrorTolerance = errorTolerance;
        _FrameSlabSize = frameSlabSize;
    }

    #endregion

    #region Properties

    /// <summary>The file guard (lock and change detection) of a file-backed source; <see langword="null"/> in memory. Tests, diagnostics.</summary>
    internal CaptureFileGuard? FileGuard => _Guard;

    /// <inheritdoc/>
    public string? Description => null;

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _FrameIndex.Count;

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

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    #endregion

    #region Factory Methods

    /// <summary>
    /// Opens an ASC file from disk and builds the frame index.
    /// Files within <see cref="AscSourceOptions.PreloadBudget"/> are fully loaded into memory;
    /// larger files use a disk-based backend that re-reads each frame's record with one positional read sized by the
    /// index (in-order reads are served from a per-thread 64 KiB read-ahead window).
    /// </summary>
    /// <param name="path">Path to the ASC file.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new AscSource ready to be started.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">
    /// The file could not be read, another process holds it open for writing (Windows), or it changed during the
    /// open-time scan (Linux/macOS).
    /// </exception>
    /// <remarks>
    /// The source keeps the file open with <see cref="FileShare.Read"/> until it is disposed: on Windows no other
    /// process can write to it meanwhile; on Linux and macOS a change is detected and reported as
    /// <see cref="FrameReadErrorKind.SourceFileChanged"/>. A preloaded file is served from its copy, so no change is
    /// detected after <c>Open</c> returns.
    /// </remarks>
    public static AscSource Open(string path, AscSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();
        string uiName = options.UiName ?? Path.GetFileName(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ASC file not found.", path);
        }

        // The guard denies writers (Windows) and detects changes (Linux/macOS) for the whole lifetime of the source.
        CaptureFileGuard guard = CaptureFileGuard.Open(path);
        try
        {
            // Small file: one buffer for zero-seek random access. Large file: scan once, re-read lines on demand.
            AscSource source = PreloadDecision.ShouldPreload(guard.Length, options.PreloadBudget)
                ? _BuildFromInMemory(guard.ReadAll(), options, uiName, guard)
                : _BuildFromDisk(guard, options, uiName);

            // An index built from bytes that changed meanwhile could point at the wrong lines: refuse it.
            if (!guard.IsUnchanged())
            {
                throw new IOException("Capture file changed while it was being scanned.");
            }

            // The in-memory index and frames come from the copy made at open: the monitor can stop for the lifetime.
            if (source._IsInMemory)
            {
                guard.StopMonitoring();
            }

            return source;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an AscSource from in-memory text data.
    /// </summary>
    /// <param name="data">Complete ASC file content as a string.</param>
    /// <param name="uiName">Display name for this source.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new AscSource ready to be started.</returns>
    public static AscSource FromText(string data, string? uiName = null, AscSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new();
        uiName ??= options.UiName ?? "ASC Data";
        byte[] bytes = Encoding.Latin1.GetBytes(data);
        return _BuildFromInMemory(bytes, options, uiName, guard: null);
    }

    /// <summary>
    /// Creates an AscSource from in-memory byte data.
    /// </summary>
    /// <param name="data">Complete ASC file content as bytes.</param>
    /// <param name="uiName">Display name for this source.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new AscSource ready to be started.</returns>
    public static AscSource FromData(ReadOnlyMemory<byte> data, string? uiName = null, AscSourceOptions? options = null)
    {
        options ??= new();
        uiName ??= options.UiName ?? "ASC Data";
        // data.ToArray() allocates a copy so the buffer lifetime and contents are owned here.
        byte[] backing = data.ToArray();
        return _BuildFromInMemory(backing, options, uiName, guard: null);
    }

    #endregion

    #region IFrameSource

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _NextFrameIndex = 0;
        _Slabs = FrameSlabAllocator.CreateOrNull(_FrameSlabSize);

        // Pre-register all interfaces discovered during index building.
        // In the common case every (busType, channel) is seen here, making
        // _InterfaceMap effectively read-only afterwards.  A thread-safe lazy
        // fallback in _RegisterInterface handles any channel missed by the scanner.
        foreach ((AscBusType busType, int channel, LinkType linkType) in _DiscoveredInterfaces)
        {
            _RegisterInterface(registry, busType, channel, linkType);
        }

        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException("AscSource has not been started. Call Start() first.");
        }

        if (_Aborted)
        {
            return null;
        }

        // Loop to skip over errored frames in tolerant mode
        while (_NextFrameIndex < _FrameIndex.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameIndex = _NextFrameIndex++;
            if (!_CheckFileUnchanged(frameIndex))
            {
                return null;
            }

            Frame? frame = _BuildFrame(frameIndex, reportErrors: true);
            if (frame.HasValue)
            {
                return frame.Value;
            }

            // A null from a source that a concurrent Dispose has closed is not a frame error: report the disposal.
            ObjectDisposedException.ThrowIf(_Disposed != 0, this);
            if (_Aborted)
            {
                return null;
            }
        }

        // End of the frames: one last change check, so a file changed after the last check is still reported.
        _ = _CheckFileUnchanged(0);
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

    #endregion

    #region IRandomAccessFrameSource

    /// <inheritdoc/>
    /// <remarks>
    /// This is a pure (read-only) random-access path: it never increments error
    /// or read-frame counters, never raises <see cref="FrameSkipped"/>, and never
    /// sets the abort flag. Frame construction failures are reported via the
    /// return value (<c>null</c>) only, so a UI thread inspecting an unrelated
    /// frame cannot poison sequential consumption.
    /// </remarks>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException("AscSource has not been started. Call Start() first.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        int index = id.Value;
        if (index < 0 || index >= _FrameIndex.Count)
        {
            return null;
        }

        Frame? frame = _BuildFrame(index, reportErrors: false);

        // A change found by a sequential check fails closed; the latch is one volatile read, no system call.
        return _Guard is not null && _Guard.ChangeDetected
            ? null
            : frame;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent. Releases the registry reference and then the file guard (the handle the disk backend reads through,
    /// which also keeps writers out). A failure to close the handle is an OS error, not a frame error: it propagates
    /// after the source's own state is released (the same rule as <see cref="Pcapng.PcapngSource"/> and
    /// <see cref="Blf.BlfSource"/>).
    /// </remarks>
    /// <exception cref="IOException">Closing the file handle failed.</exception>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _Registry = null;
        // GC.SuppressFinalize is called before the handle disposal so it executes
        // even if the guard's handle disposal throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        _Guard?.Dispose();
    }

    #endregion

    #region Index Building — In-Memory Backend

    /// <summary>
    /// Builds the source from the whole file in one buffer. Lines are walked with the split rule of the disk backend
    /// (cut at LF, drop one trailing CR); the index keeps the byte offset of every frame line, not a slice per line.
    /// </summary>
    private static AscSource _BuildFromInMemory(
        ReadOnlyMemory<byte> data, AscSourceOptions options, string uiName, CaptureFileGuard? guard)
    {
        AscHeader header = new()
        {
            TimestampTimeZone = options.TimestampTimeZone
        };
        ReadOnlySpan<byte> bytes = data.Span;
        AscLineReader reader = new(bytes);

        // Phase 1: parse header. The first line that is not a header line starts the data.
        int start = 0;
        int length = 0;
        bool hasDataLine = false;
        while (reader.TryNext(out start, out length))
        {
            if (!header.TryParseLine(AscTokenizerBytes.TrimAscii(bytes.Slice(start, length))))
            {
                hasDataLine = true;
                break;
            }
        }

        // Phase 2: classify and index frame-producing lines.
        // The clock is folded here, in file order, so later random access does not re-walk the file.
        bool relativeClock = header.TimestampFormat == "relative";
        AscFrameIndex index = new(relativeClock);
        HashSet<(AscBusType, int, LinkType)> discovered = [];
        AscTimestamp runningClock = _InitialClock(header);

        while (hasDataLine)
        {
            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(bytes.Slice(start, length));
            if (!trimmed.IsEmpty)
            {
                _ = _IndexFrameLine(trimmed, start, ref runningClock, relativeClock, index, discovered, out _);
            }

            hasDataLine = reader.TryNext(out start, out length);
        }

        index.Complete();
        return new AscSource(data, isInMemory: true, guard, header, index, discovered, options.ErrorTolerance, options.FrameSlabSize, uiName);
    }

    /// <summary>
    /// Walks the lines of an in-memory ASC buffer with the split rule of both backends: a line ends at LF, one trailing
    /// CR is dropped, and a last line without LF is included.
    /// </summary>
    private ref struct AscLineReader
    {
        private readonly ReadOnlySpan<byte> _Data;
        private int _Position;

        /// <summary>Creates a reader at the start of <paramref name="data"/>.</summary>
        internal AscLineReader(ReadOnlySpan<byte> data)
        {
            _Data = data;
            _Position = 0;
        }

        /// <summary>Returns the next line as start offset and length (without line terminator); false at the end.</summary>
        internal bool TryNext(out int start, out int length)
        {
            start = _Position;
            length = 0;
            if (_Position >= _Data.Length)
            {
                return false;
            }

            int newline = _Data[_Position..].IndexOf((byte)'\n');
            int end = newline >= 0 ? _Position + newline : _Data.Length;
            _Position = newline >= 0 ? end + 1 : _Data.Length;
            length = LineLength(_Data, start, end);
            return true;
        }

        /// <summary>Length of the line <c>[start, end)</c> after dropping one trailing CR.</summary>
        internal static int LineLength(ReadOnlySpan<byte> data, int start, int end) =>
            end > start && data[end - 1] == (byte)'\r'
                ? end - start - 1
                : end - start;
    }

    #endregion

    #region Index Building — Disk Backend

    /// <summary>
    /// Builds the source for a large file: scans the raw bytes once with a 4 MiB buffer
    /// to build an index of line byte offsets, without keeping all lines in memory.
    /// The byte offset of each frame-producing line is stored so it can be re-read on demand.
    /// </summary>
    private static AscSource _BuildFromDisk(CaptureFileGuard guard, AscSourceOptions options, string uiName)
    {
        AscHeader header = new()
        {
            TimestampTimeZone = options.TimestampTimeZone
        };
        bool headerDone = false;

        // Created when the header ends: the clock mode decides whether relative clocks are stored.
        AscFrameIndex? index = null;
        HashSet<(AscBusType, int, LinkType)> discovered = [];
        AscTimestamp runningClock = default;
        bool relativeClock = false;

        // Primary read buffer — raw bytes from disk, no string conversions during the scan pass.
        byte[] buffer = new byte[AscSourceOptions.DiskReadBufferSize];

        // Carry-over buffer: holds bytes of a line that started in the previous chunk
        // and has not yet been terminated by \n.  Grown lazily; pre-sized to a
        // reasonable line maximum to avoid reallocation in the common case.
        byte[] carryBuffer = new byte[AscSourceOptions.MaxLineLength];
        int carryLen = 0;               // valid bytes in carryBuffer
        bool carryTooLong = false;      // line in carry exceeded MaxLineLength — skip once complete

        // Positional reads through the guarded handle: no second handle, and the handle keeps no file position.
        SafeFileHandle handle = guard.Handle;

        long chunkStartOffset = 0;      // file offset of buffer[0] in the current read
        long lineStartOffset = 0;       // file offset of the first byte of the current line

        // Ethernet packet whose read window is still open: its hex may continue on the following lines.
        PendingRecord pending = new(-1, 0);

        while (true)
        {
            int bytesRead = RandomAccess.Read(handle, buffer, chunkStartOffset);
            if (bytesRead == 0)
            {
                // EOF: flush any remaining carry-over bytes as the last (unterminated) line
                if (carryLen > 0 && !carryTooLong)
                {
                    ReadOnlySpan<byte> lastLine = carryBuffer.AsSpan(0, carryLen);
                    _ProcessDiskLine(
                        lastLine,
                        AscTokenizerBytes.TrimAscii(lastLine),
                        lineStartOffset,
                        lineEnd: chunkStartOffset,
                        ref pending,
                        ref header, ref headerDone, ref runningClock, ref relativeClock, ref index, discovered);
                }

                // A packet still open at the end of the file reads up to the end of the file.
                if (pending.Frame >= 0)
                {
                    index!.RequireReadBytes(pending.Frame, chunkStartOffset - pending.Start);
                }

                break;
            }

            int inChunkLineStart = 0;   // index inside `buffer` where the current line starts

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                // ── Complete line found at buffer[i] ──────────────────────────────
                ReadOnlySpan<byte> inChunkPart = buffer.AsSpan(inChunkLineStart, i - inChunkLineStart);
                ReadOnlySpan<byte> rawLine;

                if (carryLen > 0)
                {
                    // Line started in a previous chunk — append in-chunk part to carry
                    if (!carryTooLong)
                    {
                        int needed = carryLen + inChunkPart.Length;
                        if (needed <= carryBuffer.Length)
                        {
                            inChunkPart.CopyTo(carryBuffer.AsSpan(carryLen));
                            carryLen = needed;
                            rawLine = carryBuffer.AsSpan(0, carryLen);
                        }
                        else
                        {
                            // Carry overflowed: skip this line
                            carryTooLong = true;
                            rawLine = [];
                        }
                    }
                    else
                    {
                        rawLine = [];
                    }
                }
                else
                {
                    // Line fully contained within the current chunk
                    rawLine = inChunkPart;
                }

                if (!rawLine.IsEmpty)
                {
                    // The Ethernet record tracking needs the line as the parser sees it, CR included.
                    ReadOnlySpan<byte> physicalLine = rawLine;

                    // Strip trailing \r (CRLF files)
                    if (rawLine[rawLine.Length - 1] == (byte)'\r')
                    {
                        rawLine = rawLine[..^1];
                    }

                    _ProcessDiskLine(
                        physicalLine,
                        AscTokenizerBytes.TrimAscii(rawLine),
                        lineStartOffset,
                        lineEnd: chunkStartOffset + i + 1,
                        ref pending,
                        ref header, ref headerDone, ref runningClock, ref relativeClock, ref index, discovered);
                }

                // Reset carry and advance line pointers
                carryLen = 0;
                carryTooLong = false;
                inChunkLineStart = i + 1;
                lineStartOffset = chunkStartOffset + i + 1;
            }

            // ── End of chunk reached without a final \n ────────────────────────────
            // Accumulate the tail of the chunk into carryBuffer for the next iteration.
            ReadOnlySpan<byte> tail = buffer.AsSpan(inChunkLineStart, bytesRead - inChunkLineStart);
            if (!tail.IsEmpty && !carryTooLong)
            {
                int needed = carryLen + tail.Length;
                if (needed <= carryBuffer.Length)
                {
                    tail.CopyTo(carryBuffer.AsSpan(carryLen));
                    carryLen = needed;
                }
                else
                {
                    // Partial line already exceeds the limit — mark as too long
                    carryTooLong = true;
                    carryLen = 0;
                }
            }

            chunkStartOffset += bytesRead;
        }

        // A file of header lines only never reached the data: the clock mode still comes from the header.
        index ??= new AscFrameIndex(header.TimestampFormat == "relative", trackReadWindows: true);
        index.Complete();
        return new AscSource(
            ReadOnlyMemory<byte>.Empty, isInMemory: false, guard, header, index, discovered, options.ErrorTolerance, options.FrameSlabSize, uiName);
    }

    /// <summary>An Ethernet frame of the disk scan whose record may still continue: its index and line start offset.</summary>
    private record struct PendingRecord(int Frame, long Start);

    /// <summary>
    /// Processes a single line from the disk scan pass. Updates the header state, or adds an index entry when the line
    /// produces a frame, and records the span the frame's read window must cover (<see cref="AscFrameIndex.RequireReadBytes"/>).
    /// </summary>
    /// <param name="physicalLine">The line without its LF, CR and blanks kept (what the Ethernet hex scan sees).</param>
    /// <param name="trimmed">The line without line terminator and surrounding blanks.</param>
    /// <param name="lineByteOffset">File offset of the line start.</param>
    /// <param name="lineEnd">File offset after the line's LF (the end of the file for a last line without LF).</param>
    /// <param name="pending">The Ethernet frame whose record is still open, or frame −1.</param>
    /// <param name="header">Header state.</param>
    /// <param name="headerDone">Whether the header ended.</param>
    /// <param name="runningClock">Running clock of the scan.</param>
    /// <param name="relativeClock">Whether the header says <c>timestamps relative</c>.</param>
    /// <param name="index">The index, created when the header ends.</param>
    /// <param name="discovered">Interfaces seen so far.</param>
    private static void _ProcessDiskLine(
        ReadOnlySpan<byte> physicalLine, ReadOnlySpan<byte> trimmed, long lineByteOffset, long lineEnd,
        ref PendingRecord pending,
        ref AscHeader header, ref bool headerDone,
        ref AscTimestamp runningClock, ref bool relativeClock,
        ref AscFrameIndex? index,
        HashSet<(AscBusType, int, LinkType)> discovered)
    {
        if (!headerDone)
        {
            if (!header.TryParseLine(trimmed))
            {
                headerDone = true;
                relativeClock = header.TimestampFormat == "relative";
                runningClock = _InitialClock(header);
                index = new AscFrameIndex(relativeClock, trackReadWindows: true);
                // Fall through: the first data line may produce a frame
            }
            else
            {
                return;
            }
        }

        // An open Ethernet record runs over blank and hex continuation lines. The first other line ends it, and the
        // window must hold that whole line, because the parser reads all of it to decide that the packet ended.
        if (pending.Frame >= 0 && AscEthernetParser.EndsPacket(physicalLine) == true)
        {
            index!.RequireReadBytes(pending.Frame, lineEnd - pending.Start);
            pending = new(-1, 0);
        }

        if (trimmed.IsEmpty)
        {
            return;
        }

        int frame = index!.Count;
        if (!_IndexFrameLine(trimmed, lineByteOffset, ref runningClock, relativeClock, index, discovered, out AscLineType lineType))
        {
            return;
        }

        // Read window of the new frame: its line through the LF. An Ethernet packet stays open for continuation lines;
        // one whose header does not parse from its own line gets the parser's full window, as before.
        index.RequireReadBytes(frame, lineEnd - lineByteOffset);
        if (lineType == AscLineType.EthernetPacket)
        {
            if (AscEthernetParser.HeaderFitsLine(trimmed, header.NumericBase))
            {
                pending = new(frame, lineByteOffset);
            }
            else
            {
                index.RequireReadBytes(frame, AscEthernetParser.MaxRecordBytes);
            }
        }
    }

    /// <summary>
    /// Indexes one frame-producing line and folds its timestamp into <paramref name="runningClock"/>.
    /// Non-frame lines return false; a timestamped one still moves the clock. Continuation hex is not a logging
    /// event, so it is not indexed.
    /// </summary>
    /// <returns>True when the line was added to <paramref name="index"/>.</returns>
    private static bool _IndexFrameLine(
        ReadOnlySpan<byte> trimmed,
        long location,
        ref AscTimestamp runningClock,
        bool relativeClock,
        AscFrameIndex index,
        HashSet<(AscBusType, int, LinkType)> discovered,
        out AscLineType lineType)
    {
        lineType = AscLineClassifier.Classify(trimmed);
        // Every timestamped event moves a relative clock, including statistics and status lines.
        bool folded = AscTimestamp.TryFold(ref runningClock, relativeClock, trimmed, out AscTimestamp clock);
        if (!_IsFrameProducingType(lineType))
        {
            return false;
        }

        // A line that is not a timestamp still gets an entry so the skip is visible at parse time.
        if (!folded)
        {
            clock = runningClock;
        }

        (AscBusType busType, LinkType linkType) = _ResolveInterface(trimmed, lineType);
        int channel = _PeekChannel(trimmed, lineType);
        if (channel >= 0)
        {
            discovered.Add((busType, channel, linkType));
        }

        // Only the line offset (and a clock the line cannot reproduce); the type is re-classified from the line.
        index.Add(location, clock, folded);
        return true;
    }

    #endregion

    #region Frame Building

    /// <summary>
    /// Builds a frame from the indexed line at <paramref name="frameIndex"/>.
    /// The line is re-derived at its stored byte offset — from the in-memory buffer or re-read from disk — with the
    /// scan's split rule, classified again, and parsed; the clock comes from <see cref="AscFrameIndex.TryGetClock"/>.
    /// Thread-safe: uses only read-only state after <see cref="Start"/> has returned.
    /// </summary>
    /// <param name="frameIndex">Index into <c>_FrameIndex</c>.</param>
    /// <param name="reportErrors">When <c>true</c>, parse failures route through
    /// <see cref="_HandleSkip"/> (sequential <see cref="NextFrame"/> path). When
    /// <c>false</c>, failures are silent (random-access <see cref="FrameById"/>
    /// path) and the caller observes them only via the <c>null</c> return.</param>
    private Frame? _BuildFrame(int frameIndex, bool reportErrors)
    {
        // One read: a concurrent Dispose nulls the field, and every helper below must see the same registry or none.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null || !_FrameIndex.TryGetLineOffset(frameIndex, out long lineOffset))
        {
            return null;
        }

        if (_IsInMemory)
        {
            // Re-derive the line exactly as the scan saw it (cut at LF, drop CR, trim), then classify it again.
            ReadOnlySpan<byte> data = _InMemoryData.Span;
            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(_InMemoryLine(data, lineOffset));
            AscLineType lineType = AscLineClassifier.Classify(trimmed);

            // Ethernet may continue on the next physical lines, so the window includes the bytes after this line.
            ReadOnlySpan<byte> span = lineType == AscLineType.EthernetPacket
                ? _InMemoryWindow(data, lineOffset)
                : trimmed;
            return _ParseIndexedLine(registry, span, trimmed, lineType, lineOffset, frameIndex, reportErrors);
        }

        // Disk path: one positional read of the frame's read window, which the index sized at scan time to hold the
        // whole record (the line, and for Ethernet its continuation lines plus the line that ends the packet). While
        // this thread walks the ids in order, the window comes from its read-ahead buffer instead of its own read.
        int window = _FrameIndex.ReadWindow(frameIndex);
        CaptureFileGuard guard = _Guard!;
        if (AscReadAhead.TryRead(_ReadAheadOwner, frameIndex, lineOffset, window, guard, out ReadOnlySpan<byte> buffered))
        {
            return _BuildFromDiskRecord(registry, buffered, window, lineOffset, frameIndex, reportErrors);
        }

        // Random access: exactly the window, parsed in place from a rental; the parsers copy the frame bytes out.
        byte[] rented = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            int read = ReadAt(guard, lineOffset, rented.AsSpan(0, window));
            return _BuildFromDiskRecord(registry, rented.AsSpan(0, read), window, lineOffset, frameIndex, reportErrors);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Parses the frame whose record starts <paramref name="data"/>: the first <paramref name="window"/> bytes at the
    /// line offset, fewer only at the end of the file. The line is cut at its LF and classified again; an Ethernet
    /// packet gets the data from the line start, capped at <see cref="AscEthernetParser.MaxRecordBytes"/> like the
    /// in-memory backend, because its hex may continue on the following lines.
    /// </summary>
    private Frame? _BuildFromDiskRecord(
        FrameInterfaceRegistry registry, ReadOnlySpan<byte> data, int window, long lineOffset, int frameIndex, bool reportErrors)
    {
        // Nothing at the offset: the file ended before the indexed line (it cannot shrink while the guard holds it).
        if (data.IsEmpty)
        {
            if (reportErrors)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = lineOffset,
                    Kind = FrameReadErrorKind.TruncatedStream,
                    Message = Invariant($"Could not read line at offset {lineOffset} for frame {frameIndex}."),
                });
            }

            return null;
        }

        // The window holds the LF of every indexed line. A full window without one means the bytes changed after the
        // scan (only possible on Linux/macOS before the change check latches): read the longest line, as before.
        int newline = data.IndexOf((byte)'\n');
        if (newline < 0 && data.Length == window && window < AscSourceOptions.MaxLineLength)
        {
            return _BuildFromLongWindow(registry, lineOffset, frameIndex, reportErrors);
        }

        int end = newline >= 0 ? newline : data.Length;
        ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(data[..AscLineReader.LineLength(data, 0, end)]);
        AscLineType lineType = AscLineClassifier.Classify(trimmed);
        if (lineType != AscLineType.EthernetPacket)
        {
            return _ParseIndexedLine(registry, trimmed, trimmed, lineType, lineOffset, frameIndex, reportErrors);
        }

        // Ethernet continues on the next physical lines. The clock comes from the first line only, which the data starts with.
        ReadOnlySpan<byte> record = data.Length > AscEthernetParser.MaxRecordBytes
            ? data[..AscEthernetParser.MaxRecordBytes]
            : data;
        return _ParseIndexedLine(registry, record, trimmed, lineType, lineOffset, frameIndex, reportErrors);
    }

    /// <summary>Cold path of <see cref="_BuildFromDiskRecord"/>: re-reads the record with a <see cref="AscSourceOptions.MaxLineLength"/> window.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame? _BuildFromLongWindow(FrameInterfaceRegistry registry, long lineOffset, int frameIndex, bool reportErrors)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(AscSourceOptions.MaxLineLength);
        try
        {
            int read = ReadAt(_Guard!, lineOffset, rented.AsSpan(0, AscSourceOptions.MaxLineLength));
            return _BuildFromDiskRecord(
                registry, rented.AsSpan(0, read), AscSourceOptions.MaxLineLength, lineOffset, frameIndex, reportErrors);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Resolves the clock of an indexed line and parses it. A line that is no longer a frame line, or whose clock can
    /// no longer be derived, fails like any parse failure (reported in the sequential path, null for random access).
    /// </summary>
    private Frame? _ParseIndexedLine(
        FrameInterfaceRegistry registry, ReadOnlySpan<byte> span, ReadOnlySpan<byte> trimmedLine, AscLineType lineType,
        long location, int frameIndex, bool reportErrors)
    {
        if (!_IsFrameProducingType(lineType) || !_FrameIndex.TryGetClock(frameIndex, trimmedLine, out AscTimestamp clock))
        {
            if (reportErrors)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = location,
                    Kind = FrameReadErrorKind.Other,
                    Message = Invariant($"Indexed line at offset {location} is no longer a frame line (frame {frameIndex})."),
                });
            }

            return null;
        }

        return _ParseAndBuildFrame(registry, span, lineType, clock, location, frameIndex, reportErrors);
    }

    /// <summary>The in-memory line at <paramref name="offset"/>: up to the next LF, without one trailing CR.</summary>
    private static ReadOnlySpan<byte> _InMemoryLine(ReadOnlySpan<byte> data, long offset)
    {
        int start = (int)offset;
        if ((uint)start > (uint)data.Length)
        {
            return [];
        }

        int newline = data[start..].IndexOf((byte)'\n');
        int end = newline >= 0 ? start + newline : data.Length;
        return data.Slice(start, AscLineReader.LineLength(data, start, end));
    }

    /// <summary>
    /// Bytes from the start of an in-memory line through the following file bytes, capped so a wrapped
    /// Ethernet packet fits. The slice includes newlines, which the Ethernet parser treats as continuation.
    /// </summary>
    private static ReadOnlySpan<byte> _InMemoryWindow(ReadOnlySpan<byte> data, long offset)
    {
        int start = (int)offset;
        int available = data.Length - start;
        int take = available < AscEthernetParser.MaxRecordBytes
            ? available
            : AscEthernetParser.MaxRecordBytes;
        return data.Slice(start, take);
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/> bytes of the guarded file starting at <paramref name="byteOffset"/>.
    /// Returns the number of bytes placed in the span. Zero at or past EOF. The length at open bounds the read: the
    /// guard keeps the file unchanged (Windows) or reports any change (Linux/macOS). Thread-safe (positional reads).
    /// </summary>
    internal static int ReadAt(CaptureFileGuard guard, long byteOffset, Span<byte> destination)
    {
        long fileLength = guard.Length;
        if (byteOffset >= fileLength || destination.IsEmpty)
        {
            return 0;
        }

        long available = fileLength - byteOffset;
        int toRead = available < destination.Length ? (int)available : destination.Length;

        // RandomAccess.Read may return fewer bytes than asked; read until the window is full or the file ends.
        int total = 0;
        while (total < toRead)
        {
            int read = RandomAccess.Read(guard.Handle, destination[total..toRead], byteOffset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Parses a classified ASC line from UTF-8 bytes and constructs a <see cref="Frame"/>.
    /// Used by the disk-backed path and for in-memory lines (zero-copy byte span).
    /// </summary>
    /// <param name="registry">The registry snapshot of this call (see <see cref="_BuildFrame"/>); never null.</param>
    /// <param name="span">Trimmed UTF-8 bytes for the line to parse (a multi-line window for Ethernet).</param>
    /// <param name="lineType">The line type, classified from the line exactly as the scan classified it.</param>
    /// <param name="clock">The frame clock (absolute line time or accumulated relative time).</param>
    /// <param name="location">Byte offset of the line (error reporting).</param>
    /// <param name="frameIndex">Zero-based index of the frame within the file index (used for error reporting).</param>
    /// <param name="reportErrors">
    /// When <c>true</c>, parse failures route through <see cref="_HandleSkip"/>; when
    /// <c>false</c>, failures yield a silent <c>null</c> (random-access path).
    /// </param>
    private Frame? _ParseAndBuildFrame(
        FrameInterfaceRegistry registry, ReadOnlySpan<byte> span, AscLineType lineType, AscTimestamp clock,
        long location, int frameIndex, bool reportErrors)
    {
        bool parsed;
        int channel;
        ReadOnlyMemory<byte> frameData;
        AscBusType busType;
        LinkType linkType;

        // Sequential frames (reportErrors) go into the frame slab when slabs are on; FrameById always builds its own
        // arrays. CAN XL and FlexRay always get their own array.
        FrameSlabAllocator? slabs = reportErrors ? _Slabs : null;

        switch (lineType)
        {
            case AscLineType.CanMessage:
                parsed = AscCanParser.TryParse(span, _Header.NumericBase, slabs,
                    out _, out channel, out frameData);
                busType = AscBusType.Can;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanFdMessage:
                parsed = AscCanFdParser.TryParse(span, _Header.NumericBase, slabs,
                    out _, out channel, out frameData);
                busType = AscBusType.CanFd;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanXlMessage:
                parsed = AscCanXlParser.TryParse(span,
                    out _, out channel, out busType, out byte[] canXlFrame);
                frameData = canXlFrame;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanErrorFrame:
            case AscLineType.CanOverloadFrame:
                parsed = AscCanErrorParser.TryParse(span, slabs, out _, out channel, out frameData);
                busType = AscBusType.Can;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.LinMessage:
                parsed = AscLinParser.TryParse(span, _Header.NumericBase, slabs,
                    out _, out channel, out frameData);
                busType = AscBusType.Lin;
                linkType = LinkType.Lin;
                break;

            case AscLineType.FlexRayMessage:
                parsed = AscFlexRayParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out byte[] flexRayFrame);
                frameData = flexRayFrame;
                busType = AscBusType.FlexRay;
                linkType = LinkType.Flexray;
                break;

            case AscLineType.EthernetPacket:
                parsed = AscEthernetParser.TryParse(span, _Header.NumericBase, slabs,
                    out _, out channel, out frameData);
                busType = AscBusType.Ethernet;
                linkType = LinkType.Ethernet;
                break;

            default:
                return null;
        }

        if (!parsed
            || !AscTimestamp.TryToUnixNanos(_Header.StartUnixNanos, clock, out long absoluteNanos))
        {
            if (reportErrors)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = location,
                    Kind = FrameReadErrorKind.Other,
                    Message = Invariant($"Failed to parse {lineType} (frame {frameIndex})."),
                });
            }
            return null;
        }

        // Always go through _RegisterInterface: it performs the locked cache lookup and
        // returns the existing ID without registering a duplicate. A bare _InterfaceMap
        // read here would race with concurrent FrameById writers (Dictionary is not safe
        // for a reader during a writer's resize).
        FrameInterfaceId interfaceId = _RegisterInterface(registry, busType, channel, linkType);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(absoluteNanos),
            frameData,
            linkType,
            interfaceId,
            registry);

        if (result.IsSuccess)
        {
            if (reportErrors)
            {
                Interlocked.Increment(ref _ReadFrameCount);
            }
            return result.Value;
        }

        if (reportErrors)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameIndex,
                FileOffset = location,
                Kind = FrameReadErrorKind.Other,
                Message = Invariant($"Frame creation failed for {lineType} (frame {frameIndex})."),
            });
        }

        return null;
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Records a skipped frame, updates statistics, and raises <see cref="FrameSkipped"/>.
    /// In strict mode, sets the abort flag so <see cref="NextFrame"/> stops iteration.
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

    #endregion

    #region Interface Registration

    /// <summary>
    /// Registers a single interface with the registry and caches the result.
    /// Called from <see cref="Start"/> (pre-registration) or lazily from
    /// <see cref="_ParseAndBuildFrame"/> for channels not discovered during scanning.
    /// Thread-safe: both reads and writes of <see cref="_InterfaceMap"/> are guarded by
    /// <see cref="_InterfaceLock"/>. Locking the read path is required because
    /// <see cref="Dictionary{TKey,TValue}"/> is not safe for a concurrent reader during
    /// any mutation — a concurrent resize tears the buckets array.
    /// </summary>
    private FrameInterfaceId _RegisterInterface(FrameInterfaceRegistry registry,
        AscBusType busType, int channel, LinkType linkType)
    {
        (AscBusType, int) key = (busType, channel);

        // All accesses (read and write) must be locked: Dictionary is not safe for
        // a concurrent reader during a writer's resize. The lock is uncontended in
        // the common case so the cost is a single CAS on the fast path.
        lock (_InterfaceLock)
        {
            if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existing))
            {
                return existing;
            }

            string displayName = busType.ToDisplayName();
            string interfaceName = $"{displayName} {channel}";

            FrameInterfaceId id = registry.Register(
                _SourceId, interfaceName, null, linkType,
                new Dictionary<string, object>
                {
                    [AscInterfacePropertyKeys.Channel] = (long)channel,
                    [AscInterfacePropertyKeys.BusType] = displayName,
                });

            _InterfaceMap[key] = id;
            return id;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Returns <c>true</c> when the given line type corresponds to a parseable frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsFrameProducingType(AscLineType lineType) => lineType switch
    {
        AscLineType.CanMessage or AscLineType.CanFdMessage or AscLineType.CanXlMessage
            or AscLineType.CanErrorFrame or AscLineType.CanOverloadFrame
            or AscLineType.LinMessage or AscLineType.FlexRayMessage
            or AscLineType.EthernetPacket => true,
        _ => false,
    };

    /// <summary>
    /// Maps a frame-producing line type to its bus type and link type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (AscBusType BusType, LinkType LinkType) _LineTypeToInterface(AscLineType lineType) =>
        lineType switch
        {
            AscLineType.CanMessage or AscLineType.CanErrorFrame or AscLineType.CanOverloadFrame
                => (AscBusType.Can, LinkType.CanSocketcan),
            AscLineType.CanFdMessage => (AscBusType.CanFd, LinkType.CanSocketcan),
            AscLineType.CanXlMessage => (AscBusType.CanXl, LinkType.CanSocketcan),
            AscLineType.LinMessage => (AscBusType.Lin, LinkType.Lin),
            AscLineType.FlexRayMessage => (AscBusType.FlexRay, LinkType.Flexray),
            AscLineType.EthernetPacket => (AscBusType.Ethernet, LinkType.Ethernet),
            _ => (AscBusType.Unknown, LinkType.Null),
        };

    /// <summary>
    /// CAN XL rows share one line type. The format token selects CAN, CAN FD, or CAN XL.
    /// </summary>
    private static (AscBusType BusType, LinkType LinkType) _ResolveInterface(
        ReadOnlySpan<byte> line, AscLineType lineType)
    {
        if (lineType == AscLineType.CanXlMessage
            && AscCanXlParser.TryPeekBusType(line, out AscBusType busType))
        {
            return (busType, LinkType.CanSocketcan);
        }

        return _LineTypeToInterface(lineType);
    }

    /// <summary>
    /// Quickly extracts the channel number from a trimmed ASC line for interface pre-discovery.
    /// Returns -1 if the channel cannot be determined cheaply.
    /// A full parse via the individual parsers is done again in <see cref="_ParseAndBuildFrame"/>.
    /// Byte-span variant for the disk scan path (avoids UTF-16 conversion).
    /// </summary>
    private static int _PeekChannel(ReadOnlySpan<byte> line, AscLineType lineType)
    {
        AscTokenizerBytes tok = new(line);

        // Token 0: timestamp — skip
        if (!tok.TryNextToken(out _))
        {
            return -1;
        }

        switch (lineType)
        {
            case AscLineType.CanMessage:
                if (tok.TryNextToken(out ReadOnlySpan<byte> ch)
                    && System.Buffers.Text.Utf8Parser.TryParse(ch, out int canCh, out _))
                {
                    return canCh;
                }

                break;

            case AscLineType.CanFdMessage:
            case AscLineType.CanXlMessage:
                if (tok.TryNextToken(out _) && tok.TryNextToken(out ReadOnlySpan<byte> fdCh)
                    && System.Buffers.Text.Utf8Parser.TryParse(fdCh, out int canFdCh, out _))
                {
                    return canFdCh;
                }

                break;

            case AscLineType.CanErrorFrame:
            case AscLineType.CanOverloadFrame:
                if (tok.TryNextToken(out ReadOnlySpan<byte> errCh)
                    && System.Buffers.Text.Utf8Parser.TryParse(errCh, out int errChannel, out _))
                {
                    return errChannel;
                }

                break;

            case AscLineType.LinMessage:
                if (tok.TryNextToken(out ReadOnlySpan<byte> linToken)
                    && linToken.Length >= 2
                    && (linToken[0] == (byte)'L' || linToken[0] == (byte)'l'))
                {
                    if (linToken.Length == 2 && (linToken[1] == (byte)'i' || linToken[1] == (byte)'I'))
                    {
                        return 1;
                    }

                    if (System.Buffers.Text.Utf8Parser.TryParse(linToken[1..], out int linCh, out _))
                    {
                        return linCh;
                    }
                }

                break;

            case AscLineType.FlexRayMessage:
                if (tok.TryNextToken(out _) && tok.TryNextToken(out ReadOnlySpan<byte> frKind))
                {
                    bool newFormat = frKind.Length == 4
                        && (AscLineClassifier.StartsWithAsciiIgnoreCase(frKind, "RMSG"u8)
                            || AscLineClassifier.StartsWithAsciiIgnoreCase(frKind, "PDU"u8));
                    if (newFormat)
                    {
                        // cluster, client, then the application channel
                        if (tok.TryNextToken(out _)
                            && tok.TryNextToken(out _)
                            && tok.TryNextToken(out ReadOnlySpan<byte> appChannel)
                            && System.Buffers.Text.Utf8Parser.TryParse(appChannel, out int newCh, out _))
                        {
                            return newCh;
                        }
                    }
                    else if (System.Buffers.Text.Utf8Parser.TryParse(frKind, out int flexCh, out _))
                    {
                        return flexCh;
                    }
                }

                break;

            case AscLineType.EthernetPacket:
                if (tok.TryNextToken(out _) && tok.TryNextToken(out ReadOnlySpan<byte> ethCh)
                    && System.Buffers.Text.Utf8Parser.TryParse(ethCh, out int ethChannel, out _))
                {
                    return ethChannel;
                }

                break;
        }

        return -1;
    }

    /// <summary>
    /// Relative split files start at the previous file's last absolute timestamp.
    /// Absolute files start at zero because each line replaces the clock.
    /// </summary>
    private static AscTimestamp _InitialClock(AscHeader header)
    {
        if (header.TimestampFormat == "relative" && header.PreviousLogAbsolute is AscTimestamp seed)
        {
            return seed;
        }

        return default;
    }

    #endregion
}
