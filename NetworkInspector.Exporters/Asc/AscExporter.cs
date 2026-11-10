// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using BlfObjectPayloads = NetworkInspector.Exporters.Blf.BlfObjectPayloads;

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
/// A CAN or LIN frame whose header declares more data bytes than its type carries (CAN FD above 64, classic CAN above 8, LIN
/// above 8) is corrupt and skipped as <see cref="ExportErrorKind.MalformedData"/>. A frame that declares more bytes than the
/// buffer holds (a capture cut by a snap length) is written with the bytes present; the first such CAN frame and the first such
/// LIN frame each raise one advisory <see cref="ItemSkipped"/> event (<see cref="ExportErrorKind.MalformedData"/>, not counted
/// as skipped or as an error).
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
    #region Timestamp precision range

    /// <summary>Fewest timestamp fraction digits <see cref="Builder.WithTimestampDecimals"/> accepts (microseconds).</summary>
    public const int MinTimestampDecimals = 6;

    /// <summary>Most timestamp fraction digits <see cref="Builder.WithTimestampDecimals"/> accepts (nanoseconds).</summary>
    public const int MaxTimestampDecimals = 9;

    #endregion

    #region Frame formats

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

    /// <summary>
    /// Most data bytes a classic CAN frame carries (protocol fact). A bus DLC of 9-15 still carries 8 bytes and travels in
    /// <c>len8_dlc</c> (byte 7), so a byte count above 8 in byte 4 is corrupt.
    /// </summary>
    private const int _MaxClassicCanDataLength = 8;

    // The most data bytes a CAN FD frame carries (protocol fact) is BlfObjectPayloads.MaxCanFdDataLength: one definition for
    // both frame exporters, so they refuse the same corrupt count.

    /// <summary>
    /// DLT_LIN header size (revision, reserved, dlc/type, pid, checksum, errors).
    /// </summary>
    private const int _LinHeaderSize = 8;

    /// <summary>Minimum valid DLT_LIN capture: 8-byte header.</summary>
    private const int _LinMinSize = _LinHeaderSize;

    /// <summary>Most data bytes a LIN frame carries (protocol fact); the 4-bit DLT_LIN length field can say up to 15.</summary>
    private const int _MaxLinDataLength = 8;

    /// <summary>
    /// LINKTYPE_FLEXRAY header size: 7 bytes, the same value as <c>FlexRayLinkTypeFrame.MinHeaderSize</c>.
    /// Layout: measurement header (1 byte; bit 7 = channel B) | error flags (1 byte) | ISO 17458-2 frame header (5 bytes,
    /// bit-packed: indicator bits, 11-bit frame id, 7-bit payload word count, 11-bit header CRC, 6-bit cycle) | payload.
    /// Only used for the "too short" error message; the parsing itself is done by <c>FlexRayLinkTypeFrame</c>.
    /// </summary>
    private const int _DltFlexRayHeaderSize = 7;

    #endregion

    #region Configuration and state

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

    /// <summary>
    /// Set once the advisory about a CAN frame that carries fewer data bytes than it declares was raised, so a capture cut by
    /// a snap length reports it once per export (one exporter is one export) and not once per frame.
    /// </summary>
    private bool _ShortCanFrameReported;

    /// <summary>
    /// Set once the advisory about a LIN frame that carries fewer data bytes than it declares was raised, so a capture cut by a
    /// snap length reports it once per export (one exporter is one export) and not once per frame.
    /// </summary>
    private bool _ShortLinFrameReported;

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

    #region Lifecycle

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
    /// <param name="kind">Classification of the failed finish step.</param>
    /// <param name="message">Human-readable description of the failure.</param>
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

    #region Statistics

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

    #region Error tolerance

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion

    #region Start

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
            _ReportStartFailure(Invariant($"ASC output could not be opened: {openFailure}"));
            return false;
        }

        // The writer constructor emits the header into its output buffer, so a failure here is rare (the stream is only
        // touched on the first buffer flush, which surfaces as a failed frame write or finish error). Any failure is
        // reported as item 0 and ends the export.
        try
        {
            _Writer = new AscWriter(stream, _AnchorNs, _TimestampDecimals);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ReportStartFailure(Invariant($"ASC header write failed: {ex.Message}"));
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    /// <param name="message">Human-readable description of why the export could not start.</param>
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

    #endregion

    #region Frame dispatch and writing

    /// <summary>
    /// Dispatches a single frame to the appropriate format-specific write path.
    /// Parses the raw binary frame bytes, resolves the channel number, and delegates
    /// to <see cref="AscWriter"/>. Handles I/O errors and unsupported link types
    /// according to the configured <see cref="ErrorTolerance"/>.
    /// </summary>
    /// <param name="frame">The frame to export; its data is read in place and not kept past the call.</param>
    /// <returns>
    /// <see langword="true"/> when the export goes on (the frame was written, or skipped in tolerant mode);
    /// <see langword="false"/> when the target frame count is reached or a strict-mode error stopped the export.
    /// </returns>
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
                            Invariant($"CAN XL frame is shorter than 12 bytes ({data.Length})"),
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
                    Invariant($"Unsupported link type: {frame.LinkType}"),
                    currentIndex);
        }
    }

    /// <summary>
    /// Handles a SocketCAN frame (<see cref="LinkType.CanSocketcan"/> or
    /// <see cref="LinkType.Can20B"/>). Parses the binary SocketCAN header and
    /// dispatches to <see cref="AscWriter.WriteCanMessage"/> or
    /// <see cref="AscWriter.WriteCanFdMessage"/> based on the FDF flag.
    /// CAN XL frames are handled by <see cref="_HandleCanXlFrame"/> before this method runs.
    /// A frame whose declared payload count is above the protocol maximum is corrupt and skipped as
    /// <see cref="ExportErrorKind.MalformedData"/>; a frame that declares more bytes than it carries (a capture cut by a snap
    /// length) is written with the bytes present and reported once through <see cref="ItemSkipped"/>.
    /// </summary>
    /// <param name="data">The SocketCAN frame bytes.</param>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <param name="frame">The frame, for the interface channel.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleCanFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        CanParseResult parsed = _ParseCanFrame(data,
            out uint rawCanId, out bool isExtended, out bool isRemote,
            out bool isFd, out bool brs, out bool esi, out byte dlc,
            out int declaredCount, out ReadOnlySpan<byte> payload);
        if (parsed == CanParseResult.TooShort)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                Invariant($"CAN frame too short ({data.Length} bytes; minimum {_SocketCanHeaderSize})"),
                currentIndex);
        }

        // Security: the declared count is a limit the input itself states, so a count above the protocol maximum is refused
        // and named instead of being clamped into a plausible shorter frame. Rejected: clamping (a corrupt capture would
        // convert without a message). Residual risk: none; the frame is skipped and counted.
        if (parsed == CanParseResult.CountAboveMaximum)
        {
            return _HandleSkip(ExportErrorKind.MalformedData, _DescribeCountAboveMaximum(isFd, declaredCount), currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        // A write exception becomes a skip record (so Strict mode stops the export) rather than escaping into the frame
        // driver; only an I/O exception is an I/O error. FrameCount advances only after the line is fully written.
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return _HandleWriteFailure(ex, currentIndex);
        }

        FrameCount++;

        // The advisory comes after the frame is counted and outside the try block, so a handler that throws is not mistaken
        // for a write failure.
        if (parsed == CanParseResult.Truncated)
        {
            _ReportShortCanFrame(declaredCount, payload.Length, currentIndex);
        }

        return true;
    }

    /// <summary>
    /// Raises the advisory about a CAN frame that carries fewer data bytes than it declares (a capture cut by a snap
    /// length), at most once per export. The frame is written with the bytes present, so it counts as written and is not
    /// counted as skipped or as an error.
    /// </summary>
    /// <param name="declaredCount">The data byte count the frame header declares.</param>
    /// <param name="presentCount">The data bytes the frame carries.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the event.</param>
    private void _ReportShortCanFrame(int declaredCount, int presentCount, int currentIndex)
    {
        // Security: a silent cut would turn a truncated capture into a plausible shorter trace. One event per export tells
        // the caller; rejected: one event per frame, which floods a session of cut frames (the PCAPNG exporter reports its
        // snap-length cut the same way). Residual risk: later cut frames are not named.
        if (_ShortCanFrameReported)
        {
            return;
        }

        _ShortCanFrameReported = true;
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = currentIndex,
            Kind = ExportErrorKind.MalformedData,
            Message = Invariant($"ASC: a CAN frame declares {declaredCount} data bytes but carries {presentCount}; ")
                + "it is written with the bytes present (reported once per export; later cut frames are written the same way).",
        });
    }

    /// <summary>
    /// Writes a CAN XL frame as one <c>CANXL</c> / <c>XLFF</c> line.
    /// A buffer shorter than 12 bytes, or a payload length outside 1..2048, is malformed.
    /// </summary>
    /// <param name="data">The compact CAN XL frame bytes (12-byte header plus payload).</param>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <param name="frame">The frame, for the interface channel.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleCanXlFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        if (data.Length < 12)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                Invariant($"CAN XL frame is shorter than 12 bytes ({data.Length})"),
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
                Invariant($"CAN XL payload length {length} does not fit in {data.Length} bytes"),
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return _HandleWriteFailure(ex, currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Writes an Ethernet frame as one section 4.2 <c>ETH</c> line under <c>base hex</c>.
    /// Lengths outside 0..1518 are malformed. The line is not wrapped.
    /// </summary>
    /// <param name="data">The Ethernet frame bytes, starting at the destination MAC, without the CRC.</param>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <param name="frame">The frame, for the interface channel.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleEthernetFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        // 1518 is the largest data length the ASC Ethernet record allows and the ASC reader accepts
        // (AscEthernetParser.MaxDataLength). A longer frame could not be read back, so it is rejected instead of written.
        if (data.Length > 1518)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                Invariant($"Ethernet frame length {data.Length} is outside 0..1518"),
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return _HandleWriteFailure(ex, currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a LIN frame (<see cref="LinkType.Lin"/>).
    /// Parses the BLF-derived DLT_LIN binary format and writes the ASC LIN line.
    /// A frame whose declared data length is above the LIN maximum is corrupt and skipped as
    /// <see cref="ExportErrorKind.MalformedData"/>; a frame that declares more bytes than it carries (a capture cut by a snap
    /// length) is written with the bytes present and reported once through <see cref="ItemSkipped"/>, like a cut CAN frame.
    /// </summary>
    /// <param name="data">The DLT_LIN frame bytes.</param>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <param name="frame">The frame, for the interface channel.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleLinFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex, Frame frame)
    {
        LinParseResult parsed = _ParseLinFrame(
            data, out byte frameId, out int declaredLength, out ReadOnlySpan<byte> payload, out byte checksum, out byte checksumTypeBits);
        if (parsed == LinParseResult.TooShort)
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                Invariant($"LIN frame too short ({data.Length} bytes; minimum {_LinMinSize})"),
                currentIndex);
        }

        // Security: the declared length is a limit the input itself states, so a length above the protocol maximum is refused
        // and named instead of being clamped into a plausible shorter frame. Rejected: clamping (a corrupt capture would
        // convert without a message). Residual risk: none; the frame is skipped and counted.
        if (parsed == LinParseResult.LengthAboveMaximum)
        {
            return _HandleSkip(ExportErrorKind.MalformedData, _DescribeLinLengthAboveMaximum(declaredLength), currentIndex);
        }

        if (!_TryGetChannel(frame, out int channel))
        {
            return _HandleInvalidChannel(channel, currentIndex);
        }

        try
        {
            _Writer!.WriteLinMessage(timestampNs, channel, frameId, payload, checksum, checksumTypeBits);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return _HandleWriteFailure(ex, currentIndex);
        }

        FrameCount++;

        // The advisory comes after the frame is counted and outside the try block, so a handler that throws is not mistaken
        // for a write failure.
        if (parsed == LinParseResult.Truncated)
        {
            _ReportShortLinFrame(declaredLength, payload.Length, currentIndex);
        }

        return true;
    }

    /// <summary>
    /// Raises the advisory about a LIN frame that carries fewer data bytes than it declares (a capture cut by a snap
    /// length), at most once per export. The frame is written with the bytes present, so it counts as written and is not
    /// counted as skipped or as an error.
    /// </summary>
    /// <param name="declaredLength">The data byte count the DLT_LIN header declares.</param>
    /// <param name="presentCount">The data bytes the frame carries.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the event.</param>
    private void _ReportShortLinFrame(int declaredLength, int presentCount, int currentIndex)
    {
        // Security: a silent cut would turn a truncated capture into a plausible shorter trace. One event per export tells
        // the caller; rejected: one event per frame, which floods a session of cut frames (the PCAPNG exporter reports its
        // snap-length cut the same way). Residual risk: later cut frames are not named.
        if (_ShortLinFrameReported)
        {
            return;
        }

        _ShortLinFrameReported = true;
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = currentIndex,
            Kind = ExportErrorKind.MalformedData,
            Message = Invariant($"ASC: a LIN frame declares {declaredLength} data bytes but carries {presentCount}; ")
                + "it is written with the bytes present (reported once per export; later cut frames are written the same way).",
        });
    }

    /// <summary>
    /// Handles a FlexRay frame (<see cref="LinkType.Flexray"/>).
    /// Parses the LINKTYPE_FLEXRAY binary format and writes the ASC FlexRay line.
    /// The channel (1 = A, 2 = B) is derived from the channel bit (bit 7) of the measurement header in byte 0 rather
    /// than from the interface property, because the frame itself names the physical FlexRay channel it was captured on,
    /// whereas the interface channel is only a logical numbering of the capture source. Hence no
    /// <see cref="_TryGetChannel"/> call and no <c>Frame</c> parameter here.
    /// </summary>
    /// <param name="data">The LINKTYPE_FLEXRAY frame bytes.</param>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleFlexRayFrame(
        ReadOnlySpan<byte> data, long timestampNs, int currentIndex)
    {
        if (!_TryParseFlexRayFrame(data,
            out byte channel, out ushort frameId, out byte cycle,
            out ushort headerCrc, out ReadOnlySpan<byte> payload))
        {
            return _HandleSkip(
                ExportErrorKind.MalformedData,
                Invariant($"FlexRay frame too short ({data.Length} bytes; minimum {_DltFlexRayHeaderSize})"),
                currentIndex);
        }

        try
        {
            _Writer!.WriteFlexRayMessage(timestampNs, channel, frameId, cycle, headerCrc, payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return _HandleWriteFailure(ex, currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Skips a frame whose line could not be written. An <see cref="IOException"/> is an output failure
    /// (<see cref="ExportErrorKind.IoError"/>); any other exception, such as the line buffer limit or an argument error of the
    /// line writer, is a formatting failure (<see cref="ExportErrorKind.SerializationError"/>), so a caller that retries on
    /// another target for an I/O error does not retry a frame that can never be formatted.
    /// </summary>
    /// <param name="ex">The exception of the line writer.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleWriteFailure(Exception ex, int currentIndex)
    {
        ExportErrorKind kind = ex is IOException
            ? ExportErrorKind.IoError
            : ExportErrorKind.SerializationError;
        return _HandleSkip(kind, Invariant($"Write failed: {ex.Message}"), currentIndex);
    }

    /// <summary>
    /// Increments skip/error counters and either aborts the export (Strict mode) or
    /// fires <see cref="ItemSkipped"/> and continues (Tolerant mode).
    /// </summary>
    /// <param name="kind">Classification of the skipped frame.</param>
    /// <param name="message">Human-readable reason, raised through <see cref="ItemSkipped"/> in tolerant mode.</param>
    /// <param name="itemIndex">Index of the frame among the offered frames.</param>
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

    #region Frame parsing

    /// <summary>Verdict of <see cref="_ParseCanFrame"/> on a SocketCAN classic or FD frame.</summary>
    private enum CanParseResult
    {
        /// <summary>The frame holds every data byte its header declares.</summary>
        Complete,

        /// <summary>The header declares more data bytes than the buffer holds (a capture cut by a snap length).</summary>
        Truncated,

        /// <summary>The buffer is shorter than the 8-byte SocketCAN header.</summary>
        TooShort,

        /// <summary>The header declares more data bytes than the frame type can carry (FD above 64, classic above 8).</summary>
        CountAboveMaximum,
    }

    /// <summary>Verdict of <see cref="_ParseLinFrame"/> on a DLT_LIN frame.</summary>
    private enum LinParseResult
    {
        /// <summary>The frame holds every data byte its header declares.</summary>
        Complete,

        /// <summary>The header declares more data bytes than the buffer holds (a capture cut by a snap length).</summary>
        Truncated,

        /// <summary>The buffer is shorter than the 8-byte DLT_LIN header.</summary>
        TooShort,

        /// <summary>The header declares more data bytes than a LIN frame can carry (above 8).</summary>
        LengthAboveMaximum,
    }

    /// <summary>
    /// Names the protocol limit a corrupt LIN data length exceeds. Runs only for a refused frame (cold path).
    /// </summary>
    /// <param name="declaredLength">The data length the DLT_LIN header declares.</param>
    /// <returns>The error message for <see cref="ExportErrorKind.MalformedData"/>.</returns>
    private static string _DescribeLinLengthAboveMaximum(int declaredLength) =>
        Invariant($"ASC LIN frame declares {declaredLength} data bytes; a LIN frame carries at most {_MaxLinDataLength}.");

    /// <summary>
    /// Names the protocol limit a corrupt CAN data byte count exceeds. Runs only for a refused frame (cold path).
    /// </summary>
    /// <param name="isFd">Whether the frame is a CAN FD frame.</param>
    /// <param name="declaredCount">The data byte count the frame header declares.</param>
    /// <returns>The error message for <see cref="ExportErrorKind.MalformedData"/>.</returns>
    private static string _DescribeCountAboveMaximum(bool isFd, int declaredCount)
    {
        if (isFd)
        {
            return Invariant(
                $"ASC CAN FD frame declares {declaredCount} data bytes; a CAN FD frame carries at most {BlfObjectPayloads.MaxCanFdDataLength}.");
        }

        return Invariant($"ASC CAN frame declares {declaredCount} data bytes; a classic CAN frame carries at most {_MaxClassicCanDataLength} ")
            + "(a bus DLC of 9-15 belongs in len8_dlc).";
    }

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
    /// <para>
    /// A byte count above the protocol maximum (FD above 64, classic above 8) is corrupt and refused. A count within the
    /// maximum but above the bytes present is a cut capture: the payload holds the bytes present and the verdict is
    /// <see cref="CanParseResult.Truncated"/>. A remote frame carries no data bytes by design, so it is never truncated.
    /// </para>
    /// </remarks>
    /// <param name="data">The SocketCAN frame bytes.</param>
    /// <param name="rawCanId">The identifier without the flag bits (bits 28-0).</param>
    /// <param name="isExtended">Whether the frame uses the extended identifier format.</param>
    /// <param name="isRemote">Whether the frame is a remote transmission request.</param>
    /// <param name="isFd">Whether the frame is a CAN FD frame.</param>
    /// <param name="brs">The FD bit-rate-switch flag.</param>
    /// <param name="esi">The FD error-state indicator flag.</param>
    /// <param name="dlc">The bus DLC code (0-15) to write.</param>
    /// <param name="declaredCount">The payload byte count the header declares (byte 4); 0 for a buffer that is too short.</param>
    /// <param name="payload">The data bytes to write: the declared count, limited to the bytes present; empty for a remote
    /// frame.</param>
    /// <returns>The verdict; <paramref name="payload"/> and <paramref name="dlc"/> are only meaningful for
    /// <see cref="CanParseResult.Complete"/> and <see cref="CanParseResult.Truncated"/>.</returns>
    private static CanParseResult _ParseCanFrame(
        ReadOnlySpan<byte> data,
        out uint rawCanId, out bool isExtended, out bool isRemote,
        out bool isFd, out bool brs, out bool esi, out byte dlc,
        out int declaredCount, out ReadOnlySpan<byte> payload)
    {
        rawCanId = 0;
        isExtended = false;
        isRemote = false;
        isFd = false;
        brs = false;
        esi = false;
        dlc = 0;
        declaredCount = 0;
        payload = default;

        if (data.Length < _SocketCanHeaderSize)
        {
            return CanParseResult.TooShort;
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

        // A declared count above the protocol maximum is refused (the caller names the limit). Below it, the count is limited
        // to the bytes actually present: a capture cut by a snap length still exports the data it has, the caller reports that
        // once, and the slice below cannot run past the buffer even when the header lies about the length.
        declaredCount = payloadByteCount;
        int available = data.Length - _SocketCanHeaderSize;

        if (isFd)
        {
            if (payloadByteCount > BlfObjectPayloads.MaxCanFdDataLength)
            {
                return CanParseResult.CountAboveMaximum;
            }

            // SocketCAN FD stores the actual payload byte count at offset 4 (0–64),
            // not a DLC code. Convert to ASC DLC (0–15) for the CANFD line syntax.
            // A cut count that is not a valid FD size maps to the DLC that covers it (see the lookup table).
            int actualLen = Math.Min((int)payloadByteCount, available);
            payload = data.Slice(_SocketCanHeaderSize, actualLen);
            dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount((byte)actualLen);
            return actualLen < payloadByteCount
                ? CanParseResult.Truncated
                : CanParseResult.Complete;
        }

        // Classic CAN: byte 4 is the payload count (0-8). A bus DLC of 9-15 is kept in len8_dlc (byte 7), never in byte 4, so
        // a larger count is corrupt rather than a well-formed long-DLC frame.
        if (payloadByteCount > _MaxClassicCanDataLength)
        {
            return CanParseResult.CountAboveMaximum;
        }

        // A remote frame carries the requested length in byte 4 but no payload bytes, so its DLC is byte 4, not the bytes
        // present, and it is never truncated.
        int classicLen = Math.Min((int)payloadByteCount, available);
        payload = isRemote
            ? ReadOnlySpan<byte>.Empty
            : data.Slice(_SocketCanHeaderSize, classicLen);
        byte len8Dlc = data[BlfConstants.SocketCanLen8DlcOffset];
        dlc = len8Dlc is > 8 and <= 15
            ? len8Dlc
            : isRemote
                ? payloadByteCount
                : (byte)classicLen;
        return !isRemote && classicLen < payloadByteCount
            ? CanParseResult.Truncated
            : CanParseResult.Complete;
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
    /// <para>
    /// A declared length above the LIN maximum (8) is corrupt and refused. A length within the maximum but above the bytes
    /// present is a cut capture: the payload holds the bytes present and the verdict is <see cref="LinParseResult.Truncated"/>.
    /// A frame that holds every declared byte is <see cref="LinParseResult.Complete"/>; bytes after the declared length are
    /// ignored.
    /// </para>
    /// </remarks>
    /// <param name="data">The DLT_LIN frame bytes.</param>
    /// <param name="frameId">The plain 6-bit frame id (protected-identifier parity bits removed).</param>
    /// <param name="declaredLength">The data length the header declares (byte 4, bits 7-4; 0-15); 0 for a buffer that is too short.</param>
    /// <param name="payload">The data bytes to write: the declared length, limited to the bytes present.</param>
    /// <param name="checksum">The checksum byte.</param>
    /// <param name="checksumTypeBits">The checksum-type bits: 1 = classic, 2 = enhanced, 0 = not stated.</param>
    /// <returns>The verdict; the outputs other than <paramref name="declaredLength"/> are only meaningful for
    /// <see cref="LinParseResult.Complete"/> and <see cref="LinParseResult.Truncated"/> (default for
    /// <see cref="LinParseResult.TooShort"/>).</returns>
    private static LinParseResult _ParseLinFrame(
        ReadOnlySpan<byte> data,
        out byte frameId, out int declaredLength, out ReadOnlySpan<byte> payload, out byte checksum, out byte checksumTypeBits)
    {
        frameId = 0;
        declaredLength = 0;
        payload = default;
        checksum = 0;
        checksumTypeBits = 0;

        if (data.Length < _LinMinSize)
        {
            return LinParseResult.TooShort;
        }

        // The protected identifier carries two parity bits in bits 7-6; the ASC line wants the plain 6-bit frame id.
        frameId = (byte)(data[5] & 0x3F);
        checksum = data[6];
        checksumTypeBits = (byte)(data[4] & 0x03);

        // The declared length is a 4-bit field (0-15) but a LIN frame carries 8 data bytes at most, so a larger value is a
        // corrupt header that the caller refuses and names. Below the maximum the length is limited to the bytes present
        // (a capture cut by a snap length still exports the data it has, and the caller reports that once), so the slice below
        // cannot run past the buffer. The buffer holds at least the 8 header bytes (checked above), so the available count is
        // never negative.
        declaredLength = data[4] >> 4;
        if (declaredLength > _MaxLinDataLength)
        {
            return LinParseResult.LengthAboveMaximum;
        }

        int length = Math.Min(declaredLength, data.Length - _LinHeaderSize);
        payload = data.Slice(_LinHeaderSize, length);
        return length < declaredLength
            ? LinParseResult.Truncated
            : LinParseResult.Complete;
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
    /// <param name="data">The LINKTYPE_FLEXRAY frame bytes.</param>
    /// <param name="channel">The ASC FlexRay channel: 1 = A, 2 = B.</param>
    /// <param name="frameId">The 11-bit slot id.</param>
    /// <param name="cycle">The cycle counter (0-63).</param>
    /// <param name="headerCrc">The header CRC (11 bits).</param>
    /// <param name="payload">The data bytes, at most 254.</param>
    /// <returns><see langword="false"/> when the buffer is no complete FlexRay data frame; the outputs are then default.</returns>
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

    #endregion

    #region Channel resolution

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
    /// <param name="channel">The negative channel value, named in the error message.</param>
    /// <param name="currentIndex">Index of the frame among the offered frames, for the error event.</param>
    /// <returns>Whether the export goes on (see <see cref="_HandleSkip"/>).</returns>
    private bool _HandleInvalidChannel(int channel, int currentIndex) =>
        _HandleSkip(
            ExportErrorKind.SerializationError,
            Invariant($"ASC channel value '{channel.ToString(CultureInfo.InvariantCulture)}' is negative and cannot be written."),
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
        /// <summary>Output target chosen by <see cref="ToFile"/>, <see cref="ToStream"/>, or <see cref="ToStdout"/>; <c>null</c> until set.</summary>
        private ExportOutput? _Output;

        /// <summary>Display name; replaced by <see cref="WithUiName"/>.</summary>
        private string _UiName = "ASC Exporter";

        /// <summary>Optional description; replaced by <see cref="WithDescription"/>.</summary>
        private string? _Description;

        /// <summary>Cancellation token; replaced by <see cref="WithCancellationToken"/>.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>Frames to write at most; 0 means unlimited (see <see cref="WithTargetFrameCount"/>).</summary>
        private int _TargetFrameCount;

        /// <summary>Fraction digits of the line timestamps (see <see cref="WithTimestampDecimals"/>).</summary>
        private int _TimestampDecimals = MinTimestampDecimals;

        #region Output target

        /// <summary>
        /// Directs output to a file at the given path.
        /// The file is created lazily on the first successful write path (including empty
        /// <see cref="OnFinish"/> which still emits a valid ASC header + empty trigger block).
        /// Calling this method more than once replaces the previous output target.
        /// </summary>
        /// <param name="path">Path of the file to create; relative paths resolve against the working directory.</param>
        /// <returns>This builder.</returns>
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
        /// <param name="stream">The writable stream to write to.</param>
        /// <returns>This builder.</returns>
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
        /// <returns>This builder.</returns>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        #endregion

        #region Configuration

        /// <summary>Sets the user-visible display name shown in UI and logs.</summary>
        /// <param name="uiName">Display name; not empty or white space.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="uiName"/> is null, empty, or white space.</exception>
        public Builder WithUiName(string uiName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(uiName);
            _UiName = uiName;
            return this;
        }

        /// <summary>Sets an optional human-readable description of this export.</summary>
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>
        /// Sets a cancellation token that can abort the export.
        /// After cancellation, <see cref="OnFrame"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        /// <param name="token">The token to observe; checked once per frame.</param>
        /// <returns>This builder.</returns>
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
        /// <param name="count">Frames to write at most; 0 for no limit.</param>
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
                    Invariant($"Target frame count must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} ") +
                    Invariant($"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)})."));
            }

            _TargetFrameCount = count;
            return this;
        }

        /// <summary>
        /// Sets the number of fraction digits of the line timestamps: <see cref="MinTimestampDecimals"/> (6, microseconds,
        /// the default and what CANoe writes) to <see cref="MaxTimestampDecimals"/> (9, nanoseconds). Values are rounded
        /// half up; 9 keeps the full nanosecond resolution of the source frames (the ASC reader keeps nine digits).
        /// </summary>
        /// <param name="decimals">Fraction digits, 6 to 9.</param>
        /// <returns>This builder.</returns>
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
        /// <returns>A new exporter; no file is touched before its first frame.</returns>
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
