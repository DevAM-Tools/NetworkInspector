// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using IO = System.IO;

namespace NetworkInspector.Protocols;

/// <summary>
/// HTTP/1.x protocol parser (RFC 7230-7235).
/// Parses HTTP request and response messages from TCP stream data.
/// Uses lazy population for header and body details.
/// <para>Field tree structure:</para>
/// <code>
/// http: HTTP/1.1 200 OK
/// ├── http.request: 1                             [request only]
/// ├── http.request.method: "GET"                  [request only]
/// ├── http.request.uri: "/api/v1/data"            [request only]
/// ├── http.request.version: "HTTP/1.1"            [request only]
/// ├── http.response.code: 200                     [response only]
/// ├── http.response.phrase: "OK"                  [response only]
/// ├── http.response.version: "HTTP/1.1"           [response only]
/// ├── http.header: "Content-Type: application/json" (repeated)
/// │   ├── http.header.name: "Content-Type"
/// │   └── http.header.value: "application/json"
/// ├── http.content_type: "application/json"       [if present]
/// ├── http.content_length: 1234                   [if present]
/// ├── http.host: "example.com"                    [if present]
/// ├── http.user_agent: "..."                      [if present]
/// ├── http.transfer_encoding: "chunked"           [if present]
/// ├── http.content_encoding: "gzip"               [if present]
/// ├── http.upgrade: "websocket"                   [if present]
/// ├── http.connection: "Upgrade"                  [if present]
/// ├── http.payload: (body bytes)                  [if present]
/// └── http.payload.decoded: (decoded body bytes)  [if chunked/compressed]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("http", "Hypertext Transfer Protocol", Description = "HTTP/1.x (RFC 7230-7235)")]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPort80)]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPort8080)]
public sealed partial class HttpProtocol : IProtocol
{
    #region Constants

    /// <summary>Standard HTTP port.</summary>
    public const ulong TcpPort80 = 80;

    /// <summary>Common alternate HTTP port.</summary>
    public const ulong TcpPort8080 = 8080;

    /// <summary>Index group for always-present HTTP fields.</summary>
    private const string _HttpIndexGroup = "http";

    /// <summary>Protocol table name for content-type–based dispatch.</summary>
    public const string ContentTypeTableName = "http.content_type";

    /// <summary>Maximum number of headers to parse (DoS protection).</summary>
    private const int _MaxHeaders = 256;

    /// <summary>Maximum header line length in bytes (DoS protection).</summary>
    private const int _MaxLineLength = 8192;

    /// <summary>Protocol table name for HTTP Upgrade dispatch (e.g., WebSocket).</summary>
    public const string UpgradeTableName = "http.upgrade";

    /// <summary>Status code for 101 Switching Protocols.</summary>
    private const ushort _SwitchingProtocolsCode = 101;

    #endregion

    #region Protocol container

