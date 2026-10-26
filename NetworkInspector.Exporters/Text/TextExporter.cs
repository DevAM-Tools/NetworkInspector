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
/// <para>
/// <b>Allocation strategy:</b> each packet is formatted completely into one reusable <see cref="PooledBuffer"/> and
/// handed to the output with a single write. Text is encoded to UTF-8 on the stack (or a reused instance scratch
/// array for large values) and values are formatted straight into UTF-8, so the common path allocates nothing per
/// field. Because a packet is written only after it is fully formatted, a failure while formatting never leaves half a
/// packet in the output.
/// </para>
/// </summary>
public sealed class TextExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    // ========================================================================
    // Constants
    // ========================================================================

    /// <summary>Number of spaces per indentation level.</summary>
    private const int _IndentWidth = 2;

    // ========================================================================
    // Fields
    // ========================================================================

    private readonly CancellationToken _CancellationToken;
    private readonly TextDetailLevel _DetailLevel;

    /// <summary>
    /// Maximum characters for string/bytes values. 0 = unlimited.
    /// Limit: default 256 characters, 0 (unlimited) to <see cref="int.MaxValue"/>. Threat: a single huge value (for
    /// example a large string or byte payload) inflating every output line and the memory held for formatting it.
    /// Configurable by the library caller through <c>WithMaxTextLength</c>. Posture: secure by default (truncation is
    /// on); unlimited output is an explicit opt-in. The origin of the value 256 is not recorded.
    /// </summary>
    private readonly int _MaxTextLength;
    private readonly int _TargetPacketCount;

    private ExportOutput? _Output;
    private readonly PooledBuffer _Buffer = new(16384);

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    // Instance UTF-8 scratch buffer shared by _WriteString, _WriteSpan, _WriteValueDirect and _WriteIndent for values
    // too large for the 512-byte stack buffers.
    // Grows to the maximum bytes ever needed by this exporter instance and is then
    // reused without allocation. Buffer lifetime is tied to the exporter instance (it is never shrunk, so with an
    // unlimited text length it stays as large as the largest value seen).
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
        int targetPacketCount,
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

    // ========================================================================
    // Methods
    // ========================================================================

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
            new ExporterReleaseStep("output disposal", _DisposeOutput));
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

    /// <summary>Handles a single packet — formats it and writes it to the output.</summary>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse further packets so the caller stops feeding the exporter.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // Skip packets carry no field tree, so there is nothing to print; counted as a skipped item
        // (in Strict mode the export ends instead).
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

            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = index,
                Kind = ExportErrorKind.Other,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
            return true;
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
            _WritePacketHeader(packet, packetNumber);
            _WriteFieldTree(packet);
            _WriteSeparator();

            _Output!.Write(_Buffer.WrittenSpan);

            PacketCount++;
            return true;
        }
        catch (Exception ex)
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

            _RaiseItemSkipped(index, ExportErrorKind.SerializationError, ex.Message);
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
    private void _WritePacketHeader(Packet packet, int packetNumber)
    {
        // "Packet "
        _Buffer.Write("Packet "u8);

        // Packet number — an int needs at most 11 bytes (sign plus 10 digits), so 20 bytes is ample and
        // TryFormat cannot fail; formatting into a stack buffer avoids the string that ToString() would allocate.
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
    private void _WriteSeparator() => _Buffer.Write("\n"u8);

    /// <summary>
    /// Writes the protocol field tree starting from the packet root field.
    /// The root field itself is not printed; its children begin the output.
    /// </summary>
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
    /// Recursively writes a single field and its descendants to the buffer.
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
        _WriteFieldLine(field, fieldType, depth);

        // Recurse into children unless Summary mode suppresses sub-fields.
        // materialize: true — full text dump must include lazy protocol trees.
        if (field.HasChildren(materialize: true) && _DetailLevel != TextDetailLevel.Summary)
        {
            foreach (Field child in field.Children(materialize: true))
            {
                _WriteField(child, depth + 1);
            }
        }
    }

    /// <summary>Writes the indented line for a single field.</summary>
    private void _WriteFieldLine(Field field, FieldType fieldType, int depth)
    {
        // depth 1 → 0 spaces, depth 2 → 2 spaces, etc.
        int indentSpaces = (depth - 1) * _IndentWidth;
        _WriteIndent(indentSpaces);

        if (fieldType == FieldType.None)
        {
            // Container field: write the display text (protocol section header).
            // Priority: CustomText (zero-alloc via AsSpan) → UiName → Name → "(unknown)".
            if (!field.CustomText.IsNull)
            {
                _WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            }
            else
            {
                _WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            }
        }
        else if (fieldType == FieldType.Bytes)
        {
            // Bytes field (only reached in Full mode): render as hex without per-byte string allocs.
            _WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            _WriteString(": ");
            _WriteBytesFieldHex(field);
        }
        else
        {
            // Value field: write "Label: value" using zero-alloc path when possible.
            _WriteString(field.FieldInfo?.UiName ?? field.FieldInfo?.Name ?? "(unknown)");
            _WriteString(": ");
            _WriteValueDirect(field);
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
    private void _WriteValueDirect(Field field)
    {
        // 1. CustomText: protocol-assigned display string, read as a ReadOnlySpan<char> — no alloc.
        if (!field.CustomText.IsNull)
        {
            _WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            return;
        }

        // 2. Zero-alloc IUtf8SpanFormattable path: ask the value for its size, then format directly into a UTF-8
        //    buffer. The buffer must be sized for the full value (charCount, which for numbers is an upper bound, not the
        //    exact length) so TryFormat succeeds. The truncation is decided on the formatted text, not on the size
        //    estimate: the text is cut after at most _MaxTextLength UTF-16 characters (never inside a character), and the
        //    ellipsis is written only when something was cut. Deciding on the estimate would append an ellipsis to a short
        //    number, and clamping to a byte count would not cut an ASCII value shorter than three times the limit.
        //    A 512-byte limit keeps the full-value buffer on the stack; larger values fall back to the
        //    instance scratch buffer.
        if (field.Value.TryGetStringSize(format: default, CultureInfo.InvariantCulture, out int charCount))
        {
            int fullMaxBytes = Encoding.UTF8.GetMaxByteCount(charCount);
            Span<byte> buf = fullMaxBytes <= 512
                ? stackalloc byte[fullMaxBytes]
                : _GetScratch(fullMaxBytes);
            if (field.Value.TryFormat(buf, out int written, format: default, provider: CultureInfo.InvariantCulture))
            {
                _WriteUtf8Truncated(buf[..written]);
                return;
            }
        }

        // 3. Fallback: ToString() — one allocation, covers exotic or future value types, and is reached when the
        //    value cannot report its size or formatting into the buffer failed.
        // FieldValue.ToString() uses InvariantCulture via FieldValueData.ToTempString() / ZA.String.
        _WriteString(_TruncateText(field.Value.ToString(), _MaxTextLength));
    }

    /// <summary>
    /// Returns a span over the instance scratch buffer with at least <paramref name="minSize"/> bytes, growing the buffer
    /// when it is too small (to exactly the requested size, as the other scratch users do).
    /// </summary>
    private Span<byte> _GetScratch(int minSize)
    {
        if (_Utf8Scratch is null || _Utf8Scratch.Length < minSize)
        {
            _Utf8Scratch = new byte[minSize];
        }

        return _Utf8Scratch.AsSpan(0, minSize);
    }

    /// <summary>
    /// Writes already-encoded UTF-8 text, cut after at most <see cref="_MaxTextLength"/> UTF-16 characters (0 means no
    /// limit), and appends the ellipsis only when something was cut.
    /// </summary>
    private void _WriteUtf8Truncated(ReadOnlySpan<byte> utf8)
    {
        int keep = _MaxTextLength > 0
            ? _Utf8PrefixLength(utf8, _MaxTextLength)
            : utf8.Length;
        _Buffer.Write(utf8[..keep]);
        if (keep < utf8.Length)
        {
            _WriteString("…");
        }
    }

    /// <summary>
    /// Returns the length in bytes of the longest prefix of <paramref name="utf8"/> that holds at most
    /// <paramref name="maxChars"/> UTF-16 characters and ends between two characters (a character is never split, and a
    /// surrogate pair counts as two and is kept or dropped as a whole). Bytes that are not valid UTF-8 count as one
    /// replacement character each step, so the scan always advances.
    /// </summary>
    private static int _Utf8PrefixLength(ReadOnlySpan<byte> utf8, int maxChars)
    {
        // A character takes at least as many bytes as it has UTF-16 units, so a text with no more bytes than the limit
        // fits as a whole (the common case for short values).
        if (utf8.Length <= maxChars)
        {
            return utf8.Length;
        }

        int chars = 0;
        int offset = 0;
        while (offset < utf8.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(utf8[offset..], out Rune rune, out int consumed);
            int units = status == OperationStatus.Done
                ? rune.Utf16SequenceLength
                : 1;
            if (chars + units > maxChars)
            {
                break;
            }

            chars += units;
            offset += Math.Max(consumed, 1);
        }

        return Math.Min(offset, utf8.Length);
    }

    /// <summary>
    /// Writes a <see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;char&gt;</see> to the buffer, truncating to
    /// <paramref name="maxLength"/> characters and appending an ellipsis if truncated.
    /// If <paramref name="maxLength"/> is 0, the full span is written.
    /// Encoding uses a stackalloc buffer for ≤512 max bytes; the instance scratch for larger spans.
    /// </summary>
    private void _WriteSpan(ReadOnlySpan<char> chars, int maxLength)
    {
        bool needsEllipsis = maxLength > 0 && chars.Length > maxLength;
        ReadOnlySpan<char> toWrite = needsEllipsis ? chars[..maxLength] : chars;

        if (toWrite.IsEmpty)
        {
            if (needsEllipsis)
            {
                _WriteString("…");
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
            _WriteString("…");
        }
    }

    /// <summary>
    /// Writes a <see cref="FieldType.Bytes"/> field as space-separated lowercase hex.
    /// The output is truncated at <see cref="_MaxTextLength"/> characters if configured.
    /// </summary>
    private void _WriteBytesFieldHex(Field field)
    {
        // CustomText takes priority (the protocol may have already formatted the bytes).
        if (!field.CustomText.IsNull)
        {
            _WriteSpan(field.CustomText.AsSpan, _MaxTextLength);
            return;
        }

        if (!field.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            _WriteString(_TruncateText(field.Value.ToString(), _MaxTextLength));
            return;
        }

        ReadOnlySpan<byte> span = bytes.Span;

        // Each byte renders as two hex digits plus one separator between bytes, so n bytes take 3n - 1 characters. The
        // largest n that stays within the character limit is (limit + 1) / 3 rounded down; rounding up would let the
        // output exceed the limit by a character (limit 4 would print "aa bb", five characters).
        int maxBytes = _MaxTextLength == 0
            ? span.Length
            : Math.Min(span.Length, (_MaxTextLength + 1) / 3);

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
            _WriteString("…");
        }
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to <paramref name="maxLength"/> characters,
    /// appending an ellipsis (<c>…</c>) if truncation occurred.
    /// If <paramref name="maxLength"/> is 0, the text is returned unchanged.
    /// </summary>
    private static string _TruncateText(string text, int maxLength)
    {
        if (maxLength == 0 || text.Length <= maxLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, maxLength), "…");
    }

    /// <summary>Writes indentation spaces to the buffer.</summary>
    private void _WriteIndent(int spaces)
    {
        if (spaces <= 0)
        {
            return;
        }

        // Use a fixed-size stackalloc for small indents (up to 64 spaces, i.e. 32 nesting levels), the instance
        // scratch for large indents (deeper nesting). Avoids a heap allocation per line.
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
    private void _WriteString(string text)
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

    /// <summary>Raises the <see cref="ItemSkipped"/> event.</summary>
    private void _RaiseItemSkipped(int index, ExportErrorKind kind, string message)
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
        private int _TargetPacketCount;

        /// <summary>Sets the output to a file path.</summary>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream. The caller keeps ownership: the exporter flushes the stream when
        /// it finishes but does not close it.
        /// </summary>
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
        /// Values longer than this are cut after that many characters and get an ellipsis (<c>…</c>); a value that fits is
        /// written as it is. The ellipsis does not count toward the limit.
        /// Set to 0 for unlimited output. Default: 256. With 0, one very large value is formatted completely and the
        /// exporter's scratch buffer grows to its size, so unlimited is only advisable for trusted input.
        /// </summary>
        /// <param name="maxLength">Maximum characters; 0 means unlimited.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
        public Builder WithMaxTextLength(int maxLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
            _MaxTextLength = maxLength;
            return this;
        }

        /// <summary>Stops after writing the specified number of packets. 0 means unlimited.</summary>
        /// <param name="count">Packet limit; 0 means unlimited.</param>
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
