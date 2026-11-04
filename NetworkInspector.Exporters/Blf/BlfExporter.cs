// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// BLF frame exporter. Writes captured frames to a BLF file.
/// <para>
/// Implements <see cref="IFrameListener"/> for integration with the capture pipeline.
/// Supports Ethernet (Type 120 raw frame), CAN classic (<see cref="LinkType.CanSocketcan"/>, <see cref="LinkType.Can20B"/>),
/// CAN FD (Type 101), CAN XL (SocketCAN XLF on <see cref="LinkType.CanSocketcan"/>), FlexRay (Type 50),
/// LIN data frames (Type 57), LIN errors (Types 58, 60, 61), LIN sleep (Type 20), and LIN wakeup (Type 62).
/// Unsupported link types are skipped (counted in
/// <see cref="IExporterStatistics.SkippedCount"/>).
/// Lazy initialization defers file creation until the first frame.
/// The channel comes from <see cref="BlfInterfacePropertyKeys.Channel"/> (kept, including 0), else from the ASC
/// reader's <c>asc.channel</c>, else from a trailing number in the interface name; it defaults to 1.
/// CAN FD and CAN XL objects store the channel in one byte: a frame of those types on a channel above 255 is skipped as
/// <see cref="ExportErrorKind.MalformedData"/>, and so is a CAN FD frame that declares more than 64 data bytes.
/// A <see cref="BlfInterfacePropertyKeys.HardwareChannel"/> value is written into Type 120.
/// An interface whose name is not the default <c>"{bus} {channel}"</c> gets one AppText channel-name object
/// (Type 65) before its first frame, so a BLF → BLF copy keeps the channel names.
/// The header dates are written in <see cref="Builder.WithTimestampTimeZone"/> (default UTC).
/// </para>
/// <para>
/// <b>Write mode and streams:</b> in <see cref="ExportWriteMode.Background"/> (default) a writer thread named
/// "BLF export writer" compresses (zlib) and writes each full container while the next one fills. zlib on a container of several
/// MiB is the most expensive single operation of any exporter; on the caller's thread the reader would stall for every
/// container, with the writer thread both run at the same time. This also holds for <see cref="Builder.ToStream"/> and
/// <see cref="Builder.ToStdout"/>: the caller must not read, write, seek or dispose that stream until <see cref="OnFinish"/>
/// returned; <see cref="OnFinish"/> waits for every container before it patches the header, so the complete file is in the stream
/// then. Use <see cref="ExportWriteMode.Synchronous"/> to keep all writes on the calling thread. Memory: two containers
/// (<see cref="Builder.WithMaxContainerSize"/> each) in background mode, one in synchronous mode, plus the buffer for the
/// compressed output of one container (sized like a container, absent without compression).
/// </para>
/// <para>
/// <b>Error timing:</b> in background mode a container is written after <see cref="OnFrame"/> returned for its last frame, so a
/// failing container is reported by the next <see cref="OnFrame"/> call that finds it (at the latest by <see cref="OnFinish"/>); in
/// synchronous mode by that very call. Its frames leave <see cref="WrittenCount"/> and count as skipped when it is reported.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads. Internally the writer thread never touches the counters or
/// raises <see cref="ItemSkipped"/>, which stay on the caller's thread.
/// </para>
/// </summary>
public sealed class BlfExporter : IFrameListener, IErrorTolerantExporter, IBatchWriter<BlfContainer>, IDisposable
{
    #region Configuration and state

    /// <summary>SocketCAN FD flag: FDF (FD Format indicator) at byte offset 5.</summary>
    private const byte _SocketCanFdFlagFdf = 0x04;

    /// <summary>
    /// CAN XL discriminator: byte 4 bit 7 (XLF). Classic DLC and FD length never set this bit.
    /// </summary>
    private const byte _SocketCanXlfFlag = 0x80;

    /// <summary>Cooperative cancellation; checked per frame.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>zlib level of the log containers, mapped once from <see cref="BlfCompressionLevel"/> by the builder.</summary>
    private readonly CompressionLevel _Compression;

    /// <summary>Uncompressed container size in bytes; also sizes the compressed-output buffer of the <see cref="BlfWriter"/>.</summary>
    private readonly int _MaxContainerSize;

    /// <summary>Maximum number of frames to write; 0 means unlimited.</summary>
    private readonly int _TargetFrameCount;

    /// <summary>Zone of the header start/end SYSTEMTIME fields; the reader must use the same zone to recover absolute times.</summary>
    private readonly TimeZoneInfo _TimestampTimeZone;

    /// <summary>
    /// Channel facts per interface. Interfaces are immutable once registered, so the registry and property bag are
    /// consulted once per interface instead of once per frame.
    /// </summary>
    private readonly Dictionary<FrameInterfaceId, InterfaceTarget> _InterfaceTargets = [];

    /// <summary>(AppText bus type, channel) pairs whose channel name was already written.</summary>
    private readonly HashSet<(byte BusType, ushort Channel)> _NamedChannels = [];

    /// <summary>
    /// (interface, object type) pairs whose channel name was already judged by <see cref="_WriteChannelNameOnce"/>. The
    /// verdict never changes for a pair, so later frames skip the default-name string it would otherwise build per frame.
    /// </summary>
    private readonly HashSet<(FrameInterfaceId Interface, uint ObjectType)> _JudgedInterfaces = [];

    /// <summary>Output target. Cleared to <c>null</c> after disposal to prevent a double dispose.</summary>
    private ExportOutput? _Output;