    [BytesField("http", "Hypertext Transfer Protocol", IndexGroup = _HttpIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch table — dispatches by Content-Type to sub-protocols (JSON, Text, etc.)

    [ProtocolTableString(ContentTypeTableName, "HTTP Content-Type")]
    private ProtocolTableId _ContentTypeTableId;

    #endregion

    #region Dispatch table — dispatches by Upgrade header value (e.g., "websocket")

    [ProtocolTableString(UpgradeTableName, "HTTP Upgrade")]
    private ProtocolTableId _UpgradeTableId;

    #endregion

    #region Request fields

    [BoolField("http.request", "Request", IndexGroup = "http.request")]
    private FieldId _RequestFieldId;

    [StringField("http.request.method", "Method", IndexGroup = "http.request")]
    private FieldId _RequestMethodFieldId;

    [StringField("http.request.uri", "URI", IndexGroup = "http.request")]
    private FieldId _RequestUriFieldId;

    [StringField("http.request.version", "Version", IndexGroup = "http.request")]
    private FieldId _RequestVersionFieldId;

    #endregion

    #region Response fields

    [BoolField("http.response", "Response", IndexGroup = "http.response")]
    private FieldId _ResponseFieldId;

    [U64Field("http.response.code", "Status Code", IndexGroup = "http.response")]
    private FieldId _ResponseCodeFieldId;

    [StringField("http.response.phrase", "Reason Phrase", IndexGroup = "http.response")]
    private FieldId _ResponsePhraseFieldId;

    [StringField("http.response.version", "Version", IndexGroup = "http.response")]
    private FieldId _ResponseVersionFieldId;

    #endregion

    #region Header fields

    [NoneField("http.header", "Header", IndexGroup = _HttpIndexGroup)]
    private FieldId _HeaderFieldId;

    [StringField("http.header.name", "Name", IndexGroup = _HttpIndexGroup)]
    private FieldId _HeaderNameFieldId;

    [StringField("http.header.value", "Value", IndexGroup = _HttpIndexGroup)]
    private FieldId _HeaderValueFieldId;

    #endregion

    #region Well-known header value fields

    [StringField("http.content_type_value", "Content-Type", IndexGroup = "http.content_type")]
    private FieldId _ContentTypeFieldId;

    [U64Field("http.content_length", "Content-Length", IndexGroup = "http.content_length")]
    private FieldId _ContentLengthFieldId;

    [StringField("http.host", "Host", IndexGroup = "http.host")]
    private FieldId _HostFieldId;

    [StringField("http.user_agent", "User-Agent", IndexGroup = "http.user_agent")]
    private FieldId _UserAgentFieldId;

    [StringField("http.transfer_encoding", "Transfer-Encoding", IndexGroup = "http.transfer_encoding")]
    private FieldId _TransferEncodingFieldId;

    [StringField("http.content_encoding", "Content-Encoding", IndexGroup = "http.content_encoding")]
    private FieldId _ContentEncodingFieldId;

    [StringField("http.upgrade", "Upgrade", IndexGroup = "http.upgrade")]
    private FieldId _UpgradeFieldId;

    [StringField("http.connection", "Connection", IndexGroup = "http.connection")]
    private FieldId _ConnectionFieldId;

    #endregion

    #region Payload

    [BytesField("http.payload", "Payload", IndexGroup = "http.payload")]
    private FieldId _PayloadFieldId;

    [BytesField("http.payload.decoded", "Decoded Payload", IndexGroup = "http.payload.decoded")]
    private FieldId _DecodedPayloadFieldId;

    #endregion

    #region Runtime state

    /// <summary>Lazy populator delegate for deferred field tree construction.</summary>
    private LazyPopulator _Populator = null!;

    /// <summary>Cached protocol ID for JSON sub-protocol (resolved at startup).</summary>
    private ProtocolId _JsonProtocolId;

    /// <summary>Cached protocol ID for Text sub-protocol (resolved at startup).</summary>
    private ProtocolId _TextProtocolId;

    /// <summary>
    /// Resolves sub-protocol IDs for content-type dispatch.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateHttpFields;
        _JsonProtocolId = stack.GetProtocolId("json") ?? default;
        _TextProtocolId = stack.GetProtocolId("text") ?? default;
    }

    /// <summary>
    /// Parses an HTTP/1.x message from TCP stream data.
    /// Uses lazy population for the header/body field tree — only the first line is parsed
    /// eagerly for the summary. Sub-protocol dispatch (content-type, JSON/Text fallback and the
    /// HTTP Upgrade table) is performed <b>eagerly</b> here with the real index-carrying
    /// <see cref="ParseContext"/> so dispatched sub-protocols record their index groups during
    /// the capture/index phase (the lazy populator no longer dispatches).
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlySpan<byte> span = data.Span;

        // Find the first line (request line or status line)
        int firstLineEnd = _FindLineEnd(span);
        if (firstLineEnd < 0)
        {
            return 0; // Not enough data for a complete first line — skip
        }

        ReadOnlySpan<byte> firstLine = span[..firstLineEnd];

        // Determine if this is a request or response
        bool isResponse = firstLine.StartsWith("HTTP/"u8);
        bool isRequest = !isResponse && _IsHttpMethod(firstLine);

        if (!isRequest && !isResponse)
        {
            return 0; // Not an HTTP message — let another protocol handle it
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_HttpGroupId);

        if (isRequest)
        {
            context.RecordGroupPresence(_HttpRequestGroupId);
        }
        else
        {
            context.RecordGroupPresence(_HttpResponseGroupId);
        }

        // Build summary from first line
        string firstLineText = Encoding.ASCII.GetString(firstLine);
        LazyString summary = new(firstLineText);

        // Set packet info
        parentField.SetPacketInfo(new LazyString(firstLineText));

        // Store whole data as container value for lazy population
        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, containerValue, summary, _Populator);

        // Eagerly dispatch the body to sub-protocols (content-type table, JSON/Text fallback
        // and the HTTP Upgrade table) using the real context, so the dispatched sub-protocols
        // record their index groups during the index phase (Q6: the index must be complete
        // when the packet is finalized). Dispatch targets the same container field index the
        // lazy populator builds into, so the dispatched sub-protocol nests under the HTTP
        // container exactly as before — only the timing (eager) and context (real) change.
        ParseResult dispatchResult = _DispatchHttpBody(in container, data, isResponse, in context);
        if (dispatchResult.IsError)
        {
            return dispatchResult;
        }

