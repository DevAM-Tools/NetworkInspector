// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// BLF frame exporter. Writes captured frames to a BLF file.
/// <para>
/// Implements <see cref="IFrameListener"/> for integration with the capture pipeline.
/// Supports Ethernet, CAN classic (<see cref="LinkType.CanSocketcan"/>, <see cref="LinkType.Can20B"/>),
/// CAN FD, FlexRay, and LIN frames. Unsupported link types (including CAN XL on
/// <see cref="LinkType.CanSocketcan"/>) are skipped (counted in
/// <see cref="IExporterStatistics.SkippedCount"/>).
/// Lazy initialization defers file creation until the first frame.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class BlfExporter : IFrameListener, IErrorTolerantExporter, IDisposable
{
    /// <summary>SocketCAN FD flag: FDF (FD Format indicator) at byte offset 5.</summary>
    private const byte _SocketCanFdFlagFdf = 0x04;

    /// <summary>
    /// CAN XL discriminator: byte 4 bit 7 (XLF). Classic DLC and FD length never set this bit.
    /// </summary>
    private const byte _SocketCanXlfFlag = 0x80;

    private readonly CancellationToken _CancellationToken;
    private readonly CompressionLevel _Compression;
    private readonly int _TargetFrameCount;

    // Output target consumed on lazy init
    private ExportOutput? _Output;
    // Active writer (set on lazy init)
    private BlfWriter? _Writer;

    // Reusable payload buffer to avoid per-frame allocations
    private readonly PooledBuffer _PayloadBuffer = new(4096);

    private long _FirstTimestampNs;
    private long _LastTimestampNs;
    private long _MinTimestampNs = long.MaxValue;
    private long _MaxTimestampNs = long.MinValue;
    private bool _EarlyTsClampNotified;
    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private BlfExporter(
        ExportOutput output,
        string uiName,
        string? description,
        CompressionLevel compression,
        int targetFrameCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _CancellationToken = cancellationToken;
        _Compression = compression;
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
            BlfWriter? writer = _Writer;
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
            _LastTimestampNs = tsNanos;
            if (!_Start())
            {
                return false;
            }
        }
        else
        {
            _LastTimestampNs = tsNanos;
            _Writer?.TryRealignStartEarlier(_MinTimestampNs);
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

        // Use try/finally so the underlying output is always disposed even if
        // the trailer/header update throws (e.g. broken stream). This prevents
        // resource leaks during error-path shutdown. cleanupErrors is declared
        // here so the throw can occur after the finally block — CA2219 prohibits
        // throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            // If no frame ever arrived, lazily start the writer with a zero
            // base timestamp so we always emit a valid empty BLF (LOGG header
            // with zero containers) rather than a 0-byte file. This keeps the
            // exporter contract deterministic: a successfully-built exporter
            // produces a parseable BLF on close, regardless of frame count.
            if (!_Started && !_HasError && _Output is not null)
            {
                _FirstTimestampNs = 0;
                _LastTimestampNs = 0;
                _MinTimestampNs = 0;
                _MaxTimestampNs = 0;
                _Start();
            }

            if (_Writer is not null)
            {
                // Finish the writer and update the header (end_date from max timestamp).
                long endNs = _MinTimestampNs == long.MaxValue ? 0 : _MaxTimestampNs;
                BlfWriterFinishResult result = _Writer.Finish(endNs);
                Stream? stream = _Output?.TryGetExistingStream();
                if (stream is not null)
                {
                    result.UpdateHeader(stream);
                }
            }
        }
        catch (Exception ex)
        {
            // Trailer/header update failed; record the error and continue cleanup.
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = FrameCount,
                Kind = ExportErrorKind.IoError,
                Message = $"BLF finalization failed: {ex.Message}",
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
                _PayloadBuffer.Return();
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
                    Message = $"BLF payload buffer return failed: {ex.Message}",
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
                if (ErrorCount < int.MaxValue) ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = FrameCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"BLF output disposal failed: {ex.Message}",
                });
            }
            _Output = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("BLF exporter cleanup failed.", cleanupErrors);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    // ========================================================================
    // Private implementation
    // ========================================================================

    /// <summary>Lazily initializes output and writes the file header.</summary>
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
            // Writer ctor writes the file header — failures here must be reported
            // through the standard error pipeline rather than escaping to the caller.
            _Writer = new BlfWriter(underlyingStream, _FirstTimestampNs, _Compression);
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ExportErrorKind.IoError,
                Message = $"BLF header write failed: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    /// <summary>
    /// Processes a single frame: determines the BLF object type from the link type,
    /// builds the payload, and writes the object. Looks up the channel from the
    /// frame's interface info to preserve channel assignment during round-trip export.
    /// </summary>
    private bool _HandleFrame(Frame frame)
    {
        if (_TargetFrameCount > 0 && FrameCount >= _TargetFrameCount)
        {
            return false;
        }

        ReadOnlySpan<byte> data = frame.Data.Span;
        long timestampNs = frame.Timestamp.AsNanos;
        int currentIndex = FrameCount + SkippedCount;

        // Look up channel from the frame's interface properties for round-trip preservation.
        // Opaque property bags may hold any type — convert without exception-based control flow.
        ushort channel = 0;
        if (frame.HasInterface
            && frame.Registry.TryGet(frame.InterfaceId, out FrameInterfaceInfo? interfaceInfo)
            && interfaceInfo.Properties.TryGetValue(FrameInterfacePropertyKeys.BlfChannel, out object? channelValue))
        {
            if (!InterfaceChannelConverter.TryConvertToUInt16(channelValue, out channel))
            {
                return _HandleSkip(new ExportErrorEventArgs
                {
                    ItemIndex = currentIndex,
                    Kind = ExportErrorKind.SerializationError,
                    Message = $"BLF channel value '{channelValue}' cannot be converted to a UInt16 channel id.",
                });
            }
        }

        // Determine BLF object type from link type
        uint objectType;
        bool payloadBuilt;

        switch (frame.LinkType)
        {
            case LinkType.Ethernet:
                objectType = BlfConstants.ObjTypeEthernetFrame;
                payloadBuilt = BlfObjectPayloads.TryBuildEthernetFramePayload(
                    data, channel, 0, _PayloadBuffer);
                break;

            case LinkType.CanSocketcan:
                // CAN XL shares LinkType.CanSocketcan but sets XLF on byte 4. The BLF exporter
                // does not emit XL object types — skip before classic/FD interpretation.
                if (data.Length >= 8 && (data[4] & _SocketCanXlfFlag) != 0)
                {
                    return _HandleSkip(new ExportErrorEventArgs
                    {
                        ItemIndex = currentIndex,
                        Kind = ExportErrorKind.UnsupportedType,
                        Message = "CAN XL frames are not supported by the BLF exporter",
                    });
                }

                // Check FD flag at byte offset 5 to distinguish CAN classic vs CAN FD
                if (data.Length > 5 && (data[5] & _SocketCanFdFlagFdf) != 0)
                {
                    objectType = BlfConstants.ObjTypeCanFdMessage;
                    payloadBuilt = BlfObjectPayloads.TryBuildCanFdMessagePayload(
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
                objectType = BlfConstants.ObjTypeLinMessage2;
                payloadBuilt = BlfObjectPayloads.TryBuildLinMessage2Payload(
                    data, channel, _PayloadBuffer);
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
                Message = $"Failed to build {frame.LinkType} payload ({data.Length} bytes)",
            });
        }

        // _Writer is guaranteed non-null after _Start() succeeds.
        // Wrap the actual write so I/O exceptions degrade gracefully to a
        // skipped frame in Tolerant mode rather than tearing down the pipeline.
        try
        {
            // The early-timestamp clamp notification fires at most once per export
            // (contract: consumers see a single advisory event, not one per skipped frame).
            // SkippedCount and FrameCount still reflect every affected frame.
            // The _EarlyTsClampNotified flag suppresses the repeated event deliberately
            // to avoid flooding consumers with identical messages. If per-frame events
            // are needed, a cumulative count is available via SkippedCount.
            if (timestampNs < _Writer!.AnchorStartNanos && _Writer.ObjectCount > 0 && !_EarlyTsClampNotified)
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

            _Writer.WriteRawObject(objectType, 0, timestampNs, _PayloadBuffer.WrittenSpan);
        }
        catch (Exception ex)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = currentIndex,
                Kind = ExportErrorKind.IoError,
                Message = $"BLF write failed: {ex.Message}",
            });
        }

        FrameCount++;
        // Update _MaxTimestampNs here — after the write — so the BLF end_date only
        // covers frames that were actually exported.
        long writtenTs = frame.Timestamp.AsNanos;
        _MaxTimestampNs = _MaxTimestampNs == long.MinValue
            ? writtenTs
            : Math.Max(_MaxTimestampNs, writtenTs);
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

    /// <summary>
    /// Maps <see cref="BlfCompressionLevel"/> to <see cref="CompressionLevel"/>.
    /// </summary>
    private static CompressionLevel _MapCompression(BlfCompressionLevel level) => level switch
    {
        BlfCompressionLevel.None => CompressionLevel.NoCompression,
        BlfCompressionLevel.Fast => CompressionLevel.Fastest,
        BlfCompressionLevel.Default => CompressionLevel.Optimal,
        BlfCompressionLevel.Best => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    // ========================================================================
    // Builder
    // ========================================================================

    /// <summary>Fluent builder for constructing a <see cref="BlfExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "BLF Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private BlfCompressionLevel _Compression = BlfCompressionLevel.Default;
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

        /// <summary>Sets the compression level for BLF container output.</summary>
        public Builder WithCompressionLevel(BlfCompressionLevel level)
        {
            _Compression = level;
            return this;
        }

        /// <summary>
        /// Limits the number of frames to export. <c>0</c> means unlimited (default).
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
                _CancellationToken);
        }
    }
}
