// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Exports packets to CSV format with configurable columns and delimiter. Without <see cref="Builder.WithContent"/> it writes
/// one row per packet with the configured packet columns. With content it writes one of three shapes:
/// <list type="bullet">
///   <item><b>Selected fields (wide):</b> one row per packet; the packet columns, then one column per selected field (header =
///     field name, plus <c>.repr</c> / <c>.text</c> columns when the content asks for texts). A field that occurs several times in
///     a packet is written into one cell, the values joined by the occurrence separator (default <c>|</c>), so the row count stays
///     one per packet and a spreadsheet keeps one column per field.</item>
///   <item><b>All fields (long):</b> one row per field occurrence (<c>field, occurrence, value, custom_repr, custom_text</c>),
///     because the set of fields of a capture is not known when the header is written.</item>
///   <item><b>Hierarchy (long + tree):</b> one row per field node in pre-order with <c>node_id</c>, <c>parent_node_id</c> (−1 for
///     top-level nodes) and <c>path</c> (field names from the top level down, joined by <c>/</c>), so the tree of a packet can
///     be rebuilt from the file.</item>
/// </list>
/// <para>
/// <b>Feeds:</b> the selected and all-fields shapes are filled by the exporter's field observer during a tree-free parse (see
/// <see cref="ParseDemand"/>) and committed by <see cref="OnPacket"/>; a packet that arrives with a field tree and no staged
/// fields is walked instead, so the same rows come out of both feeds. The hierarchy shape needs the tree and demands it. Packet
/// rows need no field values at all: they demand a parse without a field tree.
/// </para>
/// <para>
/// Output format: UTF-8 with an optional BOM (default on), rows terminated by <c>\r\n</c>, fields quoted per RFC 4180
/// only when they contain the delimiter, a double quote, CR, or LF.
/// </para>
/// <para>
/// <b>Formula neutralization (default on):</b> a spreadsheet application treats a cell that starts with <c>=</c>,
/// <c>+</c>, <c>-</c>, <c>@</c>, TAB, or CR as a formula, so text from a capture (a DNS name, a packet info line) could run
/// a formula when the file is opened (CSV injection). Such a text cell gets a leading single quote (<c>'</c>) before the
/// CSV quoting is applied. Only text is changed: the <c>Info</c> column, field values of type <see cref="FieldType.String"/>,
/// and the custom representation and text cells. Numeric columns, timestamps, addresses, and the header row are written as they
/// are, so a negative number keeps its minus sign. <see cref="Builder.WithFormulaNeutralization"/> switches it off for machine
/// processing, where the added quote would corrupt the data.
/// </para>
/// <para>
/// <b>Text presence:</b> a custom representation or custom text cell is empty and unquoted when the dissector set none, and the
/// quoted empty cell <c>""</c> when it set an empty text, so a reader that keeps the quoting tells the two apart. A wide cell that
/// joins several occurrences cannot show the difference (a documented limit); one occurrence per cell can.
/// </para>
/// <para>
/// <b>Frame bytes (opt-in):</b> <see cref="Builder.WithColumn"/> with <see cref="CsvColumnKind.FrameBytes"/> adds the frame of every
/// packet as one cell of upper-case hex digits without separators (empty for an empty frame). The column belongs to the packet-rows
/// and the selected-fields layouts; <see cref="Builder.Build"/> refuses it next to the all-fields and hierarchy layouts, which write
/// one row per field and would repeat the frame on each of them.
/// </para>
/// <para>
/// <b>Additional buffers (opt-in):</b> <see cref="Builder.WithColumn"/> with <see cref="CsvColumnKind.Buffers"/> adds the additional
/// packet buffers (reassembled datagrams, TCP PDUs, decoded bodies) of every packet as one cell of <c>&lt;index&gt;:&lt;HEX&gt;</c>
/// records joined by <c>;</c> (empty for a packet without additional buffers). The same layout rule as for the frame bytes applies.
/// </para>
/// <para>
/// The rows of a packet are assembled completely in an internal buffer and handed to the output in one write, so a failure while
/// formatting a field never leaves a partial packet in the output. The per-row path reuses that buffer and formats the
/// packet number, frame length, timestamp, and field values straight to UTF-8 without intermediate strings.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. The parse that feeds the observer, <see cref="OnPacket"/>, and <see cref="OnFinish"/>
/// must run on one thread, in that order per packet. The caller is responsible for synchronization when driving this exporter
/// from multiple threads.
/// </para>
/// </summary>
public sealed class CsvExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IDisposable
{
    #region Configuration and state

    /// <summary>UTF-8 BOM bytes (a span over static data: no allocation).</summary>
    private static ReadOnlySpan<byte> _Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Row terminator: CRLF, as RFC 4180 specifies.</summary>
    private static ReadOnlySpan<byte> _NewLine => "\r\n"u8;

    /// <summary>Cooperative cancellation; checked per packet.</summary>
    private readonly CancellationToken _CancellationToken;

    /// <summary>Whether a UTF-8 BOM is written before the first row.</summary>
    private readonly bool _WriteBom;

    /// <summary>
    /// Delimiter as a single ASCII byte. The builder restricts it to ASCII so that one byte is always a complete UTF-8
    /// character and the byte-level quoting scan cannot match inside a multi-byte sequence.
    /// </summary>
    private readonly byte _Delimiter;

    /// <summary>Whether a header row is written before the first packet row.</summary>
    private readonly bool _WriteHeader;

    /// <summary>Whether text cells that start with a spreadsheet formula character get a leading apostrophe.</summary>
    private readonly bool _NeutralizeFormulas;

    /// <summary>Maximum number of packets to write; 0 means unlimited.</summary>
    private readonly int _TargetPacketCount;

    /// <summary>Column layout; copied from the builder, so later builder changes do not affect a built exporter.</summary>
    private readonly CsvColumnDefinition[] _Columns;

    /// <summary>Output target. Cleared to <c>null</c> after disposal to prevent a double dispose.</summary>
    private ExportOutput? _Output;

    /// <summary>
    /// Row assembly buffer, reset per row and reused for the whole export; the 4 KiB initial capacity grows to the longest
    /// row seen (pooled rental).
    /// </summary>
    private readonly PooledBuffer _Buffer = new(4096);

    /// <summary>Latched fatal state; every later <see cref="OnPacket"/> returns <c>false</c>.</summary>
    private bool _HasError;

    /// <summary>Set once the BOM and header row were written successfully.</summary>
    private bool _Started;

    /// <summary>Latch that makes <see cref="OnFinish"/> idempotent and stops <see cref="OnPacket"/> after the output is closed.</summary>
    private bool _Finished;

    /// <summary>
    /// UTF-8 scratch buffer for text cells and formatted values, rented from the array pool. Grows geometrically to the most
    /// bytes this exporter instance ever needed and is then reused without allocation; returned to the pool when the exporter
    /// finishes.
    /// </summary>
    private byte[]? _Utf8Scratch;

    /// <summary>
    /// Starting point for <see cref="Builder.WithContent"/>: the members CSV can write (no info string, no frame bytes, and no
    /// additional buffers — all three are columns, see <see cref="Builder.WithColumn"/>), texts on, shape all fields. Without <c>WithContent</c> CSV writes
    /// packet rows, which no <see cref="ExportContent"/> describes, so this is a template and not the content written by default
    /// (the other exporters expose that as <c>DefaultContent</c>).
    /// </summary>
    public static ExportContent ContentTemplate { get; } = new()
    {
        Shape = ExportShape.AllFields,
        Info = false,
        FrameBytes = false,
        Buffers = false,
        CustomText = true,
        CustomRepresentation = true,
    };

    /// <summary>Content of this export; <see langword="null"/> writes packet rows.</summary>
    private readonly ExportContent? _Content;

    /// <summary>Per-packet staging of the flat shapes (the shared row stage); <see langword="null"/> for packet rows and hierarchy.</summary>
    private readonly RowFieldStage? _Stage;

    /// <summary>Selection and occurrence policy of the flat shapes; fed by the parse (as observer) or by a tree walk.</summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>
    /// Occurrence separator as UTF-8 (default <c>|</c>). Only text that cannot break a row is accepted (no CR or LF); a separator
    /// that contains the delimiter or a quote is still safe because the whole joined cell goes through RFC 4180 quoting.
    /// </summary>
    private readonly byte[] _OccurrenceSeparator;

    /// <summary>
    /// Joined-cell assembly buffer: the cell is built first so that quoting and formula neutralization see the whole cell (one
    /// decision per cell, not per value). Reused for the whole export; grows to the longest cell.
    /// </summary>
    private readonly PooledBuffer _Cell = new(256);

    /// <summary>
    /// Field names as UTF-8 by field id, built on first use (one small array per distinct field per stack, never per row); the
    /// long layouts write a name on every row. Holds the names of <see cref="_NameStack"/> only.
    /// </summary>
    private byte[]?[] _NameUtf8 = [];

    /// <summary>The stack whose names <see cref="_NameUtf8"/> holds; a packet of another stack clears the cache.</summary>
    private Stack? _NameStack;

    /// <summary>Field ids of the ancestors of the node being written (hierarchy shape), for the path column.</summary>
    private readonly List<int> _Ancestors = new(16);