    /// <summary>Active writer. Created on lazy init; <c>null</c> before the first frame.</summary>
    private BlfWriter? _Writer;

    /// <summary>
    /// The writer pipeline owns both open containers, the writer thread (or writes inline in synchronous mode), and the queue of
    /// lost containers (see <see cref="BatchWriterPipeline{TBatch}"/>). Created in the constructor without I/O, containers, or a
    /// thread; the exporter is its writer (explicit <see cref="IBatchWriter{TBatch}"/> implementation) and forwards to the
    /// <see cref="BlfWriter"/> that <see cref="_Start"/> creates.
    /// </summary>
    private readonly BatchWriterPipeline<BlfContainer> _Pipeline;

    /// <summary>
    /// Objects appended to containers so far (frame objects and channel-name objects). The caller's thread counts them, so the
    /// realign test and the early-timestamp advisory never read state that the writer thread changes.
    /// </summary>
    private int _ObjectsAppended;

    /// <summary>
    /// Reusable payload buffer: every frame's object payload is built here and copied into the writer's container, so the
    /// per-frame path allocates nothing. 4 KiB covers all common payloads; larger ones (jumbo Ethernet, CAN XL) grow it once.
    /// </summary>
    private readonly PooledBuffer _PayloadBuffer = new(4096);

    /// <summary>AppText channel-name payloads; created on the first custom interface name (most exports never need it).</summary>
    private PooledBuffer? _AppTextBuffer;

    /// <summary>Timestamp (ns) that anchors the LOGG start_date; fixed when the writer is created.</summary>
    private long _FirstTimestampNs;

    /// <summary>Running minimum of all offered frame timestamps; the writer anchor is realigned to it while still possible.</summary>
    private long _MinTimestampNs = long.MaxValue;

    /// <summary>Largest timestamp of frames actually written; becomes the header end_date.</summary>
    private long _MaxTimestampNs = long.MinValue;

    /// <summary>Set after the single "timestamp precedes the anchor" advisory event was raised (see <see cref="_HandleFrame"/>).</summary>
    private bool _EarlyTsClampNotified;

    /// <summary>Latched fatal state (start failure or Strict-mode error); every later <see cref="OnFrame"/> returns <c>false</c>.</summary>
    private bool _HasError;

    /// <summary>Set once <see cref="_Start"/> ran, successful or not, so a failed start is never retried.</summary>
    private bool _Started;

