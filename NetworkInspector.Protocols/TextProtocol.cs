// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;

namespace NetworkInspector.Protocols;

/// <summary>
/// Text protocol for displaying plain text payloads as line-by-line fields.
/// Equivalent to Wireshark's "data-text-lines" dissector.
/// Splits payload on LF and CRLF line endings, creating one <c>text.line</c> field per line.
/// <para>Field tree structure:</para>
/// <code>
/// text: Line-based text data (3 lines)
/// ├── text.line: "HTTP/1.1 200 OK"
/// ├── text.line: "Content-Type: text/html"
/// ├── text.line: ""
/// └── text.lines: 3
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("text", "Line-based text data", Description = "Text (line-based)")]
public sealed partial class TextProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for always-present text fields.</summary>
    private const string TextIndexGroup = "text";

    #endregion

    #region Fields

    [NoneField("text", "Line-based text data", IndexGroup = TextIndexGroup)]
    private FieldId _ProtocolFieldId;

    [StringField("text.line", "Line", IndexGroup = TextIndexGroup)]
    private FieldId _LineFieldId;

    [U64Field("text.lines", "Line Count", IndexGroup = TextIndexGroup)]
    private FieldId _LinesFieldId;

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    partial void OnStartCustom(Stack stack) =>
        _Populator = (in MutField container) => PopulateTextFields(in container);

    /// <summary>
    /// Parses text payload. Uses lazy population to defer line splitting.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TextGroupId);

        // Count lines eagerly for summary (cheap scan for 0x0A)
        int lineCount = CountLines(data.Span);

        LazyString summary = ZA.Lazy(
            "Line-based text data (", lineCount, lineCount == 1 ? " line)" : " lines)");

        parentField.SetPacketInfo(ZA.Lazy(
            "Text (", lineCount, lineCount == 1 ? " line)" : " lines)"));

        FieldValue containerValue = FieldValue.NewBytes(data);
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Populates text line fields from stored payload bytes.
    /// Splits on LF (0x0A), stripping optional CR (0x0D) before it.
    /// </summary>
    private ParseResult PopulateTextFields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> textData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = textData.Span;

        int lineCount = 0;
        int start = 0;

        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == 0x0A) // LF
            {
                int end = (i > start && span[i - 1] == 0x0D) ? i - 1 : i; // Strip CR
                string line = DecodeLine(span[start..end]);
                container.Append(_LineFieldId, FieldValue.NewString(line), in context);
                lineCount++;
                start = i + 1;
            }
        }

        // Remaining data after last LF (or entire span if no LF found)
        if (start < span.Length)
        {
            string line = DecodeLine(span[start..]);
            container.Append(_LineFieldId, FieldValue.NewString(line), in context);
            lineCount++;
        }

        container.Append(_LinesFieldId, FieldValue.NewU64((ulong)lineCount), in context);

        return 0;
    }

    /// <summary>
    /// Counts lines in a byte span by counting LF (0x0A) characters,
    /// plus one for remaining content after the last LF.
    /// </summary>
    private static int CountLines(ReadOnlySpan<byte> span)
    {
        int count = 0;
        foreach (byte b in span)
        {
            if (b == 0x0A)
            {
                count++;
            }
        }

        // If there's content after the last LF, that's another line
        if (span.Length > 0 && span[^1] != 0x0A)
        {
            count++;
        }

        return Math.Max(count, 1);
    }

    /// <summary>
    /// Decodes a line from UTF-8 bytes. Replaces invalid sequences with U+FFFD.
    /// The default <see cref="Encoding.UTF8"/> uses replacement fallback automatically.
    /// </summary>
    private static string DecodeLine(ReadOnlySpan<byte> bytes) =>
        Encoding.UTF8.GetString(bytes);
    #endregion
}
