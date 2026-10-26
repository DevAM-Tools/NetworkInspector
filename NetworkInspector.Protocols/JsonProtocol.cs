// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// JSON protocol parser. Parses JSON (RFC 8259) payloads into a field tree
/// with recursive structure matching the JSON document structure.
/// Tokenizer on <c>ReadOnlySpan&lt;byte&gt;</c> (UTF-8): no token objects are created and the document is not copied
/// while scanning, but every key, string value and number is materialized as a .NET string when its field is
/// emitted. The whole tree is built lazily, only when the children of the <c>json</c> container are first accessed.
/// <para>Field tree structure (the root value is a child of <c>json</c>; arrays nest the same way):</para>
/// <code>
/// json: JSON
/// └── json.object: Object                       (or json.array, or a scalar root value)
///     ├── json.member: Member: name
///     │   ├── json.key: "name"
///     │   └── json.value.string: "John"
///     └── json.member: Member: age
///         ├── json.key: "age"
///         └── json.value.number: 30             (also json.value.true / .false / .null, json.object, json.array)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("json", "JavaScript Object Notation", Description = "JSON (RFC 8259)")]
public sealed partial class JsonProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for always-present JSON fields.</summary>
    private const string _JsonIndexGroup = "json";

    /// <summary>Maximum nesting depth to prevent stack overflow.</summary>
    /// <remarks>
    /// Threat: the parser is recursive (value, object or array, value, ...), so a payload of nothing but opening
    /// brackets would otherwise exhaust the stack. RFC 8259 section 9 explicitly allows a parser to limit nesting.
    /// Constant, not configurable; documents deeper than this are cut off at that depth without an error (the
    /// deeper content simply produces no fields). The number of fields is bounded only by the payload size.
    /// </remarks>
    private const int _MaxDepth = 64;

    /// <summary>Maximum string value length to include in the tree (bytes without escapes, chars with escapes).</summary>
    /// <remarks>
    /// Bounds the memory one string value can add to the field tree: every string is copied into a .NET string
    /// (two bytes per char). Constant, not configurable. Longer strings are truncated; the cut is never silent: the
    /// string field is followed by a <c>json.error.string_limit</c> field that names this limit.
    /// </remarks>
    private const int _MaxStringLength = 65536; // 64 KB

    /// <summary>Text of the <c>json.error.string_limit</c> field; names the limit that cut the string.</summary>
    private const string _StringLimitText = "String truncated: longer than the limit _MaxStringLength (65536)";

    #endregion

    #region Fields

    [NoneField("json", "JSON", IndexGroup = _JsonIndexGroup)]
    private FieldId _ProtocolFieldId;

    [NoneField("json.object", "Object", IndexGroup = _JsonIndexGroup)]
    private FieldId _ObjectFieldId;

    [NoneField("json.array", "Array", IndexGroup = _JsonIndexGroup)]
    private FieldId _ArrayFieldId;

    [NoneField("json.member", "Member", IndexGroup = _JsonIndexGroup)]
    private FieldId _MemberFieldId;

    [StringField("json.key", "Key", IndexGroup = _JsonIndexGroup)]
    private FieldId _KeyFieldId;

    [StringField("json.value.string", "String", IndexGroup = _JsonIndexGroup)]
    private FieldId _ValueStringFieldId;

    [StringField("json.value.number", "Number", IndexGroup = _JsonIndexGroup)]
    private FieldId _ValueNumberFieldId;

    [BoolField("json.value.true", "True", IndexGroup = _JsonIndexGroup)]
    private FieldId _ValueTrueFieldId;

    [BoolField("json.value.false", "False", IndexGroup = _JsonIndexGroup)]
    private FieldId _ValueFalseFieldId;

    [BoolField("json.value.null", "Null", IndexGroup = "json.null")]
    private FieldId _ValueNullFieldId;

    // Reported next to a string or key that was cut at _MaxStringLength. It shares the "json" index group of the other
    // fields: the group is already recorded for every non-empty payload, so no eager scan for long strings is needed.
    [StringField("json.error.string_limit", "String Length Limit Reached", IndexGroup = _JsonIndexGroup)]
    private FieldId _StringLimitFieldId;

    // Pre-allocated populator: one delegate for all packets, so Parse does not allocate a closure per payload.
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack) =>
        _Populator = _PopulateJsonFields;

    /// <summary>
    /// Parses a JSON payload. Uses lazy population to defer recursive parsing: only the null-value scan runs
    /// eagerly (see <see cref="_JsonContainsNull"/>), the field tree is built when the children are first accessed.
    /// An empty payload consumes nothing and appends no container. The payload is not validated here; malformed
    /// JSON is never an error and shows as far as it could be read.
    /// <para>Thread safety: stateless; may be called concurrently.</para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the JSON container.</param>
    /// <param name="data">The JSON document (UTF-8).</param>
    /// <param name="context">Parse context used to record index groups.</param>
    /// <returns>The number of bytes consumed (all of <paramref name="data"/>).</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_JsonGroupId);

        // The lazy populator emits json.value.null (index group "json.null") only when the
        // document contains a null value token. Per the index-is-authoritative contract the
        // group must be recorded eagerly here, so the presence is decided up front by scanning
        // the raw bytes. This intentionally duplicates the scan the populator performs later.
        if (_JsonContainsNull(data.Span))
        {
            context.RecordGroupPresence(_JsonNullGroupId);
        }

        parentField.SetPacketInfo(new LazyString("JSON"));

        FieldValue containerValue = FieldValue.NewBytes(data);
        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, containerValue, new LazyString("JavaScript Object Notation"), _Populator);

        return data.Length;
    }

    /// <summary>
    /// Eagerly scans a raw JSON document for a top-level or nested null value token without
    /// building the field tree. Quoted strings (object keys and string values) are skipped so a
    /// literal "null" appearing inside a string does not produce a false positive. The detection
    /// mirrors the populator, which emits json.value.null whenever a value position begins with
    /// the bytes 'n','u','l','l'. Returns true as soon as such a token is found.
    /// <para>
    /// Cost: one linear pass over every JSON payload at parse time, without allocation. The scan is deliberately
    /// looser than the populator: it does not track structure, so for malformed or too deeply nested input it can
    /// report a null the populator never reaches (a recorded group without a field).
    /// </para>
    /// </summary>
    /// <param name="span">The raw JSON document.</param>
    private static bool _JsonContainsNull(ReadOnlySpan<byte> span)
    {
        int offset = 0;
        while (offset < span.Length)
        {
            byte ch = span[offset];
            if (ch == (byte)'"')
            {
                // Skip the quoted string, honouring backslash escapes, so its contents
                // (including a literal "null") are never matched as a value token.
                offset++;
                while (offset < span.Length)
                {
                    byte sc = span[offset];
                    if (sc == (byte)'\\')
                    {
                        offset += 2;
                        continue;
                    }
                    if (sc == (byte)'"')
                    {
                        offset++;
                        break;
                    }
                    offset++;
                }
                continue;
            }

            if (ch == (byte)'n'
                && offset + 4 <= span.Length
                && span[offset + 1] == (byte)'u'
                && span[offset + 2] == (byte)'l'
                && span[offset + 3] == (byte)'l')
            {
                return true;
            }

            offset++;
        }

        return false;
    }

    /// <summary>
    /// Populates JSON fields by parsing the stored JSON bytes into a field tree.
    /// <para>
    /// Thread safety: works on the immutable container bytes and keeps all parse state in locals and parameters,
    /// so it may run concurrently for different packets.
    /// </para>
    /// </summary>
    private ParseResult _PopulateJsonFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> jsonData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = jsonData.Span;

        // Whitespace-only payloads have no value; this is not an error because the payload may simply be padding.
        int offset = _SkipWhitespace(span, 0);
        if (offset >= span.Length)
        {
            return 0;
        }

        // Parse the root value. Any trailing bytes after the root value are ignored, and malformed input ends the
        // parse early instead of failing, so fields parsed so far stay visible.
        _ParseJsonValue(in container, span, ref offset, 0);

        return 0;
    }

    /// <summary>
    /// Parses a single JSON value (object, array, string, number, boolean, null) and
    /// appends appropriate fields to the parent. Recurses for nested structures.
    /// </summary>
    /// <param name="parent">Field that receives the value field.</param>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Read position; advanced past the value. Set to the end of the document on a token that
    /// cannot be parsed, which ends all enclosing loops (the remainder of a malformed document is skipped).</param>
    /// <param name="depth">Current nesting depth; values deeper than the maximum depth are not parsed.</param>
    private void _ParseJsonValue(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth)
    {
        // The depth check guards the recursion (stack overflow). It returns without advancing 'offset', which makes
        // the enclosing object or array loop see a non-separator and stop, so the loops always terminate.
        offset = _SkipWhitespace(span, offset);
        if (offset >= span.Length || depth > _MaxDepth)
        {
            return;
        }

        byte ch = span[offset];

        switch (ch)
        {
            case (byte)'{':
                _ParseObject(in parent, span, ref offset, depth);
                break;
            case (byte)'[':
                _ParseArray(in parent, span, ref offset, depth);
                break;
            case (byte)'"':
                string strVal = _ParseString(span, ref offset, out bool strTruncated);
                parent.Append(_ValueStringFieldId, FieldValue.NewString(strVal));
                if (strTruncated)
                {
                    parent.Append(_StringLimitFieldId, FieldValue.NewString(_StringLimitText));
                }
                break;
            case (byte)'t': // true
                if (offset + 4 <= span.Length
                    && span[offset + 1] == 'r' && span[offset + 2] == 'u' && span[offset + 3] == 'e')
                {
                    parent.Append(_ValueTrueFieldId, FieldValue.NewBool(true));
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
                    parent.Append(_ValueFalseFieldId, FieldValue.NewBool(false));
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
                    parent.Append(_ValueNullFieldId, FieldValue.NewBool(true));
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
                    string numStr = _ParseNumber(span, ref offset);
                    parent.Append(_ValueNumberFieldId, FieldValue.NewString(numStr));
                }
                else
                {
                    offset = span.Length; // Unrecognized — skip to end
                }
                break;
        }
    }

    /// <summary>
    /// Parses a JSON object and appends member fields. Lenient: it stops at the first structural problem (missing
    /// comma, non-string key) and keeps the members read so far.
    /// </summary>
    /// <param name="parent">Field that receives the object container.</param>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Read position at the opening brace; advanced past the closing brace.</param>
    /// <param name="depth">Current nesting depth.</param>
    private void _ParseObject(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth)
    {
        offset++; // Skip '{'
        MutField objField = parent.AppendWithCustomText(
            _ObjectFieldId, FieldValue.None, new LazyString("Object"));

        bool first = true;
        while (offset < span.Length)
        {
            offset = _SkipWhitespace(span, offset);
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
                    offset = _SkipWhitespace(span, offset);
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
            string key = _ParseString(span, ref offset, out bool keyTruncated);

            // Skip colon
            offset = _SkipWhitespace(span, offset);
            if (offset < span.Length && span[offset] == ':')
            {
                offset++;
            }

            // Member container — display text is "Member: <key>"; the value is a child of the member. A missing colon
            // is tolerated above, so a lenient reading of slightly malformed documents still shows the member.
            MutField memberField = objField.AppendWithCustomText(
                _MemberFieldId, FieldValue.None,
                "Member: ", key);

            memberField.Append(_KeyFieldId, FieldValue.NewString(key));
            if (keyTruncated)
            {
                memberField.Append(_StringLimitFieldId, FieldValue.NewString(_StringLimitText));
            }

            // Parse value recursively
            _ParseJsonValue(in memberField, span, ref offset, depth + 1);
        }
    }

    /// <summary>
    /// Parses a JSON array and appends element fields. Lenient in the same way as <see cref="_ParseObject"/>.
    /// </summary>
    /// <param name="parent">Field that receives the array container.</param>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Read position at the opening bracket; advanced past the closing bracket.</param>
    /// <param name="depth">Current nesting depth.</param>
    private void _ParseArray(in MutField parent, ReadOnlySpan<byte> span, ref int offset, int depth)
    {
        offset++; // Skip '['
        MutField arrField = parent.AppendWithCustomText(
            _ArrayFieldId, FieldValue.None, new LazyString("Array"));

        bool first = true;
        while (offset < span.Length)
        {
            offset = _SkipWhitespace(span, offset);
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

            _ParseJsonValue(in arrField, span, ref offset, depth + 1);
        }
    }

    /// <summary>
    /// Parses a JSON string value, handling escape sequences.
    /// Advances offset past the closing quote.
    /// </summary>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Read position at the opening quote; advanced past the closing quote (or to the end of
    /// the document for an unterminated string).</param>
    /// <param name="truncated">True when the string was longer than <c>_MaxStringLength</c> and was cut; the caller
    /// then appends the <c>json.error.string_limit</c> field so that the cut is visible.</param>
    /// <returns>The decoded string, truncated to the maximum string length.</returns>
    private static string _ParseString(ReadOnlySpan<byte> span, ref int offset, out bool truncated)
    {
        offset++; // Skip opening '"'

        // Fast path: scan for end of string without escapes. The common string has no escape, so it is decoded
        // straight from the document bytes with one allocation (the result) instead of going through the
        // escape decoder's intermediate buffer. A backslash skips the following byte so an escaped quote does not
        // end the string.
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

        // A backslash as the last byte of the document skipped one byte too many above, which left 'offset' behind the
        // end of the span. Slicing up to it would throw, and a malformed packet must never throw; the string simply
        // ends with the document (the lone backslash is kept as a character by the decoder).
        offset = Math.Min(offset, span.Length);

        // Decode. In the fast path the length cap counts bytes, so a cut inside a multi-byte UTF-8 sequence ends the
        // string with a replacement character. A cut is reported through 'truncated', never silently.
        string result;
        if (!hasEscape)
        {
            int len = Math.Min(offset - start, _MaxStringLength);
            truncated = offset - start > _MaxStringLength;
            result = Encoding.UTF8.GetString(span.Slice(start, len));
        }
        else
        {
            // Slow path: decode escape sequences
            result = _DecodeEscapedString(span[start..offset], out truncated);
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
    /// <param name="raw">The string content between the quotes, still containing escape sequences.</param>
    /// <param name="truncated">True when input was left over because the decoded string reached the maximum string length.</param>
    /// <returns>The decoded string, at most the maximum string length in chars.</returns>
    private static string _DecodeEscapedString(ReadOnlySpan<byte> raw, out bool truncated)
    {
        truncated = false;

        // Upper bound: each byte produces at most one char. Up to 512 chars (1 KB) live on the stack, which avoids a
        // heap buffer for the typical short string; larger strings use a heap array, which for the maximum of 65536
        // chars (128 KB) is above the large object heap threshold.
        int maxChars = Math.Min(raw.Length, _MaxStringLength);
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
                        // Unicode escape: \uXXXX — parse hex directly from bytes without Encoding.ASCII. Each escape
                        // yields one UTF-16 code unit, so a surrogate pair written as two escapes ends up as a
                        // valid pair. Invalid hex digits drop the escape, and a truncated escape (fewer than four
                        // bytes left) is skipped without consuming the rest.
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
                        // Unknown escape: keep the escaped byte itself (lenient; RFC 8259 would reject it).
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

                // Utf8.ToUtf16 reports "destination too small" instead of throwing (Encoding.GetChars throws when the
                // decoded run does not fit into the room left). A run longer than the room left happens when the string
                // is longer than the limit; the run is then decoded as far as whole characters fit and the string ends.
                ReadOnlySpan<byte> utf8Run = raw.Slice(runStart, i - runStart);
                int remaining = maxChars - written;
                OperationStatus status = System.Text.Unicode.Utf8.ToUtf16(
                    utf8Run, buffer.Slice(written, remaining), out _, out int charsDecoded,
                    replaceInvalidSequences: true, isFinalBlock: true);
                written += charsDecoded;
                if (status == OperationStatus.DestinationTooSmall)
                {
                    truncated = true;
                    break;
                }
            }
        }

        // Input left over after the loop means the maximum string length was reached before the string ended.
        if (i < raw.Length)
        {
            truncated = true;
        }

        return new string(buffer[..written]);
    }

    /// <summary>
    /// Parses a JSON number (integer, float, negative, scientific notation).
    /// Returns the number as a string to preserve full precision. The grammar is applied leniently (leading zeros
    /// and a bare minus sign are accepted) because only the extent of the token is needed; no numeric conversion
    /// happens, so there is no overflow to guard against.
    /// </summary>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Read position at the first character of the number; advanced past the number.</param>
    private static string _ParseNumber(ReadOnlySpan<byte> span, ref int offset)
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
    /// Skips JSON whitespace characters (space, tab, CR, LF; the complete set of RFC 8259 section 2).
    /// </summary>
    /// <param name="span">The whole document.</param>
    /// <param name="offset">Position to start at.</param>
    /// <returns>The position of the first non-whitespace byte, or the length of <paramref name="span"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _SkipWhitespace(ReadOnlySpan<byte> span, int offset)
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