    /// <summary>
    /// Creates a new exporter (use <see cref="CreateBuilder"/> for construction). The builder has validated every argument.
    /// </summary>
    /// <param name="output">The output target; owned by the exporter from here on.</param>
    /// <param name="uiName">Display name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="writeBom">Whether a UTF-8 BOM starts the output.</param>
    /// <param name="delimiter">The column delimiter; one ASCII byte.</param>
    /// <param name="writeHeader">Whether a header row is written.</param>
    /// <param name="neutralizeFormulas">Whether text cells that start with a formula character get a leading apostrophe.</param>
    /// <param name="targetPacketCount">Packets to write before the export finishes; 0 means unlimited.</param>
    /// <param name="columns">The packet columns, in order; never empty.</param>
    /// <param name="content">What the export writes; <see langword="null"/> writes packet rows.</param>
    /// <param name="occurrenceSeparator">UTF-8 text between the occurrences of one field in a wide-layout cell.</param>
    /// <param name="cancellationToken">Checked per packet; a cancelled token ends the export.</param>
    private CsvExporter(
        ExportOutput output,
        string uiName,
        string? description,
        bool writeBom,
        byte delimiter,
        bool writeHeader,
        bool neutralizeFormulas,
        int targetPacketCount,
        CsvColumnDefinition[] columns,
        ExportContent? content,
        byte[] occurrenceSeparator,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _WriteBom = writeBom;
        _Delimiter = delimiter;
        _WriteHeader = writeHeader;
        _NeutralizeFormulas = neutralizeFormulas;
        _TargetPacketCount = targetPacketCount;
        _Columns = columns;
        _Content = content;
        _OccurrenceSeparator = occurrenceSeparator;
        _CancellationToken = cancellationToken;

        // The flat shapes stage the fields of a packet while it is parsed and format the packet in OnPacket; the collector applies
        // the selection and the occurrence choice, the stage keeps the one packet until it is committed. The hierarchy shape and
        // the packet rows have no use for either.
        if (content is { Shape: not ExportShape.Hierarchy })
        {
            _Stage = new();
            _Collector = new ExportFieldCollector(content, _Stage);
        }
    }

    /// <summary>Creates a new <see cref="Builder"/> for fluent configuration.</summary>
    /// <returns>A builder with the defaults: packet rows with the columns number, time, info, and length, comma delimiter, BOM, header.</returns>
    public static Builder CreateBuilder() => new();

    #endregion

    #region Statistics and parse demand

