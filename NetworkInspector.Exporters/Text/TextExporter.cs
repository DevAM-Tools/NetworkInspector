// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Text;

/// <summary>
/// Exports packets to a human-readable text format with configurable detail level.
/// <para>
/// Output is a plain-text representation of the parsed protocol field tree, similar
/// to Wireshark's packet details view. Each packet begins with a header line showing
/// the packet number and timestamp, followed by the field tree with two-space
/// indentation per nesting level, and a separator line after the tree.
/// </para>
/// <para>
/// Example output (Standard level):
/// <code>
/// Packet 1  [2024-01-01T12:00:00.000000000Z]
/// Frame
///   Arrival Time: 2024-01-01T12:00:00.000000000Z
/// Ethernet II
///   Destination: aa:bb:cc:dd:ee:ff
///   Source: 11:22:33:44:55:66
///   Type: IPv4 (0x0800)
///   Internet Protocol Version 4
///     Source Address: 192.168.1.1
///     Destination Address: 10.0.0.1
///     User Datagram Protocol
///       Source Port: 53
///       Destination Port: 12345
///
/// </code>
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving this exporter from multiple threads.
/// </para>
/// </summary>
public sealed class TextExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    // ========================================================================
    // Constants
    // ========================================================================

    /// <summary>Number of spaces per indentation level.</summary>
    private const int IndentWidth = 2;

    // ========================================================================
    // Fields
    // ========================================================================

    private readonly CancellationToken _CancellationToken;
    private readonly TextDetailLevel _DetailLevel;

    /// <summary>Maximum characters for string/bytes values. 0 = unlimited.</summary>
    private readonly int _MaxTextLength;
    private readonly long _TargetPacketCount;

    private ExportOutput? _Output;
    private readonly PooledBuffer _Buffer = new(16384);

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    // Instance UTF-8 scratch buffer shared by WriteString and WriteIndent.
    // Grows to the maximum bytes ever needed by this exporter instance and is then
    // reused without allocation. Buffer lifetime is tied to the exporter instance.
    private byte[]? _Utf8Scratch;

    // ========================================================================
    // Constructors
    // ========================================================================

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private TextExporter(
        ExportOutput output,
        string uiName,
        string? description,
        TextDetailLevel detailLevel,
        int maxTextLength,
        long targetPacketCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _DetailLevel = detailLevel;
        _MaxTextLength = maxTextLength;
        _TargetPacketCount = targetPacketCount;
        _CancellationToken = cancellationToken;
    }

    /// <summary>Creates a new <see cref="Builder"/> for fluent configuration.</summary>
    public static Builder CreateBuilder() => new();

    // ========================================================================
    // Properties
    // ========================================================================

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

    // ========================================================================
    // Methods
    // ========================================================================

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

        return HandlePacket(packet);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// If no packets were written before <see cref="OnFinish"/> is called, the output
    /// will be empty (zero bytes) — no header or placeholder is written. This is valid
    /// behavior; callers that need a non-empty output must check
    /// <see cref="IExporterStatistics.WrittenCount"/> before use.
    /// </remarks>
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

    /// <summary>Handles a single packet — formats it and writes it to the output.</summary>
    private bool HandlePacket(Packet packet)
    {
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        try
        {
            _Started = true;
            packet.MaterializeAll();
            _Buffer.Reset();

            long packetNumber = PacketCount + 1;
            WritePacketHeader(packet, packetNumber);
            WriteFieldTree(packet);
            WriteSeparator();

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
            RaiseItemSkipped(index, ExportErrorKind.SerializationError, ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Writes the packet header line to the buffer.
    /// Format: <c>Packet N  [2024-01-01T12:00:00.000000000Z]\n</c>
    /// All writes go directly to <see cref="_Buffer"/> without heap allocation:
    /// the packet number is formatted into a 20-byte stackalloc and the timestamp
    /// into a <see cref="Timestamp.MaxFormattedLength"/>-byte stackalloc.
    /// </summary>
    /// <param name="packet">The packet to describe.</param>
    /// <param name="packetNumber">1-based packet number, pre-computed by the caller.</param>
    private void WritePacketHeader(Packet packet, long packetNumber)
    {
        // "Packet "
        _Buffer.Write("Packet "u8);

        // Packet number — max 19 digits for a 64-bit integer plus sign; 20 bytes is sufficient.
        Span<byte> numBuf = stackalloc byte[20];
        if (packetNumber.TryFormat(numBuf, out int numWritten, format: default, provider: CultureInfo.InvariantCulture))
        {
            _Buffer.Write(numBuf[..numWritten]);
        }

        // "  ["
        _Buffer.Write("  ["u8);

        // Timestamp — format directly into a fixed-size stackalloc (MaxFormattedLength is exact).
        // Timestamp.TryFormat writes ISO 8601 with nanosecond precision (all ASCII, 1 byte/char).
        Span<byte> tsBuf = stackalloc byte[Timestamp.MaxFormattedLength];
        if (packet.Timestamp.TryFormat(tsBuf, out int tsWritten, format: default, provider: CultureInfo.InvariantCulture))
        {
            _Buffer.Write(tsBuf[..tsWritten]);
        }

        // "]\n"
        _Buffer.Write("]\n"u8);
    }

    /// <summary>
    /// Writes a blank separator line between packets.
    /// The field tree already ends with a newline, so one additional newline
    /// produces an empty line that visually separates consecutive packets.
    /// </summary>
    private void WriteSeparator() => _Buffer.Write("\n"u8);

    /// <summary>
    /// Writes the protocol field tree starting from the packet root field.
    /// The root field itself is not printed; its children begin the output.
    /// </summary>
    private void WriteFieldTree(Packet packet)
    {
        Field root = packet.RootField();
        if (!root.IsValid || !root.HasChildren)
        {
            return;
        }

        // Root (depth 0) is skipped in output; recurse into its children at depth 1
        foreach (Field child in root.Children(materialize: false))
        {
            WriteField(child, depth: 1);
        }
    }

    /// <summary>
    /// Recursively writes a single field and its descendants to the buffer.
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="depth">
    /// Current depth in the tree. Depth 1 is a direct child of root (no indentation).
    /// Each additional level adds <see cref="IndentWidth"/> spaces.
    /// </param>
    private void WriteField(Field field, int depth)
    {
        if (!field.IsValid)
        {
            return;
        }

        FieldType fieldType = field.Value.Type;

        // In Summary mode, only container fields (FieldType.None) are shown;
        // all value fields — including Bytes — are omitted.
        if (_DetailLevel == TextDetailLevel.Summary && fieldType != FieldType.None)
        {
            return;
        }

        // In Standard mode, raw byte fields are also omitted.
        if (fieldType == FieldType.Bytes && _DetailLevel < TextDetailLevel.Full)
        {
            return;
        }

        // Write this field's line
        WriteFieldLine(field, fieldType, depth);

        // Recurse into children unless Summary mode suppresses sub-fields
        if (field.HasChildren && _DetailLevel != TextDetailLevel.Summary)
        {
            foreach (Field child in field.Children(materialize: false))
            {
                WriteField(child, depth + 1);
            }
        }
    }

    /// <summary>Writes the indented line for a single field.</summary>
    private void WriteFieldLine(Field field, FieldType fieldType, int depth)
    {
        // depth 1 → 0 spaces, depth 2 → 2 spaces, etc.
        int indentSpaces = (depth - 1) * IndentWidth;
        WriteIndent(indentSpaces);

        if (fieldType == FieldType.None)
        {
            // Container field: write the display text (protocol section header).
            // Priority: CustomText (zero-alloc via AsSpan) → UiName → Name → "(unknown)".
            if (!field.CustomText.IsNull)
            {
                WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            }
            else
            {
                WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            }
        }
        else if (fieldType == FieldType.Bytes)
        {
            // Bytes field (only reached in Full mode): render as hex without per-byte string allocs.
            WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            WriteString(": ");
            WriteBytesFieldHex(field);
        }
        else
        {
            // Value field: write "Label: value" using zero-alloc path when possible.
            WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            WriteString(": ");
            WriteValueDirect(field);
        }

        _Buffer.Write("\n"u8);
    }

    /// <summary>
    /// Writes the formatted value of a non-bytes value field directly to the buffer,
    /// eliminating per-field heap allocation on the hot path.
    /// <para>Priority order:</para>
    /// <list type="number">
    ///   <item><see cref="Field.CustomText"/> – written via <see cref="LazyString.AsSpan"/> (zero-alloc).</item>
    ///   <item><see cref="FieldValue"/> implementing <see cref="IUtf8SpanFormattable"/> – formatted
    ///         directly into a stackalloc UTF-8 buffer via <c>TryFormat</c> (zero-alloc).</item>
    ///   <item>Fallback: <c>FieldValue.ToString()</c> (one allocation, covers exotic types).</item>
    /// </list>
    /// </summary>
    private void WriteValueDirect(Field field)
    {
        // 1. CustomText: protocol-assigned display string, read as a ReadOnlySpan<char> — no alloc.
        if (!field.CustomText.IsNull)
        {
            WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            return;
        }

        // 2. Zero-alloc IUtf8SpanFormattable path: ask the value for its formatted UTF-8 byte length,
        //    then format directly into a rentable UTF-8 buffer. The buffer must be sized for the full
        //    value (charCount) so TryFormat succeeds; truncation is applied after formatting by clamping
        //    the written byte count to the byte equivalent of _MaxTextLength characters.
        //    All current value types are ASCII-only in their UTF-8 form (1 byte per char).
        //    A 512-byte limit keeps the full-value buffer on the stack; larger values fall back to the
        //    instance scratch buffer.
        if (field.Value.TryGetStringSize(format: default, CultureInfo.InvariantCulture, out int charCount))
        {
            bool needsEllipsis = _MaxTextLength > 0 && charCount > _MaxTextLength;
            int limit = needsEllipsis ? _MaxTextLength : charCount;

            // Buffer sized for the full value so TryFormat succeeds; output clamped to limit after.
            int fullMaxBytes = Encoding.UTF8.GetMaxByteCount(charCount);
            int limitMaxBytes = Encoding.UTF8.GetMaxByteCount(limit);

            if (fullMaxBytes <= 512)
            {
                Span<byte> buf = stackalloc byte[fullMaxBytes];
                if (field.Value.TryFormat(buf, out int written, format: default, provider: CultureInfo.InvariantCulture))
                {
                    _Buffer.Write(buf[..Math.Min(written, limitMaxBytes)]);
                    if (needsEllipsis)
                    {
                        WriteString("…");
                    }
                    return;
                }
            }
            else
            {
                if (_Utf8Scratch is null || _Utf8Scratch.Length < fullMaxBytes)
                {
                    _Utf8Scratch = new byte[fullMaxBytes];
                }
                Span<byte> buf = _Utf8Scratch.AsSpan(0, fullMaxBytes);
                if (field.Value.TryFormat(buf, out int written, format: default, provider: CultureInfo.InvariantCulture))
                {
                    _Buffer.Write(buf[..Math.Min(written, limitMaxBytes)]);
                    if (needsEllipsis)
                    {
                        WriteString("…");
                    }
                    return;
                }
            }
        }

        // 3. Fallback: ToString() — one allocation, covers exotic or future value types.
        WriteString(TruncateText(field.Value.ToString(), _MaxTextLength));
    }

    /// <summary>
    /// Writes a <see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;char&gt;</see> to the buffer, truncating to
    /// <paramref name="maxLength"/> characters and appending an ellipsis if truncated.
    /// If <paramref name="maxLength"/> is 0, the full span is written.
    /// Encoding uses a stackalloc buffer for ≤512 max bytes; the instance scratch for larger spans.
    /// </summary>
    private void WriteSpan(ReadOnlySpan<char> chars, int maxLength)
    {
        bool needsEllipsis = maxLength > 0 && chars.Length > maxLength;
        ReadOnlySpan<char> toWrite = needsEllipsis ? chars[..maxLength] : chars;

        if (toWrite.IsEmpty)
        {
            if (needsEllipsis)
            {
                WriteString("…");
            }
            return;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(toWrite.Length);
        if (maxBytes <= 512)
        {
            Span<byte> encoded = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(toWrite, encoded);
            _Buffer.Write(encoded[..written]);
        }
        else
        {
            if (_Utf8Scratch is null || _Utf8Scratch.Length < maxBytes)
            {
                _Utf8Scratch = new byte[maxBytes];
            }
            int written = Encoding.UTF8.GetBytes(toWrite, _Utf8Scratch);
            _Buffer.Write(_Utf8Scratch.AsSpan(0, written));
        }

        if (needsEllipsis)
        {
            WriteString("…");
        }
    }

    /// <summary>
    /// Writes a <see cref="FieldType.Bytes"/> field as space-separated lowercase hex.
    /// The output is truncated at <see cref="_MaxTextLength"/> characters if configured.
    /// </summary>
    private void WriteBytesFieldHex(Field field)
    {
        // CustomText takes priority (the protocol may have already formatted the bytes).
        if (!field.CustomText.IsNull)
        {
            WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            return;
        }

        if (!field.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            WriteString(TruncateText(field.Value.ToString(), _MaxTextLength));
            return;
        }

        ReadOnlySpan<byte> span = bytes.Span;

        int maxBytes = _MaxTextLength == 0
            ? span.Length
            : Math.Min(span.Length, (_MaxTextLength + 2) / 3);

        ReadOnlySpan<byte> nibbles = "0123456789abcdef"u8;
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
            WriteString("…");
        }
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to <paramref name="maxLength"/> characters,
    /// appending an ellipsis (<c>…</c>) if truncation occurred.
    /// If <paramref name="maxLength"/> is 0, the text is returned unchanged.
    /// </summary>
    private static string TruncateText(string text, int maxLength)
    {
        if (maxLength == 0 || text.Length <= maxLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, maxLength), "…");
    }

    /// <summary>Writes indentation spaces to the buffer.</summary>
    private void WriteIndent(int spaces)
    {
        if (spaces <= 0)
        {
            return;
        }

        // Use a fixed-size stackalloc for small indents, the instance scratch
        // for large indents (deep nesting). Avoids heap allocation.
        if (spaces <= 64)
        {
            Span<byte> indent = stackalloc byte[spaces];
            indent.Fill((byte)' ');
            _Buffer.Write(indent);
        }
        else
        {
            if (_Utf8Scratch is null || _Utf8Scratch.Length < spaces)
            {
                _Utf8Scratch = new byte[spaces];
            }
            _Utf8Scratch.AsSpan(0, spaces).Fill((byte)' ');
            _Buffer.Write(_Utf8Scratch.AsSpan(0, spaces));
        }
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8 and writes it to the buffer.</summary>
    private void WriteString(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 512)
        {
            Span<byte> encoded = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(text, encoded);
            _Buffer.Write(encoded[..written]);
        }
        else
        {
            // Instance scratch: grows to the maximum bytes ever needed and is reused
            // without allocation.
            if (_Utf8Scratch is null || _Utf8Scratch.Length < maxBytes)
            {
                _Utf8Scratch = new byte[maxBytes];
            }
            int written = Encoding.UTF8.GetBytes(text, _Utf8Scratch);
            _Buffer.Write(_Utf8Scratch.AsSpan(0, written));
        }
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

            if (!_Started && !_HasError)
            {
                _Started = true;
                try
                {
                    _Output.Write("\n"u8);
                }
                catch (Exception ex)
                {
                    _HasError = true;
                    ErrorCount++;
                    ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                    {
                        ItemIndex = 0,
                        Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                        Message = $"Text empty-export write failed: {ex.Message}",
                    });
                }
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
                    Message = $"Text final flush failed: {ex.Message}",
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
    private void RaiseItemSkipped(long index, ExportErrorKind kind, string message)
    {
        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = index,
            Kind = kind,
            Message = message,
        });
    }

    // ========================================================================
    // Builder
    // ========================================================================

    /// <summary>Fluent builder for <see cref="TextExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "Text Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private TextDetailLevel _DetailLevel = TextDetailLevel.Standard;

        /// <summary>Maximum characters for string/bytes display values. 0 = unlimited. Default: 256.</summary>
        private int _MaxTextLength = 256;
        private long _TargetPacketCount;

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

        /// <summary>
        /// Sets the detail level. Default: <see cref="TextDetailLevel.Standard"/>.
        /// </summary>
        public Builder WithDetailLevel(TextDetailLevel level)
        {
            _DetailLevel = level;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of characters for string and bytes field values.
        /// Values longer than this are truncated with an ellipsis (<c>…</c>).
        /// Set to 0 for unlimited output. Default: 256.
        /// </summary>
        public Builder WithMaxTextLength(int maxLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
            _MaxTextLength = maxLength;
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

        /// <summary>Builds the <see cref="TextExporter"/>.</summary>
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
                _TargetPacketCount, _CancellationToken);
        }
    }
}
