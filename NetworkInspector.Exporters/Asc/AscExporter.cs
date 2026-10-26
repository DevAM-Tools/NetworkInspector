// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Asc;

/// <summary>
/// ASC frame exporter. Writes captured frames to a Vector CANalyzer ASCII log file
/// (.asc) compatible with CANoe, can-utils (candump/canplayer), and NetworkInspector.
/// <para>
/// Supports CAN classic, CAN FD, CAN XL, LIN, FlexRay, and Ethernet.
/// AFDX status, AFDX bus statistics, <c>ETH STAT</c>, and <c>ETH RxEr</c> are not written. AFDX data frames are read back as Ethernet.
/// Unsupported link types are tracked in <see cref="SkippedCount"/>.
/// </para>
/// <para>
/// The output file uses <c>base hex  timestamps absolute</c>: all identifiers and data bytes
/// are uppercase hexadecimal, timestamps are decimal seconds relative to the first frame.
/// Line endings are <c>\r\n</c>.
/// </para>
/// <para>
/// Lazy initialisation defers file creation to the first <see cref="OnFrame"/> call;
/// calling <see cref="OnFinish"/> without writing any frames still produces a valid
/// (header-only) ASC file.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class AscExporter : IFrameListener, IErrorTolerantExporter, IDisposable
{
    #region Public constants

    /// <summary>Fewest timestamp fraction digits <see cref="Builder.WithTimestampDecimals"/> accepts (microseconds).</summary>
    public const int MinTimestampDecimals = 6;

    /// <summary>Most timestamp fraction digits <see cref="Builder.WithTimestampDecimals"/> accepts (nanoseconds).</summary>
    public const int MaxTimestampDecimals = 9;

    #endregion

    #region SocketCAN constants

    /// <summary>SocketCAN FD format flag (byte 5, bit 2).</summary>
    private const byte _SocketCanFdfFlag = 0x04;

    /// <summary>SocketCAN FD bit-rate switch flag (byte 5, bit 0).</summary>
    private const byte _SocketCanBrsFlag = 0x01;

    /// <summary>SocketCAN FD error-state indicator flag (byte 5, bit 1).</summary>
    private const byte _SocketCanEsiFlag = 0x02;

    /// <summary>SocketCAN extended frame format flag (bit 31 of ID word).</summary>
    private const uint _SocketCanEffFlag = 0x80000000u;

    /// <summary>SocketCAN remote-transmission request flag (bit 30 of ID word).</summary>
    private const uint _SocketCanRtrFlag = 0x40000000u;

    /// <summary>SocketCAN frame header size: id(4) + dlc(1) + flags(1) + reserved(2) = 8 bytes.</summary>
    private const int _SocketCanHeaderSize = 8;

    /// <summary>
    /// CAN XL frame discriminator: byte 4, bit 7. CAN XL frames always set this bit;
    /// classic CAN DLC (0–8) and CAN FD DLC code (0–15) never reach 0x80, so this bit
    /// is a reliable variant indicator shared by both the protocol parser and the exporter.
    /// </summary>
    private const byte _SocketCanXlfFlag = 0x80;

    #endregion

    #region DLT_LIN constants (BLF-derived format)

    /// <summary>
    /// DLT_LIN header size (revision, reserved, dlc/type, pid, checksum, errors).
    /// </summary>
    private const int _LinHeaderSize = 8;

    /// <summary>Minimum valid DLT_LIN capture: 8-byte header.</summary>
    private const int _LinMinSize = _LinHeaderSize;

    /// <summary>Most data bytes a LIN frame carries (protocol fact); the 4-bit DLT_LIN length field can say up to 15.</summary>
    private const int _MaxLinDataLength = 8;

    #endregion

    #region DLT_FLEXRAY constants

    /// <summary>
    /// LINKTYPE_FLEXRAY header size: 7 bytes, the same value as <c>FlexRayLinkTypeFrame.MinHeaderSize</c>.
    /// Layout: measurement header (1 byte; bit 7 = channel B) | error flags (1 byte) | ISO 17458-2 frame header (5 bytes,
    /// bit-packed: indicator bits, 11-bit frame id, 7-bit payload word count, 11-bit header CRC, 6-bit cycle) | payload.
    /// Only used for the "too short" error message; the parsing itself is done by <c>FlexRayLinkTypeFrame</c>.
    /// </summary>
    private const int _DltFlexRayHeaderSize = 7;

    #endregion

    #region Instance fields

    /// <summary>Cooperative cancellation; checked per frame so a cancelled export stops without further writes.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>Maximum number of frames to write; 0 means unlimited.</summary>
    private readonly int _TargetFrameCount;

    /// <summary>Fraction digits of the line timestamps (6 to 9), handed to the <see cref="AscWriter"/>.</summary>
    private readonly int _TimestampDecimals;

    /// <summary>
    /// Channel per interface. Interfaces are immutable once registered, so the registry and property bag are consulted
    /// once per interface instead of once per frame.
    /// </summary>
    private readonly Dictionary<FrameInterfaceId, int> _InterfaceChannels = [];

    /// <summary>Output target. Cleared to <c>null</c> after disposal to prevent double-dispose.</summary>
    private ExportOutput? _Output;

    /// <summary>Active writer. Created on lazy init; <c>null</c> before the first frame.</summary>
    private AscWriter? _Writer;

    /// <summary>
    /// Anchor timestamp in nanoseconds for relative-time computation.
    /// Set to the first frame's timestamp before <see cref="_Start"/> is called.
    /// Set to 0 for the empty-export case.
    /// </summary>
    private long _AnchorNs;

    /// <summary>Latched fatal state (start failure or Strict-mode error); every later <see cref="OnFrame"/> returns <c>false</c>.</summary>
    private bool _HasError;

    /// <summary>Set once <see cref="_Start"/> ran, successful or not, so a failed start is never retried.</summary>
    private bool _Started;

    /// <summary>Latch that makes <see cref="OnFinish"/> idempotent and stops <see cref="OnFrame"/> after the footer is written.</summary>
    private bool _Finished;

    #endregion

    #region Constructor / factory

    /// <summary>Use <see cref="CreateBuilder"/> to obtain an instance.</summary>
    private AscExporter(
        ExportOutput output,
        string uiName,
        string? description,
        int targetFrameCount,
        int timestampDecimals,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _TargetFrameCount = targetFrameCount;
        _TimestampDecimals = timestampDecimals;
        _CancellationToken = cancellationToken;
    }

    /// <summary>Creates a new <see cref="Builder"/> for fluent configuration.</summary>
    public static Builder CreateBuilder() => new();

    #endregion

    #region IFrameListener

    /// <inheritdoc/>
    public string UiName { get; }

    /// <inheritdoc/>
    public string? Description { get; }

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

        // Lazy initialisation: open the file and write the ASC header on the first frame. The first frame's timestamp
        // becomes the time-zero anchor, so it must be set before the writer is created (the header date derives from it).
        // Deferring creation also means an export that never receives a frame does not touch the file system here.
        if (!_Started)
        {
            _AnchorNs = frame.Timestamp.AsNanos;
            if (!_Start())
            {
                return false;
            }
        }

        return _HandleFrame(frame);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }

        _Finished = true;

        // Shared finish sequence: finalize, then release every resource even when a step fails. The release order is
        // pooled buffers first and the output last (the output close flushes and may throw); ExporterFinish runs every step
        // regardless, and rethrows the collected release failures as one AggregateException afterwards.
        ExporterFinish.Run(
            "ASC",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("writer return", () => _Writer?.Return()),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>
    /// Writes the trigger-block footer. Starts the writer first when no frame ever arrived, so an empty export is still
    /// a valid (header-only) ASC file.
    /// </summary>
    private void _FinalizeFile()
    {
        // Empty export: no frame fixed the anchor, so the header carries the epoch-zero date.
        if (!_Started && !_HasError && _Output is not null)
        {
            _AnchorNs = 0L;
            _Start();
        }

        _Writer?.Finish();
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

    #endregion

    #region IExporterStatistics

    /// <summary>Number of frames successfully written.</summary>
    public int FrameCount { get; private set; }

    /// <inheritdoc/>
    public int WrittenCount => FrameCount;

    /// <inheritdoc/>
    public long EstimatedOutputBytes
    {
        get
        {
            AscWriter? writer = _Writer;
            if (writer is null)
            {
                return 0L;
            }

            return writer.EstimatedOutputBytes;
        }
    }

    /// <inheritdoc/>
    public int SkippedCount { get; private set; }

    /// <inheritdoc/>
    public int ErrorCount { get; private set; }

    /// <inheritdoc/>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>
    /// Returns <c>true</c> when the export has finished (via <see cref="OnFinish"/> or error),
    /// was cancelled, or has reached the configured target frame count.
    /// </summary>
    public bool IsFinished =>
        _Finished
        || _HasError
        || _CancellationToken.IsCancellationRequested
        || (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount);

    #endregion

    #region IErrorTolerantExporter

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Private implementation — start / handle / skip

    /// <summary>
    /// Lazily opens the output stream and creates the <see cref="AscWriter"/>, whose constructor emits the ASC file
    /// header. Runs at most once per exporter (<see cref="_Started"/> is set before any failure can happen), so a failed
    /// start is never retried with a half-initialised writer.
    /// </summary>
    /// <returns><c>true</c> if initialization succeeded; <c>false</c> when the output is gone (a disposed exporter: latched
    /// without an error count, so no error loop starts), the output could not be opened, or the writer could not be
    /// created. The last two are counted and reported through <see cref="ItemSkipped"/>.</returns>
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
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? stream, out string openFailure))
        {
            _ReportStartFailure($"ASC output could not be opened: {openFailure}");
            return false;
        }

        // The writer constructor emits the header into its output buffer, so a failure here is rare (the stream is only
        // touched on the first buffer flush, which surfaces as a failed frame write or finish error). Any failure is
        // reported as item 0 and ends the export.
        try
        {
            _Writer = new AscWriter(stream, _AnchorNs, _TimestampDecimals);
        }
        catch (Exception ex)
        {
            _ReportStartFailure($"ASC header write failed: {ex.Message}");
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

    /// <summary>
    /// Dispatches a single frame to the appropriate format-specific write path.
    /// Parses the raw binary frame bytes, resolves the channel number, and delegates
    /// to <see cref="AscWriter"/>. Handles I/O errors and unsupported link types
    /// according to the configured <see cref="ErrorTolerance"/>.
    /// </summary>
    private bool _HandleFrame(Frame frame)
    {
        // Target reached: refuse the frame so the driver stops. IsFinished reports the same condition to polling callers.
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

        // The span points into the frame's own memory; nothing is copied and nothing may keep it past this call.
        // currentIndex counts written plus skipped frames, i.e. the position of this frame among the frames offered.
        ReadOnlySpan<byte> data = frame.Data.Span;
        long timestampNs = frame.Timestamp.AsNanos;
        int currentIndex = FrameCount + SkippedCount;

        // Dispatch on the link type; each handler validates its own binary layout before writing a line.
        switch (frame.LinkType)
        {
            case LinkType.CanSocketcan:
            case LinkType.Can20B:
                // Byte 4 is the payload byte count for classic and FD frames (at most 64), so bit 7 is only ever set by
                // CAN XL frames. This is the same discriminator the protocol parser uses (see _SocketCanXlfFlag).
                if (data.Length > 4 && (data[4] & _SocketCanXlfFlag) != 0)
                {
                    if (data.Length < 12)
                    {
                        return _HandleSkip(
                            ExportErrorKind.MalformedData,
                            $"CAN XL frame is shorter than 12 bytes ({data.Length})",
                            currentIndex);
                    }

                    return _HandleCanXlFrame(data, timestampNs, currentIndex, frame);
                }

                return _HandleCanFrame(data, timestampNs, currentIndex, frame);

            case LinkType.Ethernet:
                return _HandleEthernetFrame(data, timestampNs, currentIndex, frame);

            case LinkType.Lin:
                return _HandleLinFrame(data, timestampNs, currentIndex, frame);

            case LinkType.Flexray:
                return _HandleFlexRayFrame(data, timestampNs, currentIndex);

            default:
                return _HandleSkip(
                    ExportErrorKind.UnsupportedType,
                    $"Unsupported link type: {frame.LinkType}",
                    currentIndex);
        }
    }

    /// <summary>
    /// Handles a SocketCAN frame (<see cref="LinkType.CanSocketcan"/> or
    /// <see cref="LinkType.Can20B"/>). Parses the binary SocketCAN header and
    /// dispatches to <see cref="AscWriter.WriteCanMessage"/> or
    /// <see cref="AscWriter.WriteCanFdMessage"/> based on the FDF flag.
    /// CAN XL frames are handled by <see cref="_HandleCanXlFrame"/> before this method runs.
    /// </summary>
    private bool _HandleCanFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        if (!_TryParseCanFrame(data,
            out uint rawCanId, out bool isExtended, out bool isRemote,
            out bool isFd, out bool brs, out bool esi, out byte dlc,
            out ReadOnlySpan<byte> payload))
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"CAN frame too short ({data.Length} bytes; minimum {_SocketCanHeaderSize})",
                currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        // A writer exception is an I/O failure of the target stream. It becomes a skip record (so Strict mode stops the
        // export) rather than escaping into the frame driver. FrameCount advances only after the line is fully written.
        try
        {
            if (isFd)
            {
                _Writer!.WriteCanFdMessage(timestampNs, channel, rawCanId, isExtended, brs, esi, dlc, payload);
            }
            else
            {
                _Writer!.WriteCanMessage(timestampNs, channel, rawCanId, isExtended, isRemote, dlc, payload);
            }
        }
        catch (Exception ex)
        {
            return _HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Writes a CAN XL frame as one <c>CANXL</c> / <c>XLFF</c> line.
    /// A buffer shorter than 12 bytes, or a payload length outside 1..2048, is malformed.
    /// </summary>
    private bool _HandleCanXlFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        if (data.Length < 12)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"CAN XL frame is shorter than 12 bytes ({data.Length})",
                currentIndex);
        }

        // Compact CAN XL layout: bytes 0-3 priority/VCID word (big-endian), byte 4 flags, byte 5 SDT, bytes 6-7 payload length
        // (little-endian), bytes 8-11 acceptance field, then the payload. A CAN XL data field holds 1 to 2048 bytes, so
        // anything else is corrupt; the length is also checked against the buffer because the writer slices the payload
        // from it without further validation.
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        if (length < 1 || length > 2048 || data.Length < 12 + length)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"CAN XL payload length {length} does not fit in {data.Length} bytes",
                currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        try
        {
            _Writer!.WriteCanXlFrame(timestampNs, channel, data);
        }
        catch (Exception ex)
        {
            return _HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Writes an Ethernet frame as one section 4.2 <c>ETH</c> line under <c>base hex</c>.
    /// Lengths outside 0..1518 are malformed. The line is not wrapped.
    /// </summary>
    private bool _HandleEthernetFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        // 1518 is the largest data length the ASC Ethernet record allows and the ASC reader accepts
        // (AscEthernetParser.MaxDataLength). A longer frame could not be read back, so it is rejected instead of written.
        if (data.Length > 1518)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"Ethernet frame length {data.Length} is outside 0..1518",
                currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        try
        {
            _Writer!.WriteEthernetFrame(timestampNs, channel, data);
        }
        catch (Exception ex)
        {
            return _HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a LIN frame (<see cref="LinkType.Lin"/>).
    /// Parses the BLF-derived DLT_LIN binary format and writes the ASC LIN line.
    /// </summary>
    private bool _HandleLinFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        if (!_TryParseLinFrame(data, out byte frameId, out ReadOnlySpan<byte> payload, out byte checksum, out byte checksumTypeBits))
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"LIN frame too short ({data.Length} bytes; minimum {_LinMinSize})",
                currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        try
        {
            _Writer!.WriteLinMessage(timestampNs, channel, frameId, payload, checksum, checksumTypeBits);
        }
        catch (Exception ex)
        {
            return _HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a FlexRay frame (<see cref="LinkType.Flexray"/>).
    /// Parses the LINKTYPE_FLEXRAY binary format and writes the ASC FlexRay line.
    /// The channel (1 = A, 2 = B) is derived from the channel bit (bit 7) of the measurement header in byte 0 rather
    /// than from the interface property, because the frame itself names the physical FlexRay channel it was captured on,
    /// whereas the interface channel is only a logical numbering of the capture source. Hence no
    /// <see cref="_TryGetChannel"/> call and no <c>Frame</c> parameter here.
    /// </summary>
    private bool _HandleFlexRayFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex)
    {
        if (!_TryParseFlexRayFrame(data,
            out byte channel, out ushort frameId, out byte cycle,
            out ushort headerCrc, out ReadOnlySpan<byte> payload))
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                $"FlexRay frame too short ({data.Length} bytes; minimum {_DltFlexRayHeaderSize})",
                currentIndex);
        }

        try
        {
            _Writer!.WriteFlexRayMessage(timestampNs, channel, frameId, cycle, headerCrc, payload);
        }
        catch (Exception ex)
        {
            return _HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Increments skip/error counters and either aborts the export (Strict mode) or
    /// fires <see cref="ItemSkipped"/> and continues (Tolerant mode).
    /// </summary>
    /// <returns>
    /// <c>false</c> in Strict mode (signals the caller to stop sending frames);
    /// <c>true</c> in Tolerant mode (export continues).
    /// </returns>
    private bool _HandleSkip(ExportErrorKind kind, string message, int itemIndex)
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

        // Strict: latch the error and stop (no ItemSkipped event; the false return and HasErrors are the signal).
        // Tolerant: report the item and keep exporting the following frames.
        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _HasError = true;
            return false;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = itemIndex,
            Kind = kind,
            Message = message,
        });
        return true;
    }

    #endregion

    #region Private static — frame parsers

    /// <summary>
    /// Parses a SocketCAN binary frame.
    /// </summary>
    /// <remarks>
    /// SocketCAN frame layout:
    /// <list type="bullet">
    /// <item>Bytes 0–3: CAN ID (big-endian uint32 with flag bits 31–29).</item>
    /// <item>Byte 4: Payload byte count (classic 0–8; FD 0–64) — not a DLC code.</item>
    /// <item>Byte 5: FD flags — bit 2 = FDF (FD Format), bit 1 = ESI, bit 0 = BRS.</item>
    /// <item>Byte 6: reserved. Byte 7: <c>len8_dlc</c> (classic CAN DLC 9–15, else 0).</item>
    /// <item>Bytes 8+: Data payload.</item>
    /// </list>
    /// The ASC writer expects a DLC code (0–15) for CAN FD lines, so FD byte counts are
    /// converted via <see cref="BlfConstants.GetCanFdDlcFromPayloadByteCount"/>.
    /// </remarks>
    private static bool _TryParseCanFrame(
        ReadOnlySpan<byte> data,
        out uint rawCanId, out bool isExtended, out bool isRemote,
        out bool isFd, out bool brs, out bool esi, out byte dlc,
        out ReadOnlySpan<byte> payload)
    {
        rawCanId = 0;
        isExtended = false;
        isRemote = false;
        isFd = false;
        brs = false;
        esi = false;
        dlc = 0;
        payload = default;

        if (data.Length < _SocketCanHeaderSize)
        {
            return false;
        }

        // Identifier word: bits 31-29 are the EFF/RTR/ERR flags, bits 28-0 the identifier (11-bit values sit in the low bits).
        // The mask drops the flags; EFF and RTR are read from the unmasked word.
        uint socketCanId = BinaryPrimitives.ReadUInt32BigEndian(data);
        rawCanId = socketCanId & 0x1FFFFFFF;
        isExtended = (socketCanId & _SocketCanEffFlag) != 0;
        isRemote = (socketCanId & _SocketCanRtrFlag) != 0;

        byte payloadByteCount = data[4];
        byte fdFlags = data[5];
        isFd = (fdFlags & _SocketCanFdfFlag) != 0;
        brs = (fdFlags & _SocketCanBrsFlag) != 0;
        esi = (fdFlags & _SocketCanEsiFlag) != 0;

        // The declared byte count is clamped to the bytes actually present (and to the protocol maximum) instead of
        // rejecting the frame: a truncated capture still exports the data it has, and the slice below cannot run past the
        // buffer even when the header lies about the length.
        int available = data.Length - _SocketCanHeaderSize;

        if (isFd)
        {
            // SocketCAN FD stores the actual payload byte count at offset 4 (0–64),
            // not a DLC code. Convert to ASC DLC (0–15) for the CANFD line syntax.
            // A clamped count that is not a valid FD size maps to the DLC that covers it (see the lookup table).
            int actualLen = Math.Min((int)payloadByteCount, 64);
            actualLen = Math.Min(actualLen, available);
            payload = data.Slice(_SocketCanHeaderSize, actualLen);
            dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount((byte)actualLen);
        }
        else
        {
            // Classic CAN: byte 4 is the payload count (0-8). A remote frame carries the requested length there but no
            // payload bytes, so its DLC is byte 4, not the bytes present. A bus DLC of 9-15 is kept in len8_dlc (byte 7).
            int classicLen = Math.Min((int)payloadByteCount, 8);
            classicLen = Math.Min(classicLen, available);
            payload = isRemote
                ? ReadOnlySpan<byte>.Empty
                : data.Slice(_SocketCanHeaderSize, classicLen);
            byte requested = (byte)Math.Min((int)payloadByteCount, 8);
            byte len8Dlc = data[BlfConstants.SocketCanLen8DlcOffset];
            dlc = len8Dlc is > 8 and <= 15
                ? len8Dlc
                : isRemote
                    ? requested
                    : (byte)classicLen;
        }

        return true;
    }

    /// <summary>
    /// Parses a DLT_LIN binary frame (LINKTYPE_LIN, 212).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Byte 0: message format revision.</item>
    /// <item>Byte 4: DLC in bits 7-4, checksum type in bits 1-0 (1 = classic, 2 = enhanced).</item>
    /// <item>Byte 5: protected identifier; the 6-bit frame id is recovered with <c>&amp; 0x3F</c> (with or without parity).</item>
    /// <item>Byte 6: checksum.</item>
    /// <item>Bytes 8+: data payload.</item>
    /// </list>
    /// </remarks>
    private static bool _TryParseLinFrame(
        ReadOnlySpan<byte> data,
        out byte frameId, out ReadOnlySpan<byte> payload, out byte checksum, out byte checksumTypeBits)
    {
        frameId = 0;
        payload = default;
        checksum = 0;
        checksumTypeBits = 0;

        if (data.Length < _LinMinSize)
        {
            return false;
        }

        // The protected identifier carries two parity bits in bits 7-6; the ASC line wants the plain 6-bit frame id.
        frameId = (byte)(data[5] & 0x3F);
        checksum = data[6];
        checksumTypeBits = (byte)(data[4] & 0x03);

        // The declared length is a 4-bit field (0-15) but a LIN frame carries 8 data bytes at most, so it is limited to 8
        // (a larger value is a corrupt header, and the ASC reader would clamp it anyway). It is also clamped to the bytes
        // present (truncated captures export what they have), so the slice below cannot run past the buffer. The buffer
        // holds at least the 8 header bytes (checked above), so the available count is never negative.
        int length = Math.Min(data[4] >> 4, _MaxLinDataLength);
        int available = data.Length - _LinHeaderSize;
        if (length > available)
        {
            length = available;
        }

        payload = data.Slice(_LinHeaderSize, length);
        return true;
    }

    /// <summary>
    /// Parses a LINKTYPE_FLEXRAY binary frame.
    /// </summary>
    /// <remarks>
    /// LINKTYPE_FLEXRAY layout (tcpdump DLT 210 / ISO 17458-2), parsed by <c>FlexRayLinkTypeFrame.TryParseDataFrame</c>:
    /// <list type="bullet">
    /// <item>Byte 0: Measurement header — bit 7 = Channel B, bits [6:0] = type index.</item>
    /// <item>Byte 1: Error flags (not exported to ASC).</item>
    /// <item>Bytes 2–6: ISO 17458-2 frame header (frame ID, payload length, HCRC, cycle).</item>
    /// <item>Bytes 7+: Data payload (0–254 bytes).</item>
    /// </list>
    /// </remarks>
    private static bool _TryParseFlexRayFrame(
        ReadOnlySpan<byte> data,
        out byte channel, out ushort frameId, out byte cycle,
        out ushort headerCrc, out ReadOnlySpan<byte> payload)
    {
        channel = 0;
        frameId = 0;
        cycle = 0;
        headerCrc = 0;
        payload = default;

        // Parsing and bounds checks (including payload length versus buffer size) are shared with the protocol layer
        // (one definition of the bit-packed header). Non-data frames (other measurement type index) are rejected there.
        if (!FlexRayLinkTypeFrame.TryParseDataFrame(data, out FlexRayLinkTypeFrame.Fields fields, out payload))
        {
            return false;
        }

        // ASC FlexRay channels are numbered 1 (A) and 2 (B), not the raw bit.
        channel = (byte)FlexRayLinkTypeFrame.BusChannelToAscChannel(fields.ChannelB);
        frameId = fields.FrameId;
        cycle = fields.Cycle;
        headerCrc = fields.HeaderCrc;
        return true;
    }

    /// <summary>
    /// Resolves the channel number of the frame's interface once per interface, with the lookup shared with the BLF
    /// exporter (<see cref="InterfaceChannelConverter.TryGetChannel"/>: <c>blf.channel</c>, <c>asc.channel</c>, a trailing
    /// number in the interface name). Falls back to channel 1 when the interface has no usable hint.
    /// </summary>
    /// <param name="frame">The frame whose interface to inspect.</param>
    /// <param name="channel">Resolved channel number (also set when the result is <see langword="false"/>, for the message).</param>
    /// <returns><see langword="false"/> when the channel is negative and cannot be written to an ASC line.</returns>
    private bool _TryGetChannel(Frame frame, out int channel)
    {
        const int defaultChannel = 1;
        if (!frame.HasInterface)
        {
            channel = defaultChannel;
            return true;
        }

        // Cache hit: no registry lookup and no property parsing per frame. Dictionary is safe here because the exporter is
        // single-threaded (see the class remarks).
        if (_InterfaceChannels.TryGetValue(frame.InterfaceId, out int cached))
        {
            channel = cached;
            return cached >= 0;
        }

        // Cache miss: resolve once. The fallback is cached too, so an interface without a usable hint is not re-parsed.
        // The property value is not range-checked by the converter. A negative channel would be written as "-3" in the
        // channel column, which no ASC reader accepts, so such frames are rejected (the BLF exporter validates its channel
        // the same way). The upper end is not limited here: the ASC format fixes no common maximum for all bus types.
        channel = defaultChannel;
        if (frame.Registry.TryGet(frame.InterfaceId, out FrameInterfaceInfo? info)
            && InterfaceChannelConverter.TryGetChannel(info, out int resolved, out _) == InterfaceChannelLookup.Found)
        {
            channel = resolved;
        }

        _InterfaceChannels[frame.InterfaceId] = channel;
        return channel >= 0;
    }

    /// <summary>Skips a frame whose interface channel is negative (see <see cref="_TryGetChannel"/>).</summary>
    private bool _HandleInvalidChannel(int channel, int currentIndex) =>
        _HandleSkip(
            ExportErrorKind.SerializationError,
            $"ASC channel value '{channel.ToString(CultureInfo.InvariantCulture)}' is negative and cannot be written.",
            currentIndex);

    #endregion

    #region Builder

    /// <summary>
    /// Fluent builder for constructing an <see cref="AscExporter"/>.
    /// Not thread-safe; configure and call <see cref="Build"/> from a single thread.
    /// Single-use: do not reuse a builder after calling <see cref="Build"/>.
    /// </summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "ASC Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private int _TargetFrameCount;
        private int _TimestampDecimals = MinTimestampDecimals;

        #region Output target

        /// <summary>
        /// Directs output to a file at the given path.
        /// The file is created lazily on the first successful write path (including empty
        /// <see cref="OnFinish"/> which still emits a valid ASC header + empty trigger block).
        /// Calling this method more than once replaces the previous output target.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Directs output to an existing writable stream.
        /// The caller retains ownership; the stream is not disposed by the exporter.
        /// Calling this method more than once replaces the previous output target.
        /// </summary>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>
        /// Directs output to the standard output stream (stdout).
        /// The stdout stream is owned by the exporter and closed on finish.
        /// Calling this method more than once replaces the previous output target. The standard output stream is opened
        /// immediately by this call, not lazily on the first frame.
        /// </summary>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        #endregion

        #region Configuration

        /// <summary>Sets the user-visible display name shown in UI and logs.</summary>
        public Builder WithUiName(string uiName)
        {
            _UiName = uiName;
            return this;
        }

        /// <summary>Sets an optional human-readable description of this export.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>
        /// Sets a cancellation token that can abort the export.
        /// After cancellation, <see cref="OnFrame"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Limits the total number of frames to export.
        /// When the target is reached, <see cref="OnFrame"/> returns <c>false</c>.
        /// Pass <c>0</c> (the default) for no limit.
        /// </summary>
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
        /// Sets the number of fraction digits of the line timestamps: <see cref="MinTimestampDecimals"/> (6, microseconds,
        /// the default and what CANoe writes) to <see cref="MaxTimestampDecimals"/> (9, nanoseconds). Values are rounded
        /// half up; 9 keeps the full nanosecond resolution of the source frames (the ASC reader keeps nine digits).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="decimals"/> is outside 6..9.</exception>
        public Builder WithTimestampDecimals(int decimals)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(decimals, MinTimestampDecimals);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, MaxTimestampDecimals);
            _TimestampDecimals = decimals;
            return this;
        }

        #endregion

        /// <summary>
        /// Validates the configuration and creates the <see cref="AscExporter"/>.
        /// No file I/O occurs until the first frame is written.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No output destination was configured. Call <see cref="ToFile"/>,
        /// <see cref="ToStream"/>, or <see cref="ToStdout"/> before building.
        /// </exception>
        public AscExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "Output destination must be set via ToFile(), ToStream(), or ToStdout().");
            }

            return new AscExporter(
                _Output,
                _UiName,
                _Description,
                _TargetFrameCount,
                _TimestampDecimals,
                _CancellationToken);
        }
    }

    #endregion
}
