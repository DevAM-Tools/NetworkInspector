// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using IO = System.IO;

namespace NetworkInspector.Protocols;

/// <summary>
/// HTTP/1.x protocol parser (RFC 7230-7235).
/// Parses HTTP request and response messages from TCP stream data.
/// Uses lazy population for the header and body fields of the field tree.
/// <para>
/// Design: two passes read each message. <c>Parse</c> runs eagerly for every packet and does only what the packet
/// index and sub-protocol dispatch need: the first line (packet summary), a header scan that records the index groups
/// and collects the values that drive dispatch, body decoding (dechunk, decompress) and the dispatch to the
/// content-type, JSON/Text, or Upgrade protocol. The field tree itself is built later by the lazy populator, only when
/// somebody expands the container. The populator cannot record index state (it gets no <see cref="ParseContext"/>), so
/// the eager scan has to mirror the populator's emission conditions exactly; the cost is that headers and the body
/// decode are processed twice for a message that is expanded.
/// </para>
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
/// ├── http.payload.decoded: (decoded body bytes)  [if chunked/compressed]
/// ├── http.error.header_limit: (limit text)       [if more than 256 header lines or a line over 8192 bytes]
/// └── http.error.decompress_limit: (limit text)   [if the body inflates beyond http.max_decompressed_bytes]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
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

    /// <summary>
    /// Maximum number of headers to parse (DoS protection).
    /// Value: 256. Threat: a header flood making the eager scan and the field tree arbitrarily large. Constant, not
    /// configurable (a protocol-sized safety bound, no host dependence); secure by default (fixed), because the input
    /// comes from untrusted peers. Applies identically to the eager scan and the populator, which share one header walk.
    /// Exceeding it is an expected failure: the walk stops, the remaining header lines are shown as payload bytes, and the
    /// <c>http.error.header_limit</c> field names the limit.
    /// </summary>
    private const int _MaxHeaders = 256;

    /// <summary>
    /// Maximum header line length in bytes (DoS protection).
    /// Value: 8192. Threat: an endless unterminated line making each scan cost grow with the packet. Constant, not
    /// configurable; secure by default (fixed). A longer line stops the header walk exactly like the header count limit
    /// and is reported through the <c>http.error.header_limit</c> field.
    /// </summary>
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

    [StringField("http.error.not_http", "Not HTTP", IndexGroup = "http.error")]
    private FieldId _NotHttpFieldId;

    /// <summary>Reported when the header walk stopped at a fixed limit (header count or line length).</summary>
    [StringField("http.error.header_limit", "Header Limit Reached", IndexGroup = "http.error")]
    private FieldId _HeaderLimitFieldId;

    /// <summary>Reported when decompressing the body stopped at <c>http.max_decompressed_bytes</c>.</summary>
    [StringField("http.error.decompress_limit", "Decompression Limit Reached", IndexGroup = "http.error")]
    private FieldId _DecompressLimitFieldId;

    #endregion

    #region Settings

    // Limit: largest decompressed HTTP body in bytes (Content-Encoding gzip, deflate, br).
    // Value: default 67108864 (64 MiB), allowed 1024 (1 KiB) to 137438953472 (128 GiB); a value outside the range is rejected by
    // the settings layer and the default stays. Threat: decompression bomb, a few kilobytes expanding to gigabytes of memory.
    // Configurable (setting, changed by the operator or user), not a constant, because the right size depends on the host and
    // on the traffic; it is not a protocol fact. Posture: secure by default, because HTTP bodies come from untrusted peers;
    // raising it is opt-in. Rejected alternative: a fixed constant (cripples hosts that inspect large downloads).
    // Exceeding it is an expected failure: inflating stops at the limit, the body stays undecoded (never truncated) and the
    // http.error.decompress_limit field names this setting. Residual risk: a value near the maximum lets one body use that
    // much memory (and time); in addition one array holds the result, so about 2 GiB is the hard bound whatever is configured.
    [U64Setting("http.max_decompressed_bytes", "Maximum decompressed HTTP body size (bytes)", "http",
        Default = 67108864, HasMin = true, Min = 1024, HasMax = true, Max = 137438953472,
        Description = "Upper bound for a decompressed HTTP body in bytes (1 KiB to 128 GiB). A larger body is left undecoded and flagged. "
            + "Raising the value lets a small hostile body use more memory.")]
    private ulong _MaxDecompressedBytes;

    #endregion

    #region Runtime state

    /// <summary>
    /// Lazy populator delegate for deferred field tree construction. Created once at stack start: converting the
    /// instance method <c>_PopulateHttpFields</c> per packet would allocate a delegate on every parse.
    /// </summary>
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
    /// Uses lazy population for the header/body field tree. Eagerly it parses the first line for the summary,
    /// scans the headers for index groups and dispatch values, and decodes the body. Sub-protocol dispatch
    /// (content-type, JSON/Text fallback and the HTTP Upgrade table) is performed <b>eagerly</b> here with the real
    /// index-carrying <see cref="ParseContext"/> so dispatched sub-protocols record their index groups during
    /// the capture/index phase (the lazy populator no longer dispatches).
    /// </summary>
    /// <param name="parentField">The parent field (the TCP layer) that receives the HTTP container.</param>
    /// <param name="data">One reassembled HTTP/1 message, or a TCP payload that may not be HTTP.</param>
    /// <param name="context">The parse context used to record presence and to dispatch sub-protocols.</param>
    /// <returns>
    /// The number of bytes consumed (all of <paramref name="data"/>); an insufficient-data error when the buffer
    /// has no complete first line but could still become an HTTP start line; or a dispatch error from a sub-protocol.
    /// A payload that is not HTTP is consumed whole and flagged with the <c>http.error.not_http</c> field.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlySpan<byte> span = data.Span;

        // Find the first line (request line or status line). Without a line end the bytes either still look like the
        // start of an HTTP message (ask for more data) or are not HTTP (flagged below, consumed whole).
        int firstLineEnd = _FindLineEnd(span);
        if (firstLineEnd < 0)
        {
            if (_CouldBeHttpStart(span))
            {
                return ParseError.InsufficientDataWithInfo(ProtocolName, 1, (ulong)data.Length);
            }

            context.RecordProtocolPresence(_ProtocolId);
            context.RecordGroupPresence(_HttpErrorGroupId);
            parentField.Append(_NotHttpFieldId, FieldValue.NewString("Payload is not an HTTP/1 start line"));
            return data.Length;
        }

        ReadOnlySpan<byte> firstLine = span[..firstLineEnd];

        // Determine if this is a request or response
        bool isResponse = firstLine.StartsWith("HTTP/"u8);
        bool isRequest = !isResponse && _IsHttpMethod(firstLine);

        if (!isRequest && !isResponse)
        {
            context.RecordProtocolPresence(_ProtocolId);
            context.RecordGroupPresence(_HttpErrorGroupId);
            parentField.Append(_NotHttpFieldId, FieldValue.NewString("Payload is not an HTTP/1 start line"));
            return data.Length;
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

        // Build summary from first line. Non-ASCII bytes become '?' (the line is display text only). The first line
        // is not length-limited here; it is bounded by the size of the message handed in.
        string firstLineText = Encoding.ASCII.GetString(firstLine);
        LazyString summary = new(firstLineText);

        // Set packet info
        parentField.SetPacketInfo(new LazyString(firstLineText));

        // Store whole data as container value for lazy population: the container holds a view of the segment bytes
        // (no copy) and the populator re-reads them when the tree is expanded.
        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, containerValue, summary, _Populator);

        // Eagerly dispatch the body to sub-protocols (content-type table, JSON/Text fallback
        // and the HTTP Upgrade table) using the real context, so the dispatched sub-protocols
        // record their index groups during the index phase (the index must be complete
        // when the packet is finalized). Dispatch targets the same container field index the
        // lazy populator builds into, so the dispatched sub-protocol nests under the HTTP
        // container exactly as before — only the timing (eager) and context (real) change.
        ParseResult dispatchResult = _DispatchHttpBody(in container, data, isResponse, in context);
        if (dispatchResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        return data.Length;
    }

    /// <summary>
    /// Eagerly scans the HTTP headers (without building the field tree), decodes the body
    /// (dechunk + decompress) and dispatches it to the matching sub-protocol via the
    /// content-type table, the built-in JSON/Text fallback, or — for a 101 Switching Protocols
    /// response — the HTTP Upgrade table. All dispatch uses the real <paramref name="context"/>.
    /// </summary>
    /// <param name="container">The HTTP container field under which dispatched sub-protocols nest.</param>
    /// <param name="httpData">The whole HTTP message (start line, headers, body).</param>
    /// <param name="isResponse"><see langword="true"/> for a status line, <see langword="false"/> for a request line.</param>
    /// <param name="context">The parse context used to record index groups and to dispatch.</param>
    /// <returns>0 when nothing failed (whether or not a sub-protocol took the body), or the sub-protocol's error.</returns>
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

        // This loop must stop at exactly the same header as the loop in _PopulateHttpFields. Both take their steps from
        // _NextHeaderStep, so the limits and the end-of-headers test cannot drift apart (the recorded index groups and the
        // materialized fields would diverge). When a limit stops the walk, offset stays inside the header block and the
        // remaining bytes count as body.
        int headerCount = 0;
        _HeaderStep step;
        while ((step = _NextHeaderStep(span, ref offset, headerCount, out ReadOnlySpan<byte> headerLine)) == _HeaderStep.Line)
        {
            _ScanHeaderLineForDispatch(headerLine, ref scan);
            headerCount++;
        }

        // The populator emits http.error.header_limit exactly when the walk stopped at a limit.
        if (step is _HeaderStep.TooManyHeaders or _HeaderStep.LineTooLong)
        {
            context.RecordGroupPresence(_HttpErrorGroupId);
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
        // lazy populator uses for the http.payload.decoded field). The order is fixed: the transfer coding (chunked)
        // is removed first, then the content coding (gzip, deflate, br) of the payload.
        ReadOnlyMemory<byte> effectiveBody = body;
        bool decoded = false;
        if (scan.IsChunked)
        {
            ReadOnlyMemory<byte>? dechunked = DechunkBody(body);
            if (dechunked is not null)
            {
                effectiveBody = dechunked.Value;
                decoded = true;
            }
        }
        if (scan.ContentEncoding is not null)
        {
            DecompressionStatus status = _DecompressBody(effectiveBody, scan.ContentEncoding, out ReadOnlyMemory<byte> decompressed);
            if (status == DecompressionStatus.Succeeded)
            {
                effectiveBody = decompressed;
                decoded = true;
            }
            else if (status == DecompressionStatus.LimitExceeded)
            {
                // The populator reports http.error.decompress_limit for the same body; the body stays undecoded.
                context.RecordGroupPresence(_HttpErrorGroupId);
            }
        }

        // The populator emits http.payload.decoded only when the body was actually decoded.
        if (decoded)
        {
            context.RecordGroupPresence(_HttpPayloadDecodedGroupId);
        }

        // Dispatch order: the content-type table first, then the built-in JSON/Text fallback, and for a 101 response
        // the Upgrade table. A sub-protocol "took" the body only if it consumed at least one byte.
        ReadOnlyMemory<byte> dispatchBody = decoded ? effectiveBody : body;
        bool dispatched = false;
        if (scan.ContentType is not null)
        {
            // Extract base content type without parameters (e.g., "application/json" from "application/json; charset=utf-8")
            string baseType = _ExtractBaseContentType(scan.ContentType);

            ParseResult dispatchResult = container.TryCallNextProtocolString(
                _ContentTypeTableId, baseType, dispatchBody, in context);
            if (dispatchResult.TryPropagateError(out ParseResult error))
            {
                return error;
            }
            dispatched = dispatchResult.TryGetConsumed(out int consumed) && consumed > 0;

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
            if (upgradeResult.TryPropagateError(out ParseResult upgradeError))
            {
                return upgradeError;
            }
        }

        return 0;
    }

    /// <summary>
    /// Extracts the HTTP status code from a status line (e.g. <c>HTTP/1.1 200 OK</c>) without
    /// building any field. Returns 0 when the line cannot be parsed.
    /// </summary>
    /// <param name="line">The status line without its line ending.</param>
    /// <returns>The status code, or 0 when it cannot be parsed (0 never matches the 101 check).</returns>
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
    /// in <see cref="IProtocol.Parse"/>. The presence flags mirror the populator's emission conditions so the
    /// index records http.content_type / .content_length / .host / .user_agent / .transfer_encoding
    /// / .content_encoding / .upgrade / .connection exactly when the populator emits the field.
    /// </summary>
    /// <param name="line">One header line without its line ending.</param>
    /// <param name="scan">The scratch that collects the values; a later header of the same name overwrites an earlier one.</param>
    /// <remarks>
    /// Must stay in step with <c>_ParseHeaderLine</c>. Content-Length is read by the same helper
    /// (<c>_TryParseContentLength</c>) in both, so the index group and the materialized field always agree.
    /// </remarks>
    private static void _ScanHeaderLineForDispatch(ReadOnlySpan<byte> line, ref HttpDispatchScan scan)
    {
        int colonPos = line.IndexOf((byte)':');
        if (colonPos < 0)
        {
            return; // Malformed header — skip
        }

        // Compare name bytes directly — avoids allocating one string per header (~10–20 per HTTP packet), which the
        // eager path would otherwise pay for every packet even when nobody expands the tree. Only values that drive
        // dispatch (content type, encoding, upgrade, connection) are turned into strings.
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
            // Populator emits http.content_length only when the value parses to a non-negative long. The shared helper
            // keeps both sides on one definition of "valid" (the index group must not disagree with the field).
            if (_TryParseContentLength(valueSpan, out _))
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
    /// A mutable struct passed by <see langword="ref"/>: it lives on the stack of <c>_DispatchHttpBody</c>, so the scan
    /// allocates no scratch object; it must never be copied while it is being filled.
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
    /// <param name="container">The lazy HTTP container whose value holds the whole message bytes.</param>
    /// <returns>0 on success (not significant for a populator), or an error for a non-bytes or line-less container.</returns>
    /// <remarks>
    /// Runs when the container is expanded, on the expanding thread. It reads only the immutable message bytes and
    /// instance state that is read-only after start, so it needs no synchronization.
    /// </remarks>
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

        // Parse headers. The walk, its limits, and its end-of-headers test must match _DispatchHttpBody exactly (see there).
        string? contentType = null;
        long contentLength = -1;
        bool isChunked = false;
        string? contentEncoding = null;
        string? upgrade = null;
        string? connection = null;

        int headerCount = 0;
        _HeaderStep step;
        while ((step = _NextHeaderStep(span, ref offset, headerCount, out ReadOnlySpan<byte> headerLine)) == _HeaderStep.Line)
        {
            _ParseHeaderLine(in container, headerLine, ref contentType, ref contentLength,
                ref isChunked, ref contentEncoding, ref upgrade, ref connection);
            headerCount++;
        }

        // A fixed limit ended the walk: say so instead of silently showing the rest of the header block as payload.
        if (step == _HeaderStep.TooManyHeaders)
        {
            string limitText = string.Create(
                CultureInfo.InvariantCulture,
                $"More than {_MaxHeaders} header lines; header parsing stopped, the remaining lines are shown as payload");
            container.Append(_HeaderLimitFieldId, FieldValue.NewString(limitText));
        }
        else if (step == _HeaderStep.LineTooLong)
        {
            string limitText = string.Create(
                CultureInfo.InvariantCulture,
                $"A header line is longer than {_MaxLineLength} bytes; header parsing stopped, the remaining lines are shown as payload");
            container.Append(_HeaderLimitFieldId, FieldValue.NewString(limitText));
        }

        // Append well-known header fields that are derived from the whole header block (the others, such as Host, are
        // appended while their header line is parsed)
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
                ReadOnlyMemory<byte>? dechunked = DechunkBody(body);
                if (dechunked is not null)
                {
                    effectiveBody = dechunked.Value;
                    decoded = true;
                }
            }

            // Step 2: Decompress if Content-Encoding is gzip, deflate, or br
            if (contentEncoding is not null)
            {
                DecompressionStatus status = _DecompressBody(effectiveBody, contentEncoding, out ReadOnlyMemory<byte> decompressed);
                if (status == DecompressionStatus.Succeeded)
                {
                    effectiveBody = decompressed;
                    decoded = true;
                }
                else if (status == DecompressionStatus.LimitExceeded)
                {
                    // Name the setting so the user knows which limit to raise; the body stays undecoded, never truncated. The
                    // effective bound is shown: one array holds the result, so it cannot exceed Array.MaxLength.
                    ulong effectiveLimit = Math.Min(_MaxDecompressedBytes, (ulong)Array.MaxLength);
                    string limitText = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Decompressed body is larger than {effectiveLimit} bytes (setting http.max_decompressed_bytes); not decoded");
                    container.Append(_DecompressLimitFieldId, FieldValue.NewString(limitText));
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
            _HeaderFieldId, FieldValue.None, name, ": ", value);
        headerField.Append(_HeaderNameFieldId, FieldValue.NewString(name));
        headerField.Append(_HeaderValueFieldId, FieldValue.NewString(value));

        // Extract well-known header values
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            contentType = value;
        }
        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            // Same parse as the eager scan, otherwise a value with trailing blanks would be shown here but not indexed.
            if (_TryParseContentLength(valueSpan, out long len))
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
    /// <param name="container">The HTTP container field under which the sub-protocol nests.</param>
    /// <param name="baseType">The content type without parameters.</param>
    /// <param name="body">The decoded body, or the raw body when nothing was decoded.</param>
    /// <param name="context">The parse context used to dispatch.</param>
    /// <returns><see langword="true"/> when the sub-protocol consumed at least one byte.</returns>
    private bool _TryDispatchByContentType(in MutField container, string baseType, ReadOnlyMemory<byte> body, in ParseContext context)
    {
        // JSON content types
        if (baseType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            _JsonProtocolId.IsValid)
        {
            ParseResult result = container.CallProtocol(_JsonProtocolId, body, in context);
            return result.TryGetConsumed(out int jsonConsumed) && jsonConsumed > 0;
        }

        // Text content types (text/plain, text/html, text/xml, etc.)
        if (baseType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            _TextProtocolId.IsValid)
        {
            ParseResult result = container.CallProtocol(_TextProtocolId, body, in context);
            return result.TryGetConsumed(out int textConsumed) && textConsumed > 0;
        }

        return false;
    }

    /// <summary>
    /// Finds the end of the current line (position of CR or LF).
    /// Returns -1 if no line ending is found.
    /// </summary>
    /// <remarks>
    /// Unlike the stream detector, this stops at the first CR or LF, so a bare CR also ends a line. The detector and this
    /// parser agree on every well-formed message; they can differ on a message with a bare CR.
    /// </remarks>
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

    /// <summary>True when <paramref name="data"/> is a prefix of an HTTP start line and may still grow.</summary>
    private static bool _CouldBeHttpStart(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        ReadOnlySpan<byte> http = "HTTP/"u8;
        int httpTake = Math.Min(data.Length, http.Length);
        if (System.Text.Ascii.EqualsIgnoreCase(data[..httpTake], http[..httpTake]))
        {
            return true;
        }

        return _IsPrefixOf(data, "GET "u8)
            || _IsPrefixOf(data, "POST "u8)
            || _IsPrefixOf(data, "PUT "u8)
            || _IsPrefixOf(data, "HEAD "u8)
            || _IsPrefixOf(data, "DELETE "u8)
            || _IsPrefixOf(data, "OPTIONS "u8)
            || _IsPrefixOf(data, "PATCH "u8)
            || _IsPrefixOf(data, "CONNECT "u8)
            || _IsPrefixOf(data, "TRACE "u8);
    }

    /// <summary>
    /// True when the shorter of the two spans is a prefix of the longer: the buffer may be a partial token (a method cut
    /// off by a segment boundary), or it already starts with the whole token.
    /// </summary>
    private static bool _IsPrefixOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> token)
    {
        int take = Math.Min(data.Length, token.Length);
        return data[..take].SequenceEqual(token[..take]);
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
    /// <param name="data">The status code text (normally exactly three digits).</param>
    /// <param name="code">The parsed code, or 0 on failure.</param>
    /// <returns>
    /// <see langword="true"/> when the text is a valid code: three digits starting with 1-5 (the fast path), or 1 to 5
    /// digits with a value up to 65535 (a non-standard code such as "20" or "1000"). Longer tokens and non-digits fail.
    /// </returns>
    private static bool _TryParseStatusCode(ReadOnlySpan<byte> data, out ushort code)
    {
        code = 0;
        if (data.Length != 3)
        {
            // A non-standard code such as "20" or "1000": digits only, at most 5 of them (the longest ushort, "65535"). A
            // longer token is rejected as a whole; cutting it to its first 5 digits would show a code that is not on the
            // wire. Parsed by hand, so there is no buffer and no sign or blank handling that a text parser would add.
            if (data.Length == 0 || data.Length > 5)
            {
                return false;
            }

            int value = 0;
            foreach (byte digit in data)
            {
                if (digit < (byte)'0' || digit > (byte)'9')
                {
                    return false;
                }

                value = (value * 10) + (digit - '0');
            }

            if (value > ushort.MaxValue)
            {
                return false;
            }

            code = (ushort)value;
            return true;
        }

        // Fast path for exactly 3 digits: the standard case, parsed without going through ushort.TryParse.
        // The first digit is limited to 1-5 (the status code classes of RFC 7231).
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
    /// <param name="body">The bytes after the header block.</param>
    /// <returns>
    /// The concatenated chunk data (an empty memory for a body of only the terminal chunk), or <see langword="null"/>
    /// when the structure is invalid (including a body cut off inside a chunk) or the decoded size exceeds 16 MiB.
    /// A body that ends cleanly after a complete chunk, without the terminal chunk, is decoded as far as it goes.
    /// A null result makes the caller show the raw body.
    /// </returns>
    /// <remarks>
    /// Limit: the decoded size is capped at 16 MiB. Threat: memory exhaustion by a chunked body that is decoded into
    /// a second copy. Constant, not configurable; secure by default (fixed). A body above the cap is reported as
    /// "not decoded" (null), never truncated. Trailers after the terminal chunk are ignored.
    /// </remarks>
    internal static ReadOnlyMemory<byte>? DechunkBody(ReadOnlyMemory<byte> body)
    {
        ReadOnlySpan<byte> span = body.Span;

        // Pre-scan to compute total decoded size and validate structure.
        // This avoids resizing during the copy pass. The second pass relies on this validation: every chunk
        // that it copies was already checked to lie completely inside the body.
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

        // Second pass: copy chunk data into result buffer. The array is not zeroed first because every byte that is
        // returned is written by the copy below: the result is cut to writePos, so uninitialized tail bytes (if the
        // loop stopped early) are never exposed.
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
    /// <param name="hex">The size text of a chunk-size line (without extensions). Spaces and tabs anywhere in it are skipped.</param>
    /// <param name="size">The parsed size, or 0 on failure.</param>
    /// <returns>
    /// <see langword="true"/> when the text has 1 to 8 characters, all hex digits or blanks, and the value fits an
    /// <see cref="int"/> (at most 0x7FFFFFFF).
    /// </returns>
    /// <remarks>
    /// The length limit counts blanks as well. A value above <see cref="int.MaxValue"/> is rejected: wrapping it to a
    /// negative size would make the callers slice with a negative length.
    /// </remarks>
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

            // Reject before shifting: eight digits can reach 0xFFFFFFFF, which does not fit the int result. Written without
            // performing the overflowing operation.
            if (size > (int.MaxValue - digit) / 16)
            {
                size = 0;
                return false;
            }

            size = (size << 4) | digit;
        }
        return true;
    }

    /// <summary>
    /// Parses a Content-Length header value: optional blanks around a non-negative whole number.
    /// </summary>
    /// <param name="value">The header value after the colon (leading blanks may already be removed).</param>
    /// <param name="length">The parsed length, or 0 on failure.</param>
    /// <returns><see langword="true"/> when the whole value (blanks aside) is a non-negative number that fits a <see cref="long"/>.</returns>
    /// <remarks>
    /// One definition for the eager scan and the populator. Surrounding spaces and tabs (the optional whitespace of
    /// RFC 9110 section 5.6.3) are ignored; any other trailing byte makes the value invalid, never partially accepted.
    /// </remarks>
    private static bool _TryParseContentLength(ReadOnlySpan<byte> value, out long length)
    {
        ReadOnlySpan<byte> trimmed = value.Trim(" \t"u8);
        if (Utf8Parser.TryParse(trimmed, out length, out int consumed)
            && consumed == trimmed.Length
            && length >= 0)
        {
            return true;
        }

        length = 0;
        return false;
    }

    /// <summary>One step of the shared header walk (see <see cref="_NextHeaderStep"/>).</summary>
    private enum _HeaderStep
    {
        /// <summary>A header line was read.</summary>
        Line,

        /// <summary>The empty line (or the end of the data) ended the header block.</summary>
        End,

        /// <summary>The next line has no line ending in the data.</summary>
        Unterminated,

        /// <summary>A further header would exceed <see cref="_MaxHeaders"/>.</summary>
        TooManyHeaders,

        /// <summary>The next header line is longer than <see cref="_MaxLineLength"/>.</summary>
        LineTooLong
    }

    /// <summary>
    /// Reads the next header line of the block. The eager scan and the populator both walk the headers through this one
    /// method, so they stop at exactly the same line.
    /// </summary>
    /// <param name="span">The whole message.</param>
    /// <param name="offset">The position of the next header line; advanced past the line (or past the empty line that ends the block).</param>
    /// <param name="headerCount">How many header lines were read so far.</param>
    /// <param name="line">The header line without its line ending, for <see cref="_HeaderStep.Line"/>.</param>
    /// <returns>What was found at <paramref name="offset"/>; <paramref name="offset"/> is unchanged for the limit and unterminated results.</returns>
    private static _HeaderStep _NextHeaderStep(ReadOnlySpan<byte> span, ref int offset, int headerCount, out ReadOnlySpan<byte> line)
    {
        line = default;
        if (offset >= span.Length)
        {
            return _HeaderStep.End;
        }

        // An empty line marks the end of the headers. It is checked before the count limit, so a block of exactly
        // _MaxHeaders headers still ends cleanly instead of leaving its empty line to be shown as body.
        if (offset < span.Length - 1 && span[offset] == (byte)'\r' && span[offset + 1] == (byte)'\n')
        {
            offset += 2;
            return _HeaderStep.End;
        }

        if (span[offset] == (byte)'\n')
        {
            offset += 1;
            return _HeaderStep.End;
        }

        if (headerCount >= _MaxHeaders)
        {
            return _HeaderStep.TooManyHeaders;
        }

        int lineEnd = _FindLineEnd(span[offset..]);
        if (lineEnd < 0)
        {
            return _HeaderStep.Unterminated;
        }

        if (lineEnd > _MaxLineLength)
        {
            return _HeaderStep.LineTooLong;
        }

        line = span.Slice(offset, lineEnd);
        offset = _SkipPastCrLf(span, offset + lineEnd);
        return _HeaderStep.Line;
    }

    /// <summary>
    /// Decompresses an HTTP body based on Content-Encoding.
    /// Supports gzip, deflate, and br (Brotli).
    /// </summary>
    /// <param name="body">The (dechunked) compressed body.</param>
    /// <param name="encoding">The Content-Encoding value; a single coding, matched case-insensitively. A list such as "gzip, br" is unsupported.</param>
    /// <param name="decompressed">The decompressed bytes on success; empty otherwise.</param>
    /// <returns>
    /// <see cref="DecompressionStatus.Succeeded"/>; <see cref="DecompressionStatus.LimitExceeded"/> when the decompressed
    /// size is above <c>http.max_decompressed_bytes</c> (inflating stopped there, nothing is truncated or returned);
    /// <see cref="DecompressionStatus.Failed"/> for an empty body, an unsupported coding or corrupt data.
    /// </returns>
    /// <remarks>
    /// Security: the decompressed size is bounded by the setting (see <see cref="_MaxDecompressedBytes"/> for the full limit
    /// record) and inflated through a bounded read buffer, so a decompression bomb costs at most the limit. The compressed
    /// body is read in place when it is backed by an array. It is still inflated twice for a message that is expanded: once
    /// for the eager dispatch in <c>Parse</c> and again when the field tree is built, because the populator has no place
    /// to keep the first result without retaining a decoded body per packet.
    /// </remarks>
    private DecompressionStatus _DecompressBody(ReadOnlyMemory<byte> body, string encoding, out ReadOnlyMemory<byte> decompressed)
    {
        decompressed = default;
        if (body.Length == 0)
        {
            return DecompressionStatus.Failed;
        }

        // Normalize encoding for comparison (use uppercase per CA1308)
        string normalizedEncoding = encoding.Trim().ToUpperInvariant();
        if (normalizedEncoding is not ("GZIP" or "DEFLATE" or "BR"))
        {
            return DecompressionStatus.Failed;
        }

        // The stream wrappers read from a stream, not a memory. Read the packet memory in place when it is array backed
        // (always, for captured frames); the copy is only the fallback for other memory kinds.
        byte[] array;
        int start;
        int count;
        if (MemoryMarshal.TryGetArray(body, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            array = segment.Array;
            start = segment.Offset;
            count = segment.Count;
        }
        else
        {
            array = body.ToArray();
            start = 0;
            count = array.Length;
        }

        using IO.MemoryStream input = new(array, start, count, writable: false);
        using IO.Stream decompressor = normalizedEncoding switch
        {
            "GZIP" => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            "DEFLATE" => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true),
            _ => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true)
        };

        return BoundedDecompressor.TryDecompress(decompressor, _MaxDecompressedBytes, out decompressed);
    }
    #endregion
}
