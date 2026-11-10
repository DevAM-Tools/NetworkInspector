// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Text;

/// <summary>
/// Exports packets to a human-readable text format with configurable detail level.
/// <para>
/// Output is a plain-text representation of the parsed protocol field tree, one indented
/// line per field. Each packet begins with a header line showing
/// the packet number and timestamp, followed by the field tree with two-space
/// indentation per nesting level, and a separator line after the tree.
/// </para>
/// <para>
/// <b>Display lines:</b> every line is the display line of its field, the same text as <c>Field.ToString()</c>: a custom text is
/// the whole line (a dissector that wants the label writes it into the text), a field without a value shows its UI name, and
/// every other field shows its UI name, <c>": "</c>, and the custom representation or the default text of the value (a bytes
/// value as upper-case hex). The detail level only selects which fields get a line. A switched-off custom text or representation
/// is left out of the line. This exporter writes the parts of the line itself, by the rule of Core's <c>Field.ToString</c>, so that
/// the text limit and the escaping apply per part (see <c>_WriteLineFromParts</c>). Sync: EXPORTER_GUIDE.md § Rebuilding the
/// display line.
/// </para>
/// <para>
/// <b>Frame bytes (opt-in):</b> with <see cref="ExportContent.FrameBytes"/> every packet ends, after its lines and before the blank
/// separator line, with the block <c>Frame bytes (n):</c> followed by one line per 16 bytes: the offset (at least four upper-case hex
/// digits), two spaces, and the bytes as upper-case hex pairs separated by one space. An empty frame writes the title line alone.
/// The dump is never cut by the maximum text length and has no ASCII column.
/// </para>
/// <para>
/// <b>Additional buffers (opt-in):</b> with <see cref="ExportContent.Buffers"/> every packet that has additional buffers (reassembled
/// datagrams, TCP PDUs, decoded bodies) writes, after the frame dump, one block <c>Buffer i (n):</c> per buffer in ascending index
/// order, in the same line format as the frame dump. A packet without additional buffers writes no such block.
/// </para>
/// <para>
/// Example output (Standard level, an excerpt of one UDP packet): every protocol layer is a line with its display text,
/// followed by its value fields; raw byte buffers are left out.
/// <code>
/// Packet 1  [2024-01-01T12:00:00.000000000Z]
/// Ethernet II, Src: 00:11:22:33:44:55, Dst: AA:BB:CC:DD:EE:FF
/// Internet Protocol Version 4, Src: 192.168.1.1, Dst: 192.168.1.2
///   Source Address: 192.168.1.1
///   Destination Address: 192.168.1.2
/// User Datagram Protocol, Src Port: 12345, Dst Port: 53
///   Source Port: 12345
///   Destination Port: 53
///
/// </code>
/// </para>
/// <para>
/// <b>Control characters:</b> text that comes from a capture (values, custom texts, display texts) is written with C0 control
/// characters and DEL made visible (<c>\n</c>, <c>\r</c>, <c>\t</c>, else <c>\xHH</c>), the C1 controls U+0080–U+009F as
/// <c>\u00HH</c>, and the backslash as <c>\\</c>, so no value can end a line, send a terminal escape sequence (the C1 control
/// U+009B is a control sequence introducer on terminals that honor C1), or imitate an escape that the exporter wrote.
/// Unicode bidirectional controls are written as they are. The <c>|</c> and the two spaces that separate the parts of a
/// selected-fields line are not escaped in values: the parts are for reading, not for parsing.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// <para>
/// <b>Allocation strategy:</b> each packet is formatted completely into one reusable <see cref="PooledBuffer"/> and
/// handed to the output with a single write. Text is encoded to UTF-8 on the stack (or a pooled instance scratch
/// array for large values) and values are formatted straight into UTF-8, so the common path allocates nothing per
/// field. Because a packet is written only after it is fully formatted, a failure while formatting never leaves half a
/// packet in the output.
/// </para>
/// </summary>
/// <remarks>
/// Sync: Text/README.md § Detail Levels and § Limits And Thread-Safety Notes.
/// </remarks>
/// <seealso cref="TextDetailLevel"/>
/// <seealso cref="ExportContent"/>
public sealed class TextExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IDisposable
{
    #region Configuration and state

    /// <summary>Number of spaces per indentation level.</summary>
    private const int _IndentWidth = 2;

    /// <summary>Largest encoded size in bytes that is formatted on the stack; larger values use the pooled instance scratch.</summary>
    private const int _StackBufferLimit = 512;

    /// <summary>The cancellation token of the export; a requested cancellation ends the export at the next packet.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>The detail level of the hierarchy shape; the flat shapes ignore it.</summary>
    private readonly TextDetailLevel _DetailLevel;

    /// <summary>
    /// Maximum characters for string/bytes values. 0 = unlimited.
    /// Limit: default 256 characters, 0 (unlimited) to <see cref="int.MaxValue"/>. Threat: a single huge value (for
    /// example a large string or byte payload) inflating every output line and the memory held for formatting it.
    /// Configurable by the library caller through <c>WithMaxTextLength</c>. Posture: secure by default (truncation is
    /// on); unlimited output is an explicit opt-in. Why 256: a line stays readable in a terminal and a log viewer (about three
    /// screen lines), while the common protocol texts (names, addresses, short payload summaries) fit whole. Agreed by the owner.
    /// </summary>
    private readonly int _MaxTextLength;

    /// <summary>Stops the export after this many written packets; 0 means no limit.</summary>
    private readonly int _TargetPacketCount;

    /// <summary>The output; <see langword="null"/> after it was disposed by <see cref="OnFinish"/>.</summary>
    private ExportOutput? _Output;

    /// <summary>The formatted bytes of the packet being written; reset per packet, returned to the pool by <see cref="OnFinish"/>.</summary>
    private readonly PooledBuffer _Buffer = new(16384);

    /// <summary>What this export contains; the hierarchy shape writes the field tree at the configured detail level.</summary>
    private readonly ExportContent _Content;

    // The two text switches decide which parts a display line is built from; they are read once from the content because every
    // field of every packet asks.

    /// <summary>Whether a field's custom text is used (it is the whole line when set). When off, no text is read.</summary>
    private readonly bool _IncludeCustomText;

    /// <summary>Whether a value's custom representation is used (it replaces the default text of the value). When off, none is read.</summary>
    private readonly bool _IncludeCustomRepresentation;

    /// <summary>
    /// Whether the hex dump of the frame follows the lines of every packet. Read once from the content because every packet asks.
    /// </summary>
    private readonly bool _IncludeFrameBytes;

    /// <summary>
    /// Whether a hex dump of every additional packet buffer follows the frame dump of every packet. Read once from the content
    /// because every packet asks.
    /// </summary>
    private readonly bool _IncludeBuffers;

    /// <summary>
    /// The text of a field whose custom text the export does not use: always absent. A static readonly default is the target of the
    /// by-reference text of a switched-off field, so the switch costs no read of the field's own text slot.
    /// </summary>
    private static readonly LazyString _AbsentText;

    /// <summary>Per-packet staging of the flat shapes (the shared row stage); <see langword="null"/> for the hierarchy shape.</summary>
    private readonly RowFieldStage? _Stage;

    /// <summary>Selection and occurrence policy of the flat shapes; fed by the parse (as observer) or by a tree walk.</summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>
    /// Latched fatal state (Strict error, stack mismatch, or a failed finish step); every later <see cref="OnPacket"/> returns
    /// <see langword="false"/>.
    /// </summary>
    private bool _HasError;

    /// <summary>Whether the output received its first bytes; an export without any packet still writes one newline.</summary>
    private bool _Started;

    /// <summary>Whether <see cref="OnFinish"/> ran; the buffers are returned to the pool then, so no packet may be formatted.</summary>
    private bool _Finished;