    /// <inheritdoc/>
    /// <remarks>
    /// The hierarchy shape demands a field tree (it needs the parents). Packet rows demand a tree-free parse without observer
    /// (depth Packet): every packet column (number, time, info, frame length) is a property of the packet, available without a
    /// field tree. The flat shapes demand a tree-free parse with the collector as observer: only the selected fields (or all
    /// fields) are delivered and, when the content asks for texts, built, which is where a selected-fields CSV export saves its
    /// parse work.
    /// </remarks>
    public ParseDemand ParseDemand
    {
        get
        {
            if (_Content is null)
            {
                return new ParseDemand { Depth = ParseDepth.Packet };
            }

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

        // Lazy start: the BOM and header row are written with the first packet, so an export that receives no packet
        // leaves the output untouched (a file output is not even created).
        if (!_Started && !_Start())
        {
            return false;
        }

        return _HandlePacket(packet);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }

        _Finished = true;

        // Shared finish sequence (same order as every exporter): final flush, buffer return, output disposal.
        ExporterFinish.Run(
            "CSV",
            () => _Output?.Flush(),
            _ReportFinishError,
            new ExporterReleaseStep("buffer return", _Buffer.Return),
            new ExporterReleaseStep("cell buffer return", _Cell.Return),
            new ExporterReleaseStep("row stage disposal", _DisposeStage),
            new ExporterReleaseStep("scratch return", _ReturnScratch),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>Returns the pooled chunks of the row stage (a no-op for packet rows and the hierarchy shape).</summary>
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

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        output?.Dispose();
    }

    /// <summary>Error channel of the finish sequence: marks the error, counts it, and raises <see cref="ItemSkipped"/>.</summary>
    /// <param name="kind">What failed in the finish sequence (the flush or the close of the output).</param>
    /// <param name="message">The reason, passed to the event.</param>
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

    #region Header row

    /// <summary>
    /// Writes the BOM and header row on the first packet.
    /// </summary>
    /// <returns><see langword="true"/> when the preamble was written; <see langword="false"/> on an output failure, which is
    /// reported and ends the export in both error-tolerance modes (the failure is not tied to one packet).</returns>
    private bool _Start()
    {
        try
        {
            if (_WriteBom)
            {
                _Output!.Write(_Utf8Bom); // Invariant: the start runs from OnPacket, which refuses packets once OnFinish disposed the output.
            }

            if (_WriteHeader)
            {
                _WriteHeaderRow();
            }

            _Started = true;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bump ErrorCount so HasErrors reflects the failure. The event is raised in both modes, and _HasError makes
            // every following OnPacket call return false: a failed preamble cannot be repaired, and continuing would write
            // rows into an output that lacks the header (or has half of it).
            if (ErrorCount < int.MaxValue)
            {
                ErrorCount++;
            }

            _HasError = true;
            _OnError(0, ExportErrorKind.IoError, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes the column header row of the chosen layout. Header cells are never formula-neutralized (they are names of this
    /// library, not capture text).
    /// </summary>
    private void _WriteHeaderRow()
    {
        _Buffer.Reset();
        _WritePacketHeaderCells();

        switch (_Content?.Shape)
        {
            case null:
                break;
            case ExportShape.SelectedFields:
                _WriteWideHeaderCells();
                break;
            case ExportShape.AllFields:
                _WriteHeaderCells("field", "occurrence", "value");
                _WriteTextHeaderCells("custom_repr", "custom_text");
                break;
            default:
                _WriteHeaderCells("node_id", "parent_node_id", "path", "field", "value");
                _WriteTextHeaderCells("custom_repr", "custom_text");
                break;
        }

        _Buffer.Write(_NewLine);
        _Output!.Write(_Buffer.WrittenSpan); // Invariant: only _Start calls this, while the output exists.
    }

    /// <summary>Writes the header cells of the packet columns (the prefix of every layout).</summary>
    private void _WritePacketHeaderCells()
    {
        for (int i = 0; i < _Columns.Length; i++)
        {
            if (i > 0)
            {
                _Buffer.WriteByte(_Delimiter);
            }

            // Header names are escaped in case they contain the delimiter or quotes
            _WriteCsvField(_Columns[i].Header, neutralize: false);
        }
    }

    /// <summary>Appends header cells after the packet columns, each behind a delimiter.</summary>
    /// <param name="names">The header texts, in column order; written through the RFC 4180 quoting without formula neutralization.</param>
    private void _WriteHeaderCells(params string[] names)
    {
        foreach (string name in names)
        {
            _Buffer.WriteByte(_Delimiter);
            _WriteCsvField(name, neutralize: false);
        }
    }

    /// <summary>Appends the optional text header cells that the content asks for (representation first, as in the data columns).</summary>
    /// <param name="representation">Header of the custom representation column; written only when the content asks for it.</param>
    /// <param name="text">Header of the custom text column; written only when the content asks for it.</param>
    private void _WriteTextHeaderCells(string representation, string text)
    {
        if (_Content!.CustomRepresentation) // Invariant: only the field layouts write text headers, and they have content.
        {
            _WriteHeaderCells(representation);
        }

        if (_Content.CustomText)
        {
            _WriteHeaderCells(text);
        }
    }

    /// <summary>Wide layout header: one column per selected field, followed by its <c>.repr</c> and <c>.text</c> columns.</summary>
    private void _WriteWideHeaderCells()
    {
        foreach (SelectedField field in _Content!.Fields!.Fields) // Invariant: the wide header is written for SelectedFields content, which has fields.
        {
            _WriteHeaderCells(field.Name);
            if (_Content.CustomRepresentation)
            {
                _WriteHeaderCells(field.Name + ".repr");
            }

            if (_Content.CustomText)
            {
                _WriteHeaderCells(field.Name + ".text");
            }
        }
    }

    #endregion

    #region Packet intake

    /// <summary>Handles a single packet, writing its row (packet rows, wide) or rows (long, hierarchy).</summary>
    /// <param name="packet">The packet; its stack must be the stack of the field selection, when the content has one.</param>
    /// <returns>
    /// <see langword="true"/> when the export goes on (the packet was written or skipped in tolerant mode);
    /// <see langword="false"/> at the target count, on a stack mismatch, and when a strict export skipped the packet.
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
        if (_Content?.Fields is not null && !ReferenceEquals(packet.Stack, _Content.Fields.Stack))
        {
            _ReportStackMismatch();
            return false;
        }

        try
        {
            // All rows of the packet are built in _Buffer first and written once below, so a failure in any cell (an exception from
            // a field formatter) leaves nothing of this packet in the output.
            _Buffer.Reset();
            switch (_Content?.Shape)
            {
                case null:
                    // Packet rows need no field values: Info and the frame length come from the packet, so a packet parsed without
                    // a field tree is accepted.
                    _WritePacketColumns(packet);
                    _Buffer.Write(_NewLine);
                    break;
                case ExportShape.Hierarchy:
                    // Own pre-order walk: the tree carries the parent links this layout writes. A tree-less packet cannot be walked: it is
                    // refused without an exception, with the same kind and message as every other exporter with a hierarchy shape.
                    if (!packet.HasFieldTree)
                    {
                        return _Skip(ExportErrorKind.SerializationError, "Skip packets cannot be exported (HasFieldTree is false).");
                    }

                    _WriteHierarchyRows(packet);
                    break;
                default:
                    // Flat shapes: commit what the observer staged for this packet, or walk the tree when the packet has one and
                    // nothing was staged (a pipeline without a parse plan). A packet that has neither is reported with the cause.
                    // Invariant: the flat shapes have a stage.
                    if (_Content.Shape == ExportShape.SelectedFields && _Stage!.StagedPacketId != packet.Id.Value && packet.HasFieldTree)
                    {
                        // A tree packet nobody staged: read the few selected fields straight from the tree (see the method).
                        _WriteWideRowFromTree(packet);
                        break;
                    }

                    if (!_TryPrepareFlat(packet, out ExportErrorKind kind, out string? message))
                    {
                        return _Skip(kind, message!); // Invariant: a false result carries a message.
                    }

                    if (_Content.Shape == ExportShape.SelectedFields)
                    {
                        _WriteWideRow(packet);
                    }
                    else
                    {
                        _WriteLongRows(packet);
                    }

                    break;
            }

            _Output!.Write(_Buffer.WrittenSpan); // Invariant: _HandlePacket is not reached after OnFinish disposed the output.
            PacketCount++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any exception while building or writing the rows fails this packet. The output write is the one I/O step, so an
            // IOException is reported as IoError and everything else as a serialization error. A staging being formatted is dropped.
            _Stage?.Discard();
            ExportErrorKind kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            return _Skip(kind, ex.Message);
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

        _OnError(index, kind, message);
        return true;
    }

    /// <summary>
    /// Makes the staged fields of <paramref name="packet"/> available in <see cref="_Stage"/>: either the observer staged them
    /// during the parse of this packet (commit), or the packet has a field tree and is walked now. The same feed decision as the
    /// columnar and PBF exporters.
    /// </summary>
    /// <param name="packet">The packet to format.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>; names the cause.</param>
    /// <returns><see langword="true"/> when <see cref="_Stage"/> holds the entries of <paramref name="packet"/>.</returns>
    private bool _TryPrepareFlat(Packet packet, out ExportErrorKind kind, out string? message)
    {
        // Feed decision first: a tree packet whose fields this exporter did not stage is walked through the same collector, so
        // both feeds give the same rows. Commit rule: staged values belong to the packet only when the ids match and EndPacket
        // reported completeness; a muted observer, an id mismatch, a second commit, "nothing staged", and a failing text are
        // refused with a named message. Both live in the shared guarded feed of the row exporters.
        return RowFeed.TryPrepare(_Stage!, _Collector!, packet, out kind, out message); // Invariant: stage and collector are created together.
    }

    /// <summary>Raises the <see cref="ItemSkipped"/> event. Counters are updated by the callers, not here.</summary>
    /// <param name="index">Index of the failed item: the number of packets written before it.</param>
    /// <param name="kind">The error kind.</param>
    /// <param name="message">The reason.</param>
    private void _OnError(int index, ExportErrorKind kind, string message)
    {
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = index,
            Kind = kind,
            Message = message,
        });
    }

    #endregion

    #region Packet columns

    /// <summary>
    /// Writes the packet columns (number, timestamp, info, frame length, frame bytes, buffers) of <paramref name="packet"/>, joined by
    /// the delimiter and without a line end. The prefix of every row of every layout.
    /// </summary>
    /// <param name="packet">The packet whose number, time, info, length, frame bytes, and buffers are written as configured.</param>
    private void _WritePacketColumns(Packet packet)
    {
        // Pre-allocate the timestamp formatting buffer once outside the column loop to avoid a potential stack overflow from
        // repeated stackalloc inside the loop (CA2014).
        Span<byte> tsBuf = stackalloc byte[Timestamp.MaxFormattedLength];

        // One pass over the configured columns. Each branch appends directly to the byte buffer; only the Info column goes through
        // the string-based _ExtractColumnValue path.
        for (int i = 0; i < _Columns.Length; i++)
        {
            if (i > 0)
            {
                _Buffer.WriteByte(_Delimiter);
            }

            CsvColumnDefinition column = _Columns[i];
            switch (column.Kind)
            {
                case CsvColumnKind.PacketNumber:
                    _AppendUtf8Int32(_Buffer, packet.Id.Value);
                    break;
                case CsvColumnKind.FrameLength:
                    _AppendUtf8Int32(_Buffer, packet.Frame.Length);
                    break;
                case CsvColumnKind.Timestamp:
                    // Timestamp.TryFormat writes ISO 8601 (e.g. 2024-01-01T12:00:00.000000000Z), which contains only digits, '-',
                    // ':', '.', 'T', and 'Z' and never a quote, so no CSV quoting is needed: the builder refuses a delimiter that
                    // is one of those characters (see WithDelimiter).
                    if (packet.Timestamp.TryFormat(tsBuf, out int tsWritten, default, null))
                    {
                        _Buffer.Write(tsBuf[..tsWritten]);
                    }
                    else
                    {
                        // Cannot happen with a MaxFormattedLength buffer, but an empty cell would silently break the meaning of
                        // the column, so the text form is written instead.
                        _WriteCsvField(packet.Timestamp.Format(), neutralize: false);
                    }

                    break;
                case CsvColumnKind.FrameBytes:
                    _WriteHexCell(packet.Frame.Data.Span);
                    break;
                case CsvColumnKind.Buffers:
                    _WriteBuffersCell(packet);
                    break;
                default:
                    // The Info text is free text from the capture, so it is the cell that can carry a formula.
                    string value = _ExtractColumnValue(packet, column);
                    _WriteCsvField(value, _NeutralizeFormulas && column.Kind == CsvColumnKind.Info);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes one frame-bytes cell: the bytes as upper-case hexadecimal digits without separators, written straight into the row
    /// buffer; an empty frame is an empty cell.
    /// </summary>
    /// <param name="bytes">The frame bytes; read before the method returns (the packet's buffers are recycled after
    /// <see cref="OnPacket"/>).</param>
    /// <exception cref="InvalidOperationException">
    /// The hex text would not fit one array (a frame of more than <see cref="Array.MaxLength"/> / 2 bytes), or the converter did not
    /// write all digits (a broken invariant). The caller reports the packet as skipped.
    /// </exception>
    /// <remarks>
    /// Security: the digits 0-9 and A-F cannot start a formula (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab, CR) and cannot contain a
    /// quote, CR, or LF, so the cell needs neither neutralization nor quote doubling, whatever the capture holds. The delimiter cannot
    /// split the cell either: the builder refuses every delimiter that is a digit or a letter (see
    /// <see cref="Builder.WithDelimiter"/>). Perf: no scratch array and no string; <c>Convert.TryToHexString</c> writes into the buffer.
    /// Sync: the CSV README § Frame bytes and <see cref="Builder.WithDelimiter"/> (the refused delimiters).
    /// </remarks>
    private void _WriteHexCell(ReadOnlySpan<byte> bytes) => _WriteHexDigits(bytes, bufferIndex: 0);

    /// <summary>
    /// Writes the upper-case hexadecimal digits of <paramref name="bytes"/> (two per byte, no separator, no quoting) straight into
    /// the row buffer; nothing for an empty span.
    /// </summary>
    /// <param name="bytes">The bytes; read before the method returns.</param>
    /// <param name="bufferIndex">The index of the buffer the bytes belong to, 0 for the frame; names the cell in an error message.</param>
    /// <exception cref="InvalidOperationException">
    /// The hex text would not fit one array, or the converter did not write all digits (see <see cref="_WriteHexCell"/>); the message
    /// names the frame or the buffer.
    /// </exception>
    /// <remarks>
    /// The one home of the length guard and the conversion, shared by the frame cell and the buffers cell, so both fail the same way.
    /// Perf: see <see cref="_WriteHexCell"/>.
    /// </remarks>
    private void _WriteHexDigits(ReadOnlySpan<byte> bytes, int bufferIndex)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        // Twice the length must fit an array: the row buffer cannot hold more, so the limit is named instead of an overflow.
        if (bytes.Length > Array.MaxLength / 2)
        {
            _ThrowHexCellTooLarge(bufferIndex, bytes.Length);
        }

        int hexLength = bytes.Length * 2;
        Span<byte> cell = _Buffer.Reserve(hexLength);
        if (!Convert.TryToHexString(bytes, cell, out int written) || written != hexLength)
        {
            _ThrowHexCellFailed(bufferIndex);
        }
    }

    /// <summary>
    /// Writes the buffers cell: the additional buffers of <paramref name="packet"/> as <c>&lt;index&gt;:&lt;HEX&gt;</c> records joined by
    /// <c>;</c> in ascending index order, written straight into the row buffer; an empty cell for a packet without additional
    /// buffers, and a record without digits for a bound empty buffer (<c>2:</c>), so an empty buffer is not mistaken for a missing one.
    /// </summary>
    /// <param name="packet">The packet whose buffers are written; they are read before the method returns (the packet's buffers are
    /// recycled after <see cref="OnPacket"/>).</param>
    /// <exception cref="InvalidOperationException">
    /// A buffer inside the packet's buffer count is missing, a buffer's hex text would not fit one array, or the converter did not
    /// write all digits (broken invariants and a named limit; the caller reports the packet as skipped).
    /// </exception>
    /// <remarks>
    /// Security: the digits 0-9 and A-F, the colon, and the semicolon cannot start a formula (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab,
    /// CR) and cannot contain a quote, CR, or LF, so the cell needs neither neutralization nor quote doubling, whatever the capture
    /// holds. The one residual case is a delimiter that is one of the cell's own separators: the semicolon is a common CSV delimiter
    /// and would split the cell, so the cell is then quoted as a whole. A digit, a letter, and the colon cannot be the delimiter (the
    /// builder refuses them, see <see cref="Builder.WithDelimiter"/>); the colon stays in the check as a second line of defense.
    /// Perf: the digits go straight into the row buffer through <see cref="_WriteHexDigits"/>; no scratch array and no string.
    /// Sync: the CSV README § Buffers.
    /// </remarks>
    private void _WriteBuffersCell(Packet packet)
    {
        int bufferCount = packet.BufferCount;
        if (bufferCount <= 1)
        {
            return;
        }

        bool quote = _Delimiter is (byte)':' or (byte)';';
        if (quote)
        {
            _Buffer.WriteByte((byte)'"');
        }

        for (int index = 1; index < bufferCount; index++)
        {
            if (index > 1)
            {
                _Buffer.WriteByte((byte)';');
            }

            _AppendUtf8Int32(_Buffer, index);
            _Buffer.WriteByte((byte)':');
            _WriteHexDigits(PacketBuffers.Get(packet, index), index);
        }

        if (quote)
        {
            _Buffer.WriteByte((byte)'"');
        }
    }

    /// <summary>A frame or buffer is too long for its hex text to fit one array; a cold helper keeps the throw out of the hot method.</summary>
    /// <param name="bufferIndex">The index of the buffer, 0 for the frame; the message names which of the two failed.</param>
    /// <param name="length">The length of the frame or buffer in bytes.</param>
    /// <exception cref="InvalidOperationException">Always; the message names the limit.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowHexCellTooLarge(int bufferIndex, int length) =>
        throw new InvalidOperationException(
            Invariant($"{_DescribeBytes(bufferIndex)} of {length} bytes cannot be written as one CSV cell: its hex text would exceed ") +
            Invariant($"{Array.MaxLength} bytes (Array.MaxLength)."));

    /// <summary>The hex converter did not fill its exactly sized destination: a broken invariant, reported from a cold helper.</summary>
    /// <param name="bufferIndex">The index of the buffer, 0 for the frame; the message names which of the two failed.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowHexCellFailed(int bufferIndex) =>
        throw new InvalidOperationException(
            Invariant($"The hex conversion of {_DescribeBytes(bufferIndex).ToLowerInvariant()} did not fill its destination (a broken invariant)."));

    /// <summary>Names the frame or an additional buffer for an error message.</summary>
    /// <param name="bufferIndex">The index of the buffer, 0 for the frame.</param>
    /// <returns><c>A frame</c> for index 0, otherwise <c>Buffer i</c>.</returns>
    private static string _DescribeBytes(int bufferIndex) =>
        bufferIndex == 0
            ? "A frame"
            : Invariant($"Buffer {bufferIndex}");

    #endregion

    #region Wide layout

    /// <summary>Which member of a field a cell shows: of a staged entry (the staged path) or of a tree field (the tree path).</summary>
    private enum CellPart : byte
    {
        /// <summary>The field value.</summary>
        Value,

        /// <summary>The custom representation (empty when absent).</summary>
        CustomRepresentation,

        /// <summary>The custom text (empty when absent).</summary>
        CustomText,
    }

    /// <summary>
    /// Wide row: packet columns, then for each selected field (selection order) its staged occurrences joined into one cell, then
    /// the optional <c>.repr</c> and <c>.text</c> cells of that field. A field without occurrences writes an empty cell, so every
    /// row has the header's column count.
    /// </summary>
    /// <param name="packet">The packet whose staged entries are written; its fields are in <see cref="_Stage"/>.</param>
    private void _WriteWideRow(Packet packet)
    {
        _WritePacketColumns(packet);
        IReadOnlyList<SelectedField> fields = _Content!.Fields!.Fields; // Invariant: the wide layout has SelectedFields content, which has fields.
        for (int f = 0; f < fields.Count; f++)
        {
            // The stage keeps the entries in arrival order; the entries of one field are found by field id (a selection has a few
            // fields and a packet a few entries for them, so a scan per cell is cheaper than an index structure) and are already
            // in occurrence order.
            int fieldId = fields[f].Id.Value;
            _Buffer.WriteByte(_Delimiter);
            _WriteJoinedCell(fieldId, CellPart.Value, _NeutralizeFormulas && fields[f].FieldType == FieldType.String);
            if (_Content.CustomRepresentation)
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteJoinedCell(fieldId, CellPart.CustomRepresentation, _NeutralizeFormulas);
            }

            if (_Content.CustomText)
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteJoinedCell(fieldId, CellPart.CustomText, _NeutralizeFormulas);
            }
        }

        _Buffer.Write(_NewLine);
    }

    /// <summary>
    /// Wide row of a packet that carries a field tree and whose fields this exporter did not stage: each selected field is looked
    /// up in the tree and formatted straight into its cell, with no stage in between.
    /// <para>
    /// <b>Why a second path next to the staged one:</b> staging exists because a parse delivers fields before the filter has
    /// decided; a tree packet has all of them already, and copying two or three values into a stage only to format them again was
    /// measured at about 10 % of the time of this path. The cells are the same as the staged path writes: the same occurrence rule,
    /// the same formatting, the same joining and quoting (<c>CsvExporterShapeTests</c> compares both feeds).
    /// </para>
    /// </summary>
    /// <param name="packet">A packet with a field tree.</param>
    private void _WriteWideRowFromTree(Packet packet)
    {
        _WritePacketColumns(packet);
        IReadOnlyList<SelectedField> fields = _Content!.Fields!.Fields; // Invariant: the wide layout has SelectedFields content, which has fields.
        for (int f = 0; f < fields.Count; f++)
        {
            SelectedField selected = fields[f];
            bool neutralize = _NeutralizeFormulas && selected.FieldType == FieldType.String;
            _Buffer.WriteByte(_Delimiter);
            _WriteTreeCell(packet, selected, CellPart.Value, neutralize);
            if (_Content.CustomRepresentation)
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteTreeCell(packet, selected, CellPart.CustomRepresentation, _NeutralizeFormulas);
            }

            if (_Content.CustomText)
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteTreeCell(packet, selected, CellPart.CustomText, _NeutralizeFormulas);
            }
        }

        _Buffer.Write(_NewLine);
    }

    /// <summary>
    /// One joined cell of a tree packet: the occurrences of <paramref name="selected"/> chosen by its occurrence rule, separated
    /// by the occurrence separator. <c>First</c> stops after the first hit, <c>Last</c> formats only the last one.
    /// </summary>
    /// <param name="packet">A packet with a field tree.</param>
    /// <param name="selected">The selected field and its occurrence rule.</param>
    /// <param name="part">Which member of the field the cell shows.</param>
    /// <param name="neutralize">Whether a leading formula character gets a leading apostrophe.</param>
    private void _WriteTreeCell(Packet packet, SelectedField selected, CellPart part, bool neutralize)
    {
        _Cell.Reset();
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        bool first = true;
        Field last = default;
        bool hasLast = false;

        // Presence of an empty text (text parts only): see _WriteJoinedCell. The count and the flag of the last appended
        // occurrence decide whether the cell is the quoted empty cell.
        int occurrences = 0;
        bool lastIsEmptyText = false;
        while (packet.TryGetNextField(selected.Id, ref cookie, out Field field, materialize: true))
        {
            FieldValue value = field.Value;
            if (value.Data.Type == FieldType.None)
            {
                continue;
            }

            if (selected.Occurrences == Occurrences.Last)
            {
                last = field;
                hasLast = true;
                continue;
            }

            lastIsEmptyText = _AppendTreeOccurrence(field, part, ref first);
            occurrences++;
            if (selected.Occurrences == Occurrences.First)
            {
                break;
            }
        }

        if (hasLast)
        {
            lastIsEmptyText = _AppendTreeOccurrence(last, part, ref first);
            occurrences++;
        }

        if (occurrences == 1 && lastIsEmptyText)
        {
            _WriteEmptyTextCell();
            return;
        }

        _WriteCsvFieldUtf8(_Cell.WrittenSpan, neutralize);
    }

    /// <summary>Appends one occurrence of a tree field to <see cref="_Cell"/>, preceded by the separator unless it is the first.</summary>
    /// <param name="field">The occurrence.</param>
    /// <param name="part">Which part of the field the cell shows.</param>
    /// <param name="first">In: whether no occurrence was appended yet; set to <see langword="false"/> afterwards.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="part"/> is a text part and the occurrence has a text that is set but empty (not
    /// absent); <see langword="false"/> for the value part, an absent text, and a non-empty text.
    /// </returns>
    private bool _AppendTreeOccurrence(Field field, CellPart part, ref bool first)
    {
        if (!first)
        {
            _Cell.Write(_OccurrenceSeparator);
        }

        first = false;
        FieldValue value = field.Value;
        string? text;
        switch (part)
        {
            case CellPart.CustomRepresentation:
                text = OptionalTextReader.ToNullableString(value.CustomRepresentation);
                _Cell.Write(_EncodeNullable(text));
                return text is { Length: 0 };
            case CellPart.CustomText:
                text = OptionalTextReader.ToNullableString(field.CustomText);
                _Cell.Write(_EncodeNullable(text));
                return text is { Length: 0 };
            default:
                _Cell.Write(_FormatValueUtf8(value.Data));
                return false;
        }
    }

    /// <summary>
    /// Builds one joined cell in <see cref="_Cell"/> (values formatted straight to UTF-8, the separator between occurrences) and
    /// writes it with the normal RFC 4180 quoting and formula rule. Quoting is decided on the whole cell: a value or a separator
    /// that contains the delimiter, a quote, CR, or LF quotes the cell once. Formula neutralization looks at the first byte of the
    /// cell, which is what a spreadsheet looks at.
    /// </summary>
    /// <param name="fieldId">Id of the selected field; every staged entry with this id is joined into the cell.</param>
    /// <param name="part">Which member of the entries the cell shows.</param>
    /// <param name="neutralize">Whether a leading formula character gets a leading apostrophe.</param>
    /// <remarks>
    /// Text presence: a text part of exactly one occurrence follows the text-cell rule (<see cref="_WriteTextCell"/>): absent is an
    /// empty unquoted cell, a set but empty text is <c>""</c>. A cell that joins several occurrences cannot tell the two apart
    /// (an absent text and an empty one both contribute nothing between the separators), which is a documented limit of the wide
    /// layout (the CSV README, § Texts).
    /// </remarks>
    private void _WriteJoinedCell(int fieldId, CellPart part, bool neutralize)
    {
        _Cell.Reset();
        bool first = true;
        int occurrences = 0;
        bool lastIsEmptyText = false;
        foreach (ref readonly RowFieldStage.Entry entry in _Stage!.Entries) // Invariant: the flat layouts have a stage.
        {
            if (entry.FieldId != fieldId)
            {
                continue;
            }

            if (!first)
            {
                _Cell.Write(_OccurrenceSeparator);
            }

            first = false;
            occurrences++;
            _Cell.Write(_FormatPart(in entry, part));

            // The entry holds the evaluated text, so presence is read from the string itself: null is absent, empty is set.
            lastIsEmptyText = part switch
            {
                CellPart.CustomRepresentation => entry.Representation is { Length: 0 },
                CellPart.CustomText => entry.Text is { Length: 0 },
                _ => false,
            };
        }

        if (occurrences == 1 && lastIsEmptyText)
        {
            _WriteEmptyTextCell();
            return;
        }

        _WriteCsvFieldUtf8(_Cell.WrittenSpan, neutralize);
    }

    #endregion

    #region Long and hierarchy layouts

    /// <summary>
    /// Long rows (all-fields shape): one row per staged entry in production order — packet prefix, field name, occurrence, value,
    /// and the optional text columns.
    /// </summary>
    /// <param name="packet">The packet whose staged entries are written; its fields are in <see cref="_Stage"/>.</param>
    private void _WriteLongRows(Packet packet)
    {
        foreach (ref readonly RowFieldStage.Entry entry in _Stage!.Entries) // Invariant: the long layout has a stage.
        {
            _WritePacketColumns(packet);
            _Buffer.WriteByte(_Delimiter);
            _WriteCsvFieldUtf8(_FieldName(packet.Stack, entry.FieldId), neutralize: false);
            _Buffer.WriteByte(_Delimiter);
            _AppendUtf8Int32(_Buffer, entry.Occurrence);
            _Buffer.WriteByte(_Delimiter);
            _WriteCsvFieldUtf8(_FormatPart(in entry, CellPart.Value), _NeutralizeFormulas && entry.Type == FieldType.String);
            if (_Content!.CustomRepresentation) // Invariant: the long layout has content.
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteTextCell(entry.Representation);
            }

            if (_Content.CustomText)
            {
                _Buffer.WriteByte(_Delimiter);
                _WriteTextCell(entry.Text);
            }

            _Buffer.Write(_NewLine);
        }
    }

