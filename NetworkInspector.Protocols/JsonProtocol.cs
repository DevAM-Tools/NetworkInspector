// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System.Text;

namespace NetworkInspector.Protocols;

/// <summary>
/// JSON protocol parser. Parses JSON (RFC 8259) payloads into a field tree
/// with recursive structure matching the JSON document structure.
/// Zero-allocation tokenizer on <c>ReadOnlySpan&lt;byte&gt;</c> (UTF-8).
/// <para>Field tree structure:</para>
/// <code>
/// json: JSON
/// ├── json.object: {...}
/// │   ├── json.member: "name": "John"
/// │   │   ├── json.key: "name"
/// │   │   └── json.value.string: "John"
/// │   └── json.member: "age": 30
/// │       ├── json.key: "age"
/// │       └── json.value.number: 30
/// ├── json.value.true: true
/// ├── json.value.false: false
/// ├── json.value.null: null
/// └── json.path: $.root
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("json", "JavaScript Object Notation", Description = "JSON (RFC 8259)")]
public sealed partial class JsonProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for always-present JSON fields.</summary>
    private const string JsonIndexGroup = "json";

    /// <summary>Maximum nesting depth to prevent stack overflow.</summary>
    private const int MaxDepth = 64;

    /// <summary>Maximum string value length to include in the tree.</summary>
    private const int MaxStringLength = 65536; // 64 KB

    #endregion

    #region Fields

    [NoneField("json", "JSON", IndexGroup = JsonIndexGroup)]
    private FieldId _ProtocolFieldId;

    [NoneField("json.object", "Object", IndexGroup = JsonIndexGroup)]
    private FieldId _ObjectFieldId;

    [NoneField("json.array", "Array", IndexGroup = JsonIndexGroup)]
    private FieldId _ArrayFieldId;

    [NoneField("json.member", "Member", IndexGroup = JsonIndexGroup)]
    private FieldId _MemberFieldId;

    [StringField("json.key", "Key", IndexGroup = JsonIndexGroup)]
    private FieldId _KeyFieldId;

    [StringField("json.value.string", "String", IndexGroup = JsonIndexGroup)]
    private FieldId _ValueStringFieldId;

    [StringField("json.value.number", "Number", IndexGroup = JsonIndexGroup)]
    private FieldId _ValueNumberFieldId;

    [BoolField("json.value.true", "True", IndexGroup = JsonIndexGroup)]
    private FieldId _ValueTrueFieldId;

    [BoolField("json.value.false", "False", IndexGroup = JsonIndexGroup)]
    private FieldId _ValueFalseFieldId;

    [BoolField("json.value.null", "Null", IndexGroup = "json.null")]
    private FieldId _ValueNullFieldId;

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    partial void OnStartCustom(Stack stack) =>
        _Populator = (in MutField container) => PopulateJsonFields(in container);

    /// <summary>
    /// Parses a JSON payload. Uses lazy population to defer recursive parsing.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_JsonGroupId);

        parentField.SetPacketInfo(new LazyString("JSON"));

        FieldValue containerValue = FieldValue.NewBytes(data);
        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, containerValue, new LazyString("JavaScript Object Notation"), _Populator);

        return data.Length;
    }

    /// <summary>
    /// Populates JSON fields by parsing the stored JSON bytes into a field tree.
    /// </summary>
    private ParseResult PopulateJsonFields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> jsonData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = jsonData.Span;

        int offset = SkipWhitespace(span, 0);
        if (offset >= span.Length)
        {
            return 0;
        }

        // Parse the root value
        ParseJsonValue(in container, span, ref offset, 0, in context);

        return 0;
    }

    /// <summary>
    /// Parses a single JSON value (object, array, string, number, boolean, null) and
    /// appends appropriate fields to the parent. Recurses for nested structures.
    /// </summary>
    private void ParseJsonValue(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth, in ParseContext context)
    {
        offset = SkipWhitespace(span, offset);
        if (offset >= span.Length || depth > MaxDepth)
        {
            return;
        }

        byte ch = span[offset];

        switch (ch)
        {
            case (byte)'{':
                ParseObject(in parent, span, ref offset, depth, in context);
                break;
            case (byte)'[':
                ParseArray(in parent, span, ref offset, depth, in context);
                break;
            case (byte)'"':
                string strVal = ParseString(span, ref offset);
                parent.Append(_ValueStringFieldId, FieldValue.NewString(strVal), in context);
                break;
            case (byte)'t': // true
                if (offset + 4 <= span.Length
                    && span[offset + 1] == 'r' && span[offset + 2] == 'u' && span[offset + 3] == 'e')
                {
                    parent.Append(_ValueTrueFieldId, FieldValue.NewBool(true), in context);
                    offset += 4;
                }
                else
                {
                    offset = span.Length; // Skip to end on malformed data
                }
                break;
            case (byte)'f': // false
                if (offset + 5 <= span.Length
                    && span[offset + 1] == 'a' && span[offset + 2] == 'l'
                    && span[offset + 3] == 's' && span[offset + 4] == 'e')
                {
                    parent.Append(_ValueFalseFieldId, FieldValue.NewBool(false), in context);
                    offset += 5;
                }
                else
                {
                    offset = span.Length;
                }
                break;
            case (byte)'n': // null
                if (offset + 4 <= span.Length
                    && span[offset + 1] == 'u' && span[offset + 2] == 'l' && span[offset + 3] == 'l')
                {
                    context.RecordGroupPresence(_JsonNullGroupId);
                    parent.Append(_ValueNullFieldId, FieldValue.NewBool(true), in context);
                    offset += 4;
                }
                else
                {
                    offset = span.Length;
                }
                break;
            default:
                // Number or invalid — try to parse as number
                if (ch == '-' || (ch >= '0' && ch <= '9'))
                {
                    string numStr = ParseNumber(span, ref offset);
                    parent.Append(_ValueNumberFieldId, FieldValue.NewString(numStr), in context);
                }
                else
                {
                    offset = span.Length; // Unrecognized — skip to end
                }
                break;
        }
    }

    /// <summary>
    /// Parses a JSON object and appends member fields.
    /// </summary>
    private void ParseObject(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth, in ParseContext context)
    {
        offset++; // Skip '{'
        MutField objField = parent.AppendWithCustomText(
            _ObjectFieldId, FieldValue.None, new LazyString("Object"), in context);

        bool first = true;
        while (offset < span.Length)
        {
            offset = SkipWhitespace(span, offset);
            if (offset >= span.Length)
            {
                break;
            }

            if (span[offset] == '}')
            {
                offset++;
                break;
            }

            if (!first)
            {
                if (span[offset] == ',')
                {
                    offset++;
                    offset = SkipWhitespace(span, offset);
                }
                else
                {
                    break; // Malformed
                }
            }
            first = false;

            if (offset >= span.Length || span[offset] != '"')
            {
                break; // Expected string key
            }

            // Parse key
            string key = ParseString(span, ref offset);

            // Skip colon
            offset = SkipWhitespace(span, offset);
            if (offset < span.Length && span[offset] == ':')
            {
                offset++;
            }

            // Member container — display text shows "key": value summary
            MutField memberField = objField.AppendWithCustomText(
                _MemberFieldId, FieldValue.None,
                ZA.Lazy("Member: ", key), in context);

            memberField.Append(_KeyFieldId, FieldValue.NewString(key), in context);

            // Parse value recursively
            ParseJsonValue(in memberField, span, ref offset, depth + 1, in context);
        }
    }

    /// <summary>
    /// Parses a JSON array and appends element fields.
    /// </summary>
    private void ParseArray(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth, in ParseContext context)
    {
        offset++; // Skip '['
        MutField arrField = parent.AppendWithCustomText(
            _ArrayFieldId, FieldValue.None, new LazyString("Array"), in context);

        bool first = true;
        while (offset < span.Length)
        {
            offset = SkipWhitespace(span, offset);
            if (offset >= span.Length)
            {
                break;
            }

            if (span[offset] == ']')
            {
                offset++;
                break;
            }

            if (!first)
            {
                if (span[offset] == ',')
                {
                    offset++;
                }
                else
                {
                    break; // Malformed
                }
            }
            first = false;

            ParseJsonValue(in arrField, span, ref offset, depth + 1, in context);
        }
    }

    /// <summary>
    /// Parses a JSON string value, handling escape sequences.
    /// Advances offset past the closing quote.
    /// </summary>
    private static string ParseString(ReadOnlySpan<byte> span, ref int offset)
    {
        offset++; // Skip opening '"'

        // Fast path: scan for end of string without escapes
        int start = offset;
        bool hasEscape = false;
        while (offset < span.Length)
        {
            byte ch = span[offset];
            if (ch == '"')
            {
                break;
            }
            if (ch == '\\')
            {
                hasEscape = true;
                offset += 2; // Skip escape sequence
                continue;
            }
            offset++;
        }

        string result;
        if (!hasEscape)
        {
            int len = Math.Min(offset - start, MaxStringLength);
            result = Encoding.UTF8.GetString(span.Slice(start, len));
        }
        else
        {
            // Slow path: decode escape sequences
            result = DecodeEscapedString(span[start..offset]);
        }

        if (offset < span.Length && span[offset] == '"')
        {
            offset++; // Skip closing '"'
        }

        return result;
    }

    /// <summary>
    /// Decodes a JSON string with escape sequences (\n, \t, \uXXXX, etc.).
    /// Handles multi-byte UTF-8 sequences correctly by collecting non-escape runs
    /// and decoding them as UTF-8 rather than treating each byte as a Latin-1 character.
    /// Uses stackalloc for small strings to avoid StringBuilder allocation.
    /// </summary>
    private static string DecodeEscapedString(ReadOnlySpan<byte> raw)
    {
        // Upper bound: each byte produces at most one char
        int maxChars = Math.Min(raw.Length, MaxStringLength);
        Span<char> buffer = maxChars <= 512
            ? stackalloc char[maxChars]
            : new char[maxChars];

        int written = 0;
        int i = 0;

        // Pre-allocate hex char buffer outside the loop to avoid CA2014 stackalloc-in-loop
        Span<char> hexChars = stackalloc char[4];

        while (i < raw.Length && written < maxChars)
        {
            byte ch = raw[i];
            if (ch == '\\' && i + 1 < raw.Length)
            {
                i++;
                switch (raw[i])
                {
                    case (byte)'"':
                        buffer[written++] = '"';
                        break;
                    case (byte)'\\':
                        buffer[written++] = '\\';
                        break;
                    case (byte)'/':
                        buffer[written++] = '/';
                        break;
                    case (byte)'b':
                        buffer[written++] = '\b';
                        break;
                    case (byte)'f':
                        buffer[written++] = '\f';
                        break;
                    case (byte)'n':
                        buffer[written++] = '\n';
                        break;
                    case (byte)'r':
                        buffer[written++] = '\r';
                        break;
                    case (byte)'t':
                        buffer[written++] = '\t';
                        break;
                    case (byte)'u':
                        // Unicode escape: \uXXXX — parse hex directly from bytes without Encoding.ASCII
                        if (i + 4 < raw.Length)
                        {
                            hexChars[0] = (char)raw[i + 1];
                            hexChars[1] = (char)raw[i + 2];
                            hexChars[2] = (char)raw[i + 3];
                            hexChars[3] = (char)raw[i + 4];
                            if (ushort.TryParse(hexChars, System.Globalization.NumberStyles.HexNumber,
                                null, out ushort codePoint))
                            {
                                buffer[written++] = (char)codePoint;
                            }
                            i += 4;
                        }
                        break;
                    default:
                        buffer[written++] = (char)raw[i];
                        break;
                }
                i++;
            }
            else
            {
                // Collect a contiguous run of non-escape bytes and decode them as UTF-8.
                // This correctly handles multi-byte UTF-8 sequences (bytes >= 0x80).
                int runStart = i;
                i++;
                while (i < raw.Length && raw[i] != '\\')
                {
                    i++;
                }

                ReadOnlySpan<byte> utf8Run = raw.Slice(runStart, i - runStart);
                int remaining = maxChars - written;
                int charsDecoded = System.Text.Encoding.UTF8.GetChars(utf8Run, buffer.Slice(written, remaining));
                written += charsDecoded;
            }
        }

        return new string(buffer[..written]);
    }

    /// <summary>
    /// Parses a JSON number (integer, float, negative, scientific notation).
    /// Returns the number as a string to preserve full precision.
    /// </summary>
    private static string ParseNumber(ReadOnlySpan<byte> span, ref int offset)
    {
        int start = offset;

        // Optional minus
        if (offset < span.Length && span[offset] == '-')
        {
            offset++;
        }

        // Integer part
        while (offset < span.Length && span[offset] >= '0' && span[offset] <= '9')
        {
            offset++;
        }

        // Fractional part
        if (offset < span.Length && span[offset] == '.')
        {
            offset++;
            while (offset < span.Length && span[offset] >= '0' && span[offset] <= '9')
            {
                offset++;
            }
        }

        // Exponent part
        if (offset < span.Length && (span[offset] == 'e' || span[offset] == 'E'))
        {
            offset++;
            if (offset < span.Length && (span[offset] == '+' || span[offset] == '-'))
            {
                offset++;
            }
            while (offset < span.Length && span[offset] >= '0' && span[offset] <= '9')
            {
                offset++;
            }
        }

        return Encoding.UTF8.GetString(span[start..offset]);
    }

    /// <summary>
    /// Skips JSON whitespace characters (space, tab, CR, LF).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipWhitespace(ReadOnlySpan<byte> span, int offset)
    {
        while (offset < span.Length)
        {
            byte ch = span[offset];
            if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
            {
                break;
            }
            offset++;
        }
        return offset;
    }
    #endregion
}
