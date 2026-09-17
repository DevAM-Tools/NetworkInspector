// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Stream-based frame source for BLF (Binary Logging Format) files.
/// Implements <see cref="IFrameSource"/> for forward-only sequential reading
/// from any <see cref="Stream"/> (e.g., network streams, pipes, stdin).
/// <para>
/// Unlike <see cref="BlfSource"/>, this class does not support random access
/// (<see cref="IRandomAccessFrameSource"/>) and does not use a container cache.
/// Frames are read one at a time using two-level iteration:
/// outer LOBJ blocks from the stream, inner objects from decompressed containers.
/// </para>
/// </summary>
/// <remarks>
/// All existing format parsing code is reused via span-based APIs:
/// <see cref="BlfFileInfo"/>, <see cref="BlfObjectHeaderParser"/>,
/// <see cref="BlfFrameDispatcher"/>, <see cref="BlfContainer"/>.
/// </remarks>
public sealed class BlfStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>Underlying data stream.</summary>
    private readonly Stream _Stream;

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    /// <summary>Whether to leave the stream open on Dispose.</summary>
    private readonly bool _LeaveOpen;

    /// <summary>
    /// Timezone used to interpret the BLF file header's SYSTEMTIME date fields.
    /// Defaults to <see cref="TimeZoneInfo.Local"/> to match Vector BLF tooling.
    /// </summary>
    private readonly TimeZoneInfo _TimestampTimeZone;

    /// <summary>Parsed file header info (set during initialization).</summary>
    private BlfFileInfo? _FileInfo;

    /// <summary>Whether the file header has been parsed.</summary>
    private bool _Initialized;

    /// <summary>Whether the stream is exhausted or stopped.</summary>
    private volatile bool _Exhausted;

    /// <summary>Whether Start() has been called.</summary>
    private volatile bool _Started;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>Sequential frame counter for FrameId assignment.</summary>
    private int _FrameIndex;

    #endregion

    #region Container state (inner loop)

    /// <summary>Current decompressed container data (null when no container is pending).</summary>
    private byte[]? _PendingContainer;

    /// <summary>Read cursor within the pending container.</summary>
    private int _ContainerOffset;

    /// <summary>
    /// Incomplete inner-object bytes saved from the previous container tail, prepended to
    /// the next decompressed blob. Null when no carry is pending.
    /// </summary>
    private byte[]? _CarryOver;

    #endregion

    #region Interface registration

    /// <summary>Source ID assigned during Start().</summary>
    private FrameSourceId _SourceId;

    /// <summary>Registry for interface registration.</summary>
    private FrameInterfaceRegistry? _Registry;

    /// <summary>Maps (objectType, channel) → FrameInterfaceId.</summary>
    private readonly Dictionary<(uint, ushort), FrameInterfaceId> _InterfaceMap = [];

    /// <summary>Discovered channel names from AppText objects.</summary>
    private readonly Dictionary<(byte BusType, byte Channel), string> _ChannelNames = [];

    /// <summary>Reusable buffer for reading blocks from the stream. Grown as needed.</summary>
    private byte[] _ReadBuffer = new byte[4096];

    /// <summary>
    /// Pending frame produced by a raw (non-container) object that was decoded eagerly
    /// during outer-loop scanning. Returned by the next <c>_ReadNextFrame</c> call so
    /// that one outer object → one frame is preserved.
    /// </summary>
    private Frame? _PendingFrame;

    #endregion

    #region Error tolerance statistics
    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();

    #endregion

    #region Decompression limit

    // `volatile` is illegal on long; cross-thread access uses Volatile.Read / Volatile.Write.
    private long _MaxUncompressedContainerSize = BlfSourceOptions.DefaultMaxUncompressedContainerSize;

    /// <summary>
    /// Maximum allowed uncompressed size in bytes for a single BLF log container.
    /// When a container's <c>uncompressedSize</c> header field exceeds this value,
    /// <see cref="Format.BlfDecompressionLimitExceededException"/> is thrown before any
    /// allocation is attempted.
    /// <para>
    /// A value of <c>0</c> disables the check. Default matches
    /// <see cref="BlfSourceOptions.DefaultMaxUncompressedContainerSize"/> (128 MiB).
    /// </para>
    /// <para>
    /// Must be set before <see cref="Start"/> is called; changes after start have no effect.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is negative.</exception>
    public long MaxUncompressedContainerSize
    {
        get => Volatile.Read(ref _MaxUncompressedContainerSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _MaxUncompressedContainerSize, value);
        }
    }

    #endregion

    #region Construction

    private BlfStreamSource(Stream stream, string uiName, bool leaveOpen, TimeZoneInfo timestampTimeZone)
    {
        _Stream = stream;
        UiName = uiName;
        _LeaveOpen = leaveOpen;
        _TimestampTimeZone = timestampTimeZone;
    }

    /// <summary>
    /// Creates a new <see cref="BlfStreamSource"/> that reads from the given stream.
    /// The stream must be readable. Format detection occurs on the first call to
    /// <see cref="NextFrame"/> (after <see cref="Start"/>).
    /// </summary>
    /// <param name="stream">A readable stream containing BLF data.</param>
    /// <param name="uiName">Display name shown in the UI.</param>
    /// <param name="leaveOpen">
    /// If <c>true</c>, the stream is not disposed when this source is disposed.
    /// </param>
    /// <param name="timestampTimeZone">
    /// Time zone for interpreting SYSTEMTIME components in the stream header's <c>start_date</c>.
    /// If <see langword="null"/>, defaults to <see cref="TimeZoneInfo.Local"/> (matching Vector
    /// BLF tooling).
    /// </param>
    /// <returns>A new BlfStreamSource ready for <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static BlfStreamSource FromStream(
        Stream stream,
        string uiName = "Stream",
        bool leaveOpen = false,
        TimeZoneInfo? timestampTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        return new BlfStreamSource(stream, uiName, leaveOpen, timestampTimeZone ?? TimeZoneInfo.Local);
    }

    #endregion

    #region IFrameSource

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    /// <remarks>Always <c>null</c> for stream sources — the total is unknown until the stream is exhausted.</remarks>
    public int? EstimatedFrameCount => null;

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
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _Started = true;
        _FrameIndex = 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// All mutable state (stream position, container state, frame index) is accessed without synchronization.
    /// </remarks>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException($"{UiName} has not been started. Call Start() first.");
        }

        if (_Exhausted)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Parse file header on first call (deferred so Start() doesn't throw)
        if (!_Initialized)
        {
            bool initialized;
            try
            {
                initialized = _Initialize();
            }
            catch
            {
                // Header parse failed catastrophically: ensure subsequent NextFrame()
                // calls don't re-enter the parser with corrupt state.
                _Exhausted = true;
                throw;
            }
            if (!initialized)
            {
                _Exhausted = true;
                return null;
            }
            _Initialized = true;
        }

        // Try to produce a frame — loops until a frame is found or exhausted
        return _ReadNextFrame();
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // Clear the registry reference so the session can be GC'd after Dispose().
        _Registry = null;
        _PendingContainer = null;
        _CarryOver = null;
        if (!_LeaveOpen)
        {
            // Wrapped so that a stream disposal failure does not prevent GC.SuppressFinalize from running.
            try
            {
                _Stream.Dispose();
            }
            catch (ObjectDisposedException) { /* idempotent — stream already disposed */ }
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Reads and parses the BLF file header from the stream.
    /// </summary>
    /// <returns>True if the header was parsed successfully.</returns>
    /// <exception cref="BlfException">The stream does not contain valid BLF data.</exception>
    private bool _Initialize()
    {
        // Read the minimum file header
        byte[] headerBuffer = _EnsureBuffer(BlfConstants.FileHeaderMinSize);
        if (!_TryReadExact(headerBuffer.AsSpan(0, BlfConstants.FileHeaderMinSize)))
        {
            return false;
        }

        if (!BlfFileInfo.TryParse(headerBuffer.AsSpan(0, BlfConstants.FileHeaderMinSize), _TimestampTimeZone, out BlfFileInfo? fileInfo))
        {
            throw new BlfException("Invalid BLF stream: missing or corrupt LOGG file header.");
        }

        // If the header extends beyond the minimum, read and discard the rest
        int extraHeaderBytes = (int)fileInfo.HeaderSize - BlfConstants.FileHeaderMinSize;
        if (extraHeaderBytes > 0)
        {
            byte[] extraBuffer = _EnsureBuffer(extraHeaderBytes);
            if (!_TryReadExact(extraBuffer.AsSpan(0, extraHeaderBytes)))
            {
                return false;
            }
        }

        _FileInfo = fileInfo;
        return true;
    }

    #endregion

    #region Frame reading

    /// <summary>
    /// Reads frames from the stream using two-level iteration:
    /// 1. Drains pending container objects (inner loop)
    /// 2. Reads outer LOBJ blocks from the stream
    /// Returns null when the stream is exhausted.
    /// </summary>
    private Frame? _ReadNextFrame()
    {
        while (!_Exhausted)
        {
            // Level 1: drain pending container objects
            if (_PendingContainer is not null)
            {
                Frame? containerFrame = _DrainPendingContainer();
                if (containerFrame.HasValue)
                {
                    return containerFrame;
                }
                // Container fully consumed, fall through to read next outer block
            }

            // Level 2: read next LOBJ block from stream
            if (!_ReadNextOuterBlock())
            {
                _Exhausted = true;
                return null;
            }

            // Check if a raw frame-producing object was found
            if (_PendingFrame.HasValue)
            {
                Frame frame = _PendingFrame.Value;
                _PendingFrame = null;
                return frame;
            }

            // If we got a container, try draining it immediately
            if (_PendingContainer is not null)
            {
                Frame? containerFrame = _DrainPendingContainer();
                if (containerFrame.HasValue)
                {
                    return containerFrame;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the next outer LOBJ block from the stream.
    /// Processes containers (sets up pending container), AppText objects,
    /// and frame-producing objects. For frame-producing objects that are NOT containers,
    /// pushes the produced frame into the pending results.
    /// Returns false when the stream cannot provide more blocks.
    /// </summary>
    private bool _ReadNextOuterBlock()
    {
        // Shift-register scan: fill 4 bytes, on mismatch drop byte 0 and read 1 more,
        // until LOBJ or EOF. Then read the remaining 12 bytes of the block header.
        // Padding zeros after the previous object are consumed here, not by a computed pad skip.
        Span<byte> headerSpan = stackalloc byte[BlfConstants.BlockHeaderSize];
        if (!_TryReadLobjBlockHeader(headerSpan))
        {
            return false;
        }

        if (!BlfBlockHeader.TryParse(headerSpan, out BlfBlockHeader blockHeader, out _))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.CorruptedBlock,
                Message = "Failed to parse BLF block header; stream may be corrupt."
            });
            return false;
        }

        ushort headerSize = blockHeader.HeaderSize.Value;
        uint objectLength = blockHeader.ObjectLength.Value;
        uint objectType = blockHeader.ObjectType.Value;

        long rawObjectSize = Math.Max(
            Math.Max(BlfConstants.BlockHeaderSize, objectLength),
            headerSize);

        if (rawObjectSize > BlfConstants.MaxBlockReadSize)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = $"LOBJ object size {rawObjectSize} exceeds maximum buffer size."
            });

            // The 16-byte header is already consumed. Do not skip the claimed body:
            // object_length is untrusted and a later LOBJ may sit immediately after
            // this header. The next shift-register scan finds it.
            return !_Exhausted;
        }

        int totalObjectSize = (int)rawObjectSize;

        // Read the remaining body (totalObjectSize - 16 bytes already read)
        int bodySize = totalObjectSize - BlfConstants.BlockHeaderSize;
        if (bodySize <= 0)
        {
            return true;
        }

        // Build a complete object buffer: block header + body
        byte[] objectBuffer = _EnsureBuffer(totalObjectSize);
        headerSpan.CopyTo(objectBuffer);

        if (!_TryReadExact(objectBuffer.AsSpan(BlfConstants.BlockHeaderSize, bodySize)))
        {
            // Stream truncated mid-block (header read succeeded but body read failed)
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.TruncatedStream,
                Message = $"Stream truncated while reading LOBJ body (type=0x{objectType:X}, expected {bodySize} bytes)."
            });
            return false;
        }

        ReadOnlySpan<byte> fullObjectSpan = objectBuffer.AsSpan(0, totalObjectSize);

        if (objectType == BlfConstants.ObjTypeLogContainer)
        {
            _ProcessContainer(fullObjectSpan, headerSize);
            return true;
        }

        if (objectType == BlfConstants.ObjTypeAppText)
        {
            _ProcessAppText(fullObjectSpan);
            return true;
        }

        if (BlfConstants.IsFrameProducingType(objectType))
        {
            _ProcessRawFrameObject(fullObjectSpan);
        }

        return true;
    }

    /// <summary>
    /// Processes a container object: decompresses the payload and sets up the
    /// pending container for inner-loop draining.
    /// Reports decompression failures via <see cref="_HandleSkip"/>.
    /// </summary>
    private void _ProcessContainer(ReadOnlySpan<byte> objectData, ushort headerSize)
    {
        // The container header always sits immediately after the
        // block header padding (skip headerSize - 16 unknown bytes), then a fixed 16-byte
        // container header, then the payload.
        int containerHeaderOffset = Math.Max((int)headerSize, BlfConstants.BlockHeaderSize);
        int payloadOffset = containerHeaderOffset + BlfConstants.ContainerHeaderSize;
        if (containerHeaderOffset + BlfConstants.ContainerHeaderSize > objectData.Length)
        {
            _CarryOver = null;
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = $"Invalid container header offset {containerHeaderOffset} (headerSize={headerSize})."
            });
            return;
        }

        if (!BlfContainerHeader.TryParse(objectData[containerHeaderOffset..], out BlfContainerHeader containerHeader, out _))
        {
            _CarryOver = null;
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = "Failed to parse container header from stream."
            });
            return;
        }

        ushort compressionMethod = containerHeader.CompressionMethod.Value;
        uint uncompressedSize = containerHeader.UncompressedSize.Value;

        // Compressed/raw payload starts after block header + container header.
        ReadOnlySpan<byte> payloadData = objectData.Length > payloadOffset
            ? objectData[payloadOffset..]
            : ReadOnlySpan<byte>.Empty;

        if (payloadData.IsEmpty)
        {
            return;
        }

        try
        {
            byte[] decoded = BlfContainer.Decompress(payloadData, compressionMethod, uncompressedSize,
                Volatile.Read(ref _MaxUncompressedContainerSize));
            _PendingContainer = _PrependCarry(decoded);
            _ContainerOffset = 0;
        }
        catch (Exception ex) when (ex is BlfException or OutOfMemoryException)
        {
            // Decompression failed (format error or OOM from untrusted uncompressedSize).
            // _PendingContainer is only assigned on success so no partial state is leaked.
            // BlfDecompressionLimitExceededException is intentionally not caught here
            // — it propagates to NextFrame so the caller can react.
            _CarryOver = null;
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.DecompressionFailure,
                Message = $"Container decompression failed: {(ex is OutOfMemoryException ? "OutOfMemoryException" : ex.Message)}."
            });
            _PendingContainer = null;
        }
    }

    /// <summary>
    /// Drains objects from the pending decompressed container.
    /// Returns the first frame found, or null if the container is exhausted.
    /// Reports corrupt or unparseable objects via <see cref="_HandleSkip"/>.
    /// </summary>
    private Frame? _DrainPendingContainer()
    {
        if (_PendingContainer is null)
        {
            return null;
        }

        ReadOnlySpan<byte> containerSpan = _PendingContainer.AsSpan();

        while (_ContainerOffset + BlfConstants.BlockHeaderSize <= containerSpan.Length)
        {
            ReadOnlySpan<byte> objectData = containerSpan[_ContainerOffset..];
            ReadOnlyMemory<byte> objectMemory = _PendingContainer.AsMemory(_ContainerOffset);

            if (!BlfObjectHeaderParser.TryParse(objectData, _ContainerOffset, objectMemory,
                    out BlfObjectInfo objInfo, out int skipDistance))
            {
                int remaining = containerSpan.Length - _ContainerOffset - 1;
                if (remaining <= 0)
                {
                    break;
                }

                int nextMagic = containerSpan[(_ContainerOffset + 1)..].IndexOf(BlfConstants.ObjectMagicBytes);
                if (nextMagic < 0)
                {
                    break;
                }

                ReadOnlySpan<byte> skipped = containerSpan.Slice(_ContainerOffset, 1 + nextMagic);
                if (!_IsAllZeros(skipped))
                {
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _FrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.CorruptedBlock,
                        Message = $"Corrupt object at container offset {_ContainerOffset}, attempting LOBJ magic scan recovery."
                    });
                    if (_Exhausted)
                    {
                        _PendingContainer = null;
                        _CarryOver = null;
                        return null;
                    }
                }

                _ContainerOffset += 1 + nextMagic;
                continue;
            }

            _ContainerOffset += skipDistance;

            // Handle AppText within containers
            if (objInfo.ObjectType == BlfConstants.ObjTypeAppText)
            {
                _ProcessAppTextPayload(objInfo.Payload);
                continue;
            }

            // Try to convert to a frame
            if (BlfConstants.IsFrameProducingType(objInfo.ObjectType)
                && BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult frameResult))
            {
                long absoluteTimestamp = _FileInfo!.StartOffsetNanos + objInfo.TimestampNanos;
                Frame? frame = _BuildFrame(frameResult, absoluteTimestamp);
                if (frame is not null)
                {
                    return frame;
                }
                continue;
            }
        }

        // Container fully consumed (or leftover is a split object / padding)
        _FinishContainer(containerSpan);
        return null;
    }

    /// <summary>
    /// Parses a non-container LOBJ payload that still produces a frame (e.g. raw CAN/LIN),
    /// and stores the result in <see cref="_PendingFrame"/>.
    /// </summary>
    private void _ProcessRawFrameObject(ReadOnlySpan<byte> objectData)
    {
        if (!BlfObjectHeaderParser.TryParse(objectData, 0,
                out BlfObjectInfo objInfo, out _))
        {
            return;
        }

        if (!BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult result))
        {
            return;
        }

        long absoluteTimestamp = _FileInfo!.StartOffsetNanos + objInfo.TimestampNanos;
        _PendingFrame = _BuildFrame(result, absoluteTimestamp);
    }

    /// <summary>
    /// Processes an AppText object for channel name extraction.
    /// Reports a skip event if the AppText object header cannot be parsed.
    /// </summary>
    private void _ProcessAppText(ReadOnlySpan<byte> objectData)
    {
        if (!BlfObjectHeaderParser.TryParse(objectData, 0,
                out BlfObjectInfo objInfo, out _))
        {
            // AppText metadata header is corrupt — report as a non-frame skip so callers
            // are aware that channel-name metadata was lost. This does not abort the stream.
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = "AppText object header could not be parsed; channel name metadata lost.",
            });
            return;
        }

        _ProcessAppTextPayload(objInfo.Payload);
    }

    /// <summary>
    /// Extracts channel name from AppText payload and stores it.
    /// </summary>
    private void _ProcessAppTextPayload(ReadOnlySpan<byte> payload)
    {
        if (AppTextParser.TryParseChannelName(
                payload, out byte channelNumber, out byte busType, out string? name))
        {
            _ChannelNames[(busType, channelNumber)] = name!;
        }
    }

    #endregion

    #region Frame construction

    /// <summary>
    /// Builds a <see cref="Frame"/> from a dispatched frame result.
    /// Tracks read/skip statistics and reports errors via <see cref="_HandleSkip"/>.
    /// </summary>
    private Frame? _BuildFrame(BlfFrameResult frameResult, long timestampNanos)
    {
        // Enforce maximum frame count — FrameId is array-index-based
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_FrameIndex, "frame");

        LinkType linkType = _GetLinkTypeForObjectType(frameResult.ObjectType);
        FrameInterfaceId interfaceId = _GetOrRegisterInterface(frameResult.ObjectType, frameResult.Channel);

        int frameId = _FrameIndex++;
        ParseResult<Frame> createResult = Frame.Create(
            new FrameId(frameId),
            new Timestamp(timestampNanos),
            frameResult.FrameData,
            linkType,
            interfaceId,
            _Registry!);

        if (createResult.IsSuccess)
        {
            Interlocked.Increment(ref _ReadFrameCount);
            return createResult.Value;
        }

        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = frameId,
            FileOffset = -1,
            Kind = FrameReadErrorKind.Other,
            Message = $"Frame creation failed for object type 0x{frameResult.ObjectType:X}, channel {frameResult.Channel}."
        });

        return null;
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Handles a skipped frame by updating statistics and raising the event.
    /// In strict mode, additionally marks the stream as exhausted so subsequent reads return null.
    /// The <see cref="FrameSkipped"/> event is always raised regardless of tolerance mode
    /// so subscribers can log the first offending object even when the source aborts
    /// (per SOURCE_GUIDE.md §12.2).
    /// </summary>
    private void _HandleSkip(FrameReadErrorEventArgs error)
    {
        _SkippedFrameCount.Increment();
        _ErrorCount.Increment();

        // Always signal the error so subscribers can log the first offending object
        // regardless of the tolerance mode. In strict mode the source additionally
        // exhausts itself so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _Exhausted = true;
        }
    }

    #endregion

    #region Interface registration

    /// <summary>
    /// Gets or registers a frame interface for the given object type and channel.
    /// Uses discovered channel names from AppText when available.
    /// </summary>
    private FrameInterfaceId _GetOrRegisterInterface(uint objectType, ushort channel)
    {
        (uint, ushort) key = (objectType, channel);

        if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existingId))
        {
            return existingId;
        }

        if (_Registry is null)
        {
            return default;
        }

        string busName = _GetBusName(objectType);
        string interfaceName = _TryGetChannelName(objectType, channel)
            ?? $"{busName} {channel}";
        LinkType linkType = _GetLinkTypeForObjectType(objectType);

        FrameInterfaceId id = _Registry.Register(
            _SourceId, interfaceName, null, linkType,
            new Dictionary<string, object>
            {
                [FrameInterfacePropertyKeys.BlfChannel] = (long)channel,
                [FrameInterfacePropertyKeys.BlfObjectType] = objectType,
                [FrameInterfacePropertyKeys.BlfBusType] = _GetBusTypeForObjectType(objectType),
            });
        _InterfaceMap[key] = id;
        return id;
    }

    /// <summary>
    /// Tries to find a channel name from AppText channel name discovery.
    /// Maps the BLF object type to the AppText bus type for lookup.
    /// </summary>
    private string? _TryGetChannelName(uint objectType, ushort channel)
    {
        if (_ChannelNames.Count == 0)
        {
            return null;
        }

        byte busType = _GetBusTypeForObjectType(objectType);
        if (busType == 0)
        {
            return null;
        }

        // AppText channel numbers are 0-based
        if (!_ChannelNames.TryGetValue((busType, (byte)channel), out string? name))
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// Returns the link type for a given BLF object type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LinkType _GetLinkTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => LinkType.Ethernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => LinkType.CanSocketcan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2
            => LinkType.Lin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => LinkType.Flexray,

        _ => LinkType.Null,
    };

    /// <summary>
    /// Returns a bus name string for interface naming.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string _GetBusName(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => "Ethernet",

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 => "CAN",

        BlfConstants.ObjTypeCanFdMessage or BlfConstants.ObjTypeCanFdMessage64
            or BlfConstants.ObjTypeCanFdError64 => "CAN FD",

        BlfConstants.ObjTypeCanXlChannelFrame => "CAN XL",

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => "LIN",

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => "FlexRay",

        _ => "Unknown",
    };

    /// <summary>
    /// Maps a BLF object type to the AppText bus type constant for channel name lookup.
    /// Returns 0 if the object type has no corresponding bus type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte _GetBusTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => BlfConstants.BusTypeEthernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => BlfConstants.BusTypeCan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => BlfConstants.BusTypeLin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => BlfConstants.BusTypeFlexRay,

        _ => 0,
    };

    #endregion

    #region Stream I/O helpers

    /// <summary>
    /// Consumes bytes until the next <c>LOBJ</c> magic or EOF, then fills the remaining
    /// 12 bytes of the 16-byte block header. Does not treat padding zeros as end-of-stream.
    /// </summary>
    private bool _TryReadLobjBlockHeader(Span<byte> header16)
    {
        if (header16.Length < BlfConstants.BlockHeaderSize)
        {
            return false;
        }

        if (!_TryReadExact(header16[..4]))
        {
            return false;
        }

        while (true)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header16) == BlfConstants.ObjectMagic)
            {
                return _TryReadExact(header16[4..]);
            }

            header16[0] = header16[1];
            header16[1] = header16[2];
            header16[2] = header16[3];
            if (!_TryReadExact(header16.Slice(3, 1)))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Prepends pending carry-over bytes to <paramref name="decoded"/>. Drops the carry
    /// when the stitched size would exceed the decompress cap.
    /// </summary>
    private byte[] _PrependCarry(byte[] decoded)
    {
        if (_CarryOver is not { Length: > 0 })
        {
            return decoded;
        }

        long maxUncompressed = Volatile.Read(ref _MaxUncompressedContainerSize);
        long total = (long)_CarryOver.Length + decoded.Length;
        if (total > int.MaxValue || (maxUncompressed > 0 && total > maxUncompressed))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = "Carried container tail plus the next blob exceeds the uncompressed size cap.",
            });
            _CarryOver = null;
            return decoded;
        }

        byte[] stitched = new byte[(int)total];
        Buffer.BlockCopy(_CarryOver, 0, stitched, 0, _CarryOver.Length);
        Buffer.BlockCopy(decoded, 0, stitched, _CarryOver.Length, decoded.Length);
        _CarryOver = null;
        return stitched;
    }

    /// <summary>
    /// Saves a partial LOBJ tail as carry-over or discards padding-only remainder.
    /// </summary>
    private void _FinishContainer(ReadOnlySpan<byte> containerSpan)
    {
        if (_ContainerOffset < containerSpan.Length)
        {
            ReadOnlySpan<byte> tail = containerSpan[_ContainerOffset..];
            if (_LooksLikePartialLobj(tail) && !_DeclaredObjectLengthExceedsCap(tail))
            {
                _CarryOver = tail.ToArray();
            }
            else
            {
                _CarryOver = null;
            }
        }
        else
        {
            _CarryOver = null;
        }

        _PendingContainer = null;
        _ContainerOffset = 0;
    }

    /// <summary>
    /// True when <paramref name="tail"/> is padding zeros, optionally followed by a
    /// <c>LOBJ</c> magic or a truncated prefix of that magic.
    /// </summary>
    private static bool _LooksLikePartialLobj(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        int i = 0;
        while (i < tail.Length && tail[i] == 0)
        {
            i++;
        }

        if (i == tail.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> candidate = tail[i..];
        int n = Math.Min(candidate.Length, BlfConstants.ObjectMagicBytes.Length);
        return candidate[..n].SequenceEqual(BlfConstants.ObjectMagicBytes[..n]);
    }

    /// <summary>
    /// True when <paramref name="tail"/> begins with a parseable <c>LOBJ</c> header whose
    /// skip distance exceeds <see cref="BlfConstants.MaxBlockReadSize"/>.
    /// </summary>
    private static bool _DeclaredObjectLengthExceedsCap(ReadOnlySpan<byte> tail)
    {
        int i = 0;
        while (i < tail.Length && tail[i] == 0)
        {
            i++;
        }

        ReadOnlySpan<byte> candidate = tail[i..];
        if (candidate.Length < BlfConstants.BlockHeaderSize)
        {
            return false;
        }

        if (!BlfBlockHeader.TryParse(candidate, out BlfBlockHeader header, out _)
            || header.Signature.Value != BlfConstants.ObjectMagic)
        {
            return false;
        }

        long skip = Math.Max(
            Math.Max((long)BlfConstants.BlockHeaderSize, header.ObjectLength.Value),
            header.HeaderSize.Value);
        return skip > BlfConstants.MaxBlockReadSize;
    }

    /// <summary>True when every byte is zero (alignment padding).</summary>
    private static bool _IsAllZeros(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// Returns false if the stream ended before all bytes could be read (EOF).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryReadExact(Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = _Stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                return false; // EOF
            }
            totalRead += read;
        }
        return true;
    }

    /// <summary>
    /// Ensures the internal read buffer is at least the given size.
    /// Returns the buffer (may be larger than requested).
    /// </summary>
    /// <exception cref="BlfException">Thrown when <paramref name="minSize"/> exceeds the maximum buffer size.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] _EnsureBuffer(int minSize)
    {
        if (minSize > BlfConstants.MaxBlockReadSize)
        {
            throw new BlfException(
                $"Object size {minSize} exceeds maximum buffer size of {BlfConstants.MaxBlockReadSize} bytes. The stream data may be corrupt.");
        }

        if (_ReadBuffer.Length < minSize)
        {
            int newSize = Math.Min(Math.Max(minSize, _ReadBuffer.Length * 2), BlfConstants.MaxBlockReadSize);
            _ReadBuffer = new byte[newSize];
        }
        return _ReadBuffer;
    }
    #endregion
}
