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
/// <see cref="BlfFrameDispatcher"/>, <see cref="BlfContainer"/>; interface naming and container-tail handling are
/// shared with <see cref="BlfSource"/> (<see cref="BlfInterfaceNaming"/>, <see cref="BlfContainerCarry"/>).
/// A stream that is neither a <see cref="MemoryStream"/> nor a <see cref="BufferedStream"/> is read through a
/// <see cref="BufferedStream"/> of <see cref="StreamBufferSize"/> bytes, so the 1- to 16-byte header reads of the
/// <c>LOBJ</c> resynchronisation do not become one system call each. With <c>leaveOpen = true</c> the buffer may have
/// read ahead of the last parsed object.
/// </remarks>
public sealed class BlfStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>Read buffer for streams that do not buffer themselves: 64 KiB.</summary>
    public const int StreamBufferSize = 64 * 1024;

    /// <summary>Data stream: the caller's stream, or a <see cref="BufferedStream"/> around it.</summary>
    private readonly Stream _Stream;

    /// <summary>Frame slab allocator; null when <see cref="BlfSourceOptions.FrameSlabSize"/> is 0.</summary>
    private readonly FrameSlabAllocator? _Slabs;

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    /// <summary>Whether to leave the stream open on Dispose.</summary>
    private readonly bool _LeaveOpen;

    /// <summary>
    /// Timezone used to interpret the BLF file header's SYSTEMTIME date fields
    /// (<see cref="BlfSourceOptions.TimestampTimeZone"/>, default UTC).
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
    private readonly Dictionary<(uint ObjectType, ushort Channel, bool HasHardwareChannel, ushort HardwareChannel), FrameInterfaceId> _InterfaceMap = [];

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

    /// <summary>
    /// Maximum allowed uncompressed size in bytes for a single BLF log container
    /// (<see cref="BlfSourceOptions.MaxUncompressedContainerSize"/> of the options passed to <see cref="FromStream"/>).
    /// When a container's <c>uncompressedSize</c> header field exceeds this value,
    /// <see cref="Format.BlfDecompressionLimitExceededException"/> is thrown before any
    /// allocation is attempted. A value of <c>0</c> disables the check.
    /// Fixed at construction: the reader never sees a limit change mid-stream.
    /// </summary>
    public long MaxUncompressedContainerSize
    {
        get;
    }

    #endregion

    #region Construction

    private BlfStreamSource(Stream stream, string uiName, bool leaveOpen, BlfSourceOptions options)
    {
        // The LOBJ resynchronisation reads byte by byte; on an unbuffered stream each read would be a system call.
        _Stream = stream is MemoryStream or BufferedStream ? stream : new BufferedStream(stream, StreamBufferSize);
        UiName = uiName;
        _LeaveOpen = leaveOpen;
        _TimestampTimeZone = options.TimestampTimeZone;
        ErrorTolerance = options.ErrorTolerance;
        MaxUncompressedContainerSize = options.MaxUncompressedContainerSize;
        _Slabs = FrameSlabAllocator.CreateOrNull(options.FrameSlabSize);
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
    /// <param name="options">
    /// Reader options, or null for defaults. The stream reader applies <see cref="BlfSourceOptions.ErrorTolerance"/>
    /// (initial <see cref="ErrorTolerance"/>), <see cref="BlfSourceOptions.TimestampTimeZone"/> (default UTC),
    /// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/>, and <see cref="BlfSourceOptions.FrameSlabSize"/>;
    /// the file-only members (scan mode, preload, cache, decompression concurrency) are ignored.
    /// </param>
    /// <returns>A new BlfStreamSource ready for <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static BlfStreamSource FromStream(
        Stream stream,
        string uiName = "Stream",
        bool leaveOpen = false,
        BlfSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        return new BlfStreamSource(stream, uiName, leaveOpen, options ?? new());
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
    /// <remarks>Initialised from <see cref="BlfSourceOptions.ErrorTolerance"/>; may be changed at any time.</remarks>
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
                MaxUncompressedContainerSize);
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
                if (!BlfContainerCarry.IsAllZeros(skipped))
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

            if (!BlfConstants.IsFrameProducingType(objInfo.ObjectType))
            {
                continue;
            }

            // A frame object that cannot be reconstructed is a lost frame: report it (same kind as the file reader).
            if (!BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult frameResult, _Slabs))
            {
                _HandleUnparsableFrameObject(objInfo.ObjectType);
                if (_Exhausted)
                {
                    _PendingContainer = null;
                    _CarryOver = null;
                    return null;
                }

                continue;
            }

            long absoluteTimestamp = BlfTimestamp.ToAbsolute(_FileInfo!.StartOffsetNanos, objInfo.TimestampNanos);
            Frame? frame = _BuildFrame(frameResult, absoluteTimestamp);
            if (frame is not null)
            {
                return frame;
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
        // The object buffer is reused for the next block, so frame bytes are always copied (into the slab when slabs
        // are on); a header or payload that cannot be read is reported, never dropped silently.
        if (!BlfObjectHeaderParser.TryParse(objectData, 0,
                out BlfObjectInfo objInfo, out _)
            || !BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult result, _Slabs))
        {
            _HandleUnparsableFrameObject(BinaryPrimitives.ReadUInt32LittleEndian(objectData[12..]));
            return;
        }

        long absoluteTimestamp = BlfTimestamp.ToAbsolute(_FileInfo!.StartOffsetNanos, objInfo.TimestampNanos);
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

        LinkType linkType = BlfFrameDispatcher.LinkTypeForObject(frameResult.ObjectType);
        FrameInterfaceId interfaceId = _GetOrRegisterInterface(
            frameResult.ObjectType, frameResult.Channel, frameResult.HardwareChannel);

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
    /// Naming (AppText channel names) and properties come from <see cref="BlfInterfaceNaming"/>, shared with
    /// <see cref="BlfSource"/>.
    /// </summary>
    private FrameInterfaceId _GetOrRegisterInterface(uint objectType, ushort channel, ushort? hardwareChannel)
    {
        bool hasHardwareChannel = hardwareChannel.HasValue;
        ushort hardwareChannelValue = hardwareChannel ?? 0;
        (uint ObjectType, ushort Channel, bool HasHardwareChannel, ushort HardwareChannel) key =
            (objectType, channel, hasHardwareChannel, hardwareChannelValue);

        if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existingId))
        {
            return existingId;
        }

        if (_Registry is null)
        {
            return default;
        }

        FrameInterfaceId id = _Registry.Register(
            _SourceId,
            BlfInterfaceNaming.InterfaceName(objectType, channel, _ChannelNames),
            null,
            BlfFrameDispatcher.LinkTypeForObject(objectType),
            BlfInterfaceNaming.BuildProperties(objectType, channel, hardwareChannel));
        _InterfaceMap[key] = id;
        return id;
    }

    /// <summary>Reports a frame-producing object that could not be reconstructed (same kind as the file reader).</summary>
    private void _HandleUnparsableFrameObject(uint objectType)
    {
        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = _FrameIndex,
            FileOffset = -1,
            Kind = FrameReadErrorKind.MalformedHeader,
            Message = $"Frame object of type {objectType} could not be parsed (header or payload cut off).",
        });
    }

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
    /// Prepends pending carry-over bytes to <paramref name="decoded"/>. Drops the carry (and reports it) when the
    /// stitched size would exceed the decompress cap.
    /// </summary>
    private byte[] _PrependCarry(byte[] decoded)
    {
        if (_CarryOver is not { Length: > 0 } carry)
        {
            return decoded;
        }

        _CarryOver = null;
        if (!BlfContainerCarry.TryStitch(carry, decoded, MaxUncompressedContainerSize, out byte[] stitched))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = "Carried container tail plus the next blob exceeds the uncompressed size cap.",
            });
        }

        return stitched;
    }

    /// <summary>
    /// Saves a partial LOBJ tail as carry-over, discards padding, or reports a truncated object
    /// (rules in <see cref="BlfContainerCarry.ClassifyTail"/>, shared with the file scanner).
    /// </summary>
    private void _FinishContainer(ReadOnlySpan<byte> containerSpan)
    {
        ReadOnlySpan<byte> tail = _ContainerOffset < containerSpan.Length ? containerSpan[_ContainerOffset..] : [];
        _CarryOver = null;
        switch (BlfContainerCarry.ClassifyTail(tail))
        {
            case BlfContainerTailKind.PartialObject:
                _CarryOver = tail.ToArray();
                break;
            case BlfContainerTailKind.Garbage:
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = "Truncated BLF object at container boundary.",
                });
                break;
        }

        _PendingContainer = null;
        _ContainerOffset = 0;
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
