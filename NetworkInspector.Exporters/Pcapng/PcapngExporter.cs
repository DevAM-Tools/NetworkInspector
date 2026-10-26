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
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class PcapngExporter : IFrameListener, IErrorTolerantExporter, IDisposable
{
    private readonly CancellationToken _CancellationToken;
    private readonly uint _SnapLength;
    private readonly byte _TsResolution;
    private readonly ShbOptions? _ShbOptions;
    private readonly int _TargetFrameCount;

    // Output target consumed on lazy init
    private ExportOutput? _Output;
    // Active writer (set on lazy init)
    private PcapngWriter? _Writer;

    // Interface tracking: (FrameInterfaceId, LinkType) → pcapng interface ID.
    // The key includes the link type because a PCAPNG Interface Description Block carries exactly one link type: the
    // same source interface delivering frames of two link types needs two IDBs.
    private readonly Dictionary<(FrameInterfaceId, LinkType), uint> _Interfaces = new();
    private uint _NextInterfaceId;

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
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

    /// <summary>Number of frames written so far (each frame is written to the stream as soon as it arrives).</summary>
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

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

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

    // ========================================================================
    // Private implementation
    // ========================================================================

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

    /// <summary>Processes a single frame: registers interface if new, writes IDB+EPB.</summary>
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
            data = data[..snapLengthInt];
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
        try
        {
            if (needsIdb)
            {
                _Writer!.WriteInterfaceDescription(
                    linkType, _SnapLength, _TsResolution, idbName, fcsLength, idbDescription, idbProperties);
                // IDB written successfully — now commit the interface mapping so that
                // subsequent frames can reuse the same ID.  Doing this after the write
                // means a write failure cannot leave a dangling dict entry.
                _Interfaces[key] = _NextInterfaceId++;
            }
            _Writer!.WriteEnhancedPacket(pcapngId, frame.Timestamp, data, originalLength, _TsResolution);
        }
        catch (Exception ex)
        {
            // Any error during write — skip frame with error reporting.
            // I/O errors map to IoError; other failures map to SerializationError.
            ExportErrorKind kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError;
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = kind,
                Message = $"Write failed: {ex.Message}",
            });
        }

        FrameCount++;
        return true;
    }

    /// <summary>
    /// SHB options for this file: the builder's options, with every option it leaves unset taken from the
    /// <c>capture.hardware</c> / <c>capture.os</c> / <c>capture.application</c> properties of the first frame's interface.
    /// </summary>
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
    private static string? _TextProperty(FrameInterfaceInfo info, string key) =>
        info.Properties.TryGetValue(key, out object? value) && value is string { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// Handles a skipped frame: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
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

    // ========================================================================
    // Builder
    // ========================================================================

    /// <summary>Fluent builder for constructing a <see cref="PcapngExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "PCAPNG Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        // Snap length limit: frames longer than this are truncated on export (original length is kept in the EPB).
        // Default is the readers' default packet size cap (PcapConstants.DefaultSnapLength, 262 144 bytes), which is
        // also written into every IDB; configurable by the caller through WithSnapLength up to int.MaxValue.
        // Raising it is opt-in, so by default larger frames lose their tail bytes.
        private uint _SnapLength = PcapConstants.DefaultSnapLength;

        // Nanoseconds are the native resolution of NI timestamps, so the default loses no precision.
        private byte _TsResolution = PcapngWriter.TsResolNanoseconds;
        private string? _Hardware;
        private string? _Os;
        private string? _Application;
        private string? _Comment;
        private int _TargetFrameCount;

        /// <summary>Sets the output to a file path with a 4 MiB buffer.</summary>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream. The caller keeps ownership: the exporter flushes the stream when
        /// it finishes but does not close it.
        /// </summary>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Sets the output to stdout.</summary>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        /// <summary>Sets the user-friendly display name.</summary>
        public Builder WithUiName(string name)
        {
            _UiName = name;
            return this;
        }

        /// <summary>Sets an optional description.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the cancellation token.</summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Sets the maximum captured packet length in bytes (must be &gt; 0 and ≤ <see cref="int.MaxValue"/>).
        /// Frames longer than this are truncated; the EPB keeps the original length.
        /// </summary>
        /// <param name="length">Snap length in bytes.</param>
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
        public Builder WithHardware(string hardware)
        {
            _Hardware = hardware;
            return this;
        }

        /// <summary>Sets the SHB operating system description option.</summary>
        public Builder WithOs(string os)
        {
            _Os = os;
            return this;
        }

        /// <summary>Sets the SHB application option.</summary>
        public Builder WithApplication(string application)
        {
            _Application = application;
            return this;
        }

        /// <summary>Sets the SHB comment option.</summary>
        public Builder WithComment(string comment)
        {
            _Comment = comment;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of frames to export. <c>0</c> means unlimited (default).
        /// When the target is reached, <see cref="OnFrame"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        /// <param name="count">Frame limit; 0 means unlimited.</param>
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
    }
}