    /// <summary>
    /// Pooled UTF-8 scratch for values too large for the stack buffers. Rented by <see cref="_GetScratch"/>, which grows it by at
    /// least doubling (it is never shrunk, so with an unlimited text length it stays as large as the largest value seen), and
    /// returned to the pool by <see cref="_ReturnScratch"/>. Shared by <see cref="_WriteString"/>, <see cref="_WriteSpan"/>,
    /// <see cref="_WriteDefaultText"/>, and <see cref="_WriteIndent"/>; none of them holds a
    /// scratch span while another one asks for the scratch.
    /// </summary>
    private byte[]? _Utf8Scratch;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="output">The output to write to; the exporter owns it unless it wraps a caller stream.</param>
    /// <param name="uiName">Display name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="detailLevel">A defined detail level for the hierarchy shape.</param>
    /// <param name="maxTextLength">Maximum characters per value; 0 means unlimited.</param>
    /// <param name="targetPacketCount">Packets after which the export ends; 0 means no limit.</param>
    /// <param name="content">The validated content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private TextExporter(
        ExportOutput output,
        string uiName,
        string? description,
        TextDetailLevel detailLevel,
        int maxTextLength,
        int targetPacketCount,
        ExportContent content,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _DetailLevel = detailLevel;
        _MaxTextLength = maxTextLength;
        _TargetPacketCount = targetPacketCount;
        _Content = content;
        _IncludeCustomText = content.CustomText;
        _IncludeCustomRepresentation = content.CustomRepresentation;
        _IncludeFrameBytes = content.FrameBytes;
        _IncludeBuffers = content.Buffers;
        _CancellationToken = cancellationToken;

