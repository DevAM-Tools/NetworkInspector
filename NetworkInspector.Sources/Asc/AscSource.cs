// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Random-access frame source for Vector ASC (ASCII trace) files.
/// Implements <see cref="IRandomAccessFrameSource"/> and <see cref="IErrorTolerantFrameSource"/>.
/// <para>
/// On open, the file is either fully loaded into memory (if the file size is within
/// <see cref="AscSourceOptions.PreloadBudget"/>) or kept on disk using a buffered
/// disk-backend. In both cases the entire file is scanned once to build a frame index.
/// </para>
/// <para>
/// All frame interfaces discovered during scanning are pre-registered in
/// <see cref="Start"/>, so <see cref="FrameById"/> requires no locking and is safe
/// to call from multiple threads concurrently.
/// </para>
/// </summary>
public sealed class AscSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>
    /// In-memory backend: all lines stored as zero-copy slices into the single backing buffer.
    /// Each <see cref="ReadOnlyMemory{T}"/> points directly into the buffer from
    /// <see cref="File.ReadAllBytes"/> / <see cref="Encoding.Latin1"/>,
    /// so no per-line copy is needed. Null when using the disk backend.
    /// </summary>
    private readonly ReadOnlyMemory<byte>[]? _InMemoryLines;

    /// <summary>
    /// Disk backend: path of the ASC file for re-reading individual lines.
    /// Null when using the in-memory backend.
    /// </summary>
    private readonly string? _DiskPath;
    /// <summary>
    /// Long-lived <see cref="SafeFileHandle"/> for the disk backend. Opened once at
    /// construction and reused across <see cref="FrameById"/> / <see cref="NextFrame"/>
    /// calls, since <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> is
    /// thread-safe and stateless. Replaces the previous open-per-call <see cref="FileStream"/>
    /// which incurred a 4 KiB-buffered open + close on every random-access call.
    /// </summary>
    private readonly SafeFileHandle? _DiskHandle;

    /// <summary>Parsed ASC file header.</summary>
    private readonly AscHeader _Header;

    /// <summary>Index of frame-producing lines and their metadata.</summary>
    private readonly AscFrameIndexEntry[] _FrameIndex;

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    // ── Interface registration (pre-populated in Start(), read-only afterwards) ──
    private FrameSourceId _SourceId;
    private FrameInterfaceRegistry? _Registry;

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
        ReadOnlyMemory<byte>[]? inMemoryLines,
        string? diskPath,
        AscHeader header,
        AscFrameIndexEntry[] frameIndex,
        HashSet<(AscBusType, int, LinkType)> discoveredInterfaces,
        ErrorToleranceMode errorTolerance,
        string uiName)
    {
        _InMemoryLines = inMemoryLines;
        _DiskPath = diskPath;
        if (diskPath is not null)
        {
            _DiskHandle = File.OpenHandle(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.RandomAccess);
        }
        _Header = header;
        _FrameIndex = frameIndex;
        _DiscoveredInterfaces = discoveredInterfaces;
        UiName = uiName;
        ErrorTolerance = errorTolerance;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string? Description => null;

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _FrameIndex.Length;

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
    /// larger files use a disk-based backend with a buffered re-read per frame access.
    /// </summary>
    /// <param name="path">Path to the ASC file.</param>
    /// <param name="options">Source configuration options. If null, defaults are used.</param>
    /// <returns>A new AscSource ready to be started.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static AscSource Open(string path, AscSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new();
        string uiName = options.UiName ?? Path.GetFileName(path);

        FileInfo fileInfo = new(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("ASC file not found.", path);
        }

        long fileSize = fileInfo.Length;

        bool useInMemory = options.PreloadBudget.HasValue && fileSize <= options.PreloadBudget.Value;
        if (useInMemory)
        {
            // Small file: load all content into memory; split into zero-copy slices for zero-seek random access
            byte[] fileBytes = File.ReadAllBytes(path);
            ReadOnlyMemory<byte>[] lines = _SplitIntoLines(fileBytes);
            return _BuildFromInMemoryLines(lines, options, uiName);
        }
        else
        {
            // Large file: scan once to build index (storing byte offsets), re-read on demand
            return _BuildFromDisk(path, options, uiName);
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
        ReadOnlyMemory<byte>[] lines = _SplitIntoLines(bytes);
        return _BuildFromInMemoryLines(lines, options, uiName);
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
        // data.ToArray() allocates a copy so the backing array lifetime is owned here;
        // the resulting slices keep it alive via ReadOnlyMemory<byte> references.
        byte[] backing = data.ToArray();
        ReadOnlyMemory<byte>[] lines = _SplitIntoLines(backing);
        return _BuildFromInMemoryLines(lines, options, uiName);
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
        while (_NextFrameIndex < _FrameIndex.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameIndex = _NextFrameIndex++;
            Frame? frame = _BuildFrame(frameIndex, reportErrors: true);
            if (frame.HasValue)
            {
                return frame.Value;
            }

            if (_Aborted)
            {
                return null;
            }
        }

        return null;
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
        if (index < 0 || index >= _FrameIndex.Length)
        {
            return null;
        }

        return _BuildFrame(index, reportErrors: false);
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

        _Registry = null;
        // GC.SuppressFinalize is called before the handle disposal so it executes
        // even if _DiskHandle.Dispose() throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        _DiskHandle?.Dispose();
    }

    #endregion

    #region Index Building — In-Memory Backend

    /// <summary>
    /// Builds the source from a pre-loaded slice array.
    /// Each element is a zero-copy window into the original backing buffer.
    /// </summary>
    private static AscSource _BuildFromInMemoryLines(ReadOnlyMemory<byte>[] lines, AscSourceOptions options, string uiName)
    {
        AscHeader header = new()
        {
            TimestampTimeZone = options.TimestampTimeZone
        };
        int dataStartLine = 0;

        // Phase 1: parse header
        for (int i = 0; i < lines.Length; i++)
        {
            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(lines[i].Span);
            if (!header.TryParseLine(trimmed))
            {
                dataStartLine = i;
                break;
            }

            dataStartLine = i + 1;
        }

        // Phase 2: classify and index frame-producing lines.
        // The clock is folded here, in file order, so later random access does not re-walk the file.
        List<AscFrameIndexEntry> entries = [];
        HashSet<(AscBusType, int, LinkType)> discovered = [];
        AscTimestamp runningClock = _InitialClock(header);
        bool relativeClock = header.TimestampFormat == "relative";

        for (int i = dataStartLine; i < lines.Length; i++)
        {
            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(lines[i].Span);
            if (trimmed.IsEmpty)
            {
                continue;
            }

            _IndexFrameLine(trimmed, i, ref runningClock, relativeClock, entries, discovered);
        }

        return new AscSource(lines, null, header, [.. entries], discovered, options.ErrorTolerance, uiName);
    }

    #endregion

    #region Index Building — Disk Backend

    /// <summary>
    /// Builds the source for a large file: scans the raw bytes once with a 4 MiB buffer
    /// to build an index of line byte offsets, without keeping all lines in memory.
    /// The byte offset of each frame-producing line is stored so it can be re-read on demand.
    /// </summary>
    private static AscSource _BuildFromDisk(string path, AscSourceOptions options, string uiName)
    {
        AscHeader header = new()
        {
            TimestampTimeZone = options.TimestampTimeZone
        };
        bool headerDone = false;
        List<AscFrameIndexEntry> entries = [];
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

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: AscSourceOptions.DiskReadBufferSize, useAsync: false);

        long chunkStartOffset = 0;      // file offset of buffer[0] in the current read
        long lineStartOffset = 0;       // file offset of the first byte of the current line

        while (true)
        {
            int bytesRead = fs.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                // EOF: flush any remaining carry-over bytes as the last (unterminated) line
                if (carryLen > 0 && !carryTooLong)
                {
                    _ProcessDiskLine(
                        AscTokenizerBytes.TrimAscii(carryBuffer.AsSpan(0, carryLen)),
                        lineStartOffset,
                        ref header, ref headerDone, ref runningClock, ref relativeClock, entries, discovered);
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
                    // Strip trailing \r (CRLF files)
                    if (rawLine[rawLine.Length - 1] == (byte)'\r')
                    {
                        rawLine = rawLine[..^1];
                    }

                    _ProcessDiskLine(
                        AscTokenizerBytes.TrimAscii(rawLine),
                        lineStartOffset,
                        ref header, ref headerDone, ref runningClock, ref relativeClock, entries, discovered);
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

        return new AscSource(null, path, header, [.. entries], discovered, options.ErrorTolerance, uiName);
    }

    /// <summary>
    /// Processes a single trimmed line (as raw bytes) from the disk scan pass.
    /// Updates the header state, or adds an index entry when the line produces a frame.
    /// </summary>
    private static void _ProcessDiskLine(
        ReadOnlySpan<byte> trimmed, long lineByteOffset,
        ref AscHeader header, ref bool headerDone,
        ref AscTimestamp runningClock, ref bool relativeClock,
        List<AscFrameIndexEntry> entries,
        HashSet<(AscBusType, int, LinkType)> discovered)
    {
        if (!headerDone)
        {
            if (!header.TryParseLine(trimmed))
            {
                headerDone = true;
                relativeClock = header.TimestampFormat == "relative";
                runningClock = _InitialClock(header);
                // Fall through: the first data line may produce a frame
            }
            else
            {
                return;
            }
        }

        if (trimmed.IsEmpty)
        {
            return;
        }

        _IndexFrameLine(trimmed, lineByteOffset, ref runningClock, relativeClock, entries, discovered);
    }

    /// <summary>
    /// Indexes one frame-producing line and folds its timestamp into <paramref name="runningClock"/>.
    /// Non-frame lines return without moving the clock. Continuation hex is not a logging event, so it is not indexed.
    /// </summary>
    private static void _IndexFrameLine(
        ReadOnlySpan<byte> trimmed,
        long location,
        ref AscTimestamp runningClock,
        bool relativeClock,
        List<AscFrameIndexEntry> entries,
        HashSet<(AscBusType, int, LinkType)> discovered)
    {
        AscLineType lineType = AscLineClassifier.Classify(trimmed);
        // Every timestamped event moves a relative clock, including statistics and status lines.
        bool folded = AscTimestamp.TryFold(ref runningClock, relativeClock, trimmed, out AscTimestamp clock);
        if (!_IsFrameProducingType(lineType))
        {
            return;
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

        ArrayIndexIdRange.ThrowIfInvalidNextIndex(entries.Count, "frame");
        entries.Add(new AscFrameIndexEntry(location, lineType, clock));
    }

    #endregion

    #region Frame Building

    /// <summary>
    /// Builds a frame from the indexed entry at <paramref name="frameIndex"/>.
    /// For the in-memory backend the line string is taken directly from <c>_InMemoryLines</c>.
    /// For the disk backend the line is re-read from disk using the stored byte offset.
    /// Thread-safe: uses only read-only state after <see cref="Start"/> has returned.
    /// </summary>
    /// <param name="frameIndex">Index into <c>_FrameIndex</c>.</param>
    /// <param name="reportErrors">When <c>true</c>, parse failures route through
    /// <see cref="_HandleSkip"/> (sequential <see cref="NextFrame"/> path). When
    /// <c>false</c>, failures are silent (random-access <see cref="FrameById"/>
    /// path) and the caller observes them only via the <c>null</c> return.</param>
    private Frame? _BuildFrame(int frameIndex, bool reportErrors)
    {
        AscFrameIndexEntry entry = _FrameIndex[frameIndex];

        if (_InMemoryLines is not null)
        {
            // Ethernet may continue on the next physical lines, so the window includes the bytes after this line.
            ReadOnlySpan<byte> span = entry.LineType == AscLineType.EthernetPacket
                ? _InMemoryWindow((int)entry.Location)
                : AscTokenizerBytes.TrimAscii(_InMemoryLines[(int)entry.Location].Span);
            return _ParseAndBuildFrame(span, entry, frameIndex, reportErrors);
        }
        else if (entry.LineType == AscLineType.EthernetPacket)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(AscEthernetParser.MaxRecordBytes);
            try
            {
                int read = _ReadWindowFromDisk(entry.Location, rented.AsSpan(0, AscEthernetParser.MaxRecordBytes));
                return _ParseAndBuildFrame(rented.AsSpan(0, read), entry, frameIndex, reportErrors);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            // Disk path: seek to stored byte offset and read one line as raw bytes
            byte[]? lineBytes = _ReadLineBytesFromDisk(entry.Location);
            if (lineBytes is null)
            {
                if (reportErrors)
                {
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = frameIndex,
                        FileOffset = entry.Location,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = $"Could not read line at offset {entry.Location} for frame {frameIndex}.",
                    });
                }
                return null;
            }

            return _ParseAndBuildFrame(
                AscTokenizerBytes.TrimAscii(lineBytes.AsSpan()),
                entry, frameIndex, reportErrors);
        }
    }

    /// <summary>
    /// Bytes from the start of an in-memory line through the following file bytes, capped so a wrapped
    /// Ethernet packet fits. The slice includes newlines, which the Ethernet parser treats as continuation.
    /// </summary>
    private ReadOnlySpan<byte> _InMemoryWindow(int lineIndex)
    {
        ReadOnlyMemory<byte> line = _InMemoryLines![lineIndex];
        if (MemoryMarshal.TryGetArray(line, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            int available = segment.Array.Length - segment.Offset;
            int take = available < AscEthernetParser.MaxRecordBytes
                ? available
                : AscEthernetParser.MaxRecordBytes;
            return segment.Array.AsSpan(segment.Offset, take);
        }

        return line.Span;
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/> bytes starting at <paramref name="byteOffset"/>.
    /// Returns the number of bytes placed in the span. Zero at or past EOF.
    /// </summary>
    private int _ReadWindowFromDisk(long byteOffset, Span<byte> destination)
    {
        SafeFileHandle handle = _DiskHandle!;
        long fileLength = RandomAccess.GetLength(handle);
        if (byteOffset >= fileLength || destination.IsEmpty)
        {
            return 0;
        }

        long available = fileLength - byteOffset;
        int toRead = available < destination.Length ? (int)available : destination.Length;
        return RandomAccess.Read(handle, destination[..toRead], byteOffset);
    }

    /// <summary>
    /// Re-reads a single line from disk at the given byte offset as a raw byte array.
    /// Uses the long-lived <see cref="_DiskHandle"/> together with the stateless,
    /// thread-safe <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>,
    /// so concurrent random-access reads do not contend on a shared seek position.
    /// Returns <c>null</c> when the offset is beyond EOF.
    /// </summary>
    private byte[]? _ReadLineBytesFromDisk(long byteOffset)
    {
        SafeFileHandle handle = _DiskHandle!;
        long fileLength = RandomAccess.GetLength(handle);
        if (byteOffset >= fileLength)
        {
            return null;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(AscSourceOptions.MaxLineLength);
        try
        {
            int read = RandomAccess.Read(handle, rented.AsSpan(0, AscSourceOptions.MaxLineLength), byteOffset);

            int filled = 0;
            while (filled < read && rented[filled] != (byte)'\n')
            {
                filled++;
            }

            if (filled == 0 && read == 0)
            {
                return null;
            }

            if (filled > 0 && rented[filled - 1] == (byte)'\r')
            {
                filled--;
            }

            byte[] line = new byte[filled];
            rented.AsSpan(0, filled).CopyTo(line);
            return line;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Parses a classified ASC line from UTF-8 bytes and constructs a <see cref="Frame"/>.
    /// Used by the disk-backed path and for in-memory lines (zero-copy byte span).
    /// </summary>
    /// <param name="span">Trimmed UTF-8 bytes for the line to parse.</param>
    /// <param name="entry">Frame index entry that describes the line type and location.</param>
    /// <param name="frameIndex">Zero-based index of the frame within the file index (used for error reporting).</param>
    /// <param name="reportErrors">
    /// When <c>true</c>, parse failures route through <see cref="_HandleSkip"/>; when
    /// <c>false</c>, failures yield a silent <c>null</c> (random-access path).
    /// </param>
    private Frame? _ParseAndBuildFrame(ReadOnlySpan<byte> span, AscFrameIndexEntry entry, int frameIndex, bool reportErrors)
    {
        bool parsed;
        int channel;
        byte[] frameData;
        AscBusType busType;
        LinkType linkType;

        switch (entry.LineType)
        {
            case AscLineType.CanMessage:
                parsed = AscCanParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out frameData);
                busType = AscBusType.Can;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanFdMessage:
                parsed = AscCanFdParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out frameData);
                busType = AscBusType.CanFd;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanXlMessage:
                parsed = AscCanXlParser.TryParse(span,
                    out _, out channel, out busType, out frameData);
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanErrorFrame:
            case AscLineType.CanOverloadFrame:
                parsed = AscCanErrorParser.TryParse(span, out _, out channel, out frameData);
                busType = AscBusType.Can;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.LinMessage:
                parsed = AscLinParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out frameData);
                busType = AscBusType.Lin;
                linkType = LinkType.Lin;
                break;

            case AscLineType.FlexRayMessage:
                parsed = AscFlexRayParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out frameData);
                busType = AscBusType.FlexRay;
                linkType = LinkType.Flexray;
                break;

            case AscLineType.EthernetPacket:
                parsed = AscEthernetParser.TryParse(span, _Header.NumericBase,
                    out _, out channel, out frameData);
                busType = AscBusType.Ethernet;
                linkType = LinkType.Ethernet;
                break;

            default:
                return null;
        }

        if (!parsed
            || !AscTimestamp.TryToUnixNanos(_Header.StartUnixNanos, entry.ClockOffset, out long absoluteNanos))
        {
            if (reportErrors)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = entry.Location,
                    Kind = FrameReadErrorKind.Other,
                    Message = $"Failed to parse {entry.LineType} (frame {frameIndex}).",
                });
            }
            return null;
        }

        // Always go through _RegisterInterface: it performs the locked cache lookup and
        // returns the existing ID without registering a duplicate. A bare _InterfaceMap
        // read here would race with concurrent FrameById writers (Dictionary is not safe
        // for a reader during a writer's resize).
        FrameInterfaceId interfaceId = _RegisterInterface(_Registry!, busType, channel, linkType);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(absoluteNanos),
            frameData,
            linkType,
            interfaceId,
            _Registry!);

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
                FileOffset = entry.Location,
                Kind = FrameReadErrorKind.Other,
                Message = $"Frame creation failed for {entry.LineType} (frame {frameIndex}).",
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

    /// <summary>
    /// Splits a raw ASCII/UTF-8 byte array into individual lines.
    /// Handles both LF and CRLF line endings. Each element is a copy of the line bytes
    /// without line terminators.
    /// </summary>
    /// <summary>
    /// Splits a raw ASCII/UTF-8 byte array into zero-copy <see cref="ReadOnlyMemory{T}"/> slices.
    /// Each slice is a window into <paramref name="data"/> — no per-line copy is made.
    /// The caller must keep <paramref name="data"/> alive as long as the returned array is used;
    /// since <see cref="ReadOnlyMemory{T}"/> holds a reference to the backing object this
    /// happens automatically via normal GC reachability.
    /// Handles both LF and CRLF line endings.
    /// </summary>
    private static ReadOnlyMemory<byte>[] _SplitIntoLines(byte[] data)
    {
        List<ReadOnlyMemory<byte>> lines = [];
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n')
            {
                continue;
            }

            int end = i;
            // Strip trailing \r for CRLF files
            if (end > start && data[end - 1] == (byte)'\r')
            {
                end--;
            }

            // Zero-copy slice: points directly into data, no allocation
            lines.Add(new ReadOnlyMemory<byte>(data, start, end - start));
            start = i + 1;
        }

        // Last line (no trailing newline)
        if (start < data.Length)
        {
            int end = data.Length;
            if (end > start && data[end - 1] == (byte)'\r')
            {
                end--;
            }

            lines.Add(new ReadOnlyMemory<byte>(data, start, end - start));
        }

        return [.. lines];
    }

    #endregion
}
