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
public sealed class JsonExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IDisposable
{
    #region Configuration and state

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

    /// <summary>
    /// Output target and sole owner of its stream. Every write goes to the stream the output reports through
    /// <see cref="ExportOutput.TryGetExistingStream"/>, so no second reference to the stream exists that could be disposed twice.
    /// The packets are written straight to that stream (for a file output a 4 MiB BufferedStream), so no second buffering layer
    /// is needed between the serialization buffer and the stream. Cleared to <c>null</c> after disposal to prevent a double dispose.
    /// </summary>
    private ExportOutput? _Output;

    /// <summary>What this export contains; the hierarchy shape writes the field tree with the writers of the format.</summary>
    private readonly ExportContent _Content;

    /// <summary>Per-packet staging of the flat shapes (the shared row stage); <see langword="null"/> for the hierarchy shape.</summary>
    private readonly RowFieldStage? _Stage;

    /// <summary>Selection and occurrence policy of the flat shapes; fed by the parse (as observer) or by a tree walk.</summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>Same-as-previous and field-info deduplication state; only allocated for the Compact format.</summary>
    private readonly JsonExporterState? _State;

    /// <summary>
    /// Whether this export is a compact field tree: the one combination that names a field id once per document and elides
    /// repeated values by id (a catalog), so it accepts packets of one stack only (see <see cref="_CompactStack"/>).
    /// </summary>
    private readonly bool _IsCompactTree;

    /// <summary>
    /// The stack of the first packet written by a compact field tree export; every later packet of that export must come from it.
    /// <see langword="null"/> before the first written packet and after <see cref="OnFinish"/> (so the stack is not kept alive).
    /// </summary>
    private Stack? _CompactStack;

    /// <summary>
    /// Reusable serialization buffer: each packet is serialized here and then written to the stream in one call, which avoids
    /// per-packet allocations. Not declared readonly because the writers take it by <c>ref</c>, and a readonly field cannot be
    /// passed by ref. (<see cref="PooledBuffer"/> is a class, so the <c>ref</c> is not needed for the writes to be visible.)
    /// </summary>
    private PooledBuffer _Buffer = new(4096);

    /// <summary>Bytes handed to the stream so far; backs <see cref="EstimatedOutputBytes"/>.</summary>
    private long _CommittedBytes;

    /// <summary>Latched fatal state; every later <see cref="OnPacket"/> returns <c>false</c>.</summary>
    private bool _HasError;

    /// <summary>Set once <see cref="_Start"/> ran, successful or not, so a failed start is never retried.</summary>
    private bool _Started;

    /// <summary>Latch that stops <see cref="OnPacket"/> once <see cref="OnFinish"/> began.</summary>
    private bool _Finished;

