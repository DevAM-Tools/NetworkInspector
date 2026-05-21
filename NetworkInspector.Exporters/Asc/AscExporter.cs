// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Asc;

/// <summary>
/// ASC frame exporter. Writes captured frames to a Vector CANalyzer ASCII log file
/// (.asc) compatible with CANoe, can-utils (candump/canplayer), and NetworkInspector.
/// <para>
/// Supports CAN classic, CAN FD, LIN, and FlexRay frames.
/// Ethernet and CAN XL frames are skipped with an <see cref="ExportErrorKind.UnsupportedType"/>
/// event. Unsupported link types are tracked in <see cref="SkippedCount"/>.
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
    #region SocketCAN constants

    /// <summary>SocketCAN FD format flag (byte 5, bit 2).</summary>
    private const byte SocketCanFdfFlag = 0x04;

    /// <summary>SocketCAN FD bit-rate switch flag (byte 5, bit 0).</summary>
    private const byte SocketCanBrsFlag = 0x01;

    /// <summary>SocketCAN FD error-state indicator flag (byte 5, bit 1).</summary>
    private const byte SocketCanEsiFlag = 0x02;

    /// <summary>SocketCAN extended frame format flag (bit 31 of ID word).</summary>
    private const uint SocketCanEffFlag = 0x80000000u;

    /// <summary>SocketCAN remote-transmission request flag (bit 30 of ID word).</summary>
    private const uint SocketCanRtrFlag = 0x40000000u;

    /// <summary>SocketCAN frame header size: id(4) + dlc(1) + flags(1) + reserved(2) = 8 bytes.</summary>
    private const int SocketCanHeaderSize = 8;

    /// <summary>
    /// CAN XL frame discriminator: byte 4, bit 7. CAN XL frames always set this bit;
    /// classic CAN DLC (0–8) and CAN FD DLC code (0–15) never reach 0x80, so this bit
    /// is a reliable variant indicator shared by both the protocol parser and the exporter.
    /// </summary>
    private const byte SocketCanXlfFlag = 0x80;

    /// <summary>
    /// DLC-code-to-byte-count mapping for CAN FD (ISO 11898-1 Table 6).
    /// DLC codes 0–8 map to themselves; codes 9–15 map to 12, 16, 20, 24, 32, 48, 64.
    /// </summary>
    private static ReadOnlySpan<byte> FdDlcToLength =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    #endregion

    #region DLT_LIN constants (BLF-derived format)

    /// <summary>
    /// DLT_LIN frame header size (BLF-derived format): pid(1) + length(1) = 2 bytes.
    /// Full layout: [pid(1) | length(1) | data(0–8) | checksum(1) | errors(1)].
    /// </summary>
    private const int LinHeaderSize = 2;

    /// <summary>DLT_LIN frame trailer size: checksum(1) + errors(1) = 2 bytes.</summary>
    private const int LinTrailerSize = 2;

    /// <summary>Minimum valid DLT_LIN frame: header + trailer, no data.</summary>
    private const int LinMinSize = LinHeaderSize + LinTrailerSize;

    #endregion

    #region DLT_FLEXRAY constants

    /// <summary>
    /// DLT_FLEXRAY frame header size: 7 bytes.
    /// Layout: [channel(1) | type_flags(1) | frame_id(2 BE) | cycle(1) | header_crc(2 BE) | data...].
    /// </summary>
    private const int DltFlexRayHeaderSize = 7;

    #endregion

    #region Channel property keys

    /// <summary>
    /// Interface property key for the ASC channel number (stored by <c>AscSource</c>).
    /// Not in <see cref="FrameInterfacePropertyKeys"/> because it is source-specific;
    /// defined locally to avoid a direct dependency on the Sources assembly.
    /// </summary>
    private const string AscChannelKey = "asc.channel";

    #endregion

    #region Instance fields

    private readonly CancellationToken _CancellationToken;
    private readonly long _TargetFrameCount;

    /// <summary>Output target. Cleared to <c>null</c> after disposal to prevent double-dispose.</summary>
    private ExportOutput? _Output;

    /// <summary>Active writer. Created on lazy init; <c>null</c> before the first frame.</summary>
    private AscWriter? _Writer;

    /// <summary>
    /// Anchor timestamp in nanoseconds for relative-time computation.
    /// Set to the first frame's timestamp before <see cref="Start"/> is called.
    /// Set to 0 for the empty-export case.
    /// </summary>
    private long _AnchorNs;

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    #endregion

    #region Constructor / factory

    /// <summary>Use <see cref="CreateBuilder"/> to obtain an instance.</summary>
    private AscExporter(
        ExportOutput output,
        string uiName,
        string? description,
        long targetFrameCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _TargetFrameCount = targetFrameCount;
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
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy initialisation: open the file and write the ASC header on the first frame.
        if (!_Started)
        {
            _AnchorNs = frame.Timestamp.AsNanos;
            if (!Start())
            {
                return false;
            }
        }

        return HandleFrame(frame);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }

        _Finished = true;

        // Produce a valid (header-only) ASC file even when no frames were written.
        // This keeps the exporter contract deterministic: a successfully-built exporter
        // always produces a parseable file on close, regardless of frame count.
        if (!_Started && !_HasError && _Output is not null)
        {
            _AnchorNs = 0L;
            Start();
        }

        // Wrap clean-up in try/finally so resources are always released even when
        // the footer write throws (e.g. broken stream or disk full). cleanupErrors
        // is declared here so the throw can occur after the finally block — CA2219
        // prohibits throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            _Writer?.Finish();
        }
        catch (Exception ex)
        {
            _HasError = true;
            ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = ExportErrorKind.IoError,
                Message = $"ASC finalization failed: {ex.Message}",
            });
        }
        finally
        {
            // Each cleanup step is independent so a failure in one step does not
            // prevent remaining resources from being released. Failures are surfaced
            // via the error channel. cleanupErrors is declared before the try so the
            // throw can occur after the finally block — CA2219 prohibits throwing inside finally.
            try
            {
                _Writer?.Return();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = FrameCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"ASC writer return failed: {ex.Message}",
                });
            }

            try
            {
                _Output?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = FrameCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"ASC output disposal failed: {ex.Message}",
                });
            }

            _Output = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("ASC exporter cleanup failed.", cleanupErrors);
        }
    }

    #endregion

    #region IExporterStatistics

    /// <summary>Number of frames successfully written.</summary>
    public long FrameCount { get; private set; }

    /// <inheritdoc/>
    public long WrittenCount => FrameCount;

    /// <inheritdoc/>
    public long SkippedCount { get; private set; }

    /// <inheritdoc/>
    public long ErrorCount { get; private set; }

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

    /// <inheritdoc/>
    bool IExporterStatistics.IsFinished => IsFinished;

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
    /// Lazily opens the output stream and creates the <see cref="AscWriter"/>.
    /// On first call, also writes the ASC file header.
    /// </summary>
    /// <returns><c>true</c> if initialization succeeded; <c>false</c> on I/O failure.</returns>
    private bool Start()
    {
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        Stream? stream = _Output.GetOrCreateUnderlyingStream();
        if (stream is null)
        {
            _HasError = true;
            return false;
        }

        try
        {
            _Writer = new AscWriter(stream, _AnchorNs);
        }
        catch (Exception ex)
        {
            _HasError = true;
            ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ExportErrorKind.IoError,
                Message = $"ASC header write failed: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    /// <summary>
    /// Dispatches a single frame to the appropriate format-specific write path.
    /// Parses the raw binary frame bytes, resolves the channel number, and delegates
    /// to <see cref="AscWriter"/>. Handles I/O errors and unsupported link types
    /// according to the configured <see cref="ErrorTolerance"/>.
    /// </summary>
    private bool HandleFrame(Frame frame)
    {
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

        ReadOnlySpan<byte> data = frame.Data.Span;
        long timestampNs = frame.Timestamp.AsNanos;
        long currentIndex = FrameCount + SkippedCount;

        switch (frame.LinkType)
        {
            case LinkType.CanSocketcan:
            case LinkType.Can20B:
                return HandleCanFrame(data, timestampNs, currentIndex, frame);

            case LinkType.Lin:
                return HandleLinFrame(data, timestampNs, currentIndex, frame);

            case LinkType.Flexray:
                return HandleFlexRayFrame(data, timestampNs, currentIndex);

            default:
                return HandleSkip(
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
    /// <para>
    /// CAN XL frames share the same link type but are distinguished by the XLF bit
    /// (byte 4, bit 7). The ASC format has no CAN XL line syntax, so CAN XL frames
    /// are rejected as <see cref="ExportErrorKind.UnsupportedType"/> before any
    /// classic/FD interpretation can occur.
    /// </para>
    /// </summary>
    private bool HandleCanFrame(
        ReadOnlySpan<byte> data, long timestampNs, long currentIndex, Frame frame)
    {
        // CAN XL frames share LinkType.CanSocketcan with classic/FD but are identified by
        // the XLF bit (byte 4, bit 7). The ASC format cannot represent CAN XL, so skip early
        // before TryParseCanFrame interprets the 12-byte XL header as a classic/FD header.
        if (data.Length >= SocketCanHeaderSize && (data[4] & SocketCanXlfFlag) != 0)
        {
            return HandleSkip(
                ExportErrorKind.UnsupportedType,
                "CAN XL frames are not supported by the ASC format",
                currentIndex);
        }

        if (!TryParseCanFrame(data,
            out uint rawCanId, out bool isExtended, out bool isRemote,
            out bool isFd, out bool brs, out bool esi, out byte dlc,
            out ReadOnlySpan<byte> payload))
        {
            return HandleSkip(
                ExportErrorKind.MalformedData,
                $"CAN frame too short ({data.Length} bytes; minimum {SocketCanHeaderSize})",
                currentIndex);
        }

        int channel = GetChannel(frame, 1);

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
            return HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a LIN frame (<see cref="LinkType.Lin"/>).
    /// Parses the BLF-derived DLT_LIN binary format and writes the ASC LIN line.
    /// </summary>
    private bool HandleLinFrame(
        ReadOnlySpan<byte> data, long timestampNs, long currentIndex, Frame frame)
    {
        if (!TryParseLinFrame(data, out byte frameId, out ReadOnlySpan<byte> payload, out byte checksum))
        {
            return HandleSkip(
                ExportErrorKind.MalformedData,
                $"LIN frame too short ({data.Length} bytes; minimum {LinMinSize})",
                currentIndex);
        }

        int channel = GetChannel(frame, 1);

        try
        {
            _Writer!.WriteLinMessage(timestampNs, channel, frameId, payload, checksum);
        }
        catch (Exception ex)
        {
            return HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a FlexRay frame (<see cref="LinkType.Flexray"/>).
    /// Parses the DLT_FLEXRAY binary format and writes the ASC FlexRay line.
    /// The physical channel (A/B) is read from DLT_FLEXRAY header byte 0 rather
    /// than from the interface property, because byte 0 carries the protocol-level
    /// channel designation rather than the logical interface channel.
    /// </summary>
    private bool HandleFlexRayFrame(
        ReadOnlySpan<byte> data, long timestampNs, long currentIndex)
    {
        if (!TryParseFlexRayFrame(data,
            out byte channel, out ushort frameId, out byte cycle,
            out ushort headerCrc, out ReadOnlySpan<byte> payload))
        {
            return HandleSkip(
                ExportErrorKind.MalformedData,
                $"FlexRay frame too short ({data.Length} bytes; minimum {DltFlexRayHeaderSize})",
                currentIndex);
        }

        try
        {
            _Writer!.WriteFlexRayMessage(timestampNs, channel, frameId, cycle, headerCrc, payload);
        }
        catch (Exception ex)
        {
            return HandleSkip(ExportErrorKind.IoError, $"Write failed: {ex.Message}", currentIndex);
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
    private bool HandleSkip(ExportErrorKind kind, string message, long itemIndex)
    {
        SkippedCount++;
        ErrorCount++;

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
    /// <item>Byte 4: DLC (classic 0–8; FD DLC code 0–15).</item>
    /// <item>Byte 5: FD flags — bit 2 = FDF (FD Format), bit 1 = ESI, bit 0 = BRS.</item>
    /// <item>Bytes 6–7: Reserved (ignored).</item>
    /// <item>Bytes 8+: Data payload.</item>
    /// </list>
    /// </remarks>
    private static bool TryParseCanFrame(
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

        if (data.Length < SocketCanHeaderSize)
        {
            return false;
        }

        uint socketCanId = BinaryPrimitives.ReadUInt32BigEndian(data);
        rawCanId = socketCanId & 0x1FFFFFFF;
        isExtended = (socketCanId & SocketCanEffFlag) != 0;
        isRemote = (socketCanId & SocketCanRtrFlag) != 0;

        dlc = data[4];
        byte fdFlags = data[5];
        isFd = (fdFlags & SocketCanFdfFlag) != 0;
        brs = (fdFlags & SocketCanBrsFlag) != 0;
        esi = (fdFlags & SocketCanEsiFlag) != 0;

        int available = data.Length - SocketCanHeaderSize;

        if (isFd)
        {
            // Look up actual byte count from the DLC code using the CAN FD table.
            int fdDlc = Math.Min((int)dlc, FdDlcToLength.Length - 1);
            int actualLen = FdDlcToLength[fdDlc];
            payload = data.Slice(SocketCanHeaderSize, Math.Min(actualLen, available));
        }
        else
        {
            // Classic CAN: DLC code equals byte count (clamped to 8).
            int classicLen = Math.Min((int)dlc, 8);
            payload = data.Slice(SocketCanHeaderSize, Math.Min(classicLen, available));
        }

        return true;
    }

    /// <summary>
    /// Parses a DLT_LIN binary frame in BLF-derived format.
    /// </summary>
    /// <remarks>
    /// BLF-derived DLT_LIN layout (used by both <c>BlfSource</c> and <c>AscSource</c>):
    /// <list type="bullet">
    /// <item>Byte 0: PID — Protected Identifier (bits 7–6: parity P1/P0; bits 5–0: 6-bit frame ID).</item>
    /// <item>Byte 1: Data length (0–8).</item>
    /// <item>Bytes 2..(2+len−1): Data payload.</item>
    /// <item>Byte (2+len): Checksum.</item>
    /// <item>Byte (3+len): Error flags (not exported to ASC).</item>
    /// </list>
    /// </remarks>
    private static bool TryParseLinFrame(
        ReadOnlySpan<byte> data,
        out byte frameId, out ReadOnlySpan<byte> payload, out byte checksum)
    {
        frameId = 0;
        payload = default;
        checksum = 0;

        if (data.Length < LinMinSize)
        {
            return false;
        }

        // Frame ID is the lower 6 bits of the PID (strip parity bits P0 and P1).
        frameId = (byte)(data[0] & 0x3F);

        // Clamp length to the bytes actually available between header and trailer.
        int length = data[1];
        int available = data.Length - LinHeaderSize - LinTrailerSize;
        if (length > available)
        {
            length = available;
        }

        if (length < 0)
        {
            length = 0;
        }

        payload = data.Slice(LinHeaderSize, length);
        checksum = data[LinHeaderSize + length];
        return true;
    }

    /// <summary>
    /// Parses a DLT_FLEXRAY binary frame.
    /// </summary>
    /// <remarks>
    /// DLT_FLEXRAY header layout (ISO 17458-2 / pcap LINKTYPE_FLEXRAY):
    /// <list type="bullet">
    /// <item>Byte 0: Physical channel (raw; 0 = Ch-A, 1 = Ch-B in generator convention).</item>
    /// <item>Byte 1: Type flags — bit 5 = SFI (sync), bit 4 = STFI (startup), etc.; not exported.</item>
    /// <item>Bytes 2–3: Frame/slot ID (big-endian uint16).</item>
    /// <item>Byte 4: Cycle counter (0–63).</item>
    /// <item>Bytes 5–6: Header CRC (big-endian uint16).</item>
    /// <item>Bytes 7+: Data payload (0–254 bytes).</item>
    /// </list>
    /// </remarks>
    private static bool TryParseFlexRayFrame(
        ReadOnlySpan<byte> data,
        out byte channel, out ushort frameId, out byte cycle,
        out ushort headerCrc, out ReadOnlySpan<byte> payload)
    {
        channel = 0;
        frameId = 0;
        cycle = 0;
        headerCrc = 0;
        payload = default;

        if (data.Length < DltFlexRayHeaderSize)
        {
            return false;
        }

        channel = data[0];
        // data[1] = type_flags: not needed for ASC line output.
        frameId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        cycle = data[4];
        headerCrc = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5, 2));
        payload = data[DltFlexRayHeaderSize..];
        return true;
    }

    /// <summary>
    /// Resolves the CAN/LIN channel number from the frame's interface property bag.
    /// Checks the ASC-native key (<c>"asc.channel"</c>) first, then the BLF key
    /// (<see cref="FrameInterfacePropertyKeys.BlfChannel"/>), and falls back to
    /// <paramref name="defaultChannel"/> when neither is present or the value cannot
    /// be converted.
    /// </summary>
    /// <param name="frame">The frame whose interface properties to inspect.</param>
    /// <param name="defaultChannel">Channel to return when no property is found.</param>
    /// <returns>Resolved channel number.</returns>
    private static int GetChannel(Frame frame, int defaultChannel)
    {
        if (!frame.HasInterface
            || !frame.Registry.TryGet(frame.InterfaceId, out FrameInterfaceInfo? info))
        {
            return defaultChannel;
        }

        if (info.Properties.TryGetValue(AscChannelKey, out object? ascCh)
            && TryConvertToInt32(ascCh, out int ascChannel))
        {
            return ascChannel;
        }

        if (info.Properties.TryGetValue(FrameInterfacePropertyKeys.BlfChannel, out object? blfCh)
            && TryConvertToInt32(blfCh, out int blfChannel))
        {
            return blfChannel;
        }

        return defaultChannel;
    }

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to an <see cref="int"/> without relying
    /// on exception-based control flow. Accepts all integral numeric types and parseable strings.
    /// This precheck avoids <c>Convert.ToInt64</c> which throws on type mismatch;
    /// using exceptions for expected fallback paths violates the no-silent-failure policy.
    /// </summary>
    private static bool TryConvertToInt32(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                result = (int)l;
                return true;
            case uint u when u <= (uint)int.MaxValue:
                result = (int)u;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

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
        private long _TargetFrameCount;

        #region Output target

        /// <summary>
        /// Directs output to a file at the given path.
        /// The file is created lazily on the first frame; no file is created for empty exports
        /// unless <see cref="OnFinish"/> is called.
        /// Calling this method more than once replaces the previous output target.
        /// </summary>
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
        /// Calling this method more than once replaces the previous output target.
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
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        public Builder WithTargetFrameCount(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _TargetFrameCount = count;
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
                _CancellationToken);
        }
    }

    #endregion
}