    /// <summary>
    /// Hierarchy rows: pre-order walk of the tree (materialize: true, so lazy groups are included), node ids numbered from 0 per
    /// packet exactly like the columnar topology (a parent's id is smaller than its children's), parent −1 for top-level nodes,
    /// and the path written from the stack of ancestor field ids (no string per row). Containers get a row with an empty value
    /// cell, so the tree is complete.
    /// </summary>
    /// <param name="packet">A packet with a field tree.</param>
    private void _WriteHierarchyRows(Packet packet)
    {
        _Ancestors.Clear();
        int nodeCounter = 0;
        foreach (Field child in packet.RootField().Children(materialize: true))
        {
            _WriteHierarchyNode(packet, child, parentNodeId: -1, ref nodeCounter);
        }
    }

    /// <summary>Writes the row of one node, then the rows of its children. Recursion depth equals the nesting depth of the tree.</summary>
    /// <param name="packet">The packet the node belongs to.</param>
    /// <param name="field">The node.</param>
    /// <param name="parentNodeId">Node id of the parent; −1 for a top-level node.</param>
    /// <param name="nodeCounter">The next free node id of the packet; advanced for the node and each descendant.</param>
    private void _WriteHierarchyNode(Packet packet, Field field, int parentNodeId, ref int nodeCounter)
    {
        int nodeId = nodeCounter++;
        _WritePacketColumns(packet);
        _Buffer.WriteByte(_Delimiter);
        _AppendUtf8Int32(_Buffer, nodeId);
        _Buffer.WriteByte(_Delimiter);
        _AppendUtf8Int32(_Buffer, parentNodeId);

        // Path: the names of the ancestors, then this node, joined by '/'. Built in the cell buffer so the cell is quoted once.
        _Cell.Reset();
        foreach (int ancestor in _Ancestors)
        {
            _Cell.Write(_FieldName(packet.Stack, ancestor));
            _Cell.WriteByte((byte)'/');
        }

        int fieldIdValue = field.FieldId.Value;
        _Cell.Write(_FieldName(packet.Stack, fieldIdValue));
        _Buffer.WriteByte(_Delimiter);
        _WriteCsvFieldUtf8(_Cell.WrittenSpan, neutralize: false);
        _Buffer.WriteByte(_Delimiter);
        _WriteCsvFieldUtf8(_FieldName(packet.Stack, fieldIdValue), neutralize: false);

        // Value: a container has none and writes an empty cell.
        FieldValue fieldValue = field.Value;
        FieldValueData data = fieldValue.Data;
        _Buffer.WriteByte(_Delimiter);
        if (data.Type != FieldType.None)
        {
            _WriteCsvFieldUtf8(_FormatValueUtf8(data), _NeutralizeFormulas && data.Type == FieldType.String);
        }

        // Texts: read only when the content writes them (reading can complete a deferred text); an absent one is an empty cell.
        if (_Content!.CustomRepresentation) // Invariant: the hierarchy layout has content.
        {
            _Buffer.WriteByte(_Delimiter);
            _WriteTextCell(OptionalTextReader.GetCustomRepresentation(fieldValue, include: true));
        }

        if (_Content.CustomText)
        {
            _Buffer.WriteByte(_Delimiter);
            _WriteTextCell(OptionalTextReader.GetCustomText(field, include: true));
        }

        _Buffer.Write(_NewLine);

        _Ancestors.Add(fieldIdValue);
        foreach (Field child in field.Children(materialize: true))
        {
            _WriteHierarchyNode(packet, child, nodeId, ref nodeCounter);
        }

        _Ancestors.RemoveAt(_Ancestors.Count - 1);
    }

