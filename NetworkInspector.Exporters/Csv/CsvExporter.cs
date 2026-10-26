// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Exports packets to CSV format with configurable columns and delimiter.
/// Each row represents one packet, with columns for selected fields.
/// <para>
/// Output format: UTF-8 with an optional BOM (default on), rows terminated by <c>\r\n</c>, fields quoted per RFC 4180
/// only when they contain the delimiter, a double quote, CR, or LF.
/// </para>
/// <para>
/// <b>Formula neutralization (default on):</b> a spreadsheet application treats a cell that starts with <c>=</c>,
/// <c>+</c>, <c>-</c>, <c>@</c>, TAB, or CR as a formula, so text from a capture (a DNS name, a packet info line) could run
/// a formula when the file is opened (CSV injection). Such a text cell gets a leading single quote (<c>'</c>) before the
/// CSV quoting is applied. Only text is changed: the <c>Info</c> column and field columns of type
/// <see cref="FieldType.String"/>. Numeric columns, timestamps, addresses, and the header row are written as they are, so a
/// negative number keeps its minus sign. <see cref="Builder.WithFormulaNeutralization"/> switches it off for machine
/// processing, where the added quote would corrupt the data.
/// </para>
/// <para>
/// A row is assembled completely in an internal buffer and handed to the output in one write, so a failure while
/// formatting a field never leaves a partial row in the output. The per-row path reuses that buffer and formats the
/// packet number, frame length, timestamp, and field columns straight to UTF-8 without intermediate strings.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class CsvExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
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

    // Instance UTF-8 scratch buffer for _WriteCsvField, rented from the array pool.
    // Grows geometrically to the maximum bytes ever needed by this exporter instance and is then
    // reused without allocation. It is returned to the pool when the exporter finishes.
    private byte[]? _Utf8Scratch;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
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
        _CancellationToken = cancellationToken;
    }


    /// <summary>Creates a new <see cref="Builder"/> for fluent configuration.</summary>
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
            new ExporterReleaseStep("scratch return", _ReturnScratch),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

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

    /// <summary>
    /// Writes the BOM and header row on the first packet.
    /// </summary>
    /// <returns><c>true</c> when the preamble was written; <c>false</c> on an output failure, which is reported and ends
    /// the export in both error-tolerance modes (the failure is not tied to one packet).</returns>
    private bool _Start()
    {
        try
        {
            if (_WriteBom)
            {
                _Output!.Write(_Utf8Bom);
            }

            if (_WriteHeader)
            {
                _WriteHeaderRow();
            }

            _Started = true;
            return true;
        }
        catch (Exception ex)
        {
            // Bump ErrorCount so HasErrors reflects the failure. The event is raised in both modes, and _HasError makes
            // every following OnPacket call return false: a failed preamble cannot be repaired, and continuing would write
            // rows into an output that lacks the header (or has half of it).
            if (ErrorCount < int.MaxValue) ErrorCount++;
            _HasError = true;
            _OnError(0, ExportErrorKind.IoError, ex.Message);
            return false;
        }
    }

    /// <summary>Writes the column header row.</summary>
    private void _WriteHeaderRow()
    {
        _Buffer.Reset();
        for (int i = 0; i < _Columns.Length; i++)
        {
            if (i > 0)
            {
                _Buffer.WriteByte(_Delimiter);
            }

            // Header names are escaped in case they contain the delimiter or quotes
            _WriteCsvField(_Columns[i].Header, neutralize: false);
        }

        _Buffer.Write(_NewLine);
        _Output!.Write(_Buffer.WrittenSpan);
    }

    /// <summary>Handles a single packet, writing one CSV row.</summary>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse the packet so the driver stops. IsFinished reports the same condition to polling callers.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // A skip packet has no parsed field tree, so its columns cannot be filled. It is an expected, per-item failure:
        // Strict mode stops (without an event), Tolerant mode counts it as skipped, raises ItemSkipped, and continues.
        if (!packet.HasFieldTree)
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

            _OnError(index, ExportErrorKind.Other, "Skip packets cannot be exported (HasFieldTree is false).");
            return true;
        }

        try
        {
            // Field columns materialize via TryGetFieldValue(..., materialize: true).
            // Builtin columns (Id/Timestamp/Length/Info) do not need the full tree.
            // The whole row is built in _Buffer first and written once below, so a failure in any column (an exception
            // from a field formatter) leaves nothing of this row in the output.
            _Buffer.Reset();

            // Pre-allocate the timestamp formatting buffer once outside the column loop
            // to avoid a potential stack overflow from repeated stackalloc inside the loop (CA2014).
            Span<byte> tsBuf = stackalloc byte[Timestamp.MaxFormattedLength];

            // One pass over the configured columns. Each branch appends directly to the byte buffer; only the Info
            // column (and a Field column without a field id) goes through the string-based _ExtractColumnValue path.
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
                        // Timestamp.TryFormat writes ISO 8601 (e.g. 2024-01-01T12:00:00.000000000Z),
                        // which never contains a delimiter or a quote, so no CSV quoting is needed.
                        if (packet.Timestamp.TryFormat(tsBuf, out int tsWritten, default, null))
                        {
                            _Buffer.Write(tsBuf[..tsWritten]);
                        }
                        else
                        {
                            // Cannot happen with a MaxFormattedLength buffer, but an empty cell would silently break the
                            // meaning of the column, so the text form is written instead.
                            _WriteCsvField(packet.Timestamp.Format(), neutralize: false);
                        }
                        break;
                    case CsvColumnKind.Field when column.FieldId.HasValue:
                        _WriteFieldColumn(packet, column.FieldId.Value);
                        break;
                    default:
                        // The remaining branch writes the Info text and, for a field column without an id, an empty cell.
                        // Info is free text from the capture, so it is the cell that can carry a formula.
                        string value = _ExtractColumnValue(packet, column);
                        _WriteCsvField(value, _NeutralizeFormulas && column.Kind == CsvColumnKind.Info);
                        break;
                }
            }

            _Buffer.Write(_NewLine);
            _Output!.Write(_Buffer.WrittenSpan);

            PacketCount++;
            return true;
        }
        catch (Exception ex)
        {
            // Any exception while building or writing the row is a per-item failure (reported as a serialization
            // error). Note that an I/O failure of the output is also classified this way.
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

            _OnError(index, ExportErrorKind.SerializationError, ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Writes a field-column value via <see cref="IUtf8SpanFormattable"/> without an intermediate
    /// <see cref="string"/>, scanning the UTF-8 for CSV quoting needs.
    /// </summary>
    private void _WriteFieldColumn(Packet packet, FieldId fieldId)
    {
        // A packet that lacks the field writes an empty cell (the delimiter was already appended by the caller), so every
        // row keeps the same number of columns.
        if (!packet.TryGetFieldValue(fieldId, out FieldValue value, materialize: true))
        {
            return;
        }

        // Size the UTF-8 scratch from the formatted length when the value can tell it cheaply: 3 bytes per UTF-16 char is
        // the UTF-8 worst case for the BMP (a surrogate pair needs 4 bytes for 2 chars, i.e. 2 per char, so it fits too).
        // Otherwise a 256-byte guess is used, and an underestimate is handled by the string fallback below.
        int maxUtf8 = 256;
        if (value.TryGetStringSize(format: default, CultureInfo.InvariantCulture, out int charCount))
        {
            // UTF-8 upper bound for BMP + common multi-byte sequences.
            maxUtf8 = Math.Max(64, charCount * 3);
        }

        // Small values use the stack (no allocation); larger ones the instance scratch buffer, which grows to the
        // high-water mark and is reused, so a long field costs at most one allocation per size increase.
        Span<byte> scratch = maxUtf8 <= 512
            ? stackalloc byte[maxUtf8]
            : _GetQuotedScratch(maxUtf8);

        // Only text values can carry a formula. A number keeps its sign (-5 is not a formula), and timestamps, addresses and
        // byte values are formatted by the library into digits and hex, so they are never rewritten.
        bool neutralize = _NeutralizeFormulas && value.Type == FieldType.String;
        if (!value.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
        {
            // Rare: estimate too small — fall back to string path.
            _WriteCsvField(value.ToString(), neutralize);
            return;
        }

        _WriteCsvFieldUtf8(scratch[..written], neutralize);
    }

    /// <summary>
    /// Whether a spreadsheet application would read a cell starting with this character as a formula (OWASP CSV injection
    /// guidance: <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB, CR).
    /// </summary>
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

        // First pass: decide whether quoting is needed. Scanning bytes is safe because the delimiter is ASCII (builder
        // guarantee) and, in UTF-8, ASCII bytes never occur inside a multi-byte sequence. Most values need no quoting and
        // are copied in one write.
        bool needsQuoting = false;
        for (int i = 0; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (b == _Delimiter || b == (byte)'"' || b == (byte)'\r' || b == (byte)'\n')
            {
                needsQuoting = true;
                break;
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
    private static void _AppendUtf8Int32(PooledBuffer buffer, int value)
    {
        Span<byte> scratch = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, scratch, out int written);
        buffer.Write(scratch[..written]);
    }

    /// <summary>Extracts the value string for a column from a packet.</summary>
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
            case CsvColumnKind.Field when column.FieldId.HasValue:
                // materialize: true — CSV column may reference a lazy field.
                if (packet.TryGetFieldValue(column.FieldId.Value, out FieldValue value, materialize: true))
                {
                    // FieldValue.ToString() → FieldValueData.ToTempString() → ZA.String (InvariantCulture).
                    return value.ToString();
                }
                return string.Empty;
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

    /// <summary>Writes a CSV field with proper escaping (RFC 4180).</summary>
    /// <param name="value">The text.</param>
    /// <param name="neutralize">Whether a leading formula character gets a leading apostrophe (before the quoting).</param>
    private void _WriteCsvField(string value, bool neutralize)
    {
        // Same formula protection as _WriteCsvFieldUtf8 (see the threat note there).
        bool prefix = neutralize && value.Length > 0 && _IsFormulaTrigger(value[0]);

        // First pass: decide whether quoting is needed. The delimiter is ASCII (builder guarantee), so comparing it as a
        // char against the UTF-16 text is exact.
        bool needsQuoting = false;
        char delimChar = (char)_Delimiter;

        foreach (char c in value)
        {
            if (c == delimChar || c == '"' || c == '\r' || c == '\n')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            if (prefix)
            {
                _Buffer.WriteByte((byte)'\'');
            }

            // Write value directly as UTF-8; use instance scratch for large values (GetMaxByteCount is the worst case, so
            // GetBytes cannot run out of space).
            int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
            if (maxBytes <= 512)
            {
                Span<byte> temp = stackalloc byte[maxBytes];
                int written = Encoding.UTF8.GetBytes(value, temp);
                _Buffer.Write(temp[..written]);
            }
            else
            {
                Span<byte> large = _GetQuotedScratch(maxBytes);
                int written = Encoding.UTF8.GetBytes(value, large);
                _Buffer.Write(large[..written]);
            }
            return;
        }

        // Quoted field: "value with ""escaped"" quotes".
        // We encode the entire value once into a UTF-8 buffer and then scan the
        // resulting bytes for the quote character. Doubling a quote at the byte
        // level is safe because '"' (0x22) is ASCII and cannot appear inside the
        // continuation bytes of a multi-byte UTF-8 sequence.
        _Buffer.Write("\""u8);
        if (prefix)
        {
            _Buffer.WriteByte((byte)'\'');
        }

        int quotedMaxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);

        // Use conditional stackalloc for small values; fall back to instance scratch
        // for large values to avoid heap allocation.
        // NOTE: the ternary conditional-stackalloc pattern is the only valid way to have
        // a stackalloc span remain in scope across the subsequent quote-scanning loop.
        // For large values the instance scratch buffer is used (avoids per-call allocation).
        Span<byte> encoded = quotedMaxBytes <= 512
            ? stackalloc byte[quotedMaxBytes]
            : _GetQuotedScratch(quotedMaxBytes);
        int writtenBytes = Encoding.UTF8.GetBytes(value, encoded);
        ReadOnlySpan<byte> quotedBytes = encoded[..writtenBytes];

        int start = 0;
        for (int i = 0; i < quotedBytes.Length; i++)
        {
            if (quotedBytes[i] == (byte)'"')
            {
                if (i > start)
                {
                    _Buffer.Write(quotedBytes[start..i]);
                }
                _Buffer.Write("\"\""u8);
                start = i + 1;
            }
        }
        if (start < quotedBytes.Length)
        {
            _Buffer.Write(quotedBytes[start..]);
        }

        _Buffer.Write("\""u8);
    }

    /// <summary>Raises the <see cref="ItemSkipped"/> event. Counters are updated by the callers, not here.</summary>
    private void _OnError(int index, ExportErrorKind kind, string message)
    {
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = index,
            Kind = kind,
            Message = message,
        });
    }

    // ── Builder ──────────────────────────────────────────────────────────────

    /// <summary>Fluent builder for <see cref="CsvExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "CSV Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private bool _WriteBom = true;
        private byte _Delimiter = (byte)',';
        private bool _WriteHeader = true;
        // Posture: secure by default. Spreadsheet formula neutralization is on unless the caller turns it off, because the
        // text comes from captures (untrusted network data) and the output is commonly opened in a spreadsheet. Threat: CSV
        // injection (formula execution in the spreadsheet). Constant or configurable: configurable by the library caller
        // through WithFormulaNeutralization; there is no end-user setting in this library. Turn it off for machine
        // processing, where the added apostrophe would change the data. Residual risk when off: any text cell may run as a
        // formula in a spreadsheet; when on: spreadsheets that read a formula after leading whitespace, and the apostrophe
        // is visible in tools that do not treat it as a text marker.
        private bool _NeutralizeFormulas = true;
        private int _TargetPacketCount;
        private readonly List<CsvColumnDefinition> _Columns = [];

        /// <summary>Sets the output to a file path. The file is created lazily on the first write.</summary>
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

        /// <summary>Sets the output to standard output.</summary>
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

        /// <summary>Sets an optional description for the exporter.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Controls whether a UTF-8 BOM is written at the start. Default: true.</summary>
        public Builder WithBom(bool write)
        {
            _WriteBom = write;
            return this;
        }

        /// <summary>
        /// Sets the column delimiter. Default: comma.
        /// </summary>
        /// <param name="delimiter">An ASCII character (≤ <c>0x7F</c>) that is not a control
        /// character (<c>NUL</c>, <c>CR</c>, <c>LF</c>) or a double-quote (<c>"</c>).  Non-ASCII
        /// chars are rejected because the on-disk byte cannot represent them in the UTF-8
        /// single-byte form used by the writer.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="delimiter"/> is non-ASCII, <c>NUL</c>, <c>CR</c>, <c>LF</c>, or a double quote.
        /// </exception>
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
                throw new ArgumentOutOfRangeException(nameof(delimiter),
                    $"Character '\\u{(int)delimiter:X4}' is not permitted as a CSV delimiter.");
            }
            _Delimiter = (byte)delimiter;
            return this;
        }

        /// <summary>
        /// Enables or disables formula neutralization of text cells. Default: <see langword="true"/> (secure by default).
        /// When enabled, a text cell (the <c>Info</c> column and field columns of type <see cref="FieldType.String"/>) that
        /// starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB, or CR gets a leading single quote, so a spreadsheet reads
        /// it as text instead of running it as a formula (CSV injection). Numeric columns, timestamps, addresses, and the
        /// header row are never changed. Turn it off when the file is read by a program that must see the exact text.
        /// </summary>
        /// <param name="enabled"><see langword="true"/> to prefix such cells; <see langword="false"/> to write them unchanged.</param>
        public Builder WithFormulaNeutralization(bool enabled)
        {
            _NeutralizeFormulas = enabled;
            return this;
        }

        /// <summary>Controls whether a header row is written. Default: true.</summary>
        public Builder WithHeader(bool write)
        {
            _WriteHeader = write;
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

        /// <summary>Sets the cancellation token for cooperative cancellation.</summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>Adds a built-in column (packet number, timestamp, info, frame length).</summary>
        public Builder WithColumn(CsvColumnKind kind, string? header = null)
        {
            string columnHeader = header ?? kind switch
            {
                CsvColumnKind.PacketNumber => "No.",
                CsvColumnKind.Timestamp => "Time",
                CsvColumnKind.Info => "Info",
                CsvColumnKind.FrameLength => "Length",
                _ => kind.ToString(),
            };

            _Columns.Add(new CsvColumnDefinition(kind, columnHeader, null, null));
            return this;
        }

        /// <summary>Adds a field-based column that reads a specific protocol field.</summary>
        public Builder WithFieldColumn(string fieldName, FieldId fieldId, string? header = null)
        {
            _Columns.Add(new CsvColumnDefinition(CsvColumnKind.Field, header ?? fieldName, fieldName, fieldId));
            return this;
        }

        /// <summary>
        /// Adds default columns: No., Time, Info, Length.
        /// Useful when no custom columns are configured.
        /// </summary>
        public Builder WithDefaultColumns()
        {
            WithColumn(CsvColumnKind.PacketNumber);
            WithColumn(CsvColumnKind.Timestamp);
            WithColumn(CsvColumnKind.Info);
            WithColumn(CsvColumnKind.FrameLength);
            return this;
        }

        /// <summary>Builds the <see cref="CsvExporter"/>.</summary>
        /// <exception cref="InvalidOperationException">Thrown if no output target is configured.</exception>
        public CsvExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            // Use default columns if none were configured
            if (_Columns.Count == 0)
            {
                WithDefaultColumns();
            }

            return new CsvExporter(
                _Output, _UiName, _Description, _WriteBom, _Delimiter, _WriteHeader, _NeutralizeFormulas,
                _TargetPacketCount, [.. _Columns], _CancellationToken);
        }
    }
}