    /// <summary>
    /// Whether the closing JSON array bracket has been written. Separate from <see cref="_Finished"/> so that a failed bracket
    /// write on the first <see cref="OnFinish"/> call is not mistaken for a completed finish. In the current flow the first
    /// <see cref="OnFinish"/> also releases the output, so a later call finds no stream and cannot write the bracket (see
    /// <see cref="_FinalizeFile"/>).
    /// </summary>
    private bool _ClosingBracketWritten;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="output">The output target; the exporter owns it from now on.</param>
    /// <param name="uiName">The display name.</param>
    /// <param name="description">The optional description.</param>
    /// <param name="format">The JSON style.</param>
    /// <param name="flushPerPacket">Whether to flush the stream after every packet.</param>
    /// <param name="targetPacketCount">Maximum number of packets to write; 0 means unlimited.</param>
    /// <param name="content">What the export contains; already validated by the builder.</param>
    /// <param name="cancellationToken">Token that stops the export cooperatively.</param>
    private JsonExporter(
        ExportOutput output,
        string uiName,
        string? description,
        JsonExportFormat format,
        bool flushPerPacket,
        int targetPacketCount,
        ExportContent content,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _Format = format;
        _FlushPerPacket = flushPerPacket;
        _TargetPacketCount = targetPacketCount;
        _Content = content;
        _CancellationToken = cancellationToken;

        // The flat shapes stage the fields of a packet while it is parsed and write the packet in OnPacket; the collector applies
        // the selection and the occurrence choice, the stage keeps the one packet until it is committed.
        if (content.Shape != ExportShape.Hierarchy)
        {
            _Stage = new();
            _Collector = new ExportFieldCollector(content, _Stage);
        }

        // Allocate state only for Compact mode (dedup + same-as-previous); Pretty and Array are stateless. The dense-array
        // threshold of the previous-value store is a sizing hint for the number of distinct field ids, not a limit: larger ids
        // are handled by the growth/overflow paths.
        if (format == JsonExportFormat.Compact)
        {
            _State = new JsonExporterState(PreviousFieldStore.DenseThreshold);
        }

        // The flat shapes write the full name of every field, so only the compact field tree depends on one stack's field ids.
        _IsCompactTree = _State is not null && _Stage is null;
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    /// <returns>A builder with the defaults: compact format, field tree content, no output target.</returns>
    public static Builder CreateBuilder() => new();

    /// <summary>
    /// The content written when <see cref="Builder.WithContent"/> is not called: the field tree of every packet (the hierarchy
    /// shape), without frame bytes (see <see cref="ExportContent.FrameBytes"/>; every shape and style writes them as the
    /// <c>frame_bytes</c> key on request) and without additional buffers (see <see cref="ExportContent.Buffers"/>; every shape and
    /// style writes them as the <c>buffers</c> key on request). The <c>Info</c> member decides whether the <c>info</c> key (<c>IN</c> in the compact
    /// format) is written, in every shape and style.
    /// </summary>
    public static ExportContent DefaultContent { get; } = new() { Shape = ExportShape.Hierarchy };

    #endregion

    #region Statistics and parse demand

    /// <inheritdoc/>
    /// <remarks>
    /// The hierarchy shape demands the field tree; the flat shapes a tree-free parse with the collector, so only the selected (or
    /// all) fields are delivered and texts are built only when the content asks for them.
    /// </remarks>
    public ParseDemand ParseDemand
    {
        get
        {
            if (_Collector is null)
            {
                return ParseDemand.FieldTree;
            }

            return _Collector.CreateDemand();
        }
    }

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

    #endregion

    #region Lifecycle

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
        if (!_Output.TryGetOrCreateUnderlyingStream(out _, out string openFailure))
        {
            _ReportStartFailure(ExportErrorKind.IoError, Invariant($"JSON output could not be opened: {openFailure}"));
            return false;
        }

        // _Started is already set, so a failed opening is never retried: a half-written document cannot be repaired.
        try
        {
            // Opening bracket write may throw on a broken stream — surface as an
            // export error rather than escaping to the caller.
            _WriteDirect(_ArrayOpen);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportErrorKind kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            _ReportStartFailure(kind, Invariant($"JSON header write failed: {ex.Message}"));
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    /// <param name="kind">What failed: the output could not be opened or written, or the header could not be produced.</param>
    /// <param name="message">Human-readable reason, passed to the event.</param>
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
            new ExporterReleaseStep("row stage disposal", _DisposeStage),
            new ExporterReleaseStep("compact stack release", _ReleaseCompactStack),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>
    /// Writes the closing bracket and flushes. Starts first for an empty export so it produces <c>"[\n]\n"</c>, a valid
    /// empty JSON array. The closing bracket is guarded by <see cref="_ClosingBracketWritten"/> so it is written at most
    /// once. A repeated <see cref="OnFinish"/> after a failed first attempt reaches this method again, but the first
    /// attempt already released the output (<see cref="_DisposeOutput"/> clears the output reference), so the bracket is
    /// not retried and the file stays incomplete; the failure was reported through <see cref="ItemSkipped"/> on the first call.
    /// </summary>
    private void _FinalizeFile()
    {
        if (!_Started && _Output is not null)
        {
            _Start();
        }

        if (!_ClosingBracketWritten && _Output?.TryGetExistingStream() is not null)
        {
            _WriteClosingBracket();
            _ClosingBracketWritten = true;
            _FlushStream();
        }
    }

    /// <summary>Returns the pooled chunks of the row stage (a no-op for the hierarchy shape).</summary>
    private void _DisposeStage() => _Stage?.Dispose();

    /// <summary>Forgets the stack of the compact export, so a finished exporter does not keep the stack alive.</summary>
    private void _ReleaseCompactStack() => _CompactStack = null;

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        output?.Dispose();
    }

    /// <summary>Error channel of the finish sequence: marks the error, counts it, and raises <see cref="ItemSkipped"/>.</summary>
    /// <param name="kind">What failed during the finish sequence.</param>
    /// <param name="message">Human-readable reason, passed to the event.</param>
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

    #endregion

    #region Packet intake

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

    /// <summary>Serializes and outputs a single packet.</summary>
    /// <param name="packet">The packet to write.</param>
    /// <returns>
    /// <see langword="false"/> when the driver should stop: the target count is reached, a packet of another stack arrived, or a
    /// skipped packet is fatal in <see cref="ErrorToleranceMode.Strict"/> mode; <see langword="true"/> otherwise (also for a
    /// packet skipped in tolerant mode).
    /// </returns>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse the packet so the driver stops. IsFinished reports the same condition to polling callers.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // A field selection was resolved against one stack: its field ids name other fields in any other stack, so a packet of
        // another stack would be written with the wrong fields and no error. It is refused as a fatal StackMismatch (the same rule
        // as PBF, DuckDB, and Parquet); the rows of the earlier packets are still finished by OnFinish.
        if (_Content.Fields is not null && !ReferenceEquals(packet.Stack, _Content.Fields.Stack))
        {
            _ReportStackMismatch();
            return false;
        }

        // The compact field tree is a catalog: it names a field id once per document and elides repeated values by id. Field ids
        // are dense per stack, so a packet of another stack (a session restart) would be read with the names of the first stack.
        // The packet is refused as a fatal StackMismatch, like the catalog exporters; the earlier packets stay a valid document.
        // The stack is remembered from the first packet that reached the file, so a packet that was skipped does not pin it.
        if (_IsCompactTree && _CompactStack is not null && !ReferenceEquals(packet.Stack, _CompactStack))
        {
            _ReportStackMismatch();
            return false;
        }

        // A skip packet has no parsed field tree to serialize: an expected per-item failure, refused here without an exception and
        // handled by the tolerance mode. Every exporter with a hierarchy shape reports the same kind and message. The flat shapes
        // need no tree: their fields were staged while the packet was parsed.
        if (_Stage is null && !packet.HasFieldTree)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ExportErrorKind.SerializationError,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
        }