    #endregion

    #region Cell encoding

    /// <summary>UTF-8 field name for <paramref name="fieldId"/> in <paramref name="stack"/>, cached by id per stack.</summary>
    /// <param name="stack">The stack of the packet being written.</param>
    /// <param name="fieldId">The field id; a negative id is written as <c>-1</c>, an unregistered id as its number.</param>
    /// <returns>The name bytes; valid for the export.</returns>
    private ReadOnlySpan<byte> _FieldName(Stack stack, int fieldId)
    {
        if (fieldId < 0)
        {
            return "-1"u8;
        }

        // Invariant: the cache holds names of exactly one stack. Field ids are dense per stack, so after a session restart the same
        // id names another field; the cache starts over instead of writing the old name (one allocation per field per stack).
        if (!ReferenceEquals(stack, _NameStack))
        {
            Array.Clear(_NameUtf8);
            _NameStack = stack;
        }

        if (fieldId >= _NameUtf8.Length)
        {
            Array.Resize(ref _NameUtf8, Math.Max(checked(fieldId + 1), Math.Max(stack.FieldCount, _NameUtf8.Length * 2)));
        }

        byte[]? cached = _NameUtf8[fieldId];
        if (cached is null)
        {
            FieldInfo? info = stack.GetField(new FieldId(fieldId));
            string name = fieldId.ToString(CultureInfo.InvariantCulture);
            if (info is not null)
            {
                name = info.Name;
            }

            cached = Encoding.UTF8.GetBytes(name);
            _NameUtf8[fieldId] = cached;
        }

        return cached;
    }

