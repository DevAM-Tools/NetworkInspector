// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pcapng;

/// <summary>
/// PCAPNG frame exporter. Writes raw captured frames to a PCAPNG file.
/// <para>
/// Implements <see cref="IFrameListener"/> for integration with the capture pipeline.
/// Supports automatic interface discovery (IDBs written on-demand), lazy initialization
/// (no file until the first frame), and snap-length truncation.
/// Link-layer types are written through as PCAPNG DLT values; unknown DLTs are not
/// filtered here — decoding is left to the consumer. Interface names come from
/// <see cref="FrameInterfaceInfo.UiName"/> (PCAPNG <c>if_name</c> round-trips).
/// A <see cref="PcapInterfacePropertyKeys.FcsLength"/> byte is written as IDB
/// <c>if_fcslen</c>. Per-packet EPB flags are not written.
/// Packet <c>orig_len</c> is not a <see cref="Frame"/> field: the writer stores
/// <see cref="Frame.Length"/> (then snap-truncates captured bytes on this write).
/// </para>
/// <para>
/// Two losses are reported through <see cref="ItemSkipped"/> as advisory events that do not count as skipped frames:
/// interface metadata that an option cannot hold is left out of the IDB (once per interface), and the first frame cut at
/// the snap length raises one event per export (<see cref="TruncatedFrameCount"/> counts all of them).
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class PcapngExporter : IFrameListener, IErrorTolerantExporter, IDisposable
{
    #region Configuration and state

    /// <summary>Cancellation token of the export; a cancelled token ends the export at the next frame.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>Snap length in bytes, 1 to <see cref="int.MaxValue"/> (the builder guarantees the range); also written into every IDB.</summary>
    private readonly uint _SnapLength;

    /// <summary>Timestamp resolution as a decimal power-of-ten exponent, 0 to 19 (the builder guarantees the range).</summary>
    private readonly byte _TsResolution;

    /// <summary>SHB options set on the builder; null when none was set (the first frame's interface may still supply some).</summary>
    private readonly ShbOptions? _ShbOptions;

    /// <summary>Number of frames after which the export ends; 0 means unlimited.</summary>
    private readonly int _TargetFrameCount;

    /// <summary>Output target; consumed on lazy init and released by <see cref="OnFinish"/> (null afterwards).</summary>
    private ExportOutput? _Output;

    /// <summary>Active writer; set by the lazy init on the first frame and dropped by <see cref="OnFinish"/>.</summary>
    private PcapngWriter? _Writer;

    /// <summary>
    /// Interface tracking: (FrameInterfaceId, LinkType) → pcapng interface ID.
    /// The key includes the link type because a PCAPNG Interface Description Block carries exactly one link type: the
    /// same source interface delivering frames of two link types needs two IDBs.
    /// </summary>
    private readonly Dictionary<(FrameInterfaceId, LinkType), uint> _Interfaces = new();

    /// <summary>
    /// Names of the options the writer left out of the IDB being written. Reused for every IDB (cleared before each
    /// write), so reporting costs nothing for the common interface whose metadata fits.
    /// </summary>
    private readonly List<string> _OmittedOptions = [];

    /// <summary>PCAPNG interface id of the next IDB; ids are dense from 0 in IDB order (EPBs refer to them).</summary>
    private uint _NextInterfaceId;

    /// <summary>Whether a fatal error stopped the export (strict skip, failed start, or failed finish).</summary>
    private bool _HasError;

    /// <summary>Whether the lazy init ran (successfully or not); it runs at most once.</summary>
    private bool _Started;

    /// <summary>Whether <see cref="OnFinish"/> ran; later frames are refused.</summary>
    private bool _Finished;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="output">The output target.</param>
    /// <param name="uiName">The display name.</param>
    /// <param name="description">The optional description.</param>
    /// <param name="snapLength">Snap length in bytes, validated by the builder.</param>
    /// <param name="tsResolution">Timestamp resolution exponent, validated by the builder.</param>
    /// <param name="shbOptions">Builder-set SHB options; null when none was set.</param>
    /// <param name="targetFrameCount">Frame limit; 0 means unlimited.</param>
    /// <param name="cancellationToken">Cancellation token of the export.</param>
    private PcapngExporter(
        ExportOutput output,
        string uiName,
        string? description,
        uint snapLength,
        byte tsResolution,
        ShbOptions? shbOptions,
        int targetFrameCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _CancellationToken = cancellationToken;
        _SnapLength = snapLength;
        _TsResolution = tsResolution;
        _ShbOptions = shbOptions;
        _TargetFrameCount = targetFrameCount;
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    /// <returns>A builder with the defaults; an output must be set before <see cref="Builder.Build"/>.</returns>
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

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion

    #region Statistics

    /// <summary>Number of frames written so far (each frame is written to the stream as soon as it arrives).</summary>
    public int FrameCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => FrameCount;

    /// <summary>
    /// Number of frames whose stored bytes were cut at the snap length (their EPB keeps the original length).
    /// A frame is counted when it is cut, so a frame whose write then fails is counted too. Saturates at
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public int TruncatedFrameCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public long EstimatedOutputBytes
    {
        get
        {
            PcapngWriter? writer = _Writer;
            if (writer is null)
            {
                return 0L;
            }

            return writer.EstimatedOutputBytes;
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

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public bool OnFrame(Frame frame)
    {
        // Stop signals first: cancellation, an earlier fatal error, or a finished exporter all end the export. After
        // OnFinish the writer's buffers are returned, so a late frame must never reach the writer.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy initialization: open file and write SHB on first frame
        if (!_Started && !_Start(frame))
        {
            return false;
        }

        return _HandleFrame(frame);
    }

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the file. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;

        // Shared finish sequence: finalize, then release every resource even when a step fails.
        ExporterFinish.Run(
            "PCAPNG",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("writer buffer return", _ReturnWriterBuffers),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    /// <summary>
    /// Flushes the writer. Starts it first when no frame ever arrived, so an empty export is still a valid file with an
    /// SHB; skipped after an error so no partial file is started.
    /// </summary>
    private void _FinalizeFile()
    {
        if (!_Started && !_HasError && _Output is not null)
        {
            _Start(null);
        }

        _Writer?.Flush();
    }

    /// <summary>Returns the writer's pooled buffers and drops the writer.</summary>
    private void _ReturnWriterBuffers()
    {
        PcapngWriter? writer = _Writer;
        _Writer = null;
        writer?.ReturnBuffers();
    }

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        output?.Dispose();
    }

    /// <summary>Error channel of the finish sequence: marks the error, counts it, and raises <see cref="ItemSkipped"/>.</summary>
    /// <param name="kind">The error kind of the failed finish step.</param>
    /// <param name="message">The message of the failed finish step.</param>
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

    #region Frame intake

    /// <summary>Processes a single frame: registers interface if new, writes IDB+EPB.</summary>
    /// <param name="frame">The frame to write.</param>
    /// <returns>False when the target count is reached or a strict-mode skip stopped the export; true otherwise.</returns>
    private bool _HandleFrame(Frame frame)
    {
        // Target reached: refuse further frames so the caller stops feeding the exporter.
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

        // Map the frame's source interface to a PCAPNG interface; an IDB must precede the first EPB that uses it.
        FrameInterfaceId interfaceId = frame.InterfaceId;
        LinkType linkType = frame.LinkType;
        (FrameInterfaceId, LinkType) key = (interfaceId, linkType);

        // Probe the dictionary without mutating it yet.  The interface ID is
        // committed to the dict only after WriteInterfaceDescription succeeds so
        // that a write failure in tolerant mode does not leave a dangling entry:
        // future EPBs for the same interface would reference an IDB that was never
        // written, producing a structurally invalid PCAPNG file.
        bool needsIdb = !_Interfaces.TryGetValue(key, out uint pcapngId);
        if (needsIdb)
        {
            // Tentative ID — promoted to the dict only on successful IDB write below.
            pcapngId = _NextInterfaceId;
        }

        // Prepare frame data — truncate to snap_length if needed.
        // Frame has no on-wire orig_len: EPB original_len is this payload length
        // (before this write's snap truncation).
        ReadOnlySpan<byte> data = frame.Data.Span;
        uint originalLength = (uint)data.Length;
        // _SnapLength is constrained to <= int.MaxValue by the builder, so the
        // (int) cast below is lossless.
        int snapLengthInt = (int)_SnapLength;
        if (data.Length > snapLengthInt)
        {
            // Security: the cut itself is a format feature (the EPB keeps the original length), but a silent truncation
            // would lose data without the caller knowing. The caller is told once per export (like the BLF timestamp clamp)
            // and every cut frame is counted. Rejected: one event per frame, which would flood a session of jumbo frames.
            data = data[..snapLengthInt];
            if (TruncatedFrameCount < int.MaxValue)
            {
                TruncatedFrameCount++;
            }

            if (TruncatedFrameCount == 1)
            {
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = FrameCount,
                    Kind = ExportErrorKind.SerializationError,
                    Message = Invariant($"PCAPNG: frames longer than the snap length ({_SnapLength} bytes) are truncated; ")
                        + "raise it with WithSnapLength. Further truncations are counted in TruncatedFrameCount.",
                });
            }
        }

        // Preserve the registry display name as IDB if_name so NI→PCAPNG→NI keeps eth0 etc.
        // if_fcslen is written only when the property is a byte. A missing or non-byte
        // value omits the option; it does not fail the frame.
        // Description and the reader's interface properties (speed, filter, OS, hardware, TX / RX speed) travel back
        // into the IDB through the option table shared with the reader.
        // The registry lookup runs only for a frame that needs a new IDB, i.e. once per interface, not once per
        // frame; the common frame takes the dictionary hit and skips all of this.
        string? idbName = null;
        string? idbDescription = null;
        IReadOnlyDictionary<string, object>? idbProperties = null;
        byte? fcsLength = null;
        if (needsIdb && interfaceId != FrameInterfaceId.Invalid)
        {
            FrameInterfaceInfo? ifInfo = frame.Registry.Get(frame.InterfaceId);
            idbName = ifInfo?.UiName;
            idbDescription = ifInfo?.Description;
            idbProperties = ifInfo?.Properties;
            if (ifInfo is not null
                && ifInfo.Properties.TryGetValue(PcapInterfacePropertyKeys.FcsLength, out object? fcsValue)
                && fcsValue is byte fcsByte)
            {
                fcsLength = fcsByte;
            }
        }

        // _Writer is guaranteed non-null after _Start() succeeds. The IDB (when needed) and the EPB are written in
        // this order so the interface is always described before the first packet that references it.
        bool interfaceCommitted = false;
        ExportErrorEventArgs? failure = null;
        try
        {
            if (needsIdb)
            {
                _OmittedOptions.Clear();
                _Writer!.WriteInterfaceDescription(
                    linkType, _SnapLength, _TsResolution, idbName, fcsLength, idbDescription, idbProperties, _OmittedOptions);
                // IDB written successfully — now commit the interface mapping so that
                // subsequent frames can reuse the same ID.  Doing this after the write
                // means a write failure cannot leave a dangling dict entry.
                _Interfaces[key] = _NextInterfaceId++;
                interfaceCommitted = true;
            }
            _Writer!.WriteEnhancedPacket(pcapngId, frame.Timestamp, data, originalLength, _TsResolution);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any error during write — skip frame with error reporting. Only an I/O failure is an I/O error: a caller that
            // retries on another disk for IoError must not retry a serialization problem.
            ExportErrorKind kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            failure = new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = kind,
                Message = $"Write failed: {ex.Message}",
            };
        }

        // The loss of interface metadata is reported once, when its IDB is committed, and outside the try block so a
        // handler that throws is not mistaken for a write failure. It is raised even when the first EPB of the interface
        // then failed: the IDB is in the file and its omitted options are gone for good.
        if (interfaceCommitted && _OmittedOptions.Count > 0)
        {
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = ExportErrorKind.SerializationError,
                Message = Invariant($"PCAPNG interface {pcapngId}: options left out because their value cannot be stored ")
                    + Invariant($"(a number must be non-negative, a text at most 65535 UTF-8 bytes): {string.Join(", ", _OmittedOptions)}."),
            });
        }

        if (failure is not null)
        {
            return _HandleSkip(failure);
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// Handles a skipped frame: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
    /// <param name="error">The error to report for the skipped frame.</param>
    /// <returns>False in Strict mode (the export stops); true in Tolerant mode.</returns>
    private bool _HandleSkip(ExportErrorEventArgs error)
    {
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

    #region Section header

    /// <summary>
    /// Lazily initializes output and writes the SHB. SHB options set on the builder win; options it leaves unset come
    /// from the <c>capture.*</c> properties of the first frame's interface (the SHB of the source capture), so a
    /// PCAPNG -> PCAPNG copy keeps hardware, OS, and capture application.
    /// </summary>
    /// <param name="firstFrame">The first frame, or null for an empty export.</param>
    /// <returns>True if initialization succeeded.</returns>
    private bool _Start(Frame? firstFrame)
    {
        // Output already released or never configured: nothing can be written. Note that this path sets the error
        // flag without raising ItemSkipped or incrementing ErrorCount, so a late call on a disposed exporter does not start
        // an error loop.
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        // Creates the file or opens stdout on demand, so nothing is touched before the first frame. An open failure (bad
        // path, missing directory, access denied) or a missing stream goes through the normal error path instead of
        // escaping from OnFrame.
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? underlyingStream, out string openFailure))
        {
            _ReportStartFailure($"PCAPNG output could not be opened: {openFailure}");
            return false;
        }

        try
        {
            // SHB write may throw on a broken stream — surface as an export error
            // through the standard pipeline rather than escaping to the caller.
            _Writer = new PcapngWriter(underlyingStream);
            _Writer.WriteSectionHeader(_EffectiveShbOptions(firstFrame));
        }
        catch (Exception ex)
        {
            _ReportStartFailure($"PCAPNG SHB write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    /// <param name="message">The failure message for the event.</param>
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
    /// SHB options for this file: the builder's options, with every option it leaves unset taken from the
    /// <c>capture.hardware</c> / <c>capture.os</c> / <c>capture.application</c> properties of the first frame's interface.
    /// </summary>
    /// <param name="firstFrame">The first frame, or null for an empty export (then only the builder's options apply).</param>
    /// <returns>The options to write, or null when there are none.</returns>
    private ShbOptions? _EffectiveShbOptions(Frame? firstFrame)
    {
        FrameInterfaceInfo? info = firstFrame is { HasInterface: true } frame
            ? frame.Registry.Get(frame.InterfaceId)
            : null;
        if (info is null)
        {
            return _ShbOptions;
        }

        string? hardware = _ShbOptions?.Hardware ?? _TextProperty(info, PcapInterfacePropertyKeys.CaptureHardware);
        string? os = _ShbOptions?.Os ?? _TextProperty(info, PcapInterfacePropertyKeys.CaptureOs);
        string? application = _ShbOptions?.Application ?? _TextProperty(info, PcapInterfacePropertyKeys.CaptureApplication);
        if (hardware is null && os is null && application is null)
        {
            return _ShbOptions;
        }

        return new ShbOptions
        {
            Hardware = hardware,
            Os = os,
            Application = application,
            Comment = _ShbOptions?.Comment,
        };
    }

    /// <summary>A non-empty string property, or null.</summary>
    /// <param name="info">The interface whose properties are read.</param>
    /// <param name="key">The property key.</param>
    /// <returns>The property text; null when the property is missing, not a string, or empty.</returns>
    private static string? _TextProperty(FrameInterfaceInfo info, string key) =>
        info.Properties.TryGetValue(key, out object? value) && value is string { Length: > 0 } text
            ? text
            : null;

    #endregion

    #region Builder

    /// <summary>Fluent builder for constructing a <see cref="PcapngExporter"/>. Not thread-safe.</summary>
    public sealed class Builder
    {
        /// <summary>Output target; null until one of <c>ToFile</c>, <c>ToStream</c>, or <c>ToStdout</c> is called.</summary>
        private ExportOutput? _Output;

        /// <summary>The user-friendly display name.</summary>
        private string _UiName = "PCAPNG Exporter";

        /// <summary>The optional description.</summary>
        private string? _Description;

        /// <summary>Cancellation token of the export.</summary>
        private CancellationToken _CancellationToken;

        // Snap length limit: frames longer than this are truncated on export (original length is kept in the EPB).
        // Default is the readers' default packet size cap (PcapConstants.DefaultSnapLength, 262 144 bytes), which is
        // also written into every IDB; configurable by the caller through WithSnapLength up to int.MaxValue.
        // Raising it is opt-in, so by default larger frames lose their tail bytes; the exporter reports the first such
        // frame (TruncatedFrameCount counts them).
        /// <summary>Snap length in bytes.</summary>
        private uint _SnapLength = PcapConstants.DefaultSnapLength;

        // Nanoseconds are the native resolution of NI timestamps, so the default loses no precision.
        /// <summary>Timestamp resolution as a decimal power-of-ten exponent.</summary>
        private byte _TsResolution = PcapngWriter.TsResolNanoseconds;

        /// <summary>SHB hardware option; null leaves it to the first frame's interface.</summary>
        private string? _Hardware;

        /// <summary>SHB operating system option; null leaves it to the first frame's interface.</summary>
        private string? _Os;

        /// <summary>SHB application option; null leaves it to the first frame's interface.</summary>
        private string? _Application;

        /// <summary>SHB comment option.</summary>
        private string? _Comment;

        /// <summary>Frame limit; 0 means unlimited.</summary>
        private int _TargetFrameCount;

        /// <summary>Sets the output to a file path with a 4 MiB buffer.</summary>
        /// <param name="path">Absolute or relative path of the output file; created with the first frame.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream. The caller keeps ownership: the exporter flushes the stream when
        /// it finishes but does not close it.
        /// </summary>
        /// <param name="stream">A writable stream.</param>
        /// <returns>This builder.</returns>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Sets the output to stdout.</summary>
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

        /// <summary>Sets the cancellation token.</summary>
        /// <param name="token">A cancelled token ends the export at the next frame.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Sets the maximum captured packet length in bytes (must be &gt; 0 and ≤ <see cref="int.MaxValue"/>).
        /// Frames longer than this are truncated; the EPB keeps the original length. The first truncated frame raises one
        /// advisory <see cref="ItemSkipped"/> event, and <see cref="TruncatedFrameCount"/> counts them all.
        /// </summary>
        /// <param name="length">Snap length in bytes.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is zero or above <see cref="int.MaxValue"/>.</exception>
        public Builder WithSnapLength(uint length)
        {
            // Zero snap length would truncate every captured payload to empty,
            // producing a PCAPNG file where no packet data is preserved.
            if (length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Snap length must be greater than 0.");
            }
            // The slicing path uses (int)_SnapLength; reject values that would
            // overflow to a negative int and produce a corrupt slice / exception.
            if (length > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(length),
                    "Snap length must be ≤ int.MaxValue.");
            }
            _SnapLength = length;
            return this;
        }

        /// <summary>
        /// Sets the timestamp resolution as a decimal power-of-ten exponent (0..19).
        /// 6 is microseconds and 9 is nanoseconds. Binary resolutions (bit 7 set) are rejected
        /// because this writer divides nanoseconds by a power of ten. Resolutions finer than nanoseconds saturate:
        /// timestamps of about 1.8e18 nanoseconds since the epoch fit only up to resolution 10, so finer values
        /// produce <see cref="ulong.MaxValue"/> ticks for realistic timestamps.
        /// </summary>
        /// <param name="resolution">Decimal exponent of the tick length (6 = microseconds, 9 = nanoseconds).</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="resolution"/> is above 19 or has bit 7 set.
        /// </exception>
        public Builder WithTimestampResolution(byte resolution)
        {
            if ((resolution & 0x80) != 0 || resolution > 19)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(resolution),
                    "Timestamp resolution must be a decimal power-of-ten exponent from 0 through 19.");
            }

            _TsResolution = resolution;
            return this;
        }

        /// <summary>Sets the SHB hardware description option.</summary>
        /// <param name="hardware">Hardware description; at most 65 535 UTF-8 bytes (the 16-bit option length).</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="hardware"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="hardware"/> is longer than an option can hold.</exception>
        public Builder WithHardware(string hardware)
        {
            _Hardware = _ValidateOption(hardware, nameof(hardware));
            return this;
        }

        /// <summary>Sets the SHB operating system description option.</summary>
        /// <param name="os">Operating system description; at most 65 535 UTF-8 bytes (the 16-bit option length).</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="os"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="os"/> is longer than an option can hold.</exception>
        public Builder WithOs(string os)
        {
            _Os = _ValidateOption(os, nameof(os));
            return this;
        }

        /// <summary>Sets the SHB application option.</summary>
        /// <param name="application">Application name; at most 65 535 UTF-8 bytes (the 16-bit option length).</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="application"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="application"/> is longer than an option can hold.</exception>
        public Builder WithApplication(string application)
        {
            _Application = _ValidateOption(application, nameof(application));
            return this;
        }

        /// <summary>Sets the SHB comment option.</summary>
        /// <param name="comment">Comment text; at most 65 535 UTF-8 bytes (the 16-bit option length).</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="comment"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="comment"/> is longer than an option can hold.</exception>
        public Builder WithComment(string comment)
        {
            _Comment = _ValidateOption(comment, nameof(comment));
            return this;
        }

        /// <summary>
        /// Sets the maximum number of frames to export. <c>0</c> means unlimited (default).
        /// When the target is reached, <see cref="OnFrame"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        /// <param name="count">Frame limit; 0 means unlimited.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetFrameCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // The upper limit is the number of items an array-index ID can address (ArrayIndexIdRange.MaxCount).
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
        /// <returns>The configured exporter.</returns>
        /// <exception cref="InvalidOperationException">No output destination was set.</exception>
        public PcapngExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "Output destination must be set via ToFile(), ToStream(), or ToStdout().");
            }

            ShbOptions? shbOptions = null;
            if (_Hardware is not null || _Os is not null || _Application is not null || _Comment is not null)
            {
                shbOptions = new ShbOptions
                {
                    Hardware = _Hardware,
                    Os = _Os,
                    Application = _Application,
                    Comment = _Comment,
                };
            }

            return new PcapngExporter(
                _Output,
                _UiName,
                _Description,
                _SnapLength,
                _TsResolution,
                shbOptions,
                _TargetFrameCount,
                _CancellationToken);
        }

        /// <summary>Checks that <paramref name="value"/> fits a PCAPNG option (16-bit length).</summary>
        /// <param name="value">The option text.</param>
        /// <param name="parameterName">The caller's parameter name, for the exception.</param>
        /// <returns><paramref name="value"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException">The UTF-8 form is longer than 65 535 bytes.</exception>
        private static string _ValidateOption(string value, string parameterName)
        {
            // Security: an option longer than its 16-bit length field cannot be written, and the failure would surface only
            // with the first frame as a failed start that ends the whole export; the mitigation is to refuse the value where
            // the configuration is made. Rejected: truncating it, which would store another text than the caller set.
            // Residual risk: none for values set here; SHB texts inherited from the first frame's interface are not checked.
            ArgumentNullException.ThrowIfNull(value, parameterName);
            if (Encoding.UTF8.GetByteCount(value) > ushort.MaxValue)
            {
                throw new ArgumentException("A PCAPNG option holds at most 65535 UTF-8 bytes.", parameterName);
            }

            return value;
        }
    }

    #endregion
}