    /// <summary>Latch that makes <see cref="OnFinish"/> idempotent and stops <see cref="OnFrame"/> after the file is finalized.</summary>
    private bool _Finished;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private BlfExporter(
        ExportOutput output,
        string uiName,
        string? description,
        CompressionLevel compression,
        int targetFrameCount,
        TimeZoneInfo timestampTimeZone,
        ExportWriteMode writeMode,
        int maxContainerSize,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _CancellationToken = cancellationToken;
        _Compression = compression;
        _MaxContainerSize = maxContainerSize;
        _TargetFrameCount = targetFrameCount;
        _TimestampTimeZone = timestampTimeZone;
        _Pipeline = new BatchWriterPipeline<BlfContainer>("BLF", writeMode, () => new BlfContainer(maxContainerSize), this);
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    public static Builder CreateBuilder() => new();

    /// <inheritdoc/>
    public string UiName
    {
        get;
    }

    /// <inheritdoc/>
    public string? Description
    {
        get;
    }

    #endregion

    #region Statistics

    /// <summary>Number of frames written so far.</summary>
    public int FrameCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => FrameCount;

    /// <inheritdoc/>
    public long EstimatedOutputBytes
    {
        get
        {
            // Header + containers already written + the uncompressed content of the open container: an estimate that ignores
            // compression of the pending container. Computed from in-memory counters, no file-system probe.
            BlfWriter? writer = _Writer;
            if (writer is null)
            {
                return 0L;
            }

            return BlfWriter.FileHeaderSize + writer.BytesWritten + _Pipeline.CurrentEstimatedSizeBytes;
        }
    }

    /// <inheritdoc/>
    public int SkippedCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int ErrorCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Whether the exporter has stopped due to reaching the target count, error, or cancellation.</summary>
    public bool IsFinished => _Finished || _HasError
        || _CancellationToken.IsCancellationRequested
        || (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount);

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion

    #region Frame intake

    /// <inheritdoc/>
    public bool OnFrame(Frame frame)
    {
        // Stop conditions first: a false return tells the driver to stop sending frames.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // A container the writer failed to write since the last frame is accounted first, so the counters see what is really in the
        // file. In strict mode that failure ends the export here.
        if (_Pipeline.HasLost)
        {
            _DrainLostBatches(reportAlways: false);
            if (_HasError)
            {
                return false;
            }
        }

        // Track the earliest timestamp seen. Object timestamps are stored relative to the LOGG start_date anchor as an
        // unsigned value, so a frame earlier than the anchor cannot be represented (it is clamped to 0); keeping the
        // anchor at the minimum avoids that for out-of-order input as long as nothing was written yet.
        long tsNanos = frame.Timestamp.AsNanos;
        // Only _MinTimestampNs is updated here; _MaxTimestampNs is updated inside
        // _HandleFrame after a successful write so that the BLF end_date only
        // reflects frames that were actually exported (not frames rejected by
        // the target-count gate).
        _MinTimestampNs = _MinTimestampNs == long.MaxValue
            ? tsNanos
            : Math.Min(_MinTimestampNs, tsNanos);

        // Lazy init uses the running minimum so the LOGG anchor matches the earliest
        // frame timestamp seen so far (first frame path: min == max).
        if (!_Started)
        {
            _FirstTimestampNs = _MinTimestampNs;
            if (!_Start())
            {
                return false;
            }
        }
        else
        {
            // Cheap per frame: the writer returns immediately unless this frame lowered the minimum below the anchor and
            // no object has been written yet (then it rewrites the 144-byte header in place on a seekable stream).
            // Its result is ignored on purpose: failing to realign only means the early frame is clamped and reported.
            _ = _Writer?.TryRealignStartEarlier(_MinTimestampNs, _ObjectsAppended > 0);
        }

        return _HandleFrame(frame);
    }

    /// <summary>
    /// Processes a single frame: determines the BLF object type from the link type,
    /// builds the payload, and writes the object. Looks up the channel from the
    /// frame's interface info to preserve channel assignment during round-trip export.
    /// </summary>
    /// <param name="frame">The frame to export.</param>
    /// <returns>
    /// <see langword="true"/> when the exporter accepts further frames: the frame was written, or it was skipped in tolerant
    /// mode. <see langword="false"/> when the driver must stop: the target count is reached, a skipped frame in strict mode, or
    /// a strict-mode write failure that surfaced during this call.
    /// </returns>
    private bool _HandleFrame(Frame frame)
    {
        // Target reached: refuse the frame so the driver stops. IsFinished reports the same condition to polling callers.
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

        // The span points into the frame's own memory; nothing is copied until the payload builder runs.
        // currentIndex counts written plus skipped frames, i.e. the position of this frame among the frames offered.
        ReadOnlySpan<byte> data = frame.Data.Span;
        long timestampNs = frame.Timestamp.AsNanos;
        int currentIndex = FrameCount + SkippedCount;

        // Channel facts are resolved once per interface (cached) with the lookup shared with the ASC exporter, so the
        // registry and the property bag are not consulted on every frame.
        InterfaceTarget target = _ResolveInterface(frame);
        if (target.InvalidChannelMessage is { } invalidChannel)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = currentIndex,
                Kind = ExportErrorKind.SerializationError,
                Message = invalidChannel,
            });
        }

        ushort channel = target.Channel;
        ushort? hardwareChannel = target.HardwareChannel;

        // Determine the BLF object type from the link type and build its payload into the reusable buffer (no per-frame
        // allocation). payloadBuilt == false means the input was too short or malformed for that object type.
        uint objectType;
        bool payloadBuilt;

        switch (frame.LinkType)
        {
            case LinkType.Ethernet:
                objectType = BlfConstants.ObjTypeEthernetFrameEx;
                payloadBuilt = BlfObjectPayloads.TryBuildEthernetFrameExPayload(
                    data, channel, _PayloadBuffer, hardwareChannel);
                break;

            case LinkType.CanSocketcan:
                // CAN XL shares LinkType.CanSocketcan but sets XLF on byte 4. Emit Type 139
                // before classic/FD so the 12-byte XL header is never interpreted as DLC/FDF.
                if (data.Length >= 8 && (data[4] & _SocketCanXlfFlag) != 0)
                {
                    objectType = BlfConstants.ObjTypeCanXlChannelFrame;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanXlChannelFramePayload(
                        data, channel, _PayloadBuffer);
                    break;
                }

                // SocketCAN error frames are flagged in the identifier word (big-endian here); they become a CAN_ERROR
                // object, because they carry no data frame. Checked before the FD test so an error frame is never
                // misread as classic/FD data.
                if (data.Length >= 4
                    && (BinaryPrimitives.ReadUInt32BigEndian(data) & BlfConstants.SocketCanErr) != 0)
                {
                    objectType = BlfConstants.ObjTypeCanError;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanErrorPayload(
                        data, channel, _PayloadBuffer);
                    break;
                }

                // Check FD flag at byte offset 5 to distinguish CAN classic vs CAN FD
                if (data.Length > 5 && (data[5] & _SocketCanFdFlagFdf) != 0)
                {
                    objectType = BlfConstants.ObjTypeCanFdMessage64;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanFdMessage64Payload(
                        data, channel, _PayloadBuffer);
                }
                else
                {
                    objectType = BlfConstants.ObjTypeCanMessage;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanMessagePayload(
                        data, channel, _PayloadBuffer);
                }
                break;

            case LinkType.Can20B:
                // Can20B frames are always written as classic CAN: no XL or FD detection, only the error flag test.
                if (data.Length >= 4
                    && (BinaryPrimitives.ReadUInt32BigEndian(data) & BlfConstants.SocketCanErr) != 0)
                {
                    objectType = BlfConstants.ObjTypeCanError;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanErrorPayload(
                        data, channel, _PayloadBuffer);
                    break;
                }

                objectType = BlfConstants.ObjTypeCanMessage;
                payloadBuilt = BlfObjectPayloads.TryBuildCanMessagePayload(
                    data, channel, _PayloadBuffer);
                break;

            case LinkType.Flexray:
                objectType = BlfConstants.ObjTypeFlexRayRcvMessage;
                payloadBuilt = BlfObjectPayloads.TryBuildFlexRayRcvMessagePayload(
                    data, channel, _PayloadBuffer);
                break;

            case LinkType.Lin:
                if (data.Length < 8)
                {
                    payloadBuilt = false;
                    objectType = 0;
                    break;
                }

                // DLT_LIN classification: byte 4 bits 3-2 hold the message type, byte 7 the error flags. A plain data frame
                // has type 0 and no error. Error flags select the error object in the order CRC, receive, send (the first
                // match wins). Type 3 is a bus event whose code is in byte 11: 0x04 = wakeup, 0x01/0x02 = sleep reasons.
                // Anything else cannot be represented and is skipped as unsupported.
                byte linMsgType = (byte)((data[4] >> 2) & 0x03);
                byte linErrors = data[7];
                if (linErrors == 0 && linMsgType == 0)
                {
                    objectType = BlfConstants.ObjTypeLinMessage2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinMessage2Payload(data, channel, _PayloadBuffer);
                }
                else if ((linErrors & BlfConstants.LinErrorCrc) != 0)
                {
                    objectType = BlfConstants.ObjTypeLinCrcError2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinCrcError2Payload(data, channel, _PayloadBuffer);
                }
                else if ((linErrors & BlfConstants.LinErrorRcv) != 0)
                {
                    objectType = BlfConstants.ObjTypeLinRcvError2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinRcvError2Payload(data, channel, _PayloadBuffer);
                }
                else if ((linErrors & BlfConstants.LinErrorSnd) != 0)
                {
                    objectType = BlfConstants.ObjTypeLinSndError2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinSndError2Payload(data, channel, _PayloadBuffer);
                }
                else if (linMsgType == 3 && data.Length >= 12 && data[11] == 0x04)
                {
                    objectType = BlfConstants.ObjTypeLinWakeup2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinWakeup2Payload(data, channel, _PayloadBuffer);
                }
                else if (linMsgType == 3 && data.Length >= 12 && (data[11] == 0x01 || data[11] == 0x02))
                {
                    objectType = BlfConstants.ObjTypeLinSleep;
                    byte reason = data[11] == 0x01
                        ? (byte)1
                        : (byte)2;
                    payloadBuilt = BlfObjectPayloads.TryBuildLinSleepPayload(data, channel, reason, _PayloadBuffer);
                }
                else
                {
                    return _HandleSkip(new ExportErrorEventArgs
                    {
                        ItemIndex = currentIndex,
                        Kind = ExportErrorKind.UnsupportedType,
                        Message =
                            $"BLF LIN frame is not a data, error, sleep, or wakeup object (byte4=0x{data[4]:X2}, byte7=0x{linErrors:X2}, length={data.Length}).",
                    });
                }

                break;

            default:
                // Unsupported link type — skip with error reporting
                return _HandleSkip(new ExportErrorEventArgs
                {
                    ItemIndex = currentIndex,
                    Kind = ExportErrorKind.UnsupportedType,
                    Message = $"Unsupported link type: {frame.LinkType}",
                });
        }

        if (!payloadBuilt)
        {
            // Frame too short or malformed — skip with error reporting
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = currentIndex,
                Kind = ExportErrorKind.MalformedData,
                Message = _DescribeBuildFailure(frame.LinkType, objectType, channel, data),
            });
        }

        // _Writer is guaranteed non-null after _Start() succeeds.
        // Wrap the actual write so a failure degrades gracefully to a skipped frame in Tolerant mode rather than tearing down
        // the pipeline.
        try
        {
            // The early-timestamp clamp notification fires at most once per export
            // (contract: consumers see a single advisory event, not one per affected frame).
            // A clamped frame is still written (relative time 0), so it is counted in FrameCount, not SkippedCount.
            // The _EarlyTsClampNotified flag suppresses the repeated event deliberately
            // to avoid flooding consumers with identical messages.
            if (timestampNs < _Writer!.AnchorStartNanos && _ObjectsAppended > 0 && !_EarlyTsClampNotified)
            {
                _EarlyTsClampNotified = true;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = currentIndex,
                    Kind = ExportErrorKind.SerializationError,
                    Message =
                        "BLF: frame timestamp precedes the BLF start_date anchor; relative object time was clamped to 0 ticks.",
                });
            }

            // A custom interface name travels as one AppText object per (bus type, channel), written before the first
            // frame of that channel so a reader names the interface before it registers it.
            if (target.ChannelName is { } channelName)
            {
                _WriteChannelNameOnce(frame.InterfaceId, objectType, channel, channelName, timestampNs);
            }

            // Only LIN_MESSAGE2 is written with object_version 1; every other object type is written with version 0.
            ushort objectVersion = objectType == BlfConstants.ObjTypeLinMessage2
                ? (ushort)1
                : (ushort)0;
            _AppendObject(objectType, objectVersion, timestampNs, _PayloadBuffer.WrittenSpan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only an I/O failure is an I/O error: a caller that retries on another disk for IoError must not retry a
            // serialization problem (a buffer limit, a builder argument).
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = currentIndex,
                Kind = ex is IOException
                    ? ExportErrorKind.IoError
                    : ExportErrorKind.SerializationError,
                Message = $"BLF write failed: {ex.Message}",
            });
        }

        // The frame is part of the open container from here on (the pipeline counts frames for the lost-container accounting).
        _Pipeline.Current.CountFrame();
        FrameCount++;
        // Update _MaxTimestampNs here — after the write — so the BLF end_date only
        // covers frames that were actually exported.
        long writtenTs = frame.Timestamp.AsNanos;
        _MaxTimestampNs = _MaxTimestampNs == long.MinValue
            ? writtenTs
            : Math.Max(_MaxTimestampNs, writtenTs);

        // A synchronous write that failed inside _AppendObject latched the strict stop: report it in this very call, like the
        // other batching exporters, so a driver does not feed one more frame to an exporter that has stopped.
        return !_HasError;
    }

    /// <summary>
    /// Names why a payload builder refused a frame. The builders return only <see langword="false"/>, so the two refusals that
    /// are limits of the BLF format (a channel above 255 for the one-byte channel of Types 101 and 139, and more than 64 CAN FD
    /// data bytes) are recognized here again to name the limit in the error; every other refusal is "too short or malformed".
    /// Runs only for a refused frame (cold path).
    /// </summary>
    /// <param name="linkType">Link type of the refused frame.</param>
    /// <param name="objectType">BLF object type chosen for the frame.</param>
    /// <param name="channel">BLF channel resolved for the frame's interface.</param>
    /// <param name="data">The frame bytes.</param>
    /// <returns>The error message for <see cref="ExportErrorKind.MalformedData"/>.</returns>
    private static string _DescribeBuildFailure(LinkType linkType, uint objectType, ushort channel, ReadOnlySpan<byte> data)
    {
        if (channel > byte.MaxValue
            && objectType is BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanXlChannelFrame)
        {
            return Invariant($"BLF object type {objectType} stores the channel in one byte; channel {channel} is above 255.");
        }

        if (objectType == BlfConstants.ObjTypeCanFdMessage64
            && data.Length >= 8
            && data[4] > BlfObjectPayloads.MaxCanFdDataLength)
        {
            return Invariant(
                $"BLF CAN FD frame declares {data[4]} data bytes; a CAN FD frame carries at most {BlfObjectPayloads.MaxCanFdDataLength}.");
        }

        return Invariant($"Failed to build {linkType} payload ({data.Length} bytes)");
    }

    /// <summary>
    /// Handles a skipped frame: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode (no event is raised then; the false return and
    /// <see cref="HasErrors"/> are the signal).
    /// </summary>
    /// <param name="error">The reason the frame was skipped; raised through <see cref="ItemSkipped"/> in tolerant mode.</param>
    /// <returns><see langword="true"/> in tolerant mode (go on with the next frame); <see langword="false"/> in strict mode.</returns>
    private bool _HandleSkip(ExportErrorEventArgs error)
    {
        // Saturating counters: a multi-billion-frame export must not wrap into negative counts.
        if (SkippedCount < int.MaxValue)
        {
            SkippedCount++;
        }

        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _HasError = true;
            return false;
        }

        // Tolerant mode: fire event and continue
        ItemSkipped?.Invoke(this, error);
        return true;
    }

    #endregion

    #region Interface resolution

    /// <summary>
    /// Resolves (once per interface) the BLF channel, the hardware channel, and the custom name of the frame's interface.
    /// Frames without an interface use channel 1 and no name.
    /// </summary>
    /// <param name="frame">The frame whose interface is resolved.</param>
    /// <returns>The channel facts of the frame's interface; cached, so later frames of the interface cost one lookup.</returns>
    private InterfaceTarget _ResolveInterface(Frame frame)
    {
        if (!frame.HasInterface)
        {
            return InterfaceTarget.Default;
        }

        // Cache hit: no registry lookup and no property parsing per frame. Dictionary is safe here because the exporter is
        // single-threaded (see the class remarks).
        if (_InterfaceTargets.TryGetValue(frame.InterfaceId, out InterfaceTarget cached))
        {
            return cached;
        }

        // Cache miss: resolve once and cache the result, including an invalid-channel verdict, so every frame of a bad
        // interface is skipped cheaply without re-parsing its properties.
        InterfaceTarget target = InterfaceTarget.Default;
        if (frame.Registry.TryGet(frame.InterfaceId, out FrameInterfaceInfo? info))
        {
            InterfaceChannelLookup lookup = InterfaceChannelConverter.TryGetChannel(info, out int channel, out object? invalid);
            ushort? hardwareChannel = info.Properties.TryGetValue(BlfInterfacePropertyKeys.HardwareChannel, out object? hardware)
                && hardware is ushort hardwareValue
                    ? hardwareValue
                    : null;

            // A channel property that is not an integer, or one outside 0..65535, skips the interface's frames.
            target = lookup switch
            {
                InterfaceChannelLookup.Invalid => new(1, null, null,
                    $"BLF channel value '{invalid}' cannot be converted to a UInt16 channel id."),
                InterfaceChannelLookup.Found when channel is < 0 or > ushort.MaxValue => new(1, null, null,
                    $"BLF channel value '{channel}' cannot be converted to a UInt16 channel id."),
                InterfaceChannelLookup.Found => new((ushort)channel, hardwareChannel, info.UiName, null),
                _ => new(1, hardwareChannel, info.UiName, null),
            };
        }

        _InterfaceTargets[frame.InterfaceId] = target;
        return target;
    }

    /// <summary>
    /// Writes the AppText channel-name object for (bus type, channel) the first time it is needed. Names equal to the
    /// default <c>"{bus} {channel}"</c>, names the AppText encoding cannot carry (<c>;</c>, NUL, channel above 255),
    /// and object types without an AppText bus type are not written.
    /// </summary>
    private void _WriteChannelNameOnce(FrameInterfaceId interfaceId, uint objectType, ushort channel, string name, long timestampNs)
    {
        // Runs for every frame of an interface with a name, so the cheap exit comes first: the verdict for an
        // (interface, object type) pair is the same for all its frames, and building the default name below allocates a
        // string. The set lookup allocates nothing.
        if (!_JudgedInterfaces.Add((interfaceId, objectType)))
        {
            return;
        }

        // _NamedChannels.Add is evaluated last because it records the pair: a channel is marked as named even when the
        // name later turns out to be unrepresentable (TryBuildAppTextChannelNamePayload fails), so it is not retried.
        byte busType = BlfInterfaceNaming.BusType(objectType);
        string defaultName = BlfInterfaceNaming.InterfaceName(objectType, channel, null);
        if (busType == 0 || name == defaultName || !_NamedChannels.Add((busType, channel)))
        {
            return;
        }

        _AppTextBuffer ??= new PooledBuffer(256);
        if (BlfObjectPayloads.TryBuildAppTextChannelNamePayload(busType, channel, defaultName, name, _AppTextBuffer))
        {
            _AppendObject(BlfConstants.ObjTypeAppText, 0, timestampNs, _AppTextBuffer.WrittenSpan);
        }
    }

    /// <summary>Channel facts of one interface, resolved once (see <see cref="_ResolveInterface"/>).</summary>
    /// <param name="Channel">BLF channel written into every object of the interface.</param>
    /// <param name="HardwareChannel">Ethernet hardware channel, when the interface carries one.</param>
    /// <param name="ChannelName">Interface name, written as AppText unless it is the default name.</param>
    /// <param name="InvalidChannelMessage">Non-null when the channel property is unusable; the frames are skipped.</param>
    private readonly record struct InterfaceTarget(
        ushort Channel, ushort? HardwareChannel, string? ChannelName, string? InvalidChannelMessage)
    {
        /// <summary>Frames without an interface: channel 1, no hardware channel, no name.</summary>
        internal static InterfaceTarget Default => new(1, null, null, null);
    }

    #endregion

    #region Container hand-off

    /// <summary>
    /// Appends one object to the open container, handing the container to the writer first when the object does not fit. The
    /// timestamp is converted on the caller's thread.
    /// </summary>
    private void _AppendObject(uint objectType, ushort objectVersion, long timestampNs, ReadOnlySpan<byte> payload)
    {
        ulong relative = _Writer!.ToRelative(timestampNs); // Invariant: a started export has a writer.
        if (!_Pipeline.Current.TryAppendObject(objectType, objectVersion, relative, payload))
        {
            _Pipeline.Submit(firstItemIndex: FrameCount - _Pipeline.Current.PacketCount);

            // An empty container always takes the object (an oversized object gets a container of its own).
            _ = _Pipeline.Current.TryAppendObject(objectType, objectVersion, relative, payload);

            // A synchronous write has already failed inside Submit: report it in this very call (uniform timing). In background
            // mode the flag is rarely set yet; the per-frame check in OnFrame reports it later.
            if (_Pipeline.HasLost)
            {
                _DrainLostBatches(reportAlways: false);
            }
        }

        _ObjectsAppended++;
    }

    /// <summary>
    /// Accounts the containers the writer reported as lost: their frames leave <see cref="WrittenCount"/>, count as skipped, and
    /// each container raises one error. Runs on the caller's thread only.
    /// </summary>
    /// <param name="reportAlways">
    /// <see langword="true"/> at the end of the export: the error is raised and latched whatever the tolerance mode.
    /// <see langword="false"/> during the export: strict mode latches the error without an event, tolerant mode raises
    /// <see cref="ItemSkipped"/> and goes on.
    /// </param>
    private void _DrainLostBatches(bool reportAlways)
    {
        while (_Pipeline.TryTakeLost(out LostBatch lost))
        {
            // The frames were counted as written when they were accepted; take them back so WrittenCount and SkippedCount
            // describe what is in the file.
            FrameCount = Math.Max(0, FrameCount - lost.PacketCount);
            SkippedCount = SkippedCount > int.MaxValue - lost.PacketCount
                ? int.MaxValue
                : SkippedCount + lost.PacketCount;
            if (ErrorCount < int.MaxValue)
            {
                ErrorCount++;
            }

            if (!reportAlways && ErrorTolerance == ErrorToleranceMode.Strict)
            {
                _HasError = true;
                continue;
            }

            if (reportAlways)
            {
                _HasError = true;
            }

            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = lost.FirstItemIndex,
                Kind = lost.Kind,
                Message = lost.Message,
            });
        }
    }

    /// <summary>
    /// Writer-thread entry (or caller entry in synchronous mode): forwards to the writer that <see cref="_Start"/> created. The
    /// writer is non-null here because a container is only handed over after a started export.
    /// </summary>
    void IBatchWriter<BlfContainer>.Write(BlfContainer batch) => _Writer!.Write(batch);

    #endregion

    #region Start and finish

    /// <summary>
    /// Lazily initializes the output and writes the 144-byte file header (the <see cref="BlfWriter"/> constructor writes it
    /// to the stream immediately, with placeholder statistics that <see cref="OnFinish"/> patches later). Runs at most once:
    /// <see cref="_Started"/> is set before any failure can happen, so a failed start is never retried.
    /// </summary>
    /// <returns>True if initialization succeeded. False when the output is gone (a disposed exporter: latched without an
    /// error count, so no error loop starts), the output could not be opened, or the header write failed; the last two are
    /// counted and reported through <see cref="ItemSkipped"/>.</returns>
    private bool _Start()
    {
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        // Opening the file is where a bad path, a missing directory, or a permission problem shows up. It is reported
        // through the normal error path instead of escaping from OnFrame (a driver would see an unexpected exception for an
        // ordinary I/O failure).
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? underlyingStream, out string openFailure))
        {
            _ReportStartFailure($"BLF output could not be opened: {openFailure}");
            return false;
        }

        try
        {
            // Writer ctor writes the file header — failures here must be reported
            // through the standard error pipeline rather than escaping to the caller.
            _Writer = new BlfWriter(underlyingStream, _FirstTimestampNs, _MaxContainerSize, _Compression, _TimestampTimeZone);
        }
        catch (Exception ex)
        {
            _ReportStartFailure($"BLF header write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    private void _ReportStartFailure(string message)
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = 0,
            Kind = ExportErrorKind.IoError,
            Message = message,
        });
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;

        // Shared finish sequence: finalize, then release every resource even when a step fails. Pooled buffers are returned
        // first and the output last (the output close flushes and may throw); ExporterFinish runs every step regardless.
        ExporterFinish.Run(
            "BLF",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("writer pipeline disposal", _Pipeline.Dispose),
            new ExporterReleaseStep("payload buffer return", _PayloadBuffer.Return),
            new ExporterReleaseStep("AppText buffer return", () => _AppTextBuffer?.Return()),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>
    /// Writes the remaining container, waits for the writer, accounts lost containers, and patches the file header (object count,
    /// sizes, end_date). Starts the writer first when no frame ever arrived, so an empty export is still a valid BLF file
    /// (header with zero containers).
    /// </summary>
    private void _FinalizeFile()
    {
        // Empty export: no frame fixed the anchor, so the header carries zero dates (BlfWriter writes an all-zero
        // SYSTEMTIME for a timestamp <= 0).
        if (!_Started && !_HasError && _Output is not null)
        {
            _FirstTimestampNs = 0;
            _MinTimestampNs = 0;
            _MaxTimestampNs = 0;
            _Start();
        }

        // Last (possibly partial) container; Complete joins the writer thread, which makes everything it wrote visible here.
        if (!_Pipeline.IsCompleted)
        {
            _Pipeline.Complete(firstItemIndex: FrameCount - _Pipeline.CurrentPacketCount);
        }

        // Lost containers first, so the counters describe what is really in the file.
        _DrainLostBatches(reportAlways: true);

        if (_Writer is not null)
        {
            // Finish the writer and update the header (end_date from max timestamp). The header patch seeks, so it runs here,
            // after the join, when no container write can be in flight. The header is only rewritten when the stream is
            // seekable; otherwise the placeholder header stays, which readers tolerate because they count objects themselves
            // (see BlfWriter). TryGetExistingStream is used so finishing never creates a file as a side effect.
            long endNs = _MinTimestampNs == long.MaxValue
                ? 0
                : _MaxTimestampNs;
            BlfWriterFinishResult result = _Writer.Finish(endNs);
            Stream? stream = _Output?.TryGetExistingStream();
            if (stream is not null)
            {
                result.UpdateHeader(stream);
            }
        }
    }

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        output?.Dispose();
    }

    /// <summary>Error channel of the finish sequence: marks the error, counts it, and raises <see cref="ItemSkipped"/>.</summary>
    private void _ReportFinishError(ExportErrorKind kind, string message)
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = FrameCount,
            Kind = kind,
            Message = message,
        });
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Builder

    /// <summary>
    /// Maps <see cref="BlfCompressionLevel"/> to <see cref="CompressionLevel"/> (zlib levels 0, 1, 6, 9; BlfWriter records
    /// the corresponding level byte in the file header).
    /// </summary>
    /// <param name="level">A defined level; <see cref="Builder.WithCompressionLevel"/> refuses every other value.</param>
    /// <returns>The matching .NET compression level.</returns>
    private static CompressionLevel _MapCompression(BlfCompressionLevel level) => level switch
    {
        BlfCompressionLevel.None => CompressionLevel.NoCompression,
        BlfCompressionLevel.Fast => CompressionLevel.Fastest,
        BlfCompressionLevel.Default => CompressionLevel.Optimal,
        BlfCompressionLevel.Best => CompressionLevel.SmallestSize,
        _ => throw new System.Diagnostics.UnreachableException("BlfCompressionLevel is validated by WithCompressionLevel."),
    };

    /// <summary>Fluent builder for constructing a <see cref="BlfExporter"/>.</summary>
    public sealed class Builder
    {
        /// <summary>Output target; required before <see cref="Build"/>.</summary>
        private ExportOutput? _Output;

        /// <summary>User-friendly display name.</summary>
        private string _UiName = "BLF Exporter";

        /// <summary>Optional description of the exporter.</summary>
        private string? _Description;

        /// <summary>Cooperative cancellation token; <c>default</c> never cancels.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>Compression preset of the log containers; always a defined value.</summary>
        private BlfCompressionLevel _Compression = BlfCompressionLevel.Default;

        /// <summary>Maximum number of frames to write; 0 means unlimited.</summary>
        private int _TargetFrameCount;

        /// <summary>Zone of the header SYSTEMTIME fields.</summary>
        private TimeZoneInfo _TimestampTimeZone = TimeZoneInfo.Utc;

        /// <summary>Whether containers are written on a writer thread or on the calling thread.</summary>
        private ExportWriteMode _WriteMode = ExportWriteMode.Background;

        /// <summary>Uncompressed container size in bytes.</summary>
        private int _MaxContainerSize = BlfConstants.MaxContainerBufferSize;

        /// <summary>
        /// Sets the zone in which the header <c>start_date</c> / <c>end_date</c> SYSTEMTIME fields are written.
        /// Default: <see cref="TimeZoneInfo.Utc"/>, the same default as <c>BlfSourceOptions.TimestampTimeZone</c>, so files
        /// this product writes and reads round-trip on any host. Use <see cref="TimeZoneInfo.Local"/> for tools that read
        /// the dates as local civil time.
        /// </summary>
        /// <param name="zone">The zone of the header dates.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is null.</exception>
        public Builder WithTimestampTimeZone(TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);
            _TimestampTimeZone = zone;
            return this;
        }

        /// <summary>
        /// Sets the output to a file path with a 4 MiB buffer. The file is created lazily on the first write.
        /// Calling an output method again replaces the previous target.
        /// </summary>
        /// <param name="path">Path of the BLF file to create; an existing file is overwritten.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream; the caller keeps ownership and the exporter does not dispose it.
        /// A seekable stream positioned at its start allows the header to be patched with the final statistics on finish.
        /// </summary>
        /// <param name="stream">The writable stream that receives the file; not disposed by the exporter.</param>
        /// <returns>This builder.</returns>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>
        /// Sets the output to stdout, which the exporter owns and closes on finish. stdout is normally not seekable, so
        /// the header keeps its placeholder statistics.
        /// </summary>
        /// <returns>This builder.</returns>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        /// <summary>Sets the user-friendly display name.</summary>
        /// <param name="name">Display name; not empty or white space.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or white space.</exception>
        public Builder WithUiName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _UiName = name;
            return this;
        }

        /// <summary>Sets an optional description.</summary>
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the token that stops the export: <see cref="OnFrame"/> returns <see langword="false"/> once it is cancelled.</summary>
        /// <param name="token">The cancellation token.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Chooses how full containers are written: <see cref="ExportWriteMode.Background"/> (default) compresses and writes on a
        /// writer thread named "BLF export writer" while the next container fills; <see cref="ExportWriteMode.Synchronous"/> does
        /// it inside the <see cref="OnFrame"/> call that completed the container. Background holds two containers in memory,
        /// synchronous one. With a caller-owned stream (<see cref="ToStream"/>, <see cref="ToStdout"/>) the caller must not use
        /// the stream until <see cref="OnFinish"/> returned in background mode.
        /// </summary>
        /// <param name="mode">The write mode.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
        public Builder WithWriteMode(ExportWriteMode mode)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Write mode must be Background or Synchronous.");
            }

            _WriteMode = mode;
            return this;
        }

        /// <summary>
        /// Sets the uncompressed size at which a log container is compressed and written. Larger containers compress slightly
        /// better and hold more memory (two containers in background mode, plus the compressed-output buffer of the same size).
        /// </summary>
        /// <param name="bytes">Container size in bytes, 64 KiB to 64 MiB; default 10 MiB.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is outside the range; the message names it.</exception>
        public Builder WithMaxContainerSize(long bytes)
        {
            // Limit: container size 65 536 to 67 108 864 bytes, default 10 MiB (BlfConstants.MaxContainerBufferSize). Threat: memory
            // exhaustion — a container is held uncompressed until it is compressed, twice in background mode, and the writer
            // rents a compressed-output buffer of the same size. Configurable by the library caller and the CLI user (option
            // containerSize). Posture: secure by default — the default bounds memory on any host; the upper bound keeps one zlib
            // call and one PooledBuffer below sizes where a mistyped value would stall the export. Lower bound: below 64 KiB the
            // per-container headers and zlib setup dominate. Agreed by the owner.
            const long minimum = 64L * 1024;
            const long maximum = 64L * 1024 * 1024;
            if (bytes < minimum || bytes > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bytes),
                    bytes,
                    Invariant($"BLF container size must be between {minimum} and {maximum} bytes."));
            }

            // Bounded above by 64 MiB, so the narrowing is exact.
            _MaxContainerSize = checked((int)bytes);
            return this;
        }

        /// <summary>Sets the zlib level of the log containers.</summary>
        /// <param name="level">A defined <see cref="BlfCompressionLevel"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not defined.</exception>
        public Builder WithCompressionLevel(BlfCompressionLevel level)
        {
            if (!Enum.IsDefined(level))
            {
                throw new ArgumentOutOfRangeException(nameof(level), level, "Compression level must be None, Fast, Default, or Best.");
            }

            _Compression = level;
            return this;
        }

        /// <summary>
        /// Limits the number of frames to export. <c>0</c> means unlimited (default).
        /// When the target is reached, <see cref="OnFrame"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        /// <param name="count">Number of frames after which the export stops; 0 for no limit.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetFrameCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // Frames are addressed by array-index ids, so a source cannot deliver more than MaxCount frames; a larger
            // target would be unreachable and is rejected as a configuration error.
            if (count > ArrayIndexIdRange.MaxCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"Target frame count must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");
            }

            _TargetFrameCount = count;
            return this;
        }

        /// <summary>
        /// Builds the exporter. No file I/O occurs until the first frame is written.
        /// </summary>
        /// <returns>A new exporter that writes to the configured output.</returns>
        /// <exception cref="InvalidOperationException">No output destination was set.</exception>
        public BlfExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "Output destination must be set via ToFile(), ToStream(), or ToStdout().");
            }

            return new BlfExporter(
                _Output,
                _UiName,
                _Description,
                _MapCompression(_Compression),
                _TargetFrameCount,
                _TimestampTimeZone,
                _WriteMode,
                _MaxContainerSize,
                _CancellationToken);
        }
    }

    #endregion
}
