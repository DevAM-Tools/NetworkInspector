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

    /// <summary>Cooperative cancellation; checked per packet.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>Output format; selects the packet writer.</summary>
    private readonly JsonExportFormat _Format;

    /// <summary>Whether the stream is flushed after every packet (costs one flush call per packet).</summary>
    private readonly bool _FlushPerPacket;

    /// <summary>Maximum number of packets to write; 0 means unlimited.</summary>
    private readonly int _TargetPacketCount;

    /// <summary>Output target. Cleared to <c>null</c> after disposal to prevent a double dispose.</summary>
    private ExportOutput? _Output;

    // _DirectStream is a non-owning reference to the stream created by _Output.
    // _Output is the sole owner and is responsible for disposing it on Dispose/OnFinish.
    // Never dispose _DirectStream directly — doing so would double-dispose the underlying stream.
    // The packets are written straight to this stream (for a file output a 4 MiB BufferedStream), so no second
    // buffering layer is needed between the serialization buffer and the stream.
    [SuppressMessage("Design", "CA2213:Disposable fields should be disposed",
        Justification = "_DirectStream is a non-owning reference to _Output's stream; _Output.Dispose() handles cleanup.")]
    private Stream? _DirectStream;

    /// <summary>Same-as-previous and field-info deduplication state; only allocated for the Compact format.</summary>
    private readonly JsonExporterState? _State;

    // Reusable serialization buffer: each packet is serialized here and then written to the stream in one call, which
    // avoids per-packet allocations. Not declared readonly because the writers take it by `ref`, and a readonly field
    // cannot be passed by ref. (PooledBuffer is a class, so the `ref` is not needed for the writes to be visible.)
    private PooledBuffer _Buffer = new(4096);

    /// <summary>Bytes handed to the stream so far; backs <see cref="EstimatedOutputBytes"/>.</summary>
    private long _CommittedBytes;

    /// <summary>Latched fatal state; every later <see cref="OnPacket"/> returns <c>false</c>.</summary>
    private bool _HasError;

    /// <summary>Set once <see cref="_Start"/> ran, successful or not, so a failed start is never retried.</summary>
    private bool _Started;

    /// <summary>Latch that stops <see cref="OnPacket"/> once <see cref="OnFinish"/> began.</summary>
    private bool _Finished;

    // Tracks whether the closing JSON array bracket has been written.
    // Separate from _Finished so that a failed bracket write on the first OnFinish call is not mistaken for a
    // completed finish. In the current flow the first OnFinish also releases the output, so a later call finds no
    // stream and cannot write the bracket (see _FinalizeFile).
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

        // Allocate state only for Compact mode (dedup + same-as-previous); Pretty and Array are stateless. 2048 is a sizing
        // hint for the number of distinct field ids (it matches the dense-array threshold of the previous-value store), not
        // a limit: larger ids are handled by the growth/overflow paths.
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
        // Stop conditions first: a false return tells the driver to stop sending packets.
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

        // Shared finish sequence: finalize, then release every resource even when a step fails. Pooled buffer first and
        // the output last (the output close flushes and may throw); ExporterFinish runs every step regardless.
        ExporterFinish.Run(
            "JSON",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("buffer return", _Buffer.Return),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>
    /// Writes the closing bracket and flushes. Starts first for an empty export so it produces <c>"[\n]\n"</c>, a valid
    /// empty JSON array. The closing bracket is guarded by <see cref="_ClosingBracketWritten"/> so it is written at most
    /// once. A repeated <see cref="OnFinish"/> after a failed first attempt reaches this method again, but the first
    /// attempt already released the output (<see cref="_DisposeOutput"/> clears the stream reference), so the bracket is
    /// not retried and the file stays incomplete; the failure was reported through <see cref="ItemSkipped"/> on the first call.
    /// </summary>
    private void _FinalizeFile()
    {
        if (!_Started && _Output is not null)
        {
            _Start();
        }

        if (!_ClosingBracketWritten && _DirectStream is not null)
        {
            _WriteClosingBracket();
            _ClosingBracketWritten = true;
            _DirectStream?.Flush();
        }
    }

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        _DirectStream = null;
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
            ItemIndex = PacketCount,
            Kind = kind,
            Message = message,
        });
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

        // An open failure (bad path, missing directory, access denied) or a missing stream goes through the normal error
        // path instead of escaping from OnPacket. A disposed exporter (output already null, above) stays silent so a late
        // call does not start an error loop.
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? underlyingStream, out string openFailure))
        {
            _ReportStartFailure(ExportErrorKind.IoError, $"JSON output could not be opened: {openFailure}");
            return false;
        }

        // _Started is already set, so a failed opening is never retried: a half-written document cannot be repaired.
        try
        {
            // Opening bracket write may throw on a broken stream — surface as an
            // export error rather than escaping to the caller.
            _DirectStream = underlyingStream;
            _WriteDirect(_ArrayOpen);
        }
        catch (Exception ex)
        {
            _ReportStartFailure(
                ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                $"JSON header write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    private void _ReportStartFailure(ExportErrorKind kind, string message)
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = 0,
            Kind = kind,
            Message = message,
        });
    }

    /// <summary>Serializes and outputs a single packet.</summary>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse the packet so the driver stops. IsFinished reports the same condition to polling callers.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // A skip packet has no parsed field tree to serialize: an expected per-item failure, handled by the tolerance mode.
        if (!packet.HasFieldTree)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ExportErrorKind.Other,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
        }

        _Buffer.Reset();

        // Comma + blank line separator between packets. It is written in front of a packet (not after it) and only when a
        // packet was already written: PacketCount counts written packets only, so a skipped packet leaves neither a
        // dangling comma nor a missing one, and the closing bracket needs no special handling for the last element.
        if (PacketCount > 0)
        {
            _Buffer.Write(_PacketSeparator);
        }

        // Serialize the whole packet into the buffer first, then write it with one stream call. A packet is therefore
        // either handed to the stream completely or not at all. Serialization is inside the try block with the write: a
        // field whose text cannot be produced (a deferred protocol string that throws on corrupt data, for example) is a
        // property of this packet and must become a skipped packet, not an exception out of OnPacket.
        // _DirectStream is guaranteed non-null after _Start() succeeds
        try
        {
            _SerializePacket(packet);
            _WriteDirect(_Buffer.WrittenSpan);
            if (_FlushPerPacket)
            {
                _DirectStream?.Flush();
            }
        }
        catch (Exception ex)
        {
            // The compact writer updated its same-as-previous state while it serialized this packet, but the packet never
            // reached the file. Forget that state so the next packet is written in full instead of referring to values
            // the file does not contain. Not needed for the other formats (they keep no state).
            _State?.Reset();

            // Any error during serialization or write — skip packet with error reporting.
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

    /// <summary>Serializes one packet into <see cref="_Buffer"/> in the configured format.</summary>
    /// <exception cref="InvalidOperationException">The format is not one of the defined values (a broken invariant).</exception>
    private void _SerializePacket(Packet packet)
    {
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
            default:
                // The builder rejects undefined values, so this is a bug (a cast enum that bypassed it). Writing nothing
                // would produce an empty object for every packet.
                throw new InvalidOperationException(
                    $"Undefined JSON export format {((int)_Format).ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    /// Handles a skipped packet: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
    private bool _HandleSkip(ExportErrorEventArgs error)
    {
        // Saturating counters: a multi-billion-packet export must not wrap into negative counts.
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

        /// <summary>Sets the output to a file path with a 4 MiB buffer. The file is created lazily on the first write.</summary>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
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
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined <see cref="JsonExportFormat"/>.</exception>
        public Builder WithFormat(JsonExportFormat format)
        {
            // A cast integer would otherwise select no writer and every packet would come out empty.
            if (!Enum.IsDefined(format))
            {
                throw new ArgumentOutOfRangeException(nameof(format), format, "The JSON export format is not defined.");
            }

            _Format = format;
            return this;
        }

        /// <summary>
        /// Enables or disables flushing the stream after each packet. Off by default: a flush per packet defeats the
        /// output buffering and is only useful for consumers that read the file while it is being written.
        /// </summary>
        public Builder WithFlushPerPacket(bool flush)
        {
            _FlushPerPacket = flush;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // Packets are addressed by array-index ids, so a source cannot deliver more than MaxCount packets; a larger
            // target would be unreachable and is rejected as a configuration error.
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
