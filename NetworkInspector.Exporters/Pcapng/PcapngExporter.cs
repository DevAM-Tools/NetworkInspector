// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pcapng;

/// <summary>
/// PCAPNG frame exporter. Writes raw captured frames to a PCAPNG file.
/// <para>
/// Implements <see cref="IFrameListener"/> for integration with the capture pipeline.
/// Supports automatic interface discovery (IDBs written on-demand), lazy initialization
/// (no file until the first frame), and snap-length truncation.
/// Link-layer types are written through as PCAPNG DLT values; unknown DLTs are not
/// filtered here — decoding is left to the consumer (e.g. Wireshark).
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

    // Interface tracking: (FrameInterfaceId, LinkType) → pcapng interface ID
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
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy initialization: open file and write SHB on first frame
        if (!_Started && !_Start())
        {
            return false;
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

        // try/finally guarantees the underlying output is disposed even if
        // lazy init or final flush throws — prevents resource leaks on
        // partial-failure shutdown. cleanupErrors is declared here so the
        // throw can occur after the finally block — CA2219 prohibits throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            // If never started and output target is set, trigger lazy init
            // so that even empty exports produce a valid SHB file.
            // Skip if an error already occurred — do not write a partial file.
            if (!_Started && !_HasError && _Output is not null)
            {
                _Start();
            }

            _Writer?.Flush();
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = ExportErrorKind.IoError,
                Message = $"PCAPNG finalization failed: {ex.Message}",
            });
        }
        finally
        {
            _Writer?.ReturnBuffers();
            _Writer = null;

            // Surface disposal failures via the error channel rather than discarding them silently.
            // cleanupErrors is declared before the try so the throw can occur after
            // the finally block — CA2219 prohibits throwing from within a finally clause.
            try
            {
                _Output?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                if (ErrorCount < int.MaxValue) ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = FrameCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"PCAPNG output disposal failed: {ex.Message}",
                });
            }
            _Output = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("PCAPNG exporter cleanup failed.", cleanupErrors);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    // ========================================================================
    // Private implementation
    // ========================================================================

    /// <summary>Lazily initializes output and writes the SHB.</summary>
    /// <returns>True if initialization succeeded.</returns>
    private bool _Start()
    {
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        Stream? underlyingStream = _Output.GetOrCreateUnderlyingStream();
        if (underlyingStream is null)
        {
            _HasError = true;
            return false;
        }

        try
        {
            // SHB write may throw on a broken stream — surface as an export error
            // through the standard pipeline rather than escaping to the caller.
            _Writer = new PcapngWriter(underlyingStream);
            _Writer.WriteSectionHeader(_ShbOptions);
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ExportErrorKind.IoError,
                Message = $"PCAPNG SHB write failed: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    /// <summary>Processes a single frame: registers interface if new, writes IDB+EPB.</summary>
    private bool _HandleFrame(Frame frame)
    {
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

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
        // Preserve the original on-wire length for EPB original length.
        ReadOnlySpan<byte> data = frame.Data.Span;
        uint originalLength = (uint)data.Length;
        // _SnapLength is constrained to <= int.MaxValue by the builder, so the
        // (int) cast below is lossless.
        int snapLengthInt = (int)_SnapLength;
        if (data.Length > snapLengthInt)
        {
            data = data[..snapLengthInt];
        }

        // Build interface name for the IDB
        string? idbName = needsIdb && interfaceId != FrameInterfaceId.Invalid
            ? $"Interface {interfaceId.Value}"
            : null;

        // _Writer is guaranteed non-null after _Start() succeeds
        try
        {
            if (needsIdb)
            {
                _Writer!.WriteInterfaceDescription(linkType, _SnapLength, _TsResolution, idbName);
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
        private uint _SnapLength = PcapConstants.DefaultSnapLength;
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

        /// <summary>Sets the output to an existing stream.</summary>
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

        /// <summary>Sets the maximum captured packet length in bytes (must be &gt; 0 and ≤ <see cref="int.MaxValue"/>).</summary>
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

        /// <summary>Sets the timestamp resolution (power-of-10 exponent, e.g. 9 = nanosecond).</summary>
        public Builder WithTimestampResolution(byte resolution)
        {
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
        public Builder WithTargetFrameCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
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
