// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Stream-based frame source for Vector ASC (ASCII trace) files.
/// Implements <see cref="IFrameSource"/> for forward-only sequential reading
/// from any <see cref="Stream"/>.
/// <para>
/// Supports CAN classic, CAN FD, LIN, FlexRay, and Ethernet bus types.
/// Each line is parsed and converted to the appropriate binary frame format
/// (SocketCAN, DLT_LIN, DLT_FLEXRAY, or raw Ethernet).
/// </para>
/// <para>
/// The header is parsed lazily on the first <see cref="NextFrame"/> call so that
/// <see cref="Start"/> returns immediately. Interface registration is also deferred
/// to the first time each (busType, channel) combination is seen.
/// </para>
/// </summary>
public sealed class AscStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>Underlying data stream.</summary>
    private readonly Stream _Stream;

    /// <summary>User-friendly display name.</summary>
    private readonly string _UiName;

    /// <summary>Whether to leave the stream open on Dispose.</summary>
    private readonly bool _LeaveOpen;

    /// <summary>Whether <see cref="Start"/> has been called.</summary>
    private bool _Started;

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    private bool _Disposed;

    /// <summary>Whether the stream is exhausted or strict-mode abort was triggered.</summary>
    private bool _Exhausted;

    /// <summary>Whether the header has been parsed.</summary>
    private bool _Initialized;

    /// <summary>
    /// A frame parsed from the first data line during <see cref="Initialize"/>.
    /// ASC files that lack a "Begin Triggerblock" start data immediately after the header;
    /// the first data line is captured here so it is not lost when the header scanning loop
    /// consumes it.
    /// </summary>
    private Frame? _PendingFirstFrame;

    /// <summary>Sequential frame counter for <see cref="FrameId"/> assignment.</summary>
    private int _FrameIndex;

    // ── Byte-based line reader state ──────────────────────────────────────────

    /// <summary>
    /// Raw read buffer — bytes are read from the stream in chunks to avoid per-line allocations.
    /// </summary>
    private readonly byte[] _ReadBuffer = new byte[AscSourceOptions.DiskReadBufferSize];

    /// <summary>Number of valid bytes currently in <see cref="_ReadBuffer"/>.</summary>
    private int _BufferFilled;

    /// <summary>Read cursor within <see cref="_ReadBuffer"/>.</summary>
    private int _BufferPos;

    /// <summary>
    /// Carry-over buffer: bytes belonging to an incomplete line that started near the end
    /// of the previous chunk and did not fit entirely into <see cref="_ReadBuffer"/>.
    /// </summary>
    private readonly byte[] _CarryBuffer = new byte[AscSourceOptions.MaxLineLength];

    /// <summary>Number of valid bytes in <see cref="_CarryBuffer"/>.</summary>
    private int _CarryLen;

    /// <summary>
    /// When <c>true</c>, the current line already overflowed <see cref="_CarryBuffer"/>.
    /// Its bytes are discarded until the next \n.
    /// </summary>
    private bool _CarryTooLong;

    /// <summary>Parsed ASC file header.</summary>
    private AscHeader _Header = new();

    /// <summary>
    /// Timezone applied when interpreting the ASC <c>date</c> header. See
    /// <see cref="AscSourceOptions.TimestampTimeZone"/> for rationale.
    /// </summary>
    private readonly TimeZoneInfo _TimestampTimeZone;

    /// <summary>Start time from the file header in seconds since Unix epoch.</summary>
    private double _BaseEpoch;

    #endregion

    #region Interface registration

    /// <summary>Source ID assigned during <see cref="Start"/>.</summary>
    private FrameSourceId _SourceId;

    /// <summary>Registry for interface registration.</summary>
    private FrameInterfaceRegistry? _Registry;

    /// <summary>
    /// Maps (busType, channel) → <see cref="FrameInterfaceId"/> for lazy per-channel registration.
    /// <see cref="AscStreamSource"/> is always single-threaded, so no locking is required.
    /// </summary>
    private readonly Dictionary<(AscBusType, int), FrameInterfaceId> _InterfaceMap = [];

    #endregion

    #region Error tolerance statistics

    private long _ReadFrameCount;
    private long _SkippedFrameCount;
    private long _ErrorCount;

    #endregion

    #region Construction

    private AscStreamSource(Stream stream, string uiName, bool leaveOpen,
        ErrorToleranceMode errorTolerance, TimeZoneInfo timestampTimeZone)
    {
        _Stream = stream;
        _UiName = uiName;
        _LeaveOpen = leaveOpen;
        ErrorTolerance = errorTolerance;
        _TimestampTimeZone = timestampTimeZone;
    }

    /// <summary>
    /// Creates a new <see cref="AscStreamSource"/> from the given readable stream.
    /// </summary>
    /// <param name="stream">A readable stream containing ASC data.</param>
    /// <param name="uiName">Display name shown in the UI. Defaults to <c>"ASC Stream"</c>.</param>
    /// <param name="leaveOpen">
    /// If <c>true</c>, the stream is not disposed when this source is disposed.
    /// </param>
    /// <param name="options">
    /// Optional ASC source options; <c>null</c> uses defaults
    /// (UTC timestamp interpretation, tolerant error mode).
    /// </param>
    /// <returns>A new <see cref="AscStreamSource"/> ready for <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static AscStreamSource FromStream(
        Stream stream, string uiName = "ASC Stream",
        bool leaveOpen = false, AscSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        ErrorToleranceMode tolerance = options?.ErrorTolerance ?? ErrorToleranceMode.Tolerant;
        TimeZoneInfo timestampTimeZone = options?.TimestampTimeZone ?? TimeZoneInfo.Utc;
        return new AscStreamSource(stream, uiName, leaveOpen, tolerance, timestampTimeZone);
    }

    #endregion

    #region IFrameSource

    /// <inheritdoc />
    public string UiName => _UiName;

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    /// <remarks>Always <c>null</c> — the total frame count is unknown until the stream is exhausted.</remarks>
    public int? EstimatedFrameCount => null;

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
        Volatile.Write(ref _Started, true);
        _FrameIndex = 0;
        // Note: we deliberately do NOT reset ErrorTolerance here. The construction-time
        // value is applied directly in the private ctor (FromStream forwards the option),
        // and callers may legitimately change the property between FromStream() and Start();
        // overwriting it on Start would silently revert their choice.
    }

    /// <inheritdoc />
    public Frame? NextFrame()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
        {
            throw new InvalidOperationException($"{UiName} has not been started. Call Start() first.");
        }

        if (Volatile.Read(ref _Exhausted))
        {
            return null;
        }

        // Deferred initialization: parse header on the first NextFrame() call.
        if (!_Initialized)
        {
            try
            {
                Initialize();
            }
            catch
            {
                // Match the PCAP/BLF stream-source contract: a failed deferred
                // initialization permanently exhausts the source and rethrows
                // so the caller cannot re-enter with partial state.
                Volatile.Write(ref _Exhausted, true);
                _Initialized = true;
                throw;
            }
            _Initialized = true;

            // Return the first data frame captured during header parsing, if any.
            if (_PendingFirstFrame.HasValue)
            {
                Frame f = _PendingFirstFrame.Value;
                _PendingFirstFrame = null;
                return f;
            }

            if (Volatile.Read(ref _Exhausted))
            {
                return null;
            }
        }

        return ReadNextFrame();
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
        _Registry = null;

        // GC.SuppressFinalize is called before the conditional stream disposal so it
        // executes even if _Stream.Dispose() throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        if (!_LeaveOpen)
        {
            _Stream.Dispose();
        }
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Creates the <see cref="StreamReader"/> with a 4 MiB buffer and parses the ASC file header.
    /// If the first non-header line is a data line (files without "Begin Triggerblock"),
    /// it is parsed immediately and stored in <see cref="_PendingFirstFrame"/> so it is not lost.
    /// </summary>
    private void Initialize()
    {
        _Header = new AscHeader { TimestampTimeZone = _TimestampTimeZone };

        while (true)
        {
            if (!TryReadNextLine(out ReadOnlySpan<byte> lineBytes))
            {
                // Stream exhausted before or during header parsing
                Volatile.Write(ref _Exhausted, true);
                break;
            }

            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(lineBytes);

            if (_Header.TryParseLine(trimmed))
            {
                // Still inside the header section — keep consuming
                continue;
            }

            // Header section ended. If this first non-header line produces a frame,
            // parse and capture it; the reader has already advanced past it.
            if (!trimmed.IsEmpty)
            {
                AscLineType firstLineType = AscLineClassifier.Classify(trimmed);
                if (IsFrameProducingType(firstLineType))
                {
                    _PendingFirstFrame = ParseLine(trimmed, firstLineType);
                }
            }

            break;
        }

        _BaseEpoch = _Header.StartTimeEpoch;
    }

    /// <summary>
    /// Reads the next newline-terminated line from the stream into a span backed by an
    /// internal buffer.  The returned span is valid only until the next call to this method.
    /// Returns <c>false</c> when the stream is exhausted.
    /// Lines that exceed <see cref="AscSourceOptions.MaxLineLength"/> are silently skipped;
    /// the method continues reading until it finds a shorter line or the stream ends.
    /// </summary>
    private bool TryReadNextLine(out ReadOnlySpan<byte> line)
    {
        while (true)
        {
            // Scan from the current buffer position for a \n byte
            for (int i = _BufferPos; i < _BufferFilled; i++)
            {
                if (_ReadBuffer[i] != (byte)'\n')
                {
                    continue;
                }

                // \n found at index i
                ReadOnlySpan<byte> inBufChunk = _ReadBuffer.AsSpan(_BufferPos, i - _BufferPos);

                ReadOnlySpan<byte> rawLine;
                if (_CarryLen > 0 && !_CarryTooLong)
                {
                    int needed = _CarryLen + inBufChunk.Length;
                    if (needed <= _CarryBuffer.Length)
                    {
                        inBufChunk.CopyTo(_CarryBuffer.AsSpan(_CarryLen));
                        _CarryLen = needed;
                        rawLine = _CarryBuffer.AsSpan(0, _CarryLen);
                    }
                    else
                    {
                        // Line too long — discard and skip until next \n
                        _CarryLen = 0;
                        _CarryTooLong = false;
                        _BufferPos = i + 1;
                        // Continue outer while to look for the next \n
                        goto nextLine;
                    }
                }
                else if (_CarryTooLong)
                {
                    // Line was already marked too long — discard remainder
                    _CarryLen = 0;
                    _CarryTooLong = false;
                    _BufferPos = i + 1;
                    goto nextLine;
                }
                else
                {
                    rawLine = inBufChunk;
                }

                // Strip trailing \r (CRLF files)
                if (!rawLine.IsEmpty && rawLine[rawLine.Length - 1] == (byte)'\r')
                {
                    rawLine = rawLine[..^1];
                }

                _CarryLen = 0;
                _CarryTooLong = false;
                _BufferPos = i + 1;
                line = rawLine;
                return true;

            nextLine:
                ;
            }

            // \n not found in the current buffer window — accumulate tail into carry
            ReadOnlySpan<byte> tail = _ReadBuffer.AsSpan(_BufferPos, _BufferFilled - _BufferPos);
            if (!tail.IsEmpty && !_CarryTooLong)
            {
                int needed = _CarryLen + tail.Length;
                if (needed <= _CarryBuffer.Length)
                {
                    tail.CopyTo(_CarryBuffer.AsSpan(_CarryLen));
                    _CarryLen = needed;
                }
                else
                {
                    _CarryTooLong = true;
                    _CarryLen = 0;
                }
            }

            // Refill the buffer
            int bytesRead = _Stream.Read(_ReadBuffer, 0, _ReadBuffer.Length);
            if (bytesRead == 0)
            {
                // Stream exhausted — flush carry-over as the last (unterminated) line
                if (_CarryLen > 0 && !_CarryTooLong)
                {
                    ReadOnlySpan<byte> finalLine = _CarryBuffer.AsSpan(0, _CarryLen);
                    if (!finalLine.IsEmpty && finalLine[finalLine.Length - 1] == (byte)'\r')
                    {
                        finalLine = finalLine[..^1];
                    }

                    _CarryLen = 0;
                    line = finalLine;
                    return true;
                }

                _CarryLen = 0;
                line = default;
                return false;
            }

            _BufferFilled = bytesRead;
            _BufferPos = 0;
        }
    }

    #endregion

    #region Frame reading

    /// <summary>
    /// Reads lines from the stream until a valid frame is produced or the stream is exhausted.
    /// Lines are classified once and the classified type is forwarded to avoid redundant work.
    /// </summary>
    private Frame? ReadNextFrame()
    {
        while (!Volatile.Read(ref _Exhausted))
        {
            if (_FrameIndex == int.MaxValue)
            {
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            if (!TryReadNextLine(out ReadOnlySpan<byte> lineBytes))
            {
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(lineBytes);
            if (trimmed.IsEmpty)
            {
                continue;
            }

            // Classify once — avoid re-classifying inside ParseLine
            AscLineType lineType = AscLineClassifier.Classify(trimmed);
            if (!IsFrameProducingType(lineType))
            {
                continue;
            }

            Frame? frame = ParseLine(trimmed, lineType);
            if (frame.HasValue)
            {
                return frame.Value;
            }

            // ParseLine has already reported any skip via HandleSkip; do not double-report
            // here. If strict mode flipped the exhausted flag, abort the loop.
            if (Volatile.Read(ref _Exhausted))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a single already-trimmed and already-classified ASC line into a <see cref="Frame"/>.
    /// All failure paths (parser failure, empty payload, Frame.Create failure) report exactly
    /// one skip via <see cref="HandleSkip"/> and return <c>null</c>. Callers must not report
    /// an additional skip on a <c>null</c> result.
    /// </summary>
    /// <param name="span">Trimmed ASC line content.</param>
    /// <param name="lineType">Pre-classified line type.</param>
    private Frame? ParseLine(ReadOnlySpan<byte> span, AscLineType lineType)
    {
        bool parsed;
        double timestamp;
        int channel;
        byte[] frameData;
        AscBusType busType;
        LinkType linkType;

        switch (lineType)
        {
            case AscLineType.CanMessage:
                parsed = AscCanParser.TryParse(span, _Header.NumericBase,
                    out timestamp, out channel, out frameData);
                busType = AscBusType.Can;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.CanFdMessage:
                parsed = AscCanFdParser.TryParse(span, _Header.NumericBase,
                    out timestamp, out channel, out frameData);
                busType = AscBusType.CanFd;
                linkType = LinkType.CanSocketcan;
                break;

            case AscLineType.LinMessage:
                parsed = AscLinParser.TryParse(span, _Header.NumericBase,
                    out timestamp, out channel, out frameData);
                busType = AscBusType.Lin;
                linkType = LinkType.Lin;
                break;

            case AscLineType.FlexRayMessage:
                parsed = AscFlexRayParser.TryParse(span, _Header.NumericBase,
                    out timestamp, out channel, out frameData);
                busType = AscBusType.FlexRay;
                linkType = LinkType.Flexray;
                break;

            case AscLineType.EthernetPacket:
                parsed = AscEthernetParser.TryParse(span,
                    out timestamp, out channel, out frameData);
                busType = AscBusType.Ethernet;
                linkType = LinkType.Ethernet;
                break;

            default:
                return null;
        }

        if (!parsed || frameData.Length == 0)
        {
            HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = $"Failed to parse {lineType} line.",
            });
            return null;
        }

        long absoluteNanos = (long)((_BaseEpoch + timestamp) * 1_000_000_000.0);
        FrameInterfaceId interfaceId = GetOrRegisterInterface(busType, channel, linkType);

        int frameId = _FrameIndex++;
        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(absoluteNanos),
            frameData,
            linkType,
            interfaceId,
            _Registry!);

        if (result.IsSuccess)
        {
            Interlocked.Increment(ref _ReadFrameCount);
            return result.Value;
        }

        // Frame.Create failed — record and handle the error (F14)
        HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = frameId,
            FileOffset = -1,
            Kind = FrameReadErrorKind.Other,
            Message = $"Frame creation failed for {lineType} (frame {frameId}).",
        });

        return null;
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Records a skipped frame, updates statistics, and raises <see cref="FrameSkipped"/>.
    /// In strict mode, marks the stream as exhausted so <see cref="ReadNextFrame"/> stops.
    /// </summary>
    private void HandleSkip(FrameReadErrorEventArgs error)
    {
        Interlocked.Increment(ref _SkippedFrameCount);
        Interlocked.Increment(ref _ErrorCount);

        // Always signal the error so subscribers can log the first offending line
        // regardless of the tolerance mode. In strict mode the source additionally
        // sets _Exhausted so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Exhausted, true);
        }
    }

    #endregion

    #region Interface registration

    /// <summary>
    /// Gets or lazily registers a frame interface for the given bus type and channel.
    /// Single-threaded; no locking required.
    /// </summary>
    private FrameInterfaceId GetOrRegisterInterface(AscBusType busType, int channel, LinkType linkType)
    {
        (AscBusType, int) key = (busType, channel);
        if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existingId))
        {
            return existingId;
        }

        if (_Registry is null)
        {
            return default;
        }

        string displayName = busType.ToDisplayName();
        FrameInterfaceId id = _Registry.Register(
            _SourceId, $"{displayName} {channel}", null, linkType,
            new Dictionary<string, object>
            {
                [AscInterfacePropertyKeys.Channel] = (long)channel,
                [AscInterfacePropertyKeys.BusType] = displayName,
            });

        _InterfaceMap[key] = id;
        return id;
    }

    #endregion

    #region Helpers

    /// <summary>Returns <c>true</c> when the line type corresponds to a parseable frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFrameProducingType(AscLineType lineType) => lineType switch
    {
        AscLineType.CanMessage => true,
        AscLineType.CanFdMessage => true,
        AscLineType.LinMessage => true,
        AscLineType.FlexRayMessage => true,
        AscLineType.EthernetPacket => true,
        _ => false,
    };

    #endregion
}