    /// <summary>
    /// Formats one member of a staged entry as UTF-8 into the instance scratch: the value, or the custom representation or text
    /// (empty when absent). The span is valid until the next call that uses the scratch.
    /// </summary>
    /// <param name="entry">An entry of <see cref="_Stage"/>.</param>
    /// <param name="part">Which member of the entry to format.</param>
    /// <returns>The UTF-8 text, empty when the member is absent; valid until the next scratch use.</returns>
    private ReadOnlySpan<byte> _FormatPart(in RowFieldStage.Entry entry, CellPart part)
    {
        switch (part)
        {
            case CellPart.CustomRepresentation:
                return _EncodeNullable(entry.Representation);
            case CellPart.CustomText:
                return _EncodeNullable(entry.Text);
            default:
                // A bytes payload lives in the stage's arena; the formatter takes a value, so the arena range is wrapped as memory
                // (scalars, strings, and addresses are inline in the entry). Perf: no array per value; the range stays valid until
                // the stage's next BeginPacket, which is after this row is formatted.
                FieldValueData data = entry.Value;
                if (entry.Type == FieldType.Bytes)
                {
                    data = FieldValueData.NewBytes(_Stage!.GetBytesMemory(in entry)); // Invariant: flat layouts always have a stage.
                }

                return _FormatValueUtf8(data);
        }
    }

    /// <summary>Formats a value as UTF-8 into the instance scratch (invariant culture); valid until the next scratch use.</summary>
    /// <param name="data">The value; a bytes value is written as hexadecimal pairs separated by spaces.</param>
    /// <returns>The UTF-8 text; valid until the next scratch use.</returns>
    private ReadOnlySpan<byte> _FormatValueUtf8(FieldValueData data)
    {
        // Size the scratch from the formatted length when the value can tell it cheaply: 3 bytes per UTF-16 char is the UTF-8 worst
        // case for the BMP (a surrogate pair needs 4 bytes for 2 chars, i.e. 2 per char, so it fits too). Otherwise a 256-byte guess
        // is used, and an underestimate is handled by the string fallback below.
        int maxUtf8 = 256;
        if (data.TryGetStringSize(format: default, CultureInfo.InvariantCulture, out int charCount))
        {
            // The product is computed in long and clamped to the largest array: above 715 827 882 characters the int product would
            // wrap negative. A value that long cannot be formatted into the clamped scratch, so the string fallback below runs.
            maxUtf8 = (int)Math.Max(64L, Math.Min((long)charCount * 3, Array.MaxLength));
        }

        Span<byte> scratch = _GetQuotedScratch(maxUtf8);
        if (data.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
        {
            return scratch[..written];
        }

        // Rare: estimate too small — fall back to the string path.
        return _EncodeNullable(data.ToString());
    }

    /// <summary>
    /// Writes a custom-text or custom-representation cell with presence: an absent text is an empty unquoted cell; a present
    /// non-empty text is written like any text cell (quoted only when it needs it, formula neutralization applies); a present
    /// empty text is the quoted empty cell <c>""</c>. A reader therefore tells absent (nothing between the delimiters) from empty
    /// (<c>""</c>).
    /// </summary>
    /// <param name="text">The text; <see langword="null"/> is absent, the empty string is set but empty.</param>
    /// <remarks>
    /// Perf and size: only the rare empty text costs two extra bytes; quoting every present text would grow every text cell and
    /// break the size gate. Readers that unquote cells (spreadsheets, a data-frame reader that turns empty fields into missing
    /// values) show both as empty; the CSV README names the reader options that keep the difference.
    /// Sync: <see cref="_WriteEmptyTextCell"/> (the joined-cell paths) and the CSV README § Texts.
    /// </remarks>
    private void _WriteTextCell(string? text)
    {
        if (text is null)
        {
            return;
        }

        if (text.Length == 0)
        {
            _WriteEmptyTextCell();
            return;
        }

        _WriteCsvFieldUtf8(_EncodeNullable(text), _NeutralizeFormulas);
    }

    /// <summary>
    /// Writes the quoted empty cell <c>""</c> that stands for a text that is set but empty. Shared by <see cref="_WriteTextCell"/>
    /// and the wide-layout joined cell of exactly one occurrence, so both write the same bytes.
    /// </summary>
    private void _WriteEmptyTextCell() => _Buffer.Write("\"\""u8);

    /// <summary>Encodes text as UTF-8 into the instance scratch; <see langword="null"/> is the empty span. Valid until the next scratch use.</summary>
    /// <param name="text">The text; <see langword="null"/> and empty give an empty span.</param>
    /// <returns>The UTF-8 bytes; valid until the next scratch use.</returns>
    /// <exception cref="InvalidOperationException">The text is longer than <see cref="_MaxEncodableChars"/> characters.</exception>
    private ReadOnlySpan<byte> _EncodeNullable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        Span<byte> scratch = _GetQuotedScratch(_MaxUtf8Bytes(text.Length));
        int written = Encoding.UTF8.GetBytes(text, scratch);
        return scratch[..written];
    }

    /// <summary>
    /// The largest number of UTF-16 code units whose worst-case UTF-8 size (three bytes per code unit) still fits an
    /// <see cref="int"/>: a fact of the encoder (<see cref="Encoding.GetMaxByteCount"/>), a constant. Threat: a text near
    /// <see cref="int.MaxValue"/> characters would make the size computation throw a framework exception that names no export
    /// limit. Posture: not configurable, the encoder cannot do more; the packet that holds such a text is skipped with a message
    /// that names the limit.
    /// </summary>
    private const int _MaxEncodableChars = 715_827_881;

    /// <summary>The worst-case UTF-8 size of a text of <paramref name="charCount"/> UTF-16 code units, checked against the encoder's limit.</summary>
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

    /// <summary>A text is longer than the encoder can size a buffer for; a cold helper that names the limit.</summary>
    /// <param name="charCount">The length of the text in UTF-16 code units.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowTextTooLarge(int charCount) =>
        throw new InvalidOperationException(
            Invariant($"A text of {charCount} characters cannot be written as one CSV cell: the limit is {_MaxEncodableChars} characters."));

