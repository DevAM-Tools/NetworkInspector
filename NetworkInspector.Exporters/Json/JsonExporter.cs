// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// JSON packet exporter. Writes parsed packets as a JSON array to a file or stream.
/// <para>
/// Implements <see cref="IPacketListener"/> for integration with the parsing pipeline.
/// Supports three output formats:
/// <list type="bullet">
///   <item><see cref="JsonExportFormat.Compact"/> — short keys, same-as-previous deduplication</item>
///   <item><see cref="JsonExportFormat.Pretty"/> — full keys, 2-space indented</item>
///   <item><see cref="JsonExportFormat.Array"/> — full keys, flat (no indent)</item>
/// </list>
/// Lazy initialization defers file creation until the first packet.
/// Skipped packets are counted in <see cref="IExporterStatistics.SkippedCount"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class JsonExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    /// <summary>Opening bracket of the JSON array: <c>[</c> + newline.</summary>
    private static ReadOnlySpan<byte> _ArrayOpen => "[\n"u8;

    /// <summary>Closing bracket of the JSON array: newline + <c>]</c> + newline.</summary>
    private static ReadOnlySpan<byte> _ArrayClose => "\n]\n"u8;

    /// <summary>Separator between packets: comma + newline + blank line.</summary>
    private static ReadOnlySpan<byte> _PacketSeparator => ",\n\n"u8;

    private readonly CancellationToken _CancellationToken;
    private readonly JsonExportFormat _Format;
    private readonly bool _FlushPerPacket;
    private readonly int _TargetPacketCount;

    // Output target consumed on lazy init
    private ExportOutput? _Output;
    // _OutputStreamRef: non-owning reference to the stream created by _Output.
    // _Output is the sole owner and is responsible for disposing it on Dispose/OnFinish.
    // Never dispose _OutputStreamRef directly — doing so would double-dispose the underlying stream.
    // Rename kept as _DirectStream for API-surface stability; the 'Ref' suffix communicates
    // the non-owning semantics to future readers even without this comment.
    [SuppressMessage("Design", "CA2213:Disposable fields should be disposed",
        Justification = "_DirectStream is a non-owning reference to _Output's stream; _Output.Dispose() handles cleanup.")]
    private Stream? _DirectStream;

    // Compact format state (only allocated for Compact mode)
    private readonly JsonExporterState? _State;

    // Reusable serialization buffer to avoid per-packet allocations.
    // Not declared readonly because the writers receive it by `ref` (see CompactWriter / PrettyWriter / ArrayWriter),
    // and `ref readonly` would prevent in-place writes through PooledBuffer's mutable methods.
    private PooledBuffer _Buffer = new(4096);

    private long _CommittedBytes;
    private bool _HasError;
    private bool _Started;
    private bool _Finished;
    // Tracks whether the closing JSON array bracket has been written.
    // Separate from _Finished so that a failed bracket write on the first
    // OnFinish call can be retried on a subsequent call.
    private bool _ClosingBracketWritten;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private JsonExporter(
        ExportOutput output,
        string uiName,
        string? description,
        JsonExportFormat format,
        bool flushPerPacket,
        int targetPacketCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _Format = format;
        _FlushPerPacket = flushPerPacket;
        _TargetPacketCount = targetPacketCount;
        _CancellationToken = cancellationToken;

        // Allocate state only for Compact mode (dedup + same-as-previous)
        if (format == JsonExportFormat.Compact)
        {
            _State = new JsonExporterState(2048);
        }
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

    /// <summary>Number of packets written so far.</summary>
    public int PacketCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    public long EstimatedOutputBytes => _CommittedBytes + _Buffer.Length;

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
        || (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount);

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    /// <inheritdoc/>
    public bool OnPacket(Packet packet)
    {
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy initialization: open output and write opening bracket on first packet
        if (!_Started && !_Start())
        {
            return false;
        }

        return _HandlePacket(packet);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished && _ClosingBracketWritten)
        {
            return;
        }
        _Finished = true;

        // try/finally guarantees pooled buffer return + output disposal even
        // if the closing-bracket write or final flush throws on a broken stream.
        // cleanupErrors is declared here so the throw can occur after the finally
        // block — CA2219 prohibits throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            // If never started, trigger lazy init so empty exports produce "[\n]\n"
            if (!_Started && _Output is not null)
            {
                _Start();
            }

            // Write the closing bracket when it has not been written yet.
            // A previous OnFinish call may have failed before completing the write;
            // retrying here ensures the file becomes a valid JSON document when
            // the underlying stream recovers or is retried by the caller.
            if (!_ClosingBracketWritten && _DirectStream is not null)
            {
                _WriteClosingBracket();
                _ClosingBracketWritten = true;
                _DirectStream?.Flush();
            }
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"JSON finalization failed: {ex.Message}",
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
                _Buffer.Return();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                if (ErrorCount < int.MaxValue) ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"JSON buffer return failed: {ex.Message}",
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
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"JSON output disposal failed: {ex.Message}",
                });
            }
            _Output = null;
            _DirectStream = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("JSON exporter cleanup failed.", cleanupErrors);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    // ========================================================================
    // Private implementation
    // ========================================================================

    /// <summary>Lazily initializes output and writes the opening bracket.</summary>
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
            // Opening bracket write may throw on a broken stream — surface as an
            // export error rather than escaping to the caller.
            _DirectStream = underlyingStream;
            _WriteDirect(_ArrayOpen);
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"JSON header write failed: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    /// <summary>Serializes and outputs a single packet.</summary>
    private bool _HandlePacket(Packet packet)
    {
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        _Buffer.Reset();

        // Comma + blank line separator between packets
        if (PacketCount > 0)
        {
            _Buffer.Write(_PacketSeparator);
        }

        // Serialize packet using the configured format
        switch (_Format)
        {
            case JsonExportFormat.Compact:
                CompactWriter.WritePacket(packet, ref _Buffer, _State!);
                break;
            case JsonExportFormat.Pretty:
                PrettyWriter.WritePacket(packet, ref _Buffer);
                break;
            case JsonExportFormat.Array:
                ArrayWriter.WritePacket(packet, ref _Buffer);
                break;
        }

        // _DirectStream is guaranteed non-null after _Start() succeeds
        try
        {
            _WriteDirect(_Buffer.WrittenSpan);
            if (_FlushPerPacket)
            {
                _DirectStream?.Flush();
            }
        }
        catch (Exception ex)
        {
            // Any error during write — skip packet with error reporting.
            // I/O errors map to IoError; other failures (encoding, etc.) map
            // to SerializationError so callers can distinguish the cause.
            ExportErrorKind kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError;
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = kind,
                Message = $"Write failed: {ex.Message}",
            });
        }

        PacketCount++;
        return true;
    }

    /// <summary>
    /// Handles a skipped packet: increments counters, fires the event in Tolerant mode,
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

    /// <summary>Writes bytes to the direct stream and updates <see cref="_CommittedBytes"/>.</summary>
    private void _WriteDirect(ReadOnlySpan<byte> data)
    {
        if (_DirectStream is null)
        {
            return;
        }

        _DirectStream.Write(data);
        _CommittedBytes += data.Length;
    }

    /// <summary>Writes the closing bracket to the output.</summary>
    private void _WriteClosingBracket() =>
        // _DirectStream may be null if _Start() was never called (empty export handled in OnFinish)
        _WriteDirect(_ArrayClose);

    // ========================================================================
    // Builder
    // ========================================================================

    /// <summary>Fluent builder for constructing a <see cref="JsonExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "JSON Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private JsonExportFormat _Format = JsonExportFormat.Compact;
        private bool _FlushPerPacket;
        private int _TargetPacketCount;

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

        /// <summary>Sets the user-friendly display name shown in UI and logs.</summary>
        public Builder WithUiName(string name)
        {
            _UiName = name;
            return this;
        }

        /// <summary>
        /// Sets an optional description.
        /// <para>
        /// The description is exposed via <see cref="IPacketListener.Description"/> but is
        /// not written to the JSON output file.
        /// </para>
        /// </summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the JSON output format.</summary>
        public Builder WithFormat(JsonExportFormat format)
        {
            _Format = format;
            return this;
        }

        /// <summary>Enables or disables flushing the stream after each packet.</summary>
        public Builder WithFlushPerPacket(bool flush)
        {
            _FlushPerPacket = flush;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > ArrayIndexIdRange.MaxCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"Target packet count must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");
            }

            _TargetPacketCount = count;
            return this;
        }

        /// <summary>Sets the cancellation token for cooperative shutdown.</summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output target was configured.</summary>
        /// <exception cref="InvalidOperationException">No output target was configured.</exception>
        public JsonExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException("No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            return new JsonExporter(
                _Output,
                _UiName,
                _Description,
                _Format,
                _FlushPerPacket,
                _TargetPacketCount,
                _CancellationToken);
        }
    }
}