        // The flat shapes stage the fields of a packet while it is parsed and write the packet in OnPacket; the collector applies
        // the selection and the occurrence choice, the stage keeps the one packet until it is committed.
        if (content.Shape != ExportShape.Hierarchy)
        {
            _Stage = new();
            _Collector = new ExportFieldCollector(content, _Stage);
        }
    }

    /// <summary>Creates a new <see cref="Builder"/> for fluent configuration.</summary>
    /// <returns>A builder with the defaults: standard detail level, 256 characters per value, the field tree of every packet.</returns>
    public static Builder CreateBuilder() => new();

    /// <summary>
    /// The content written when <see cref="Builder.WithContent"/> is not called: the field tree of every packet at the configured
    /// detail level (the hierarchy shape), without frame bytes (see <see cref="ExportContent.FrameBytes"/>) and without additional
    /// buffers (see <see cref="ExportContent.Buffers"/>). The <c>Info</c>
    /// member is not used by the text format (the tree shows the info as a field line).
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

    /// <summary>Number of packets successfully written.</summary>
    public int PacketCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    /// <remarks>
    /// Bytes accepted by the output plus the length of the packet buffer. The buffer is only reset when the next
    /// packet starts, so it still holds the most recently written packet and the value can exceed the true size by
    /// that packet.
    /// </remarks>
    public long EstimatedOutputBytes =>
        (_Output?.AcceptedBytes ?? 0L) + _Buffer.Length;

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

    /// <inheritdoc/>
    public bool IsFinished => _Finished || _HasError
        || _CancellationToken.IsCancellationRequested
        || (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount);

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    /// <remarks>
    /// If no packets were written before <see cref="OnFinish"/> is called, the output
    /// is a single newline (valid empty text export). Callers that need packet content must
    /// check <see cref="IExporterStatistics.WrittenCount"/> before use. Idempotent; also invoked by <see cref="Dispose"/>.
    /// </remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }

        _Finished = true;

        // Shared finish sequence (same order as every exporter): final write and flush, buffer return, output disposal.
        ExporterFinish.Run(
            "Text",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("buffer return", _Buffer.Return),
            new ExporterReleaseStep("row stage disposal", _DisposeStage),
            new ExporterReleaseStep("scratch return", _ReturnScratch),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    /// <summary>Returns the pooled chunks of the row stage (a no-op for the hierarchy shape).</summary>
    private void _DisposeStage() => _Stage?.Dispose();

    /// <summary>Returns the UTF-8 scratch array to the pool, if one was rented. Idempotent.</summary>
    private void _ReturnScratch()
    {
        byte[]? scratch = _Utf8Scratch;
        _Utf8Scratch = null;
        if (scratch is not null)
        {
            // Not cleared: the bytes are export data the process already holds, the same trade-off as PooledBuffer.Return.
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Writes the single newline of an empty export, then flushes.</summary>
    private void _FinalizeFile()
    {
        if (_Output is null)
        {
            return;
        }

        // An export without packets still creates the output and writes a single newline, so the result is a valid
        // (empty) text file as the exporter guide requires; skipped after an error so no output is started then.
        if (!_Started && !_HasError)
        {
            _Started = true;
            _Output.Write("\n"u8);
        }

        _Output.Flush();
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
    /// <param name="message">The reason.</param>
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

    #endregion

    #region Packet intake

    /// <inheritdoc/>
    public bool OnPacket(Packet packet)
    {
        // Stop signals first: cancellation, an earlier fatal error, or a finished exporter all end the export. After
        // OnFinish the buffer is returned to the pool, so a late packet must never reach it.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        return _HandlePacket(packet);
    }

    /// <summary>Handles a single packet — formats it and writes it to the output.</summary>
    /// <param name="packet">The packet to write.</param>
    /// <returns><see langword="true"/> when the export goes on; <see langword="false"/> after a fatal error or at the target count.</returns>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse further packets so the caller stops feeding the exporter.
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

        // Flat shapes: commit what the observer staged for this packet, or walk the tree when the packet has one and nothing was
        // staged (a pipeline without a parse plan). A packet that has neither is reported with the cause.
        if (_Stage is not null && !_TryPrepareFlat(packet, out ExportErrorKind stageKind, out string? stageMessage))
        {
            return _Skip(stageKind, stageMessage!); // Invariant: a false result carries a message.
        }

        // Skip packets carry no field tree, so there is nothing to print: an expected input error, refused here without an exception
        // and counted as a skipped item (in Strict mode the export ends instead). Every exporter with a hierarchy shape reports the
        // same kind and message. The flat shapes need no tree: their fields were staged during the parse.
        if (_Stage is null && !packet.HasFieldTree)
        {
            return _Skip(ExportErrorKind.SerializationError, "Skip packets cannot be exported (HasFieldTree is false).");
        }

        try
        {
            _Started = true;

            // Format the whole packet into the reusable buffer first and write it with one call at the end, so an
            // exception while walking the tree (for example a lazy field that fails to materialize) cannot leave a
            // partial packet in the output.
            // Visit-time materialize via Children/HasChildren — avoid full-tree MaterializeAll.
            _Buffer.Reset();

            int packetNumber = PacketCount + 1;
            if (_Stage is null)
            {
                _WritePacketHeader(packet, packetNumber);
                _WriteFieldTree(packet);
                _WriteByteDumps(packet);
                _WriteSeparator();
            }
            else if (_Content.Shape == ExportShape.SelectedFields)
            {
                _WriteSelectedLine(packet, packetNumber);
            }
            else
            {
                _WriteAllLines(packet, packetNumber);
            }

            _Output!.Write(_Buffer.WrittenSpan); // Invariant: OnPacket refuses packets once OnFinish ran, which is the only place that clears the output.

            PacketCount++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ErrorCount < int.MaxValue)
            {
                ErrorCount++;
            }

            // The item counts as skipped in both modes (like in the other exporters); Strict then stops the export.
            int index = PacketCount;
            if (SkippedCount < int.MaxValue)
            {
                SkippedCount++;
            }

            if (ErrorTolerance == ErrorToleranceMode.Strict)
            {
                _HasError = true;
                return false;
            }

            // A failed write of the output is an I/O error (the data was fine); anything else went wrong while formatting the packet.
            ExportErrorKind kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            _RaiseItemSkipped(index, kind, ex.Message);
            return true;
        }
    }

    /// <summary>
    /// A packet of another stack than the field selection's arrived: latches the fatal state, counts it, and raises
    /// <see cref="ItemSkipped"/> with <see cref="ExportErrorKind.StackMismatch"/>. The packet is not written.
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
            Message = "The packet was parsed on another protocol stack than this export's field selection; start a new export after a session restart.",
        });
    }

    /// <summary>
    /// Counts one skipped packet and decides how the export goes on: Strict stops (without an event), Tolerant raises
    /// <see cref="ItemSkipped"/> and continues. The item counts as skipped in both modes, like in the other exporters.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <param name="message">The reason.</param>
    /// <returns><see langword="true"/> when the export goes on.</returns>
    private bool _Skip(ExportErrorKind kind, string message)
    {
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        int index = PacketCount;
        if (SkippedCount < int.MaxValue)
        {
            SkippedCount++;
        }

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _HasError = true;
            return false;
        }

        _RaiseItemSkipped(index, kind, message);
        return true;
    }

    /// <summary>
    /// Makes the staged fields of <paramref name="packet"/> available in <see cref="_Stage"/>: either the observer staged them
    /// during the parse of this packet (commit), or the packet has a field tree and is walked now. The same feed decision as the
    /// other exporters of the flat shapes.
    /// </summary>
    /// <param name="packet">The packet to stage.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the stage holds the fields of the packet.</returns>
    private bool _TryPrepareFlat(Packet packet, out ExportErrorKind kind, out string? message)
    {
        // A tree packet whose fields this exporter did not stage is walked through the same collector, so both feeds give the same
        // output; a failure of that walk (a faulty text) skips the packet. The commit rule: staged values belong to the packet only
        // when the ids match and EndPacket reported completeness. Both live in the shared guarded feed of the row exporters.
        return RowFeed.TryPrepare(_Stage!, _Collector!, packet, out kind, out message); // Invariant: stage and collector are created together.
    }

    /// <summary>Raises the <see cref="ItemSkipped"/> event.</summary>
    /// <param name="index">Zero-based index of the skipped packet.</param>
    /// <param name="kind">The error kind.</param>
    /// <param name="message">The reason.</param>
    private void _RaiseItemSkipped(int index, ExportErrorKind kind, string message)
    {
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = index,
            Kind = kind,
            Message = message,
        });
    }

    #endregion

    #region Flat shapes

    /// <summary>
    /// Selected-fields line: the packet header as in the tree format, then for each selected field present in the packet two
    /// spaces and <c>name=line</c>, several occurrences joined by <c>|</c>, where <c>line</c> is the field's display line (the
    /// rule of the tree lines). One line per packet keeps grep and diff useful; the detail level does not apply (the user
    /// named the fields, so a bytes field is written as hex too).
    /// </summary>
    /// <param name="packet">The packet whose staged fields are written.</param>
    /// <param name="packetNumber">1-based packet number for the header.</param>
    private void _WriteSelectedLine(Packet packet, int packetNumber)
    {
        _WritePacketHeader(packet, packetNumber, endLine: false);

        // Perf: an indexed loop; foreach over the IReadOnlyList would box an enumerator for every packet.
        IReadOnlyList<SelectedField> fields = _Content.Fields!.Fields; // Invariant: the SelectedFields shape is validated to carry fields.
        RowFieldStage stage = _Stage!; // Invariant: the flat shapes create the stage in the constructor.
        for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            SelectedField field = fields[fieldIndex];
            bool first = true;
            foreach (ref readonly RowFieldStage.Entry entry in stage.Entries)
            {
                if (entry.FieldId != field.Id.Value)
                {
                    continue;
                }

                if (first)
                {
                    _Buffer.Write("  "u8);
                    _WriteString(field.Name);
                    _Buffer.WriteByte((byte)'=');
                }
                else
                {
                    _Buffer.WriteByte((byte)'|');
                }

                first = false;
                _WriteEntryLine(packet.Stack, in entry);
            }
        }

        _Buffer.WriteByte((byte)'\n');
        _WriteByteDumps(packet);
    }

    /// <summary>
    /// All-fields lines: the packet header line, then one line <c>name[occurrence]=line</c> per staged value in production
    /// order (<c>line</c> is the field's display line, as in the tree), then the blank separator line of the tree format.
    /// </summary>
    /// <param name="packet">The packet whose staged fields are written.</param>
    /// <param name="packetNumber">1-based packet number for the header.</param>
    private void _WriteAllLines(Packet packet, int packetNumber)
    {
        _WritePacketHeader(packet, packetNumber, endLine: true);
        Span<byte> occurrence = stackalloc byte[20]; // outside the loop (CA2014)
        foreach (ref readonly RowFieldStage.Entry entry in _Stage!.Entries) // Invariant: the flat shapes create the stage in the constructor.
        {
            FieldInfo? info = packet.Stack.GetField(new FieldId(entry.FieldId));
            if (info is null)
            {
                _WriteString(entry.FieldId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _WriteString(info.Name);
            }

            _Buffer.WriteByte((byte)'[');
            if (!entry.Occurrence.TryFormat(occurrence, out int written, default, CultureInfo.InvariantCulture))
            {
                _ThrowFormatFailed("occurrence number");
            }

            _Buffer.Write(occurrence[..written]);
            _Buffer.Write("]="u8);
            _WriteEntryLine(packet.Stack, in entry);
            _Buffer.WriteByte((byte)'\n');
        }

        _WriteByteDumps(packet);
        _WriteSeparator();
    }

    /// <summary>
    /// Writes the display line of a staged entry (the flat shapes): the line the tree shows for the field, built from the staged
    /// parts by <see cref="_WriteLineFromParts"/>. The label is the UI name registered in the stack of the packet.
    /// </summary>
    /// <param name="stack">The stack of the packet; names the field.</param>
    /// <param name="entry">The staged value and its texts.</param>
    private void _WriteEntryLine(Stack stack, in RowFieldStage.Entry entry)
    {
        FieldInfo? info = stack.GetField(new FieldId(entry.FieldId));
        string label = info?.UiName ?? string.Empty; // No label for an id the stack does not know (see _WriteLineFromParts).

        // A bytes payload lives in the stage's arena; the line writer takes a value, so the arena range is wrapped as memory
        // (scalars, strings, and addresses are inline in the entry). Perf: no array per value; the range stays valid until the
        // stage's next BeginPacket, which is after this packet is written.
        FieldValueData data = entry.Value;
        if (entry.Type == FieldType.Bytes)
        {
            data = FieldValueData.NewBytes(_Stage!.GetBytesMemory(in entry)); // Invariant: flat shapes always have a stage.
        }

        // Invariant: the stage holds only the texts the content writes (the collector leaves a switched-off text absent), and
        // a null string is an absent text, the empty string a set but empty one.
        string? representation = entry.Representation;
        string? text = entry.Text;
        _WriteLineFromParts(label, in data, representation is not null, representation, text is not null, text);
    }

    #endregion

    #region Tree lines

    /// <summary>
    /// Writes the packet header line to the buffer.
    /// Format: <c>Packet N  [2024-01-01T12:00:00.000000000Z]\n</c>
    /// All writes go directly to <see cref="_Buffer"/> without heap allocation:
    /// the packet number is formatted into a 20-byte stackalloc and the timestamp
    /// into a <see cref="Timestamp.MaxFormattedLength"/>-byte stackalloc.
    /// </summary>
    /// <param name="packet">The packet to describe.</param>
    /// <param name="packetNumber">1-based packet number, pre-computed by the caller.</param>
    /// <param name="endLine">Whether the header ends its line (the selected-fields line continues after it).</param>
    /// <exception cref="InvalidOperationException">
    /// The number or the timestamp did not fit its stack buffer (a broken invariant); the caller reports the packet as skipped.
    /// </exception>
    private void _WritePacketHeader(Packet packet, int packetNumber, bool endLine = true)
    {
        // "Packet "
        _Buffer.Write("Packet "u8);

        // Packet number — an int needs at most 11 bytes (sign plus 10 digits), so 20 bytes is ample and the format cannot fail
        // (a failure is a broken invariant); formatting into a stack buffer avoids the string that ToString() would allocate.
        Span<byte> numBuf = stackalloc byte[20];
        if (!packetNumber.TryFormat(numBuf, out int numWritten, format: default, provider: CultureInfo.InvariantCulture))
        {
            _ThrowFormatFailed("packet number");
        }

        _Buffer.Write(numBuf[..numWritten]);

        // "  ["
        _Buffer.Write("  ["u8);

        // Timestamp — format directly into a fixed-size stackalloc (MaxFormattedLength is exact).
        // Timestamp.TryFormat writes ISO 8601 with nanosecond precision (all ASCII, 1 byte/char).
        Span<byte> tsBuf = stackalloc byte[Timestamp.MaxFormattedLength];
        if (!packet.Timestamp.TryFormat(tsBuf, out int tsWritten, format: default, provider: CultureInfo.InvariantCulture))
        {
            _ThrowFormatFailed("packet timestamp");
        }

        _Buffer.Write(tsBuf[..tsWritten]);

        // "]" and, unless the line goes on, the line end
        _Buffer.WriteByte((byte)']');
        if (endLine)
        {
            _Buffer.WriteByte((byte)10); // line feed
        }
    }

    /// <summary>
    /// Writes a blank separator line between packets.
    /// The field tree already ends with a newline, so one additional newline
    /// produces an empty line that visually separates consecutive packets.
    /// </summary>
    private void _WriteSeparator() => _Buffer.Write("\n"u8);

    /// <summary>
    /// Writes the protocol field tree starting from the packet root field.
    /// The root field itself is not printed; its children begin the output.
    /// </summary>
    /// <param name="packet">The packet whose field tree is written; it must have one.</param>
    private void _WriteFieldTree(Packet packet)
    {
        Field root = packet.RootField();
        // Full text dump must include lazy protocol trees (same policy as JSON/PBF).
        if (!root.IsValid || !root.HasChildren(materialize: true))
        {
            return;
        }

        // Root (depth 0) is skipped in output; recurse into its children at depth 1. Recursion depth follows the
        // field tree depth; unlike the PBF Standard writer there is no explicit nesting limit here, so it is bounded
        // only by the protocol nesting of the parsed packet.
        foreach (Field child in root.Children(materialize: true))
        {
            _WriteField(child, depth: 1);
        }
    }

    /// <summary>
    /// Recursively writes a single field and its descendants to the buffer, as far as the detail level allows.
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="depth">
    /// Current depth in the tree. Depth 1 is a direct child of root (no indentation).
    /// Each additional level adds <see cref="_IndentWidth"/> spaces.
    /// </param>
    private void _WriteField(Field field, int depth)
    {
        if (!field.IsValid)
        {
            return;
        }

        FieldType fieldType = field.Value.Type;

        // A container is a field with children: a group (no value) or a protocol layer (its raw bytes as a Bytes value). Only raw
        // byte leaves are "raw byte buffers" in the sense of the detail levels; a protocol layer is structure and stays visible.
        // materialize: true — a full text dump must include lazy protocol trees, and an unmaterialized layer would look like a leaf.
        bool hasChildren = field.HasChildren(materialize: true);
        bool isContainer = fieldType == FieldType.None || (fieldType == FieldType.Bytes && hasChildren);

        // Summary: containers only. Standard: everything but raw byte leaves. Full: everything.
        if (_DetailLevel == TextDetailLevel.Summary && !isContainer)
        {
            return;
        }

        if (fieldType == FieldType.Bytes && !hasChildren && _DetailLevel < TextDetailLevel.Full)
        {
            return;
        }

        // The detail level only selected this field; its line is the display line of the rule, the same at every level.
        _WriteFieldLine(field, depth);

        // Recurse into children unless Summary mode suppresses sub-fields.
        if (hasChildren && _DetailLevel != TextDetailLevel.Summary)
        {
            foreach (Field child in field.Children(materialize: true))
            {
                _WriteField(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Writes the indented display line of a single field: the line of <see cref="_WriteLineFromParts"/>, built from the parts the
    /// export's text switches allow, so a reader that rebuilds a line from an export with the same switches gets exactly this
    /// text. The detail level has already decided that the field gets a line; it never changes the line itself.
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="depth">Depth in the tree; depth 1 is not indented.</param>
    /// <remarks>
    /// Perf: allocation-free for texts and numbers (the parts are read through references and formatted into stack or pooled
    /// scratch). The texts are read through the field's own slots, so a deferred text caches there; a switched-off text is never
    /// read. Sync: <c>FieldDisplayLine</c> in Core states the same rule, and the Text tests compare each line with
    /// <c>Field.ToString()</c>.
    /// </remarks>
    private void _WriteFieldLine(Field field, int depth)
    {
        // depth 1 → 0 spaces, depth 2 → 2 spaces, etc.
        _WriteIndent((depth - 1) * _IndentWidth);

        // The value is read once (a 32-byte copy); its representation and the field's text are used only when the export writes
        // them. The ref locals point at the stored slots (or at the absent default), so a deferred text drops its factory after the
        // first build instead of being built again for the next consumer.
        FieldValue value = field.Value;
        ref readonly LazyString representation = ref (_IncludeCustomRepresentation
            ? ref value.CustomRepresentation
            : ref _AbsentText);
        ref readonly LazyString text = ref (_IncludeCustomText
            ? ref field.CustomText
            : ref _AbsentText);
        string label = field.FieldInfo?.UiName ?? string.Empty; // No label for an id the stack does not know (see _WriteLineFromParts).
        FieldValueData data = value.Data;

        _WriteLineFromParts(
            label,
            in data,
            !representation.IsNull,
            representation.AsSpan,
            !text.IsNull,
            text.AsSpan);
        _Buffer.Write("\n"u8);
    }

    /// <summary>The text between the label and the value part of a line.</summary>
    /// <remarks>Sync: the separator of <c>FieldDisplayLine</c> (Core) and EXPORTER_GUIDE.md § Rebuilding the display line.</remarks>
    private const string _LineSeparator = ": ";

    /// <summary>
    /// Writes a display line built from its parts, by the rule of Core's <c>Field.ToString</c>: a set custom text is the whole line
    /// (a dissector that wants the label writes it into the text); a field without a value shows its UI name; otherwise the line
    /// is the UI name, <c>": "</c>, and the value's default string (the custom representation when set, else the default text of
    /// the data, a bytes value as upper-case hex). A field id that the stack does not know has no UI name: the line is then the
    /// value part alone, without <c>": "</c>. This method writes each part its own way. Used for the tree lines and for the flat
    /// shapes (staged value and texts).
    /// </summary>
    /// <param name="label">
    /// The UI name registered in the stack (never empty: registration refuses an empty one), or the empty string (no
    /// label) for an id the stack does not know; trusted code, not capture text.
    /// </param>
    /// <param name="data">The field's value; <see cref="FieldType.None"/> for a container.</param>
    /// <param name="hasRepresentation">Whether a custom representation is set (also an empty one).</param>
    /// <param name="representation">The custom representation; read only when <paramref name="hasRepresentation"/> is set.</param>
    /// <param name="hasText">Whether a custom text is set (also an empty one: it then replaces the whole line with nothing).</param>
    /// <param name="text">The custom text; read only when <paramref name="hasText"/> is set.</param>
    /// <remarks>
    /// The maximum text length cuts the text of the line, the custom text or the value part: a label is never cut, so a
    /// reader always sees which field a truncated value belongs to. Capture text is written with control characters escaped.
    /// Sync: <c>FieldDisplayLine</c> (Core) states the same three cases; the Text tests compare the lines with
    /// <c>Field.ToString()</c>, so a drift between the two fails a test. Rejected alternative: call <c>Field.TryFormat</c> and
    /// escape and cut the whole line: it would count the label against the limit, would escape the trusted label as if it were
    /// capture text, would format a whole bytes value (which can be megabytes) to show its first few bytes, and could not serve the
    /// flat shapes, which have staged parts and no field. Rejected alternative: a decision type shared with Core through a linked
    /// source file: the rule is three comparisons, and the link would add a build coupling for them.
    /// </remarks>
    private void _WriteLineFromParts(
        string label,
        in FieldValueData data,
        bool hasRepresentation,
        ReadOnlySpan<char> representation,
        bool hasText,
        ReadOnlySpan<char> text)
    {
        // Case 1: a custom text is the whole line, even an empty one; the limit and the escaping apply to it as to any capture text.
        if (hasText)
        {
            _WriteSpan(text, _MaxTextLength);
            return;
        }

        // The label is trusted registered text: written as it is, never cut. An id the stack does not know has none, and then the
        // separator stays out too (a separator in front of nothing would be noise).
        bool hasLabel = label.Length > 0;
        if (hasLabel)
        {
            _WriteString(label);
        }

        // Case 2: a field without a value shows its label alone (the empty line without a label).
        if (data.Type == FieldType.None)
        {
            return;
        }

        // Case 3: separator and value part: the representation when the export writes one, else the default text of the data (cut
        // and escaped).
        if (hasLabel)
        {
            _WriteString(_LineSeparator);
        }

        if (hasRepresentation)
        {
            _WriteSpan(representation, _MaxTextLength);
            return;
        }

        _WriteDefaultText(in data);
    }

    /// <summary>
    /// Writes the default text of a value (the value part of a line without a custom representation) straight into the buffer: a
    /// bytes value as upper-case hex cut at whole bytes, every other type through the UTF-8 formatting of the data, cut at the
    /// maximum text length. Control characters are written escaped.
    /// </summary>
    /// <param name="data">The value; not <see cref="FieldType.None"/>.</param>
    /// <exception cref="InvalidOperationException">The text is longer than <see cref="_MaxEncodableChars"/> characters.</exception>
    private void _WriteDefaultText(in FieldValueData data)
    {
        // Bytes: bounded work. The hex writer reads only as many bytes as the limit shows, so a huge payload costs the limit, not its
        // size. Perf: formatting the whole value to cut it afterwards would need a scratch of nine bytes per payload byte.
        if (data.Type == FieldType.Bytes)
        {
            if (data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
            {
                _WriteBytesHex(bytes.Span);
                return;
            }

            _WriteSpan(data.ToString(null, CultureInfo.InvariantCulture), _MaxTextLength);
            return;
        }

        // String: bounded work, like the bytes. The text is cut by characters before it is encoded, so only the characters that
        // are written are encoded and the scratch is bounded by the maximum text length, not by the size of the value.
        // Perf: formatting the whole string first would need a scratch of three bytes per character of a value that is cut at the
        // limit (a threat the limit exists for: the memory held while formatting a huge value).
        if (data.Type == FieldType.String && data.TryGetAsString(out string text))
        {
            _WriteSpan(text, _MaxTextLength);
            return;
        }

        // Zero-alloc IUtf8SpanFormattable path for the other types (numbers, addresses, timestamps, flags): ask the value for its
        // size, then format directly into a UTF-8 buffer. The buffer must be sized for the full value (charCount, which for numbers
        // is an upper bound, not the exact length) so TryFormat succeeds. The truncation is decided on the formatted text, not on
        // the size estimate: the text is cut after at most _MaxTextLength UTF-16 characters (never inside a character), and the
        // ellipsis is written only when something was cut. Deciding on the estimate would append an ellipsis to a short number, and
        // clamping to a byte count would not cut an ASCII value shorter than three times the limit. A 512-byte limit keeps the
        // full-value buffer on the stack; larger values fall back to the instance scratch buffer.
        if (data.TryGetStringSize(format: default, CultureInfo.InvariantCulture, out int charCount))
        {
            int fullMaxBytes = _MaxUtf8Bytes(charCount);
            Span<byte> buf = fullMaxBytes <= _StackBufferLimit
                ? stackalloc byte[fullMaxBytes]
                : _GetScratch(fullMaxBytes);
            if (data.TryFormat(buf, out int written, format: default, provider: CultureInfo.InvariantCulture))
            {
                _WriteUtf8Truncated(buf[..written]);
                return;
            }
        }

        // Fallback: ToString() — one allocation, covers exotic or future value types, and is reached when the value cannot report
        // its size or formatting into the buffer failed.
        _WriteSpan(data.ToString(null, CultureInfo.InvariantCulture), _MaxTextLength);
    }

    #endregion

    #region Hex dumps

    /// <summary>Digits of a dump line offset: at least four, as many as the offset needs beyond 0xFFFF.</summary>
    private const string _OffsetFormat = "X4";

    /// <summary>Bytes per dump line.</summary>
    private const int _DumpBytesPerLine = 16;

    /// <summary>
    /// Writes the byte dumps of <paramref name="packet"/> after the packet's lines: the frame as a hex dump when the content asks for
    /// frame bytes, then one hex dump per additional buffer when it asks for buffers; nothing otherwise. The blocks belong to the
    /// packet, so they sit before the blank separator line of the shapes that have one.
    /// </summary>
    /// <param name="packet">The packet whose frame and additional buffers are dumped.</param>
    /// <exception cref="InvalidOperationException">
    /// A buffer inside the packet's buffer count is missing, or a dump number did not fit its buffer (broken invariants); the caller
    /// reports the packet as skipped, and nothing of it reaches the output.
    /// </exception>
    private void _WriteByteDumps(Packet packet)
    {
        // The bytes are written into the packet buffer right here, inside OnPacket: the packet's own buffers are recycled once
        // OnPacket returns.
        if (_IncludeFrameBytes)
        {
            _WriteHexDump("Frame bytes"u8, packet.Frame.Data.Span);
        }

        if (!_IncludeBuffers)
        {
            return;
        }

        // One dump per additional buffer in ascending index order, titled "Buffer <index> (<length>):". The title is built on the
        // stack: the prefix is a constant and the index is at most three digits (1 to 255), so 16 bytes always fit.
        ReadOnlySpan<byte> prefix = "Buffer "u8;
        Span<byte> title = stackalloc byte[16];
        prefix.CopyTo(title);
        int bufferCount = packet.BufferCount;
        for (int index = 1; index < bufferCount; index++)
        {
            if (!index.TryFormat(title[prefix.Length..], out int digits, default, CultureInfo.InvariantCulture))
            {
                _ThrowFormatFailed("hex dump number");
            }

            _WriteHexDump(title[..(prefix.Length + digits)], PacketBuffers.Get(packet, index));
        }
    }

    /// <summary>
    /// Writes a titled hex dump: the line <c>title (length):</c>, then one line per 16 bytes with the offset (at least four upper-case
    /// hex digits), two spaces, and the bytes as upper-case hex pairs separated by one space. An empty span writes the title line
    /// only. The dump is never cut by the maximum text length (it is the packet's own bytes, not a value of the field tree) and has
    /// no ASCII column, so no capture byte reaches the output as a character that a terminal could interpret: every byte is two hex
    /// digits.
    /// </summary>
    /// <param name="title">The words before the length, for example <c>Frame bytes</c>; trusted exporter text, not capture text.</param>
    /// <param name="bytes">The bytes to dump; read before the method returns.</param>
    /// <remarks>
    /// Perf: allocation-free; each line is formatted straight into the packet buffer (<see cref="PooledBuffer.Reserve"/>), with a
    /// stack buffer for the two numbers. A byte costs three characters, so a frame of n bytes adds about 3.5n bytes to the packet
    /// buffer. Security: threat: capture bytes written as characters could carry a terminal escape sequence to the user of
    /// <c>ToStdout</c>; mitigation: every byte is written as two hex digits, so no byte of the frame is ever a character of the
    /// output; rejected alternative: an ASCII column with control characters escaped (more code, and a printable run of capture bytes
    /// would still be shown as text); residual risk: none for the terminal, the output is about 3.5 times the frame. Sync: the Text
    /// README § Frame bytes (the line format).
    /// </remarks>
    private void _WriteHexDump(ReadOnlySpan<byte> title, ReadOnlySpan<byte> bytes)
    {
        // Title line: "<title> (<length>):". An int needs at most 11 bytes, so the stack buffer always fits.
        _Buffer.Write(title);
        _Buffer.Write(" ("u8);
        Span<byte> number = stackalloc byte[16];
        if (!bytes.Length.TryFormat(number, out int numberLength, default, CultureInfo.InvariantCulture))
        {
            _ThrowFormatFailed("hex dump number");
        }

        _Buffer.Write(number[..numberLength]);
        _Buffer.Write("):\n"u8);

        // Dump lines. The offset is formatted first because its width depends on it (four digits, more beyond 0xFFFF); the byte
        // part is then filled in place: two digits and one separator per byte, the last separator is the line end.
        ReadOnlySpan<byte> nibbles = "0123456789ABCDEF"u8;
        for (int offset = 0; offset < bytes.Length; offset += _DumpBytesPerLine)
        {
            if (!offset.TryFormat(number, out int offsetLength, _OffsetFormat, CultureInfo.InvariantCulture))
            {
                _ThrowFormatFailed("hex dump number");
            }

            _Buffer.Write(number[..offsetLength]);
            _Buffer.Write("  "u8);

            ReadOnlySpan<byte> line = bytes.Slice(offset, Math.Min(_DumpBytesPerLine, bytes.Length - offset));
            Span<byte> text = _Buffer.Reserve(line.Length * 3);
            for (int i = 0; i < line.Length; i++)
            {
                byte value = line[i];
                text[i * 3] = nibbles[value >> 4];
                text[(i * 3) + 1] = nibbles[value & 0x0F];
                text[(i * 3) + 2] = (byte)' ';
            }

            // The last separator becomes the line end, so a line has no trailing space.
            text[^1] = (byte)'\n';
        }
    }

    /// <summary>
    /// A number or timestamp did not fit its stack buffer: a broken invariant (an <see cref="int"/> needs at most 11 bytes and a
    /// timestamp has an exact maximum length), reported from a cold helper so the formatting code has no throw site. The packet is
    /// then skipped like any packet whose formatting failed.
    /// </summary>
    /// <param name="what">The part that failed, in words (for example <c>packet number</c>); trusted exporter text.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowFormatFailed(string what) =>
        throw new InvalidOperationException(Invariant($"The {what} did not fit its stack buffer (a broken invariant)."));

    /// <summary>
    /// A text is longer than the largest text that can be encoded into one buffer: the size of its worst-case UTF-8 form would
    /// exceed <see cref="int.MaxValue"/>. Reported from a cold helper that names the limit, so the packet that holds the text is
    /// skipped with a message that says why (instead of a framework message about an argument).
    /// </summary>
    /// <param name="charCount">The length of the text in UTF-16 code units.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowTextTooLarge(int charCount) =>
        throw new InvalidOperationException(
            Invariant($"A text of {charCount} characters cannot be written: the limit is {_MaxEncodableChars} characters per text.") +
            " Set a maximum text length to cut long texts.");

    /// <summary>
    /// The largest number of UTF-16 code units whose worst-case UTF-8 size (three bytes per code unit) still fits an
    /// <see cref="int"/>: a fact of the encoder (<see cref="Encoding.GetMaxByteCount"/>), a constant. Threat: a text near
    /// <see cref="int.MaxValue"/> characters (only reachable with an unlimited maximum text length) would make the size
    /// computation throw a framework exception that names no export limit. Posture: not configurable, the format cannot hold more.
    /// </summary>
    private const int _MaxEncodableChars = 715_827_881;

    /// <summary>
    /// The worst-case UTF-8 size of a text of <paramref name="charCount"/> UTF-16 code units, checked against the encoder's limit.
    /// </summary>
    /// <param name="charCount">The length of the text; not negative.</param>
    /// <returns>The number of bytes that always suffice to encode the text.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="charCount"/> exceeds <see cref="_MaxEncodableChars"/>.</exception>
    private static int _MaxUtf8Bytes(int charCount)
    {
        if (charCount > _MaxEncodableChars)
        {
            _ThrowTextTooLarge(charCount);
        }

        return Encoding.UTF8.GetMaxByteCount(charCount);
    }

    #endregion

    #region Text encoding and truncation

    /// <summary>
    /// UTF-8 bytes at which an escape may be needed: the C0 controls (0x00–0x1F), DEL (0x7F), the backslash (0x5C), and 0xC2, the
    /// lead byte of the two-byte forms of the C1 controls U+0080–U+009F (and of U+00A0–U+00BF, which are copied unchanged). The
    /// search only finds candidates; <see cref="_WriteVisibleUtf8"/> decides per candidate. Sync: <see cref="_EscapeWidth"/> and
    /// <see cref="_WriteVisibleUtf8"/> use the same set of escaped characters.
    /// </summary>
    private static readonly SearchValues<byte> _ControlBytes = _CreateControlBytes();

    /// <summary>
    /// The UTF-16 form of <see cref="_ControlBytes"/>: the C0 controls, DEL, the backslash, and the C1 controls U+0080–U+009F; each
    /// is a single code unit and each is escaped.
    /// </summary>
    private static readonly SearchValues<char> _ControlChars = _CreateControlChars();

    /// <summary>Builds <see cref="_ControlBytes"/>.</summary>
    /// <returns>The candidate bytes as a vectorized set.</returns>
    private static SearchValues<byte> _CreateControlBytes()
    {
        byte[] controls = new byte[0x23];
        for (int i = 0; i < 0x20; i++)
        {
            controls[i] = (byte)i;
        }

        controls[0x20] = 0x7F;
        controls[0x21] = (byte)'\\';
        controls[0x22] = 0xC2;
        return SearchValues.Create(controls);
    }

    /// <summary>Builds <see cref="_ControlChars"/>.</summary>
    /// <returns>The escaped characters as a vectorized set.</returns>
    private static SearchValues<char> _CreateControlChars()
    {
        char[] controls = new char[0x22 + 0x20];
        for (int i = 0; i < 0x20; i++)
        {
            controls[i] = (char)i;
        }

        controls[0x20] = '\x7F';
        controls[0x21] = '\\';
        for (int i = 0x80; i <= 0x9F; i++)
        {
            controls[0x22 + i - 0x80] = (char)i;
        }

        return SearchValues.Create(controls);
    }

    /// <summary>
    /// A span over the pooled instance scratch with at least <paramref name="minSize"/> bytes. Perf: grows by at least doubling, so a
    /// run of growing values reallocates O(log n) times, and rents from the pool; returned in <see cref="OnFinish"/>. The
    /// scratch is not cleared between uses; callers read back only the bytes they just wrote.
    /// </summary>
    /// <param name="minSize">Bytes needed.</param>
    /// <returns>A span of exactly <paramref name="minSize"/> bytes, valid until the next scratch use.</returns>
    private Span<byte> _GetScratch(int minSize)
    {
        byte[]? current = _Utf8Scratch;
        if (current is null || current.Length < minSize)
        {
            // Doubling is computed in long and capped at the largest array, then raised to the requested size; the pool may hand
            // out a bigger array than asked for, which is fine.
            long doubled = current is null
                ? minSize
                : Math.Min((long)current.Length * 2, Array.MaxLength);
            byte[] bigger = ArrayPool<byte>.Shared.Rent(checked((int)Math.Max(doubled, minSize)));

            // Rent first, return second: if the rent fails, the old scratch is still owned and gets returned at finish.
            if (current is not null)
            {
                ArrayPool<byte>.Shared.Return(current);
            }

            _Utf8Scratch = bigger;
            current = bigger;
        }

        return current.AsSpan(0, minSize);
    }

    /// <summary>
    /// Appends UTF-8 text with C0 controls, DEL, and C1 controls made visible and the backslash doubled, so capture text can neither
    /// end a line, nor send a terminal escape sequence, nor imitate an escape that this method wrote. Security: threat = line
    /// forging in logs and terminal escape injection through <c>ToStdout</c> (the C1 control U+009B is a control sequence
    /// introducer and U+009D an operating system command on terminals that honor C1); mitigation = escape every byte below 0x20
    /// and 0x7F (<c>\n</c>, <c>\r</c>, <c>\t</c>, else <c>\xHH</c>), write U+0080–U+009F as <c>\u00HH</c>, and write the backslash
    /// as <c>\\</c>, which makes the escaping injective (a literal <c>\n</c> in a value reads differently from an escaped line
    /// feed); rejected = dropping them (silently changes data); residual risk = Unicode bidirectional controls are printed as they
    /// are, and the <c>|</c> and two spaces between the parts of a selected-fields line are not escaped.
    /// </summary>
    /// <param name="utf8">Encoded text; bytes of 0x80 and above are copied unchanged, except the two-byte form of a C1 control.</param>
    private void _WriteVisibleUtf8(ReadOnlySpan<byte> utf8)
    {
        ReadOnlySpan<byte> digits = "0123456789abcdef"u8;
        Span<byte> hex = stackalloc byte[4]; // outside the loop (CA2014)
        hex[0] = (byte)'\\';
        hex[1] = (byte)'x';

        // Perf: the vectorized search finds the next candidate byte, so text without any (the common case) is one copy.
        while (true)
        {
            int index = utf8.IndexOfAny(_ControlBytes);
            if (index < 0)
            {
                _Buffer.Write(utf8);
                return;
            }

            _Buffer.Write(utf8[..index]);
            byte control = utf8[index];
            switch (control)
            {
                case (byte)'\\':
                    _Buffer.Write("\\\\"u8);
                    break;
                case 0xC2 when index + 1 < utf8.Length && utf8[index + 1] is >= 0x80 and <= 0x9F:
                    // A C1 control is one two-byte sequence (C2 80 to C2 9F): written as \u00HH, then both bytes are consumed.
                    byte c1 = utf8[index + 1];
                    _Buffer.Write("\\u00"u8);
                    _Buffer.WriteByte(digits[c1 >> 4]);
                    _Buffer.WriteByte(digits[c1 & 0x0F]);
                    utf8 = utf8[(index + 2)..];
                    continue;
                case 0xC2:
                    // The lead byte of an ordinary character (U+00A0 to U+00BF): copied; its continuation byte is not a candidate.
                    _Buffer.WriteByte(control);
                    break;
                case (byte)'\n':
                    _Buffer.Write("\\n"u8);
                    break;
                case (byte)'\r':
                    _Buffer.Write("\\r"u8);
                    break;
                case (byte)'\t':
                    _Buffer.Write("\\t"u8);
                    break;
                default:
                    hex[2] = digits[control >> 4];
                    hex[3] = digits[control & 0x0F];
                    _Buffer.Write(hex);
                    break;
            }

            utf8 = utf8[(index + 1)..];
        }
    }

    /// <summary>
    /// The number of output characters the escaped form of a control character takes, so the text length limit counts what is
    /// written. Sync: <see cref="_WriteVisibleUtf8"/> writes these forms.
    /// </summary>
    /// <param name="value">A UTF-16 code unit or a Unicode scalar value.</param>
    /// <returns>
    /// 2 for tab, line feed, carriage return, and the backslash; 6 for a C1 control (U+0080–U+009F); 4 for any other C0 control and
    /// DEL; 0 for every other character.
    /// </returns>
    private static int _EscapeWidth(int value)
    {
        if (value is '\n' or '\r' or '\t' or '\\')
        {
            return 2;
        }

        if (value is >= 0x80 and <= 0x9F)
        {
            return 6;
        }

        if (value < 0x20 || value == 0x7F)
        {
            return 4;
        }

        return 0;
    }

    /// <summary>
    /// Writes already-encoded UTF-8 text, cut after at most <see cref="_MaxTextLength"/> output characters (0 means no limit;
    /// an escaped control character counts with its escaped width), and appends the ellipsis only when something was cut.
    /// </summary>
    /// <param name="utf8">The encoded text.</param>
    private void _WriteUtf8Truncated(ReadOnlySpan<byte> utf8)
    {
        int keep = _MaxTextLength > 0
            ? _Utf8PrefixLength(utf8, _MaxTextLength)
            : utf8.Length;
        _WriteVisibleUtf8(utf8[..keep]);
        if (keep < utf8.Length)
        {
            _WriteString("…");
        }
    }

    /// <summary>
    /// Returns the length in bytes of the longest prefix of <paramref name="utf8"/> that holds at most
    /// <paramref name="maxChars"/> output characters and ends between two characters (a character is never split, and a
    /// surrogate pair counts as two and is kept or dropped as a whole). A control character counts with the width of its
    /// escaped form. Bytes that are not valid UTF-8 count as one replacement character each step, so the scan always advances.
    /// </summary>
    /// <param name="utf8">The encoded text.</param>
    /// <param name="maxChars">The limit in output characters; positive.</param>
    /// <returns>The byte length of the prefix.</returns>
    private static int _Utf8PrefixLength(ReadOnlySpan<byte> utf8, int maxChars)
    {
        // A character takes at least as many bytes as it has UTF-16 units, so a text with no more bytes than the limit
        // fits as a whole (the common case for short values) unless an escape makes a control character wider than its byte.
        if (utf8.Length <= maxChars && utf8.IndexOfAny(_ControlBytes) < 0)
        {
            return utf8.Length;
        }

        int chars = 0;
        int offset = 0;
        while (offset < utf8.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(utf8[offset..], out Rune rune, out int consumed);
            int units = 1;
            if (status == OperationStatus.Done)
            {
                int escaped = _EscapeWidth(rune.Value);
                units = escaped > 0
                    ? escaped
                    : rune.Utf16SequenceLength;
            }

            // Compared as units > room (not chars + units > limit) so a limit near int.MaxValue cannot overflow.
            if (units > maxChars - chars)
            {
                break;
            }

            chars += units;
            offset += Math.Max(consumed, 1);
        }

        return Math.Min(offset, utf8.Length);
    }

    /// <summary>
    /// Returns the number of leading characters of <paramref name="chars"/> that fit <paramref name="maxWidth"/> output characters,
    /// counting a control character with the width of its escaped form. A surrogate pair is kept or dropped as a whole.
    /// </summary>
    /// <param name="chars">The text.</param>
    /// <param name="maxWidth">The limit in output characters; positive.</param>
    /// <returns>The number of characters to keep.</returns>
    private static int _CharPrefixLength(ReadOnlySpan<char> chars, int maxWidth)
    {
        // Without controls every character is one output character, so a text that fits is kept as a whole.
        if (chars.Length <= maxWidth && chars.IndexOfAny(_ControlChars) < 0)
        {
            return chars.Length;
        }

        int width = 0;
        int count = 0;
        while (count < chars.Length)
        {
            int units = _EscapeWidth(chars[count]);
            if (units == 0)
            {
                units = 1;
            }

            // Compared as units > room so a limit near int.MaxValue cannot overflow.
            if (units > maxWidth - width)
            {
                break;
            }

            width += units;
            count++;
        }

        // A surrogate pair is one character: when the cut would fall between its two halves the whole pair is dropped, like the UTF-8
        // scan does, so the output never holds a lone surrogate (which would encode as a replacement character).
        if (count > 0 && count < chars.Length && char.IsHighSurrogate(chars[count - 1]) && char.IsLowSurrogate(chars[count]))
        {
            count--;
        }

        return count;
    }

    /// <summary>
    /// Writes a <see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;char&gt;</see> to the buffer, truncating to
    /// <paramref name="maxLength"/> output characters (an escaped control character counts with its escaped width) and appending an
    /// ellipsis if truncated. If <paramref name="maxLength"/> is 0, the full span is written. Control characters are written
    /// escaped. Encoding uses a stackalloc buffer for ≤512 max bytes; the pooled instance scratch for larger spans.
    /// </summary>
    /// <param name="chars">The text; capture text is untrusted.</param>
    /// <param name="maxLength">Maximum output characters; 0 means unlimited.</param>
    /// <exception cref="InvalidOperationException">The text to write is longer than <see cref="_MaxEncodableChars"/> characters.</exception>
    private void _WriteSpan(ReadOnlySpan<char> chars, int maxLength)
    {
        int keep = maxLength > 0
            ? _CharPrefixLength(chars, maxLength)
            : chars.Length;
        bool needsEllipsis = keep < chars.Length;
        ReadOnlySpan<char> toWrite = chars[..keep];

        if (!toWrite.IsEmpty)
        {
            int maxBytes = _MaxUtf8Bytes(toWrite.Length);
            Span<byte> encoded = maxBytes <= _StackBufferLimit
                ? stackalloc byte[maxBytes]
                : _GetScratch(maxBytes);
            int written = Encoding.UTF8.GetBytes(toWrite, encoded);
            _WriteVisibleUtf8(encoded[..written]);
        }

        if (needsEllipsis)
        {
            _WriteString("…");
        }
    }

    /// <summary>
    /// Writes bytes as space-separated upper-case hex (the default text of a bytes value), cut at whole bytes within
    /// <see cref="_MaxTextLength"/> characters when a limit is set. Shared by the tree lines and the flat shapes.
    /// </summary>
    /// <param name="span">The bytes to write.</param>
    /// <remarks>
    /// Sync: the default text of <see cref="FieldType.Bytes"/> in Core (<c>0A FF 00</c>) and EXPORTER_GUIDE.md § Rebuilding the
    /// display line.
    /// </remarks>
    private void _WriteBytesHex(ReadOnlySpan<byte> span)
    {
        // Each byte renders as two hex digits plus one separator between bytes, so n bytes take 3n - 1 characters. The
        // largest n that stays within the character limit is (limit + 1) / 3 rounded down; rounding up would let the
        // output exceed the limit by a character (limit 4 would print "AA BB", five characters).
        int maxBytes = _MaxTextLength == 0
            ? span.Length
            : Math.Min(span.Length, (_MaxTextLength + 1) / 3);

        ReadOnlySpan<byte> nibbles = "0123456789ABCDEF"u8;
        Span<byte> pair = stackalloc byte[2];

        for (int i = 0; i < maxBytes; i++)
        {
            if (i > 0)
            {
                _Buffer.WriteByte((byte)' ');
            }

            byte b = span[i];
            pair[0] = nibbles[b >> 4];
            pair[1] = nibbles[b & 0x0F];
            _Buffer.Write(pair);
        }

        if (_MaxTextLength > 0 && span.Length > maxBytes)
        {
            _WriteString("…");
        }
    }

    /// <summary>Writes indentation spaces to the buffer.</summary>
    /// <param name="spaces">Number of spaces; zero or negative writes nothing.</param>
    private void _WriteIndent(int spaces)
    {
        if (spaces <= 0)
        {
            return;
        }

        // Use a fixed-size stackalloc for small indents (up to 64 spaces, i.e. 32 nesting levels), the pooled instance
        // scratch for large indents (deeper nesting). Avoids a heap allocation per line.
        if (spaces <= 64)
        {
            Span<byte> indent = stackalloc byte[spaces];
            indent.Fill((byte)' ');
            _Buffer.Write(indent);
        }
        else
        {
            Span<byte> indent = _GetScratch(spaces);
            indent.Fill((byte)' ');
            _Buffer.Write(indent);
        }
    }

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8 and writes it to the buffer as it is. For the exporter's own literals and the
    /// names registered with the protocol stack, which are trusted code; text from a capture goes through
    /// <see cref="_WriteSpan"/> or <see cref="_WriteUtf8Truncated"/>, which escape control characters.
    /// </summary>
    /// <param name="text">The text; empty writes nothing.</param>
    /// <exception cref="InvalidOperationException">The text is longer than <see cref="_MaxEncodableChars"/> characters.</exception>
    private void _WriteString(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        int maxBytes = _MaxUtf8Bytes(text.Length);
        Span<byte> encoded = maxBytes <= _StackBufferLimit
            ? stackalloc byte[maxBytes]
            : _GetScratch(maxBytes);
        int written = Encoding.UTF8.GetBytes(text, encoded);
        _Buffer.Write(encoded[..written]);
    }

    #endregion

    #region Builder

    /// <summary>Fluent builder for <see cref="TextExporter"/>.</summary>
    /// <remarks>Not thread-safe. Setters return the builder itself.</remarks>
    public sealed class Builder
    {
        /// <summary>The output chosen by <c>ToFile</c>, <c>ToStream</c>, or <c>ToStdout</c>; required by <see cref="Build"/>.</summary>
        private ExportOutput? _Output;

        /// <summary>The display name.</summary>
        private string _UiName = "Text Exporter";

        /// <summary>The optional description.</summary>
        private string? _Description;

        /// <summary>The cancellation token of the export.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>The detail level of the hierarchy shape.</summary>
        private TextDetailLevel _DetailLevel = TextDetailLevel.Standard;

        /// <summary>Maximum characters for string/bytes display values. 0 = unlimited. Default: 256.</summary>
        private int _MaxTextLength = 256;

        /// <summary>Packets after which the export ends; 0 means no limit.</summary>
        private int _TargetPacketCount;

        /// <summary>What the export contains.</summary>
        private ExportContent _Content = DefaultContent;

        /// <summary>Sets the output to a file path.</summary>
        /// <param name="path">The file to create or overwrite.</param>
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
        /// <remarks>
        /// Throws nothing: the stream is not validated here. A missing or unwritable stream fails the first write, and the packet is
        /// reported as skipped through <see cref="ItemSkipped"/>, like any other failed write.
        /// </remarks>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Sets the output to standard output.</summary>
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

        /// <summary>Sets an optional description for the exporter.</summary>
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the detail level. Default: <see cref="TextDetailLevel.Standard"/>.</summary>
        /// <param name="level">A defined <see cref="TextDetailLevel"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not defined.</exception>
        public Builder WithDetailLevel(TextDetailLevel level)
        {
            if (!Enum.IsDefined(level))
            {
                throw new ArgumentOutOfRangeException(nameof(level), level, "Detail level must be Summary, Standard, or Full.");
            }

            _DetailLevel = level;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of characters for string and bytes field values.
        /// Values longer than this are cut after that many characters and get an ellipsis (<c>…</c>); a value that fits is
        /// written as it is. The ellipsis does not count toward the limit; an escaped control character counts with its escaped
        /// width (<c>\n</c> is two characters).
        /// Set to 0 for unlimited output. Default: 256. With 0, one very large value is formatted completely and the
        /// exporter's scratch buffer grows to its size, so unlimited is only advisable for trusted input.
        /// </summary>
        /// <param name="maxLength">Maximum characters; 0 means unlimited.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
        public Builder WithMaxTextLength(int maxLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
            _MaxTextLength = maxLength;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        /// <param name="count">Packet limit; 0 means unlimited.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // The upper limit is the number of items an array-index ID can address (ArrayIndexIdRange.MaxCount).
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
        /// Sets what the export contains: the shape (selected fields, all fields, or the field tree at the configured detail
        /// level), the selected fields, and the custom text and custom representation members (each switched-off member is left out
        /// of every line). Defaults to <see cref="DefaultContent"/>. <see cref="ExportContent.Info"/> has no effect in the Text
        /// exporter and is not refused: the field tree shows the info as the <c>packet.info</c> field line and the flat shapes
        /// write no info line, so both values of the member give the same output (its default is <see langword="true"/>).
        /// </summary>
        /// <param name="content">
        /// The content; <c>FrameBytes</c> adds the hex dump of the frame and <c>Buffers</c> the hex dump of each additional buffer after
        /// the lines of every packet (see the type remarks).
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

        /// <summary>Sets the cancellation token for cooperative cancellation.</summary>
        /// <param name="cancellationToken">The token; a requested cancellation ends the export at the next packet.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken cancellationToken)
        {
            _CancellationToken = cancellationToken;
            return this;
        }

        /// <summary>Builds the <see cref="TextExporter"/>.</summary>
        /// <returns>The exporter; the caller disposes it.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no output target is configured.</exception>
        public TextExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            return new TextExporter(
                _Output, _UiName, _Description, _DetailLevel, _MaxTextLength,
                _TargetPacketCount, _Content, _CancellationToken);
        }
    }

    #endregion
}
