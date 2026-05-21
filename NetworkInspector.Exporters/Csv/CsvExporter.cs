// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Exports packets to CSV format with configurable columns and delimiter.
/// Each row represents one packet, with columns for selected fields.
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class CsvExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    /// <summary>UTF-8 BOM bytes.</summary>
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> NewLine => "\r\n"u8;

    private readonly CancellationToken _CancellationToken;
    private readonly bool _WriteBom;
    private readonly byte _Delimiter;
    private readonly bool _WriteHeader;
    private readonly long _TargetPacketCount;
    private readonly CsvColumnDefinition[] _Columns;

    private ExportOutput? _Output;
    private readonly PooledBuffer _Buffer = new(4096);

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    // Instance UTF-8 scratch buffer for WriteCsvField.
    // Grows to the maximum bytes ever needed by this exporter instance and is then
    // reused without allocation. Buffer lifetime is tied to the exporter instance.
    private byte[]? _Utf8Scratch;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private CsvExporter(
        ExportOutput output,
        string uiName,
        string? description,
        bool writeBom,
        byte delimiter,
        bool writeHeader,
        long targetPacketCount,
        CsvColumnDefinition[] columns,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _WriteBom = writeBom;
        _Delimiter = delimiter;
        _WriteHeader = writeHeader;
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
    public long PacketCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public long WrittenCount => PacketCount;

    /// <inheritdoc/>
    public long SkippedCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public long ErrorCount
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
    bool IExporterStatistics.IsFinished => IsFinished;

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

        if (!_Started && !Start())
        {
            return false;
        }

        return HandlePacket(packet);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }

        _Finished = true;
        FlushAndClose();
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    /// <summary>Writes the BOM and header row on first packet.</summary>
    private bool Start()
    {
        try
        {
            if (_WriteBom)
            {
                _Output!.Write(Utf8Bom);
            }

            if (_WriteHeader)
            {
                WriteHeaderRow();
            }

            _Started = true;
            return true;
        }
        catch (Exception ex)
        {
            // Bump ErrorCount so HasErrors reflects the failure; in Tolerant mode
            // the OnError event subscribers see it via ItemSkipped, in Strict mode
            // the next OnPacket call will be rejected via the _HasError gate.
            ErrorCount++;
            _HasError = true;
            OnError(0, ExportErrorKind.IoError, ex.Message);
            return false;
        }
    }

    /// <summary>Writes the column header row.</summary>
    private void WriteHeaderRow()
    {
        _Buffer.Reset();
        for (int i = 0; i < _Columns.Length; i++)
        {
            if (i > 0)
            {
                _Buffer.WriteByte(_Delimiter);
            }

            // Header names are escaped in case they contain the delimiter or quotes
            WriteCsvField(_Columns[i].Header);
        }

        _Buffer.Write(NewLine);
        _Output!.Write(_Buffer.WrittenSpan);
    }

    /// <summary>Handles a single packet, writing one CSV row.</summary>
    private bool HandlePacket(Packet packet)
    {
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        try
        {
            packet.MaterializeAll();
            _Buffer.Reset();

            // Pre-allocate the timestamp formatting buffer once outside the column loop
            // to avoid a potential stack overflow from repeated stackalloc inside the loop (CA2014).
            Span<byte> tsBuf = stackalloc byte[Timestamp.MaxFormattedLength];

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
                        AppendUtf8Int32(_Buffer, packet.Id.Value);
                        break;
                    case CsvColumnKind.FrameLength:
                        AppendUtf8Int32(_Buffer, packet.Frame.Length);
                        break;
                    case CsvColumnKind.Timestamp:
                        // Timestamp.TryFormat writes ISO 8601 (e.g. 2024-01-01T12:00:00.000000000Z),
                        // which never contains a delimiter or a quote, so no CSV quoting is needed.
                        if (packet.Timestamp.TryFormat(tsBuf, out int tsWritten, default, null))
                        {
                            _Buffer.Write(tsBuf[..tsWritten]);
                        }
                        break;
                    default:
                        string value = ExtractColumnValue(packet, column);
                        WriteCsvField(value);
                        break;
                }
            }

            _Buffer.Write(NewLine);
            _Output!.Write(_Buffer.WrittenSpan);

            PacketCount++;
            return true;
        }
        catch (Exception ex)
        {
            ErrorCount++;
            long index = PacketCount;

            if (ErrorTolerance == ErrorToleranceMode.Strict)
            {
                _HasError = true;
                return false;
            }

            SkippedCount++;
            OnError(index, ExportErrorKind.SerializationError, ex.Message);
            return true;
        }
    }

    /// <summary>Writes a UTF-8 integer without heap-allocating a <see cref="string"/>.</summary>
    private static void AppendUtf8Int32(PooledBuffer buffer, int value)
    {
        Span<byte> scratch = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, scratch, out int written);
        buffer.Write(scratch[..written]);
    }

    /// <summary>Extracts the value string for a column from a packet.</summary>
    private static string ExtractColumnValue(Packet packet, CsvColumnDefinition column)
    {
        return column.Kind switch
        {
            CsvColumnKind.PacketNumber => packet.Id.Value.ToString(CultureInfo.InvariantCulture),
            CsvColumnKind.Timestamp => packet.Timestamp.Format(),
            CsvColumnKind.Info => packet.Info ?? string.Empty,
            CsvColumnKind.FrameLength => packet.Frame.Length.ToString(CultureInfo.InvariantCulture),
            CsvColumnKind.Field when column.FieldId.HasValue =>
                packet.TryGetFieldValue(column.FieldId.Value, out FieldValue value)
                    ? value.ToString()
                    : string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Returns a <see cref="Span{T}"/> over the instance scratch buffer,
    /// growing it if needed. Used as the heap fallback in the conditional-stackalloc
    /// pattern for large quoted-field encoding (avoids a per-call heap allocation).
    /// </summary>
    private Span<byte> GetQuotedScratch(int minSize)
    {
        if (_Utf8Scratch is null || _Utf8Scratch.Length < minSize)
        {
            _Utf8Scratch = new byte[minSize];
        }
        return _Utf8Scratch;
    }

    /// <summary>Writes a CSV field with proper escaping (RFC 4180).</summary>
    private void WriteCsvField(string value)
    {
        // Check if quoting is needed
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
            // Write value directly as UTF-8; use instance scratch for large values
            int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
            if (maxBytes <= 512)
            {
                Span<byte> temp = stackalloc byte[maxBytes];
                int written = Encoding.UTF8.GetBytes(value, temp);
                _Buffer.Write(temp[..written]);
            }
            else
            {
                if (_Utf8Scratch is null || _Utf8Scratch.Length < maxBytes)
                {
                    _Utf8Scratch = new byte[maxBytes];
                }
                int written = Encoding.UTF8.GetBytes(value, _Utf8Scratch);
                _Buffer.Write(_Utf8Scratch.AsSpan(0, written));
            }
            return;
        }

        // Quoted field: "value with ""escaped"" quotes".
        // We encode the entire value once into a UTF-8 buffer and then scan the
        // resulting bytes for the quote character. Doubling a quote at the byte
        // level is safe because '"' (0x22) is ASCII and cannot appear inside the
        // continuation bytes of a multi-byte UTF-8 sequence.
        _Buffer.Write("\""u8);

        int quotedMaxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);

        // Use conditional stackalloc for small values; fall back to ThreadStatic scratch
        // for large values to avoid heap allocation.
        // NOTE: the ternary conditional-stackalloc pattern is the only valid way to have
        // a stackalloc span remain in scope across the subsequent quote-scanning loop.
        // For large values the instance scratch buffer is used (avoids per-call allocation).
        Span<byte> encoded = quotedMaxBytes <= 512
            ? stackalloc byte[quotedMaxBytes]
            : GetQuotedScratch(quotedMaxBytes);
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

    /// <summary>Flushes remaining data and closes the output.</summary>
    private void FlushAndClose()
    {
        // Always return the rented buffer, even on flush/dispose failure.
        try
        {
            if (_Output is null)
            {
                return;
            }

            try
            {
                _Output.Flush();
            }
            catch (Exception ex)
            {
                // Surface the flush failure: setting _HasError is enough to make
                // IsFinished/HasErrors observable, and the caller's ItemSkipped
                // handler is invoked with an IO/Serialization classification so
                // it is never lost silently.
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                    Message = $"CSV final flush failed: {ex.Message}",
                });
            }

            // Surface disposal failures via the error channel rather than discarding them silently.
            Exception? disposalError = null;
            try
            {
                _Output.Dispose();
            }
            catch (Exception ex)
            {
                disposalError = ex;
            }
            _Output = null;
            if (disposalError is not null)
            {
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = disposalError is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                    Message = $"Output disposal failed: {disposalError.Message}",
                });
                throw new AggregateException("Exporter cleanup failed.", disposalError);
            }
        }
        finally
        {
            _Buffer.Return();
        }
    }

    /// <summary>Raises the <see cref="ItemSkipped"/> event.</summary>
    private void OnError(long index, ExportErrorKind kind, string message)
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
        private long _TargetPacketCount;
        private readonly List<CsvColumnDefinition> _Columns = [];

        /// <summary>Sets the output to a file path.</summary>
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
        /// character (<c>NUL</c>, <c>CR</c>, <c>LF</c>) or a double-quote (<c>\"</c>).  Non-ASCII
        /// chars are rejected because the on-disk byte cannot represent them in the UTF-8
        /// single-byte form used by the writer.</param>
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

        /// <summary>Controls whether a header row is written. Default: true.</summary>
        public Builder WithHeader(bool write)
        {
            _WriteHeader = write;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        public Builder WithTargetPacketCount(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
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
                _Output, _UiName, _Description, _WriteBom, _Delimiter, _WriteHeader,
                _TargetPacketCount, [.. _Columns], _CancellationToken);
        }
    }
}