        // Flat shapes: commit what the observer staged for this packet, or walk the tree when the packet has one and nothing was
        // staged (a pipeline without a parse plan). A packet that has neither is reported with the cause, before anything is
        // written, so no separator is left dangling.
        if (_Stage is not null && !_TryPrepareFlat(packet, out ExportErrorKind stageKind, out string? stageMessage))
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = stageKind,
                Message = stageMessage!, // Invariant: a false result carries a message.
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
        // The output is guaranteed to have a stream after _Start() succeeded.
        try
        {
            _SerializePacket(packet);
            _WriteDirect(_Buffer.WrittenSpan);
            if (_FlushPerPacket)
            {
                _FlushStream();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The compact writer updated its same-as-previous state while it serialized this packet, but the packet never
            // reached the file. Forget that state so the next packet is written in full instead of referring to values
            // the file does not contain. Not needed for the other formats (they keep no state).
            _State?.Reset();

            // Any error during serialization or write — skip packet with error reporting.
            // I/O errors map to IoError; other failures (encoding, etc.) map
            // to SerializationError so callers can distinguish the cause.
            ExportErrorKind kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = kind,
                Message = Invariant($"Packet {packet.Id.Value} could not be serialized or written: {ex.Message}"),
            });
        }

        // The packet is in the file: from now on a compact field tree document belongs to this packet's stack.
        if (_IsCompactTree)
        {
            _CompactStack ??= packet.Stack;
        }

        PacketCount++;
        return true;
    }

    /// <summary>
    /// A packet of another stack than the field selection's (or than the compact document's) arrived: latches the fatal state,
    /// counts it, and raises <see cref="ItemSkipped"/> with <see cref="ExportErrorKind.StackMismatch"/>. The packet is not written.
    /// </summary>
    private void _ReportStackMismatch()
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = PacketCount,
            Kind = ExportErrorKind.StackMismatch,
            Message = "The packet was parsed on another protocol stack than this export's field selection or earlier packets; " +
                "start a new export after a session restart.",
        });
    }

    /// <summary>
    /// Makes the staged fields of <paramref name="packet"/> available in <see cref="_Stage"/>: either the observer staged them
    /// during the parse of this packet (commit), or the packet has a field tree and is walked now. The same feed decision as the
    /// other exporters of the flat shapes.
    /// </summary>
    /// <param name="packet">The packet about to be written.</param>
    /// <param name="kind">The error kind when the result is <see langword="false"/>.</param>
    /// <param name="message">The reason when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the stage holds the fields of <paramref name="packet"/>.</returns>
    private bool _TryPrepareFlat(Packet packet, out ExportErrorKind kind, out string? message)
    {
        // A tree packet whose fields this exporter did not stage is walked through the same collector, so both feeds give the same
        // output; a failure of that walk (a faulty text) skips the packet. The commit rule: staged values belong to the packet only
        // when the ids match and EndPacket reported completeness. Both live in the shared guarded feed of the row exporters.
        return RowFeed.TryPrepare(_Stage!, _Collector!, packet, out kind, out message); // Invariant: stage and collector are created together.
    }

    /// <summary>
    /// Handles a skipped packet: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
    /// <param name="error">The report of the skipped packet; raised through <see cref="ItemSkipped"/> in tolerant mode.</param>
    /// <returns><see langword="false"/> in <see cref="ErrorToleranceMode.Strict"/> mode (the export stops); otherwise <see langword="true"/>.</returns>
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

    #endregion

    #region Serialization

    /// <summary>Serializes one packet into <see cref="_Buffer"/> in the configured format.</summary>
    /// <param name="packet">The packet to serialize; a field tree packet unless a flat shape staged its fields.</param>
    /// <exception cref="InvalidOperationException">The format is not one of the defined values (a broken invariant).</exception>
    private void _SerializePacket(Packet packet)
    {
        if (_Stage is not null)
        {
            bool indent = _Format == JsonExportFormat.Pretty;
            if (_Content.Shape == ExportShape.SelectedFields)
            {
                FlatWriter.WriteSelected(packet, _Stage, _Content.Fields!, _Content, indent, ref _Buffer); // Invariant: SelectedFields content carries fields.
            }
            else
            {
                FlatWriter.WriteAll(packet, _Stage, _Content, indent, ref _Buffer);
            }

            return;
        }

        switch (_Format)
        {
            case JsonExportFormat.Compact:
                CompactWriter.WritePacket(
                    packet,
                    ref _Buffer,
                    _State!, // Invariant: the compact format creates its state in the constructor.
                    _Content.Info,
                    _Content.CustomText,
                    _Content.CustomRepresentation,
                    _Content.FrameBytes,
                    _Content.Buffers);
                break;
            case JsonExportFormat.Pretty:
                PrettyWriter.WritePacket(
                    packet,
                    ref _Buffer,
                    _Content.Info,
                    _Content.CustomText,
                    _Content.CustomRepresentation,
                    _Content.FrameBytes,
                    _Content.Buffers);
                break;
            case JsonExportFormat.Array:
                ArrayWriter.WritePacket(
                    packet,
                    ref _Buffer,
                    _Content.Info,
                    _Content.CustomText,
                    _Content.CustomRepresentation,
                    _Content.FrameBytes,
                    _Content.Buffers);
                break;
            default:
                // The builder rejects undefined values, so this is a bug (a cast enum that bypassed it). Writing nothing
                // would produce an empty object for every packet.
                _ThrowUndefinedFormat(_Format);
                break;
        }
    }

    /// <summary>
    /// The configured format is not one of the defined values: a broken invariant (a cast enum that bypassed the builder check), reported
    /// from a cold helper so the serializer has no throw site.
    /// </summary>
    /// <param name="format">The undefined value.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowUndefinedFormat(JsonExportFormat format) =>
        throw new InvalidOperationException(Invariant($"Undefined JSON export format {(int)format}."));

    /// <summary>Writes bytes to the output's stream and updates <see cref="_CommittedBytes"/>; nothing when the output is gone.</summary>
    /// <param name="data">The bytes to write.</param>
    private void _WriteDirect(ReadOnlySpan<byte> data)
    {
        Stream? stream = _Output?.TryGetExistingStream();
        if (stream is null)
        {
            return;
        }

        stream.Write(data);
        _CommittedBytes += data.Length;
    }

    /// <summary>Flushes the output's stream; nothing when the output is gone or has no stream yet.</summary>
    private void _FlushStream() => _Output?.TryGetExistingStream()?.Flush();

    /// <summary>Writes the closing bracket to the output.</summary>
    private void _WriteClosingBracket() =>
        // The output has no stream when _Start() never succeeded (an empty export is started in OnFinish first).
        _WriteDirect(_ArrayClose);

    #endregion

    #region Builder

    /// <summary>
    /// Fluent builder for constructing a <see cref="JsonExporter"/>. Not thread-safe: configure it from one thread; setters return the
    /// builder itself.
    /// </summary>
    /// <remarks>
    /// Output: <c>ToFile</c>, <c>ToStream</c>, <c>ToStdout</c>, and <c>ToTarget</c> replace each other, and the last call before
    /// <c>Build</c> wins. Nothing is opened before the first item, so a replaced output creates no file and holds no resource.
    /// The output belongs to the exporter that <c>Build</c> returns: build every further exporter from a new builder, because a
    /// second <c>Build</c> hands both exporters the same output.
    /// </remarks>
    public sealed class Builder
    {
        /// <summary>The output target; <see langword="null"/> until one of the <c>To…</c> methods ran.</summary>
        private ExportOutput? _Output;

        /// <summary>The display name.</summary>
        private string _UiName = "JSON Exporter";

        /// <summary>The optional description.</summary>
        private string? _Description;

        /// <summary>The cancellation token handed to the exporter.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>The JSON style.</summary>
        private JsonExportFormat _Format = JsonExportFormat.Compact;

        /// <summary>Whether the stream is flushed after every packet.</summary>
        private bool _FlushPerPacket;

        /// <summary>Maximum number of packets to write; 0 means unlimited.</summary>
        private int _TargetPacketCount;

        /// <summary>What the export contains.</summary>
        private ExportContent _Content = DefaultContent;

        /// <summary>Sets the output to a file path with a 4 MiB buffer. The file is created lazily on the first write.</summary>
        /// <param name="path">Absolute or relative path of the output file.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>Sets the output to an existing stream.</summary>
        /// <param name="stream">The writable target stream; the caller keeps ownership and closes it.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
        /// <remarks>
        /// A stream that fails during the export is reported through <see cref="ItemSkipped"/>.
        /// </remarks>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>
        /// Sets the output to a host target: the exporter opens one entry, <see cref="ExportEntryName.Main"/>, on its first packet
        /// and never learns where it lands.
        /// </summary>
        /// <param name="target">The target; a refused entry becomes an export error of the first packet.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
        public Builder ToTarget(IExportTarget target)
        {
            _Output = ExportOutput.FromTarget(target);
            return this;
        }

        /// <summary>Sets the output to stdout.</summary>
        /// <returns>This builder.</returns>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        /// <summary>Sets the user-friendly display name shown in UI and logs.</summary>
        /// <param name="name">Display name; not empty or white space.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or white space.</exception>
        public Builder WithUiName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
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
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the JSON output format.</summary>
        /// <param name="format">The JSON style; a defined <see cref="JsonExportFormat"/> member.</param>
        /// <returns>This builder.</returns>
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
        /// <param name="flush"><see langword="true"/> to flush after every packet.</param>
        /// <returns>This builder.</returns>
        public Builder WithFlushPerPacket(bool flush)
        {
            _FlushPerPacket = flush;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        /// <param name="count">Number of packets to write; 0 for no limit.</param>
        /// <returns>This builder.</returns>
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
                    Invariant($"Target packet count must not exceed {ArrayIndexIdRange.MaxCount} (Array.MaxLength={Array.MaxLength})."));
            }

            _TargetPacketCount = count;
            return this;
        }

        /// <summary>
        /// Sets what the export contains: the shape (selected fields, all fields, or the field tree), the selected fields, and the
        /// optional members (info, custom representation and text). Defaults to <see cref="DefaultContent"/>.
        /// </summary>
        /// <param name="content">
        /// The content; <c>FrameBytes</c> adds the <c>frame_bytes</c> key (standard Base64) to every packet, and <c>Buffers</c> adds the
        /// <c>buffers</c> key (index and Base64 bytes of each additional buffer) to every packet that has one.
        /// </param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The shape and the fields do not match (<see cref="ExportContent.TryValidateShape"/>).</exception>
        public Builder WithContent(ExportContent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (!content.TryValidateShape(out string? problem))
            {
                throw new ArgumentException(problem, nameof(content));
            }

            _Content = content;
            return this;
        }

        /// <summary>Sets the cancellation token for cooperative shutdown.</summary>
        /// <param name="cancellationToken">The token; once cancelled, <see cref="OnPacket"/> returns <see langword="false"/>.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken cancellationToken)
        {
            _CancellationToken = cancellationToken;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output target was configured.</summary>
        /// <returns>A new exporter that owns the configured output.</returns>
        /// <exception cref="InvalidOperationException">No output target was configured.</exception>
        public JsonExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException("No output target configured. Call ToFile(), ToStream(), ToStdout(), or ToTarget().");
            }

            return new JsonExporter(
                _Output,
                _UiName,
                _Description,
                _Format,
                _FlushPerPacket,
                _TargetPacketCount,
                _Content,
                _CancellationToken);
        }
    }

    #endregion
}