    /// <summary>
    /// Whether a spreadsheet application would read a cell starting with this character as a formula (OWASP CSV injection
    /// guidance: <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB, CR).
    /// </summary>
    /// <param name="first">The first character (or the first byte of an ASCII character) of the cell.</param>
    /// <returns><see langword="true"/> when a spreadsheet would read the cell as a formula.</returns>
    private static bool _IsFormulaTrigger(int first) =>
        first is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>
    /// Writes a CSV field from already-encoded UTF-8 with RFC 4180 quoting.
    /// </summary>
    /// <param name="utf8">The encoded text.</param>
    /// <param name="neutralize">Whether a leading formula character gets a leading apostrophe (before the quoting).</param>
    private void _WriteCsvFieldUtf8(ReadOnlySpan<byte> utf8, bool neutralize)
    {
        // Threat: a text cell that starts with a formula character is executed by a spreadsheet (CSV injection).
        // Mitigation: a single leading apostrophe, written inside the quotes when the cell is quoted. Rejected alternative:
        // wrapping every cell in quotes, which does not stop a spreadsheet from reading the content as a formula. Residual
        // risk: the apostrophe is data for machine readers (hence the opt-out), and a formula that starts after leading
        // whitespace is not covered because the spreadsheets this guards against read only the first character.
        bool prefix = neutralize && utf8.Length > 0 && _IsFormulaTrigger(utf8[0]);

        // The prefix is cell content too: with the apostrophe as delimiter an unquoted prefix would end the cell (an extra empty
        // column that shifts every later one), so such a cell is quoted before the content is scanned.
        bool needsQuoting = prefix && _Delimiter == (byte)'\'';

        // First pass: decide whether quoting is needed. Scanning bytes is safe because the delimiter is ASCII (builder
        // guarantee) and, in UTF-8, ASCII bytes never occur inside a multi-byte sequence. Most values need no quoting and
        // are copied in one write.
        if (!needsQuoting)
        {
            for (int i = 0; i < utf8.Length; i++)
            {
                byte b = utf8[i];
                if (b == _Delimiter || b == (byte)'"' || b == (byte)'\r' || b == (byte)'\n')
                {
                    needsQuoting = true;
                    break;
                }
            }
        }

        if (!needsQuoting)
        {
            if (prefix)
            {
                _Buffer.WriteByte((byte)'\'');
            }

            _Buffer.Write(utf8);
            return;
        }

        // Second pass (quoted): copy runs between quotes in bulk and double every quote (RFC 4180).
        _Buffer.Write("\""u8);
        if (prefix)
        {
            _Buffer.WriteByte((byte)'\'');
        }

        int start = 0;
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'"')
            {
                if (i > start)
                {
                    _Buffer.Write(utf8[start..i]);
                }
                _Buffer.Write("\"\""u8);
                start = i + 1;
            }
        }
        if (start < utf8.Length)
        {
            _Buffer.Write(utf8[start..]);
        }
        _Buffer.Write("\""u8);
    }

    /// <summary>Writes a UTF-8 integer without heap-allocating a <see cref="string"/>.</summary>
    /// <param name="buffer">The buffer the decimal digits (and a leading minus sign) are appended to.</param>
    /// <param name="value">The integer.</param>
    /// <exception cref="InvalidOperationException">The number did not fit its 20-byte stack buffer (a broken invariant).</exception>
    private static void _AppendUtf8Int32(PooledBuffer buffer, int value)
    {
        Span<byte> scratch = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(value, scratch, out int written))
        {
            _ThrowIntegerFormatFailed();
        }

        buffer.Write(scratch[..written]);
    }

    /// <summary>An integer did not fit its stack buffer: a broken invariant (an int needs at most 11 bytes), reported from a cold helper.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIntegerFormatFailed() =>
        throw new InvalidOperationException("An integer cell did not fit its 20-byte stack buffer (a broken invariant).");

    /// <summary>Extracts the value string for a column from a packet.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="column">The column; only the kind is used.</param>
    /// <returns>The cell text; empty for an absent info text and for a kind this method does not know.</returns>
    private static string _ExtractColumnValue(Packet packet, CsvColumnDefinition column)
    {
        switch (column.Kind)
        {
            case CsvColumnKind.PacketNumber:
                return packet.Id.Value.ToString(CultureInfo.InvariantCulture);
            case CsvColumnKind.Timestamp:
                return packet.Timestamp.Format();
            case CsvColumnKind.Info:
                if (packet.Info is not null)
                {
                    return packet.Info;
                }
                return string.Empty;
            case CsvColumnKind.FrameLength:
                return packet.Frame.Length.ToString(CultureInfo.InvariantCulture);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Returns a <see cref="Span{T}"/> over the instance scratch buffer,
    /// growing it if needed. The buffer never shrinks — it is grown to the
    /// high-water mark so subsequent calls for a smaller size reuse the existing
    /// allocation. Used as the heap fallback in the conditional-stackalloc pattern
    /// for large field encoding, quoted or not (avoids a per-call heap allocation). The buffer is not cleared between
    /// uses; callers read back only the bytes they just wrote. It is rented from the array pool, grows to at least twice
    /// its previous size (so a run of slowly growing values reallocates O(log n) times, not once per value), and is
    /// returned to the pool by <see cref="_ReturnScratch"/>. It is never bounded: its size follows the largest field value
    /// that is already held in memory by the packet. The returned span may be longer than <paramref name="minSize"/>.
    /// </summary>
    /// <param name="minSize">The number of bytes the caller is going to write.</param>
    /// <returns>The scratch span, at least <paramref name="minSize"/> bytes long; valid until the next call that grows it.</returns>
    private Span<byte> _GetQuotedScratch(int minSize)
    {
        byte[]? current = _Utf8Scratch;
        if (current is null || current.Length < minSize)
        {
            // Doubling is computed in long and capped at the largest array, then raised to the requested size; the pool
            // may hand out a bigger array than asked for, which is fine.
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

        return current;
    }

    /// <summary>Writes a CSV field from text with proper escaping (RFC 4180).</summary>
    /// <param name="value">The text.</param>
    /// <param name="neutralize">Whether a leading formula character gets a leading apostrophe (before the quoting).</param>
    /// <exception cref="InvalidOperationException">The text is longer than <see cref="_MaxEncodableChars"/> characters.</exception>
    /// <remarks>
    /// The text is encoded once and written by <see cref="_WriteCsvFieldUtf8"/>, the one home of the quoting and neutralization rules,
    /// so the string and the UTF-8 paths cannot drift apart. The formula trigger characters are ASCII, so the check on the first
    /// byte equals the check on the first character.
    /// </remarks>
    private void _WriteCsvField(string value, bool neutralize)
    {
        // Small texts are encoded on the stack, larger ones into the instance scratch (no heap allocation per cell). The conditional
        // stackalloc is the only form that keeps the span in scope for the call below (CA2014 forbids a loop; this is not one).
        int maxBytes = _MaxUtf8Bytes(value.Length);
        Span<byte> encoded = maxBytes <= 512
            ? stackalloc byte[maxBytes]
            : _GetQuotedScratch(maxBytes);
        int written = Encoding.UTF8.GetBytes(value, encoded);
        _WriteCsvFieldUtf8(encoded[..written], neutralize);
    }

    #endregion

    #region Builder

    /// <summary>
    /// Fluent builder for <see cref="CsvExporter"/>. Not thread-safe; configure it on one thread, then call <see cref="Build"/>.
    /// </summary>
    public sealed class Builder
    {
        /// <summary>The output target; required before <see cref="Build"/>.</summary>
        private ExportOutput? _Output;

        /// <summary>Display name of the exporter.</summary>
        private string _UiName = "CSV Exporter";

        /// <summary>Optional description of the exporter.</summary>
        private string? _Description;

        /// <summary>Cancellation token handed to the exporter.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>Whether a UTF-8 BOM starts the output.</summary>
        private bool _WriteBom = true;

        /// <summary>The column delimiter as one ASCII byte.</summary>
        private byte _Delimiter = (byte)',';

        /// <summary>Whether a header row is written.</summary>
        private bool _WriteHeader = true;

        /// <summary>
        /// Whether text cells that start with a formula character get a leading apostrophe.
        /// </summary>
        /// <remarks>
        /// Posture: secure by default. Spreadsheet formula neutralization is on unless the caller turns it off, because the
        /// text comes from captures (untrusted network data) and the output is commonly opened in a spreadsheet. Threat: CSV
        /// injection (formula execution in the spreadsheet). Constant or configurable: configurable by the library caller
        /// through <see cref="WithFormulaNeutralization"/>; there is no end-user setting in this library. Turn it off for machine
        /// processing, where the added apostrophe would change the data. Residual risk when off: any text cell may run as a
        /// formula in a spreadsheet; when on: spreadsheets that read a formula after leading whitespace, and the apostrophe
        /// is visible in tools that do not treat it as a text marker.
        /// </remarks>
        private bool _NeutralizeFormulas = true;

        /// <summary>Packets to write before the export finishes; 0 means unlimited.</summary>
        private int _TargetPacketCount;

        /// <summary>The packet columns added so far; empty selects the default columns of the layout.</summary>
        private readonly List<CsvColumnDefinition> _Columns = [];

        /// <summary>What the export writes; <see langword="null"/> writes packet rows.</summary>
        private ExportContent? _Content;

        /// <summary>Text between the occurrences of one field in a wide-layout cell.</summary>
        private string _OccurrenceSeparator = "|";

        /// <summary>Sets the output to a file path. The file is created lazily on the first write.</summary>
        /// <param name="path">Absolute or relative path of the output file.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>Sets the output to an existing stream.</summary>
        /// <param name="stream">A writable stream; the caller keeps ownership and closes it.</param>
        /// <returns>This builder.</returns>
        /// <remarks>
        /// Throws nothing: the stream is not validated here. A missing or unwritable stream fails the first write, and the export ends
        /// with an <see cref="ExportErrorKind.IoError"/> through <see cref="ItemSkipped"/>.
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

        /// <summary>Controls whether a UTF-8 BOM is written at the start. Default: true.</summary>
        /// <param name="write"><see langword="true"/> to write the BOM before the first row.</param>
        /// <returns>This builder.</returns>
        public Builder WithBom(bool write)
        {
            _WriteBom = write;
            return this;
        }

        /// <summary>
        /// Sets the column delimiter. Default: comma.
        /// </summary>
        /// <param name="delimiter">An ASCII character (≤ <c>0x7F</c>) that is not a control
        /// character (<c>NUL</c>, <c>CR</c>, <c>LF</c>), a double-quote (<c>"</c>), an ASCII letter or digit, or one of <c>-</c>, <c>+</c>,
        /// <c>.</c>, <c>:</c> (they occur in the unquoted number, timestamp, and hex cells). Non-ASCII chars are rejected because the
        /// on-disk byte cannot represent them in the UTF-8 single-byte form used by the writer.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="delimiter"/> is non-ASCII, <c>NUL</c>, <c>CR</c>, <c>LF</c>, a double quote, an ASCII letter or digit, or one of
        /// <c>-</c>, <c>+</c>, <c>.</c>, <c>:</c>; the message names the allowed set.
        /// </exception>
        /// <remarks>
        /// An apostrophe is accepted: a formula-neutralized cell, whose added prefix is an apostrophe, is quoted in that case so the
        /// prefix cannot end the cell.
        /// </remarks>
        public Builder WithDelimiter(char delimiter)
        {
            // The CSV writer stores the delimiter as a single byte and uses it
            // both as the field separator and as the quoting trigger. Casting a
            // non-ASCII char to byte would produce a corrupt UTF-8 sequence on
            // disk, so reject those explicitly instead of failing silently.
            if (delimiter > '\u007F')
            {
                throw new ArgumentOutOfRangeException(nameof(delimiter),
                    "Delimiter must be an ASCII character (≤ 0x7F).");
            }
            // Reject control characters and double-quote that would break
            // CSV quoting or line-ending logic:
            //   NUL (0x00) — terminates strings in many tools, silently truncating output.
            //   CR  (0x0D) — part of the CRLF line terminator; a CR delimiter corrupts rows.
            //   LF  (0x0A) — same as CR.
            //   '"' (0x22) — the quoting character; using it as a delimiter makes escaping
            //               ambiguous and breaks all conformant CSV readers.
            if (delimiter == '\0' || delimiter == '\r' || delimiter == '\n' || delimiter == '"')
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delimiter),
                    Invariant($"Character '\\u{(int)delimiter:X4}' is not permitted as a CSV delimiter."));
            }

            // Security: the packet number, frame length, timestamp, occurrence, node id, and hex cells are written without the quoting
            // scan (they never need quotes), so a delimiter that can occur in them would split a cell and shift every later column
            // (a corrupt file that every reader parses differently). Mitigation: refuse such a delimiter here, naming the allowed set.
            // Rejected: quoting every numeric and timestamp cell, which costs a scan per cell on the hot path of every row.
            // Residual risk: none for these cells; the text cells are scanned and quoted for any accepted delimiter.
            if (char.IsAsciiLetterOrDigit(delimiter) || delimiter is '-' or '+' or '.' or ':')
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delimiter),
                    Invariant($"Character '\\u{(int)delimiter:X4}' is not permitted as a CSV delimiter: ASCII letters, digits, '-', '+', ") +
                    "'.', and ':' occur in unquoted cells.");
            }

            _Delimiter = (byte)delimiter;
            return this;
        }

        /// <summary>
        /// Enables or disables formula neutralization of text cells. Default: <see langword="true"/> (secure by default).
        /// When enabled, a text cell (the <c>Info</c> column, the field columns of type <see cref="FieldType.String"/>, and the custom
        /// representation and custom text cells) that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB, or CR gets a leading
        /// single quote, so a spreadsheet reads it as text instead of running it as a formula (CSV injection). Numeric columns,
        /// timestamps, addresses, and the header row are never changed. Turn it off when the file is read by a program that must see the exact text.
        /// </summary>
        /// <param name="enabled"><see langword="true"/> to prefix such cells; <see langword="false"/> to write them unchanged.</param>
        /// <returns>This builder.</returns>
        public Builder WithFormulaNeutralization(bool enabled)
        {
            _NeutralizeFormulas = enabled;
            return this;
        }

        /// <summary>Controls whether a header row is written. Default: true.</summary>
        /// <param name="write"><see langword="true"/> to write the header row before the first packet row.</param>
        /// <returns>This builder.</returns>
        public Builder WithHeader(bool write)
        {
            _WriteHeader = write;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        /// <param name="count">The number of packets to write; 0 for no limit.</param>
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

        /// <summary>Sets the cancellation token for cooperative cancellation.</summary>
        /// <param name="cancellationToken">Checked per packet; a cancelled token ends the export.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken cancellationToken)
        {
            _CancellationToken = cancellationToken;
            return this;
        }

        /// <summary>Adds a built-in packet column.</summary>
        /// <param name="kind">
        /// A defined <see cref="CsvColumnKind"/>. <see cref="CsvColumnKind.FrameBytes"/> and <see cref="CsvColumnKind.Buffers"/> are only
        /// valid in the packet-rows and the selected-fields layout; <see cref="Build"/> refuses them next to an all-fields or hierarchy
        /// content.
        /// </param>
        /// <param name="header">Header text; <see langword="null"/> uses the default name of the kind.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined.</exception>
        public Builder WithColumn(CsvColumnKind kind, string? header = null)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kind), kind, "Column kind must be PacketNumber, Timestamp, Info, FrameLength, FrameBytes, or Buffers.");
            }

            string columnHeader = header ?? kind switch
            {
                CsvColumnKind.PacketNumber => "No.",
                CsvColumnKind.Timestamp => "Time",
                CsvColumnKind.Info => "Info",
                CsvColumnKind.FrameLength => "Length",
                CsvColumnKind.FrameBytes => "frame_bytes",
                CsvColumnKind.Buffers => "buffers",
                _ => kind.ToString(),
            };

            _Columns.Add(new CsvColumnDefinition(kind, columnHeader));
            return this;
        }

        /// <summary>
        /// Adds default columns: No., Time, Info, Length.
        /// Useful when no custom columns are configured.
        /// </summary>
        /// <returns>This builder.</returns>
        public Builder WithDefaultColumns()
        {
            WithColumn(CsvColumnKind.PacketNumber);
            WithColumn(CsvColumnKind.Timestamp);
            WithColumn(CsvColumnKind.Info);
            WithColumn(CsvColumnKind.FrameLength);
            return this;
        }

        /// <summary>Builds the <see cref="CsvExporter"/>.</summary>
        /// <returns>A new exporter that owns the configured output; later builder changes do not affect it.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no output target is configured.</exception>
        /// <exception cref="ArgumentException">
        /// A <see cref="CsvColumnKind.FrameBytes"/> or <see cref="CsvColumnKind.Buffers"/> column is configured next to an all-fields or
        /// hierarchy content (checked here
        /// because <see cref="WithColumn"/> and <see cref="WithContent"/> may be called in either order).
        /// </exception>
        public CsvExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            // The long layouts (all fields, hierarchy) repeat the packet columns on every field row, so a frame or buffers column would
            // write the whole frame or the buffers once per field and multiply the file by the field count. Packet rows and the wide
            // layout write one row per packet. A caller bug of the library API (the plugin checks before it builds and reports a
            // PluginError instead). The message names the column kind and the shape. Sync: CsvExporterRegistration (the same wording for the
            // plugin options) and the CSV README § Frame bytes and § Buffers.
            if (_Content is { Shape: ExportShape.AllFields or ExportShape.Hierarchy } && _TryGetBytesColumn(out CsvColumnKind bytesKind))
            {
                throw new ArgumentException(
                    Invariant($"The {bytesKind} column is only valid in packet rows and the selected-fields layout, not with the shape ") +
                    Invariant($"{_Content.Shape}."));
            }

            // Packet columns: the configured ones, or a default. Packet rows default to No., Time, Info, Length; the layouts that
            // repeat the prefix on every field row (long, hierarchy) or add field columns to it (wide) default to No., Time, because
            // the Info and Length columns would repeat on every row for no use.
            CsvColumnDefinition[] columns = [.. _Columns];
            if (columns.Length == 0)
            {
                columns = _Content is null
                    ? _DefaultPacketColumns
                    : _DefaultFieldPrefixColumns;
            }

            return new CsvExporter(
                _Output, _UiName, _Description, _WriteBom, _Delimiter, _WriteHeader, _NeutralizeFormulas,
                _TargetPacketCount, columns, _Content, Encoding.UTF8.GetBytes(_OccurrenceSeparator), _CancellationToken);
        }

        /// <summary>Finds the first frame-bytes or buffers column that was configured, so the refusal in <see cref="Build"/> can name it.</summary>
        /// <param name="kind">The kind of the first such column when the method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="true"/> when <see cref="WithColumn"/> added a <see cref="CsvColumnKind.FrameBytes"/> or a
        /// <see cref="CsvColumnKind.Buffers"/> column.
        /// </returns>
        private bool _TryGetBytesColumn(out CsvColumnKind kind)
        {
            foreach (CsvColumnDefinition column in _Columns)
            {
                if (column.Kind is CsvColumnKind.FrameBytes or CsvColumnKind.Buffers)
                {
                    kind = column.Kind;
                    return true;
                }
            }

            kind = default;
            return false;
        }

        /// <summary>Packet columns of the packet-rows layout when none were configured.</summary>
        private static readonly CsvColumnDefinition[] _DefaultPacketColumns =
        [
            new(CsvColumnKind.PacketNumber, "No."),
            new(CsvColumnKind.Timestamp, "Time"),
            new(CsvColumnKind.Info, "Info"),
            new(CsvColumnKind.FrameLength, "Length"),
        ];

        /// <summary>Packet columns of the field layouts when none were configured: Info and Length would repeat on every field row.</summary>
        private static readonly CsvColumnDefinition[] _DefaultFieldPrefixColumns =
        [
            new(CsvColumnKind.PacketNumber, "No."),
            new(CsvColumnKind.Timestamp, "Time"),
        ];

        /// <summary>
        /// Selects what is exported (shape, fields, texts). Without this call the exporter writes packet rows. Start from
        /// <see cref="ContentTemplate"/>; <c>Info</c>, <c>FrameBytes</c>, and <c>Buffers</c> must be off: CSV keeps packet-level data in
        /// columns, so use <see cref="WithColumn"/> with <see cref="CsvColumnKind.Info"/>, <see cref="CsvColumnKind.FrameBytes"/>, and
        /// <see cref="CsvColumnKind.Buffers"/>.
        /// </summary>
        /// <param name="content">The content.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The content asks for info, frame bytes, or buffers (the message names the column kind to use instead), or a selected-fields
        /// content has no selection, or an all-fields or hierarchy content has one.
        /// </exception>
        public Builder WithContent(ExportContent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (content.Info)
            {
                throw new ArgumentException(
                    "CSV writes the info string through WithColumn(CsvColumnKind.Info); set Info to false.", nameof(content));
            }

            if (content.FrameBytes)
            {
                throw new ArgumentException(
                    "CSV writes the frame bytes through WithColumn(CsvColumnKind.FrameBytes); set FrameBytes to false.", nameof(content));
            }

            if (content.Buffers)
            {
                throw new ArgumentException(
                    "CSV writes the additional buffers through WithColumn(CsvColumnKind.Buffers); set Buffers to false.", nameof(content));
            }

            // The shared shape / fields rule of every exporter.
            if (!content.TryValidateShape(out string? problem))
            {
                throw new ArgumentException(problem, nameof(content));
            }

            _Content = content;
            return this;
        }

        /// <summary>Sets the text written between the occurrences of one field in a wide-layout cell. Default <c>|</c>.</summary>
        /// <param name="separator">Non-empty text without CR or LF.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException">The separator is null, empty, or contains CR or LF.</exception>
        /// <remarks>
        /// CR and LF are refused because they would end the row in readers that do not honor quoting; any other text is safe because
        /// a cell that contains the delimiter or a quote is quoted as a whole (RFC 4180). Not a resource limit: the separator is
        /// written once per extra occurrence, bounded by the packet's field count.
        /// </remarks>
        public Builder WithOccurrenceSeparator(string separator)
        {
            ArgumentException.ThrowIfNullOrEmpty(separator);
            if (separator.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException("The occurrence separator must not contain CR or LF.", nameof(separator));
            }

            _OccurrenceSeparator = separator;
            return this;
        }
    }

    #endregion
}