        return data.Length;
    }

    /// <summary>
    /// Eagerly scans the HTTP headers (without building the field tree), decodes the body
    /// (dechunk + decompress) and dispatches it to the matching sub-protocol via the
    /// content-type table, the built-in JSON/Text fallback, or — for a 101 Switching Protocols
    /// response — the HTTP Upgrade table. All dispatch uses the real <paramref name="context"/>.
    /// </summary>
    private ParseResult _DispatchHttpBody(
        in MutField container, ReadOnlyMemory<byte> httpData, bool isResponse, in ParseContext context)
    {
        ReadOnlySpan<byte> span = httpData.Span;

        int firstLineEnd = _FindLineEnd(span);
        if (firstLineEnd < 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> firstLine = span[..firstLineEnd];
        ushort statusCode = isResponse ? _ParseStatusCodeFromLine(firstLine) : (ushort)0;

        // Move past the first line + CRLF
        int offset = _SkipPastCrLf(span, firstLineEnd);

        // Scan headers for the values that drive dispatch and for the header-derived index groups —
        // no field tree is built here. This single eager walk both selects the dispatch target and
        // records exactly the header groups the populator will emit (content-consistent, no false
        // positives), so the presence index is complete when the packet is finalized.
        HttpDispatchScan scan = default;

        int headerCount = 0;
        while (offset < span.Length && headerCount < _MaxHeaders)
        {
            // Empty line marks end of headers
            if (offset < span.Length - 1 && span[offset] == (byte)'\r' && span[offset + 1] == (byte)'\n')
            {
                offset += 2;
                break;
            }
            if (offset < span.Length && span[offset] == (byte)'\n')
            {
                offset += 1;
                break;
            }

            int lineEnd = _FindLineEnd(span[offset..]);
            if (lineEnd < 0 || lineEnd > _MaxLineLength)
            {
                break; // Unterminated or excessively long header
            }

            _ScanHeaderLineForDispatch(span.Slice(offset, lineEnd), ref scan);

            offset = _SkipPastCrLf(span, offset + lineEnd);
            headerCount++;
        }

        // Record header-derived index groups before any body handling so they are present even for
        // header-only messages (no body).
        if (scan.ContentType is not null)
        {
            context.RecordGroupPresence(_HttpContent_typeGroupId);
        }
        if (scan.HasContentLength)
        {
            context.RecordGroupPresence(_HttpContent_lengthGroupId);
        }
        if (scan.HasHost)
        {
            context.RecordGroupPresence(_HttpHostGroupId);
        }
        if (scan.HasUserAgent)
        {
            context.RecordGroupPresence(_HttpUser_agentGroupId);
        }
        if (scan.HasTransferEncoding)
        {
            context.RecordGroupPresence(_HttpTransfer_encodingGroupId);
        }
        if (scan.ContentEncoding is not null)
        {
            context.RecordGroupPresence(_HttpContent_encodingGroupId);
        }
        if (scan.Upgrade is not null)
        {
            context.RecordGroupPresence(_HttpUpgradeGroupId);
        }
        if (scan.Connection is not null)
        {
            context.RecordGroupPresence(_HttpConnectionGroupId);
        }

        if (offset >= span.Length)
        {
            return 0; // No body to dispatch
        }

        ReadOnlyMemory<byte> body = httpData[offset..];

        // The populator emits http.payload for any present body.
        context.RecordGroupPresence(_HttpPayloadGroupId);

        // Dechunk / decompress to obtain the effective body for dispatch (same logic the
        // lazy populator uses for the http.payload.decoded field).
        ReadOnlyMemory<byte> effectiveBody = body;
        bool decoded = false;
        if (scan.IsChunked)
        {
            ReadOnlyMemory<byte>? dechunked = _DechunkBody(body);
            if (dechunked is not null)
            {
                effectiveBody = dechunked.Value;
                decoded = true;
            }
        }
        if (scan.ContentEncoding is not null)
        {
            ReadOnlyMemory<byte>? decompressed = _DecompressBody(effectiveBody, scan.ContentEncoding);
            if (decompressed is not null)
            {
                effectiveBody = decompressed.Value;
                decoded = true;
            }
        }

        // The populator emits http.payload.decoded only when the body was actually decoded.
        if (decoded)
        {
            context.RecordGroupPresence(_HttpPayloadDecodedGroupId);
        }

        ReadOnlyMemory<byte> dispatchBody = decoded ? effectiveBody : body;
        bool dispatched = false;
        if (scan.ContentType is not null)
        {
            // Extract base content type without parameters (e.g., "application/json" from "application/json; charset=utf-8")
            string baseType = _ExtractBaseContentType(scan.ContentType);

            ParseResult dispatchResult = container.TryCallNextProtocolString(
                _ContentTypeTableId, baseType, dispatchBody, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
            dispatched = dispatchResult.IsSuccess && dispatchResult.Value > 0;

            // Fallback: dispatch known content types to built-in protocols
            if (!dispatched)
            {
                dispatched = _TryDispatchByContentType(in container, baseType, dispatchBody, in context);
            }
        }

        // Handle HTTP 101 Switching Protocols — dispatch remaining data via upgrade table
        if (!dispatched && isResponse && statusCode == _SwitchingProtocolsCode
            && scan.Upgrade is not null
            && scan.Connection is not null
            && scan.Connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            // Dispatch the body (if any) to the upgraded protocol via http.upgrade table.
            // Use lowercase key to match registration (e.g., "websocket" per RFC 6455).
#pragma warning disable CA1308 // Normalize strings to uppercase — protocol table keys are lowercase by convention
            string upgradeKey = scan.Upgrade.Trim().ToLowerInvariant();
#pragma warning restore CA1308
            ParseResult upgradeResult = container.TryCallNextProtocolString(
                _UpgradeTableId, upgradeKey, dispatchBody, in context);
            if (upgradeResult.IsError)
            {
                return upgradeResult;
            }
        }

        return 0;
    }

    /// <summary>
    /// Extracts the HTTP status code from a status line (e.g. <c>HTTP/1.1 200 OK</c>) without
    /// building any field. Returns 0 when the line cannot be parsed.
    /// </summary>
    private static ushort _ParseStatusCodeFromLine(ReadOnlySpan<byte> line)
    {
        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace < 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> rest = line[(firstSpace + 1)..];
        int secondSpace = rest.IndexOf((byte)' ');
        ReadOnlySpan<byte> codeSpan;
        if (secondSpace < 0)
        {
            codeSpan = rest;
        }
        else
        {
            codeSpan = rest[..secondSpace];
        }

        if (_TryParseStatusCode(codeSpan, out ushort code))
        {
            return code;
        }

        return 0;
    }

    /// <summary>
    /// Scans a single HTTP header line for the values that drive sub-protocol dispatch and the
    /// header-derived index groups, without appending any field. Used by the eager dispatch path
    /// in <see cref="Parse"/>. The presence flags mirror the populator's emission conditions so the
    /// index records http.content_type / .content_length / .host / .user_agent / .transfer_encoding
    /// / .content_encoding / .upgrade / .connection exactly when the populator emits the field.
    /// </summary>
    private static void _ScanHeaderLineForDispatch(ReadOnlySpan<byte> line, ref HttpDispatchScan scan)
    {
        int colonPos = line.IndexOf((byte)':');
        if (colonPos < 0)
        {
            return; // Malformed header — skip
        }

        // Compare name bytes directly — avoids allocating one string per header (~10–20 per HTTP packet).
        // All comparisons are case-insensitive ASCII; the header names in the RFC are ASCII-only.
        ReadOnlySpan<byte> nameSpan = line[..colonPos];
        ReadOnlySpan<byte> valueSpan = line[(colonPos + 1)..];

        // Trim leading whitespace from value
        while (valueSpan.Length > 0 && valueSpan[0] == (byte)' ')
        {
            valueSpan = valueSpan[1..];
        }

        if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Content-Type"u8))
        {
            scan.ContentType = Encoding.ASCII.GetString(valueSpan);
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Content-Length"u8))
        {
            // Populator emits http.content_length only when the value parses to a non-negative long.
            // Utf8Parser avoids the intermediate string from Encoding.ASCII.GetString.
            // _FindLineEnd strips the CRLF, so valueSpan contains only the digits after leading-space trim.
            if (Utf8Parser.TryParse(valueSpan, out long len, out int consumed)
                && consumed == valueSpan.Length
                && len >= 0)
            {
                scan.HasContentLength = true;
            }
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Host"u8))
        {
            scan.HasHost = true;
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "User-Agent"u8))
        {
            scan.HasUserAgent = true;
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Transfer-Encoding"u8))
        {
            scan.HasTransferEncoding = true;
            // Byte-level substring scan avoids allocating a string just to check for "chunked".
            if (_ContainsIgnoreAsciiCase(valueSpan, "chunked"u8))
            {
                scan.IsChunked = true;
            }
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Content-Encoding"u8))
        {
            scan.ContentEncoding = Encoding.ASCII.GetString(valueSpan);
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Upgrade"u8))
        {
            scan.Upgrade = Encoding.ASCII.GetString(valueSpan);
        }
        else if (System.Text.Ascii.EqualsIgnoreCase(nameSpan, "Connection"u8))
        {
            scan.Connection = Encoding.ASCII.GetString(valueSpan);
        }
    }

    /// <summary>
    /// Mutable scratch holding the header-derived values gathered during the eager dispatch scan.
    /// Carries both the values that drive sub-protocol dispatch (content type, transfer/content
    /// encoding, upgrade, connection) and the presence flags used to record header-derived index
    /// groups, so the eager scan decides both in a single header walk.
    /// </summary>
    private struct HttpDispatchScan
    {
        public string? ContentType;
        public bool HasContentLength;
        public bool HasHost;
        public bool HasUserAgent;
        public bool HasTransferEncoding;
        public bool IsChunked;
        public string? ContentEncoding;
        public string? Upgrade;
        public string? Connection;
    }

    /// <summary>
    /// Lazily populates all HTTP fields from the stored message data.
    /// </summary>
    private ParseResult _PopulateHttpFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> httpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = httpData.Span;

        int firstLineEnd = _FindLineEnd(span);
        if (firstLineEnd < 0)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, 1, 0);
        }

        ReadOnlySpan<byte> firstLine = span[..firstLineEnd];
        bool isResponse = firstLine.StartsWith("HTTP/"u8);

        // Parse and append first line fields
        if (isResponse)
        {
            _ParseStatusLine(in container, firstLine);
        }
        else
        {
            _ParseRequestLine(in container, firstLine);
        }

        // Move past the first line + CRLF
        int offset = _SkipPastCrLf(span, firstLineEnd);

        // Parse headers
        string? contentType = null;
        long contentLength = -1;
        bool isChunked = false;
        string? contentEncoding = null;
        string? upgrade = null;
        string? connection = null;

        int headerCount = 0;
        while (offset < span.Length && headerCount < _MaxHeaders)
        {
            // Empty line marks end of headers
            if (offset < span.Length - 1 && span[offset] == (byte)'\r' && span[offset + 1] == (byte)'\n')
            {
                offset += 2;
                break;
            }
            if (offset < span.Length && span[offset] == (byte)'\n')
            {
                offset += 1;
                break;
            }

            int lineEnd = _FindLineEnd(span[offset..]);
            if (lineEnd < 0 || lineEnd > _MaxLineLength)
            {
                break; // Unterminated or excessively long header
            }

            ReadOnlySpan<byte> headerLine = span.Slice(offset, lineEnd);
            _ParseHeaderLine(in container, headerLine, ref contentType, ref contentLength,
                ref isChunked, ref contentEncoding, ref upgrade, ref connection);

            offset = _SkipPastCrLf(span, offset + lineEnd);
            headerCount++;
        }

        // Append well-known header fields
        if (contentType is not null)
        {
            container.Append(_ContentTypeFieldId, FieldValue.NewString(contentType));
        }
        if (contentLength >= 0)
        {
            container.Append(_ContentLengthFieldId, FieldValue.NewU64((ulong)contentLength));
        }

        // Handle payload/body
        if (offset < span.Length)
        {
            ReadOnlyMemory<byte> body = httpData[offset..];

            // Step 1: Dechunk if Transfer-Encoding: chunked
            ReadOnlyMemory<byte> effectiveBody = body;
            bool decoded = false;
            if (isChunked)
            {
                ReadOnlyMemory<byte>? dechunked = _DechunkBody(body);
                if (dechunked is not null)
                {
                    effectiveBody = dechunked.Value;
                    decoded = true;
                }
            }

            // Step 2: Decompress if Content-Encoding is gzip, deflate, or br
            if (contentEncoding is not null)
            {
                ReadOnlyMemory<byte>? decompressed = _DecompressBody(effectiveBody, contentEncoding);
                if (decompressed is not null)
                {
                    effectiveBody = decompressed.Value;
                    decoded = true;
                }
            }

            // Always store raw payload
            container.Append(_PayloadFieldId, FieldValue.NewBytes(body));

            // If body was decoded, store the decoded version
            if (decoded)
            {
                container.Append(_DecodedPayloadFieldId, FieldValue.NewBytes(effectiveBody));
            }

            // Sub-protocol dispatch (content-type table, JSON/Text fallback and the HTTP
            // Upgrade table) is performed eagerly in Parse() with the real index-carrying
            // context, so dispatched sub-protocols record their index groups during the index
            // phase. The lazy populator only builds the descriptive HTTP field tree.
        }

        return 0;
    }

    /// <summary>
    /// Parses an HTTP request line: METHOD SP URI SP VERSION.
    /// </summary>
    private void _ParseRequestLine(in MutField container, ReadOnlySpan<byte> line)
    {
        container.Append(_RequestFieldId, FieldValue.NewBool(true));

        // Split by spaces: METHOD URI VERSION
        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace < 0)
        {
            return;
        }

        string method = Encoding.ASCII.GetString(line[..firstSpace]);
        container.Append(_RequestMethodFieldId, FieldValue.NewString(method));

        ReadOnlySpan<byte> rest = line[(firstSpace + 1)..];
        int secondSpace = rest.IndexOf((byte)' ');
        if (secondSpace < 0)
        {
            // Just URI, no version
            string uri = Encoding.ASCII.GetString(rest);
            container.Append(_RequestUriFieldId, FieldValue.NewString(uri));
            return;
        }

        string requestUri = Encoding.ASCII.GetString(rest[..secondSpace]);
        container.Append(_RequestUriFieldId, FieldValue.NewString(requestUri));

        string version = Encoding.ASCII.GetString(rest[(secondSpace + 1)..]);
        container.Append(_RequestVersionFieldId, FieldValue.NewString(version));
    }

    /// <summary>
    /// Parses an HTTP status line: VERSION SP STATUS SP REASON and appends the response fields.
    /// </summary>
    private void _ParseStatusLine(in MutField container, ReadOnlySpan<byte> line)
    {
        container.Append(_ResponseFieldId, FieldValue.NewBool(true));

        // Split: HTTP/1.1 200 OK
        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace < 0)
        {
            return;
        }

        string version = Encoding.ASCII.GetString(line[..firstSpace]);
        container.Append(_ResponseVersionFieldId, FieldValue.NewString(version));

        ReadOnlySpan<byte> rest = line[(firstSpace + 1)..];
        int secondSpace = rest.IndexOf((byte)' ');

        if (secondSpace < 0)
        {
            // Status code only, no reason phrase
            if (_TryParseStatusCode(rest, out ushort code))
            {
                container.Append(_ResponseCodeFieldId, FieldValue.NewU64(code));
            }
            return;
        }

        if (_TryParseStatusCode(rest[..secondSpace], out ushort statusCode))
        {
            container.Append(_ResponseCodeFieldId, FieldValue.NewU64(statusCode));
        }

        string phrase = Encoding.ASCII.GetString(rest[(secondSpace + 1)..]);
        container.Append(_ResponsePhraseFieldId, FieldValue.NewString(phrase));
    }

    /// <summary>
    /// Parses a single HTTP header line and appends it to the field tree.
    /// Also extracts well-known header values.
    /// </summary>
    private void _ParseHeaderLine(
        in MutField container,
        ReadOnlySpan<byte> line,
        ref string? contentType,
        ref long contentLength,
        ref bool isChunked,
        ref string? contentEncoding,
        ref string? upgrade,
        ref string? connection)
    {
        int colonPos = line.IndexOf((byte)':');
        if (colonPos < 0)
        {
            return; // Malformed header — skip
        }

        string name = Encoding.ASCII.GetString(line[..colonPos]);
        ReadOnlySpan<byte> valueSpan = line[(colonPos + 1)..];

        // Trim leading whitespace from value
        while (valueSpan.Length > 0 && valueSpan[0] == (byte)' ')
        {
            valueSpan = valueSpan[1..];
        }

        string value = Encoding.ASCII.GetString(valueSpan);

        // Append header container with display text "Name: Value"
        MutField headerField = container.AppendWithCustomText(
            _HeaderFieldId, FieldValue.None, ZA.Lazy(name, ": ", value));
        headerField.Append(_HeaderNameFieldId, FieldValue.NewString(name));
        headerField.Append(_HeaderValueFieldId, FieldValue.NewString(value));

        // Extract well-known header values
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            contentType = value;
        }
        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long len) && len >= 0)
            {
                contentLength = len;
            }
        }
        else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_HostFieldId, FieldValue.NewString(value));
        }
        else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_UserAgentFieldId, FieldValue.NewString(value));
        }
        else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_TransferEncodingFieldId, FieldValue.NewString(value));
            if (value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                isChunked = true;
            }
        }
        else if (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_ContentEncodingFieldId, FieldValue.NewString(value));
            contentEncoding = value;
        }
        else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_UpgradeFieldId, FieldValue.NewString(value));
            upgrade = value;
        }
        else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
        {
            container.Append(_ConnectionFieldId, FieldValue.NewString(value));
            connection = value;
        }
    }

    /// <summary>
    /// Attempts to dispatch the body to a known protocol based on content type.
    /// Falls back to JSON protocol for application/json, Text protocol for text/* types.
    /// </summary>
    private bool _TryDispatchByContentType(in MutField container, string baseType, ReadOnlyMemory<byte> body, in ParseContext context)
    {
        // JSON content types
        if (baseType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            _JsonProtocolId.IsValid)
        {
            ParseResult result = container.CallProtocol(_JsonProtocolId, body, in context);
            return result.IsSuccess && result.Value > 0;
        }

        // Text content types (text/plain, text/html, text/xml, etc.)
        if (baseType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            _TextProtocolId.IsValid)
        {
            ParseResult result = container.CallProtocol(_TextProtocolId, body, in context);
            return result.IsSuccess && result.Value > 0;
        }

        return false;
    }

    /// <summary>
    /// Finds the end of the current line (position of CR or LF).
    /// Returns -1 if no line ending is found.
    /// </summary>
    private static int _FindLineEnd(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\r' || data[i] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Advances past CRLF or LF at the given position.
    /// </summary>
    private static int _SkipPastCrLf(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < data.Length && data[pos] == (byte)'\r')
        {
            pos++;
        }
        if (pos < data.Length && data[pos] == (byte)'\n')
        {
            pos++;
        }
        return pos;
    }

    /// <summary>
    /// Checks if the first line looks like an HTTP request by testing for known methods.
    /// </summary>
    private static bool _IsHttpMethod(ReadOnlySpan<byte> line)
    {
        return line.StartsWith("GET "u8) ||
               line.StartsWith("POST "u8) ||
               line.StartsWith("PUT "u8) ||
               line.StartsWith("DELETE "u8) ||
               line.StartsWith("HEAD "u8) ||
               line.StartsWith("OPTIONS "u8) ||
               line.StartsWith("PATCH "u8) ||
               line.StartsWith("CONNECT "u8) ||
               line.StartsWith("TRACE "u8);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="haystack"/> contains
    /// <paramref name="needle"/> using ASCII case-insensitive comparison.
    /// Both spans must contain only ASCII bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _ContainsIgnoreAsciiCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length > haystack.Length)
        {
            return false;
        }
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (System.Text.Ascii.EqualsIgnoreCase(haystack.Slice(i, needle.Length), needle))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses an ASCII status code from the given span.
    /// </summary>
    private static bool _TryParseStatusCode(ReadOnlySpan<byte> data, out ushort code)
    {
        code = 0;
        if (data.Length != 3)
        {
            // Try to parse whatever we have
            Span<char> chars = stackalloc char[Math.Min(data.Length, 5)];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)data[i];
            }
            return ushort.TryParse(chars, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
        }

        // Fast path for exactly 3 digits
        if (data[0] >= (byte)'1' && data[0] <= (byte)'5' &&
            data[1] >= (byte)'0' && data[1] <= (byte)'9' &&
            data[2] >= (byte)'0' && data[2] <= (byte)'9')
        {
            code = (ushort)((data[0] - '0') * 100 + (data[1] - '0') * 10 + (data[2] - '0'));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the base content type without parameters.
    /// E.g., "application/json; charset=utf-8" → "application/json".
    /// </summary>
    private static string _ExtractBaseContentType(string contentType)
    {
        int semicolonPos = contentType.IndexOf(';', StringComparison.Ordinal);
        if (semicolonPos >= 0)
        {
            return contentType[..semicolonPos].Trim();
        }
        return contentType.Trim();
    }

    /// <summary>
    /// Dechunks an HTTP chunked transfer-encoded body.
    /// Format: {hex-size}\r\n{data}\r\n ... 0\r\n\r\n
    /// Returns null if the data does not look like valid chunked encoding.
    /// </summary>
    private static ReadOnlyMemory<byte>? _DechunkBody(ReadOnlyMemory<byte> body)
    {
        ReadOnlySpan<byte> span = body.Span;

        // Pre-scan to compute total decoded size and validate structure.
        // This avoids resizing during the copy pass.
        int scanPos = 0;
        long totalSize = 0;
        while (scanPos < span.Length)
        {
            // Find end of chunk-size line
            int lineEnd = span[scanPos..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                return null; // Malformed — no CRLF after chunk size
            }

            ReadOnlySpan<byte> sizeLine = span.Slice(scanPos, lineEnd);

            // Strip optional chunk-extension (;ext=value)
            int semiPos = sizeLine.IndexOf((byte)';');
            if (semiPos >= 0)
            {
                sizeLine = sizeLine[..semiPos];
            }

            // Parse hex chunk size
            if (!_TryParseHexChunkSize(sizeLine, out int chunkSize))
            {
                return null; // Not valid hex
            }

            scanPos += lineEnd + 2; // past size line + CRLF

            if (chunkSize == 0)
            {
                break; // Terminal chunk
            }

            totalSize += chunkSize;

            // Guard against unreasonable decoded sizes (16 MB limit)
            if (totalSize > 16 * 1024 * 1024)
            {
                return null;
            }

            scanPos += chunkSize;

            // Expect CRLF after chunk data
            if (scanPos + 2 > span.Length || span[scanPos] != (byte)'\r' || span[scanPos + 1] != (byte)'\n')
            {
                return null;
            }
            scanPos += 2;
        }

        if (totalSize == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // Second pass: copy chunk data into result buffer
        byte[] result = GC.AllocateUninitializedArray<byte>((int)totalSize);
        int writePos = 0;
        int readPos = 0;
        while (readPos < span.Length)
        {
            int lineEnd = span[readPos..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                break;
            }

            ReadOnlySpan<byte> sizeLine = span.Slice(readPos, lineEnd);
            int semiPos = sizeLine.IndexOf((byte)';');
            if (semiPos >= 0)
            {
                sizeLine = sizeLine[..semiPos];
            }

            if (!_TryParseHexChunkSize(sizeLine, out int chunkSize) || chunkSize == 0)
            {
                break;
            }

            readPos += lineEnd + 2;
            span.Slice(readPos, chunkSize).CopyTo(result.AsSpan(writePos));
            writePos += chunkSize;
            readPos += chunkSize + 2; // data + CRLF
        }

        return result.AsMemory(0, writePos);
    }

    /// <summary>
    /// Parses a hexadecimal chunk size from ASCII bytes.
    /// </summary>
    private static bool _TryParseHexChunkSize(ReadOnlySpan<byte> hex, out int size)
    {
        size = 0;
        if (hex.Length == 0 || hex.Length > 8) // max 8 hex digits (32-bit)
        {
            return false;
        }

        foreach (byte b in hex)
        {
            int digit;
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                digit = b - '0';
            }
            else if (b >= (byte)'a' && b <= (byte)'f')
            {
                digit = b - 'a' + 10;
            }
            else if (b >= (byte)'A' && b <= (byte)'F')
            {
                digit = b - 'A' + 10;
            }
            else if (b == (byte)' ' || b == (byte)'\t')
            {
                continue; // Tolerate leading/trailing whitespace
            }
            else
            {
                return false;
            }

            size = (size << 4) | digit;
        }
        return true;
    }

    /// <summary>
    /// Decompresses an HTTP response body based on Content-Encoding.
    /// Supports gzip, deflate, and br (Brotli).
    /// Returns null if decompression fails or the encoding is unsupported.
    /// </summary>
    private static ReadOnlyMemory<byte>? _DecompressBody(ReadOnlyMemory<byte> body, string encoding)
    {
        if (body.Length == 0)
        {
            return null;
        }

        // Normalize encoding for comparison (use uppercase per CA1308)
        string normalizedEncoding = encoding.Trim().ToUpperInvariant();

        try
        {
            using IO.MemoryStream input = new(body.ToArray(), writable: false);
            using IO.Stream decompressor = normalizedEncoding switch
            {
                "GZIP" => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
                "DEFLATE" => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true),
                "BR" => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true),
                _ => IO.Stream.Null // Unsupported encoding — will produce empty output
            };

            // Unsupported encoding returns null
            if (ReferenceEquals(decompressor, IO.Stream.Null))
            {
                return null;
            }

            using IO.MemoryStream output = new();
            decompressor.CopyTo(output);

            return output.ToArray();
        }
        catch (IO.InvalidDataException)
        {
            return null; // Corrupted compressed data
        }
        catch (IO.IOException)
        {
            return null; // I/O error during decompression
        }
    }
    #endregion
}
