// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http;

/// <summary>
/// Finds one HTTP/1 message in a reassembled TCP byte stream.
/// <para>
/// <b>Framing</b> follows RFC 9112 section 6.3: a message without body ends at the header terminator; otherwise
/// <c>Transfer-Encoding: chunked</c> wins over <c>Content-Length</c>; a response with neither runs until the sender closes
/// the connection and is therefore reported as incomplete until the transport says the stream is closed
/// (<see cref="StreamDetectionContext.StreamClosed"/>). A request without length has no body.
/// </para>
/// <para>
/// <b>Request context:</b> the framing of a response depends on the request it answers. A response to <c>HEAD</c> carries
/// <c>Content-Length</c> but no body, and a <c>2xx</c> response to <c>CONNECT</c> ends HTTP framing for the connection
/// (the bytes that follow belong to the tunneled protocol). The detector therefore remembers, per connection and
/// direction, the kinds of the requests whose response is still due (a bounded queue per direction).
/// </para>
/// <para>
/// <b>Incremental work (performance):</b> the stream buffer calls <c>Detect</c> again with the whole contiguous prefix after
/// every accepted segment. A stateless detector would re-search the header block and re-walk every chunk from byte 0 on
/// each call, which is quadratic in the number of segments of one message (a chunked 16 MiB body in 1460-byte segments
/// means about 11 500 calls; re-walking the body each time scans roughly 96 GB). The detector keeps a cursor per stream and
/// direction instead: where the header terminator search stopped, where the next chunk-size line starts and how far a
/// partial line was already scanned. Each call then examines only the bytes that arrived since the previous one. The
/// cursor is valid because the buffer consumes bytes only when a message is reported complete (the cursor resets then),
/// and <see cref="ResetStream"/> is called whenever the buffer drops or rebuilds its prefix.
/// </para>
/// <para>
/// <b>Not HTTP:</b> bytes at a message start that cannot begin an HTTP message are reported as a complete PDU that ends at
/// the next plausible start line (or at the end of the buffer). The protocol layer labels them, so the bytes stay visible
/// instead of being dropped, and reassembly continues at the next message instead of ending for the whole connection. This
/// is also how a connection recovers after the buffer limit dropped the middle of an oversized message.
/// </para>
/// <para>
/// The size of one message is bounded by the reassembly limits (<c>MaxPduSize</c>, <c>MaxBufferSize</c>) that the
/// registration gives the stream; this class enforces no limit of its own. Every scan is incremental, so a hostile peer that
/// never terminates a line cannot make the work grow faster than the number of bytes it sends.
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe. The per-stream state is mutated by <c>Detect</c>, so one owner thread must drive
/// one instance (the stream buffer contract in <see cref="IStreamPduBoundaryDetector"/>). A stack builds its own instance
/// and parses packets on one ordered thread, which satisfies this. The stateless overload <c>Detect(ReadOnlySpan)</c> uses
/// no shared state and is safe anywhere.
/// </para>
/// </summary>
internal sealed class Http1MessageDetector : IStreamPduBoundaryDetector
{
    #region Constants

    /// <summary>
    /// How many request kinds are remembered per direction while their responses are due. Threat: a peer that sends endless
    /// requests (pipelining) without ever receiving a response would grow the queue without bound. Constant of this class
    /// (a memory bound, not a protocol fact); secure by default. Exceeding it drops the OLDEST entry, which can only
    /// mis-frame a response to a very old HEAD or CONNECT request; 64 is far above the pipelining depth real clients use.
    /// </summary>
    private const int _MaxPendingRequests = 64;

    /// <summary>
    /// Segment-start prefixes that can begin an HTTP/1 message. Used to tell "not HTTP" from "HTTP, not complete yet".
    /// Only the listed methods are known; an extension method (for example a WebDAV one) is not recognized.
    /// </summary>
    private static readonly byte[][] _MethodPrefixes =
    [
        "GET "u8.ToArray(), "POST "u8.ToArray(), "PUT "u8.ToArray(), "HEAD "u8.ToArray(), "DELETE "u8.ToArray(),
        "OPTIONS "u8.ToArray(), "PATCH "u8.ToArray(), "CONNECT "u8.ToArray(), "TRACE "u8.ToArray(),
    ];

    #endregion

    #region Types

    /// <summary>How the body of the current message is delimited.</summary>
    private enum Framing : byte
    {
        /// <summary>Header terminator not found yet.</summary>
        Unknown,

        /// <summary>No body: the message ends at the header terminator.</summary>
        HeaderOnly,

        /// <summary>The body is <c>Content-Length</c> bytes long.</summary>
        ContentLength,

        /// <summary>The body is a chunked sequence ended by a zero chunk and optional trailers.</summary>
        Chunked,

        /// <summary>The body runs until the sender closes the connection (response without length).</summary>
        UntilClose,

        /// <summary>A 101 response: everything buffered behind the header block belongs to the upgraded protocol.</summary>
        Upgraded,
    }

    /// <summary>The kind of an HTTP request that matters for framing its response.</summary>
    private enum RequestKind : byte
    {
        /// <summary>Any request whose response has the framing its headers announce.</summary>
        Other,

        /// <summary>HEAD: the response has headers but never a body.</summary>
        Head,

        /// <summary>CONNECT: a 2xx response ends HTTP framing on the connection.</summary>
        Connect,
    }

    /// <summary>
    /// Where the walk of the current message of one direction stopped. Valid until the message completes (the cursor is
    /// reset to default then) or the stream resets. A default value means "start of a new message".
    /// </summary>
    private struct FramingCursor
    {
        /// <summary>Bytes of the buffer already searched for the header terminator.</summary>
        internal int HeaderScanOffset;

        /// <summary>Whether the header block has been found and its framing decided.</summary>
        internal bool HeaderFound;

        /// <summary>End of the header block (index after the terminating empty line); valid when <see cref="HeaderFound"/>.</summary>
        internal int HeaderEnd;

        /// <summary>Body delimiting decided from the headers.</summary>
        internal Framing Kind;

        /// <summary>For <see cref="Framing.ContentLength"/>: header size plus announced body size (64-bit, no overflow).</summary>
        internal long BodyTotal;

        /// <summary>For <see cref="Framing.Chunked"/>: start of the next chunk-size line (or trailer line) not yet consumed.</summary>
        internal int ChunkOffset;

        /// <summary>For <see cref="Framing.Chunked"/>: bytes of the line at <see cref="ChunkOffset"/> already scanned for its LF.</summary>
        internal int LineScanned;

        /// <summary>For <see cref="Framing.Chunked"/>: the zero chunk was read, only trailer lines remain.</summary>
        internal bool InTrailers;

        /// <summary>This message is a successful response to CONNECT: both directions leave HTTP framing once it completes.</summary>
        internal bool TunnelOnComplete;
    }

    /// <summary>Everything the detector remembers about one stream.</summary>
    private sealed class StreamState
    {
        /// <summary>Cursor of the forward direction (lower endpoint of the normalized key).</summary>
        internal FramingCursor Forward;

        /// <summary>Cursor of the reverse direction.</summary>
        internal FramingCursor Reverse;

        /// <summary>Kinds of the requests sent by the forward direction whose responses (from reverse) are still due. Lazily created.</summary>
        internal Queue<RequestKind>? ForwardRequests;

        /// <summary>Kinds of the requests sent by the reverse direction whose responses (from forward) are still due. Lazily created.</summary>
        internal Queue<RequestKind>? ReverseRequests;

        /// <summary>A CONNECT was accepted: the stream carries a tunneled protocol and no HTTP framing applies any more.</summary>
        internal bool Tunnel;
    }

    #endregion

    #region Fields

    /// <summary>Per-stream state keyed by the stream id of the reassembly engine. Entries are removed by <see cref="ResetStream"/>.</summary>
    private readonly Dictionary<ulong, StreamState> _Streams = [];

    #endregion

    #region Detect

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        // Direct callers have no stream identity, so no request/response pairing and no close signal: a local cursor gives
        // the same framing as the stream overload for a message seen in one piece. Nothing is stored.
        FramingCursor cursor = default;
        return _Detect(data, ref cursor, stream: null, isForward: true, streamClosed: false);
    }

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data, in StreamDetectionContext context)
    {
        // Block 1: find (or create) the state of this stream and pick the cursor of the sending direction. The state is
        // created on the first call and lives until ResetStream, which the owner calls when it drops the stream.
        if (!_Streams.TryGetValue(context.StreamId, out StreamState? stream))
        {
            stream = new();
            _Streams[context.StreamId] = stream;
        }

        // Block 2: a stream that carries a tunnel after CONNECT has left HTTP. Invalid hands the stream over to the
        // owner's error path (no resync heuristic is registered), which stops buffering so later segments reach the
        // content-based protocol detection instead of being framed as HTTP.
        if (stream.Tunnel)
        {
            return PduBoundaryResult.Invalid;
        }

        ref FramingCursor cursor = ref (context.IsForward
            ? ref stream.Forward
            : ref stream.Reverse);
        return _Detect(data, ref cursor, stream, context.IsForward, context.StreamClosed);
    }

    /// <inheritdoc />
    public void ResetStream(ulong streamId)
    {
        // Idempotent: removing an unknown id does nothing. The state goes entirely, so a stream that is rebuilt after the
        // buffer limit starts with a clean cursor and an empty request queue.
        _Streams.Remove(streamId);
    }

    /// <summary>
    /// Runs the framing for one call and resets the cursor when the message is done, so the next message starts fresh.
    /// </summary>
    private static PduBoundaryResult _Detect(
        ReadOnlySpan<byte> data, ref FramingCursor cursor, StreamState? stream, bool isForward, bool streamClosed)
    {
        PduBoundaryResult result = _DetectMessage(data, ref cursor, stream, isForward, streamClosed);
        if (result.IsComplete || result.IsInvalid)
        {
            // A completed CONNECT response switches the whole stream to the tunnel before the cursor is cleared.
            if (result.IsComplete && cursor.TunnelOnComplete && stream is not null)
            {
                stream.Tunnel = true;
            }

            cursor = default;
        }

        return result;
    }

    /// <summary>Frames the message at the start of <paramref name="data"/>, resuming where <paramref name="cursor"/> stopped.</summary>
    private static PduBoundaryResult _DetectMessage(
        ReadOnlySpan<byte> data, ref FramingCursor cursor, StreamState? stream, bool isForward, bool streamClosed)
    {
        // Block 1: locate the end of the header block. Without a terminator, either more bytes are due (the buffer still
        // looks like the start of an HTTP message) or the bytes are not HTTP at all.
        if (!cursor.HeaderFound)
        {
            // Not HTTP: hand over the junk up to the next plausible start line as one PDU instead of waiting for a
            // terminator that may never come (which would stall the stream until the buffer limit is hit). The protocol
            // layer then flags the bytes, and the framing resumes at the start line.
            if (!_CouldBeHttpPrefix(data))
            {
                return PduBoundaryResult.Complete(_JunkLength(data));
            }

            if (!_TryFindHeaderEnd(data, ref cursor))
            {
                return PduBoundaryResult.Incomplete;
            }

            // Block 2: the header block is complete. Read the framing headers and pair the message with its request.
            if (!_TryDecideFraming(data, ref cursor, stream, isForward))
            {
                return PduBoundaryResult.Invalid;
            }
        }

        // Block 3: apply the framing. Each branch only looks at bytes that arrived since the previous call.
        switch (cursor.Kind)
        {
            case Framing.HeaderOnly:
                return PduBoundaryResult.Complete(cursor.HeaderEnd);

            case Framing.ContentLength:
                // The total fits an int: _DecideFraming rejected anything longer as Invalid, so the cast cannot truncate.
                return data.Length < cursor.BodyTotal
                    ? PduBoundaryResult.Incomplete
                    : PduBoundaryResult.Complete((int)cursor.BodyTotal);

            case Framing.Chunked:
                return _DetectChunked(data, ref cursor);

            case Framing.UntilClose:
                // The message ends with the connection. Until the transport signals that, no length is known; at close the
                // whole buffer is the message (the caller delivers what is buffered, so nothing is lost either way).
                return streamClosed
                    ? PduBoundaryResult.Complete(data.Length)
                    : PduBoundaryResult.Incomplete;

            case Framing.Upgraded:
                // 101 Switching Protocols: the bytes after the header block belong to the upgraded protocol and are already
                // in the same buffer, so the message is complete at whatever is buffered.
                return PduBoundaryResult.Complete(data.Length);

            default:
                return PduBoundaryResult.Invalid;
        }
    }

    #endregion

    #region Headers

    /// <summary>
    /// Searches the header terminator in the bytes that arrived since the last call. Both the standard CRLF CRLF and a bare
    /// LF LF terminator are accepted (RFC 9112 section 2.2 lets a recipient treat a bare LF as a line end). The terminator
    /// that starts earliest wins.
    /// </summary>
    /// <param name="data">The buffered prefix.</param>
    /// <param name="cursor">Cursor; on success <c>HeaderFound</c> and <c>HeaderEnd</c> are set, otherwise the scan position advances.</param>
    /// <returns><see langword="true"/> when the header block is complete.</returns>
    private static bool _TryFindHeaderEnd(ReadOnlySpan<byte> data, ref FramingCursor cursor)
    {
        // Resume three bytes before the previous end: a terminator "\r\n\r\n" may have straddled the segment boundary, and
        // three bytes of it can already have been scanned. This is what keeps the search linear over the whole message.
        int from = Math.Max(0, cursor.HeaderScanOffset - 3);
        ReadOnlySpan<byte> fresh = data[from..];

        // The u8 literals are static data and IndexOf is vectorized: no allocation and no per-byte loop here.
        int crlf = fresh.IndexOf("\r\n\r\n"u8);
        int lf = fresh.IndexOf("\n\n"u8);

        // Take the earliest terminator. Preferring CRLF CRLF unconditionally would mis-frame a header block that ends with
        // a bare LF LF when the body after it happens to contain CRLF CRLF: the body would be read as part of the headers.
        int end = -1;
        if (lf >= 0 && (crlf < 0 || lf < crlf))
        {
            end = from + lf + 2;
        }
        else if (crlf >= 0)
        {
            end = from + crlf + 4;
        }

        if (end < 0)
        {
            cursor.HeaderScanOffset = data.Length;
            return false;
        }

        cursor.HeaderFound = true;
        cursor.HeaderEnd = end;
        return true;
    }

    /// <summary>
    /// Reads the framing headers of the complete header block, pairs the message with its request or response and stores the
    /// decision in the cursor. Returns <see langword="false"/> when the framing cannot be represented.
    /// </summary>
    private static bool _TryDecideFraming(
        ReadOnlySpan<byte> data, ref FramingCursor cursor, StreamState? stream, bool isForward)
    {
        ReadOnlySpan<byte> headers = data[..cursor.HeaderEnd];
        if (!_TryReadFraming(headers, out Http1Headers info))
        {
            return false;
        }

        // Pair the message with the other direction. A request is queued for the response that will come back; a response
        // takes the oldest request of the other direction. Without stream state (direct callers) there is no pairing and
        // every response is framed by its own headers.
        RequestKind answered = RequestKind.Other;
        if (stream is not null)
        {
            if (!info.IsResponse)
            {
                _EnqueueRequest(_RequestQueue(stream, isForward), info.Kind);
            }
            else if (!info.IsInterimResponse)
            {
                // A 1xx response (except 101) is interim: the final response follows, so the request stays queued. The
                // response answers the oldest request that the OTHER direction sent.
                Queue<RequestKind>? requests = isForward
                    ? stream.ReverseRequests
                    : stream.ForwardRequests;
                if (requests is not null && requests.TryDequeue(out RequestKind dequeued))
                {
                    answered = dequeued;
                }
            }
        }

        // Precedence of RFC 9112 section 6.3: responses to HEAD and CONNECT 2xx, 1xx/204/304, then 101 (the upgraded
        // protocol follows in the same segment), then chunked, then Content-Length, then until-close for responses.
        bool connectAccepted = answered == RequestKind.Connect && info.IsResponse && info.Status is >= 200 and < 300;
        if (answered == RequestKind.Head || connectAccepted || info.NoBody)
        {
            cursor.Kind = Framing.HeaderOnly;
            cursor.TunnelOnComplete = connectAccepted;
        }
        else if (info.SwitchingProtocols)
        {
            cursor.Kind = Framing.Upgraded;
        }
        else if (info.Chunked)
        {
            cursor.Kind = Framing.Chunked;
            cursor.ChunkOffset = cursor.HeaderEnd;
        }
        else if (info.ContentLength >= 0)
        {
            // The total must fit an int (spans are int-indexed). Written as a subtraction so the check itself cannot
            // overflow; a longer body is reported as Invalid rather than clamped, so the length is never silently cut.
            if (info.ContentLength > int.MaxValue - cursor.HeaderEnd)
            {
                return false;
            }

            cursor.Kind = Framing.ContentLength;
            cursor.BodyTotal = cursor.HeaderEnd + info.ContentLength;
        }
        else
        {
            // No length and not chunked: a response runs until the connection closes; a request has no body at all.
            cursor.Kind = info.IsResponse
                ? Framing.UntilClose
                : Framing.HeaderOnly;
        }

        return true;
    }

    /// <summary>Returns the queue of requests sent by one direction, creating it on first use (most streams never need one).</summary>
    private static Queue<RequestKind> _RequestQueue(StreamState stream, bool isForward)
    {
        if (isForward)
        {
            stream.ForwardRequests ??= new();
            return stream.ForwardRequests;
        }

        stream.ReverseRequests ??= new();
        return stream.ReverseRequests;
    }

    /// <summary>Adds a request kind to a bounded queue; the oldest entry is dropped at the cap (see <see cref="_MaxPendingRequests"/>).</summary>
    private static void _EnqueueRequest(Queue<RequestKind> requests, RequestKind kind)
    {
        if (requests.Count >= _MaxPendingRequests)
        {
            _ = requests.Dequeue();
        }

        requests.Enqueue(kind);
    }

    /// <summary>What the start line and the framing headers of one message say.</summary>
    private struct Http1Headers
    {
        internal bool IsResponse;
        internal int Status;
        internal RequestKind Kind;
        internal bool NoBody;
        internal bool SwitchingProtocols;
        internal bool Chunked;
        internal long ContentLength;

        /// <summary>A 1xx response other than 101: an interim answer that does not complete its request.</summary>
        internal readonly bool IsInterimResponse => IsResponse && Status is >= 100 and < 200 && Status != 101;
    }

    /// <summary>
    /// Reads the start line and the framing headers. <c>ContentLength</c> is -1 when the header is absent.
    /// </summary>
    /// <remarks>
    /// Header names are matched ASCII case-insensitively without allocating. A repeated Content-Length keeps the last
    /// valid value (no conflict check: this is a passive analyzer, not a forwarding proxy, so it has no smuggling
    /// decision to protect, but a differing duplicate is framed by the last one). The Transfer-Encoding value is
    /// searched for the token substring "chunked", so a coding list such as "gzip, chunked" is recognized.
    /// </remarks>
    private static bool _TryReadFraming(ReadOnlySpan<byte> headers, out Http1Headers info)
    {
        info = default;
        info.ContentLength = -1;

        // The first line is the request line or status line; it decides the messages that never have a body.
        int lineEnd = _FindLineEnd(headers);
        if (lineEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> firstLine = headers[..lineEnd];
        _ReadStartLine(firstLine, ref info);

        // Walk the header lines with span slices only; the empty line that ends the block stops the walk.
        int offset = _SkipLineEnding(headers, lineEnd);
        while (offset < headers.Length)
        {
            if (headers[offset] == (byte)'\r' || headers[offset] == (byte)'\n')
            {
                break;
            }

            int next = _FindLineEnd(headers[offset..]);
            if (next < 0)
            {
                break;
            }

            ReadOnlySpan<byte> line = headers.Slice(offset, next);
            int colon = line.IndexOf((byte)':');
            if (colon > 0)
            {
                ReadOnlySpan<byte> name = line[..colon];
                ReadOnlySpan<byte> value = line[(colon + 1)..].Trim(" \t"u8);

                // Content-Length must be the whole value and non-negative; a value that does not parse completely is
                // ignored (treated as absent), never partially accepted.
                if (System.Text.Ascii.EqualsIgnoreCase(name, "Content-Length"u8)
                    && Utf8Parser.TryParse(value, out long parsed, out int consumed)
                    && consumed == value.Length
                    && parsed >= 0)
                {
                    info.ContentLength = parsed;
                }
                else if (System.Text.Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8)
                    && _ContainsIgnoreCase(value, "chunked"u8))
                {
                    info.Chunked = true;
                }
            }

            offset = _SkipLineEnding(headers, offset + next);
        }

        // A no-body message ignores any Content-Length it carries (a 304 response may announce the length of the
        // representation although no body is sent), and its Transfer-Encoding too (the status or method forbids a body).
        if (info.NoBody)
        {
            info.ContentLength = 0;
            info.Chunked = false;
        }

        return true;
    }

    /// <summary>
    /// Decides from the first line whether the message is a request or a response, which request kind it is, and whether
    /// it has no body. No body: GET and HEAD requests (their bodies have no defined meaning, so a Content-Length on them is
    /// ignored) and responses 1xx, 204 and 304 (RFC 9112 section 6.3). Status 101 is reported through
    /// <see cref="Http1Headers.SwitchingProtocols"/> instead, because the bytes after it belong to the upgraded protocol.
    /// </summary>
    private static void _ReadStartLine(ReadOnlySpan<byte> firstLine, ref Http1Headers info)
    {
        if (!firstLine.StartsWith("HTTP/"u8))
        {
            // Request line. Only HEAD and CONNECT change how the response is framed.
            if (firstLine.StartsWith("GET "u8) || firstLine.StartsWith("HEAD "u8))
            {
                info.NoBody = true;
            }

            if (firstLine.StartsWith("HEAD "u8))
            {
                info.Kind = RequestKind.Head;
            }
            else if (firstLine.StartsWith("CONNECT "u8))
            {
                info.Kind = RequestKind.Connect;
            }

            return;
        }

        info.IsResponse = true;

        // Status line: "HTTP/1.1 200 OK". The three digits after the first space are the status code.
        int firstSpace = firstLine.IndexOf((byte)' ');
        // The space plus three code bytes must all be present (index firstSpace + 3 is the last one). A shorter status line
        // such as "HTTP/1.1 20" would otherwise make the slice below throw on wire input.
        if (firstSpace < 0 || firstSpace + 4 > firstLine.Length)
        {
            return;
        }

        ReadOnlySpan<byte> code = firstLine.Slice(firstSpace + 1, 3);
        if (!Utf8Parser.TryParse(code, out int status, out int consumed) || consumed != 3)
        {
            return;
        }

        info.Status = status;
        if (status == 101)
        {
            info.SwitchingProtocols = true;
        }
        else if (status is >= 100 and < 200 || status is 204 or 304)
        {
            info.NoBody = true;
        }
    }

    #endregion

    #region Chunked body

    /// <summary>
    /// Walks the chunks of a chunked body (RFC 9112 section 7.1) from the position stored in the cursor and returns the end
    /// of the final chunk plus its trailer section. Complete chunks advance the cursor, so a later call starts at the first
    /// chunk that was not complete before.
    /// </summary>
    private static PduBoundaryResult _DetectChunked(ReadOnlySpan<byte> data, ref FramingCursor cursor)
    {
        // One iteration per chunk: "size[;extension] CRLF data CRLF". Running out of bytes at any point means the
        // message continues in a later segment, so the answer is Incomplete; a size line that is not hex is Invalid.
        while (!cursor.InTrailers)
        {
            int offset = cursor.ChunkOffset;
            if (offset >= data.Length || !_TryFindLineEnd(data, offset, ref cursor.LineScanned, out int lineEnd))
            {
                return PduBoundaryResult.Incomplete;
            }

            // Chunk extensions after ';' carry no framing information and are dropped.
            ReadOnlySpan<byte> sizeLine = data.Slice(offset, lineEnd - offset);
            int semi = sizeLine.IndexOf((byte)';');
            if (semi >= 0)
            {
                sizeLine = sizeLine[..semi];
            }

            if (!_TryParseHex(sizeLine.Trim((byte)' '), out int chunkSize))
            {
                return PduBoundaryResult.Invalid;
            }

            // A zero-size chunk ends the body; only the (possibly empty) trailer section remains.
            int afterLine = _SkipLineEnding(data, lineEnd);
            if (chunkSize == 0)
            {
                cursor.InTrailers = true;
                cursor.ChunkOffset = afterLine;
                cursor.LineScanned = 0;
                break;
            }

            // Skip the chunk data and the line ending that follows it. If the data is not fully buffered, wait. The sum is
            // formed in 64 bits: a chunk size near int.MaxValue added to the data offset would wrap to a negative index
            // and make the line-ending skip below throw on crafted input.
            long chunkEnd = (long)afterLine + chunkSize;
            if (chunkEnd >= data.Length)
            {
                return PduBoundaryResult.Incomplete;
            }

            // The chunk is committed only when its line ending is complete: a lone CR at the end of the buffer must not
            // advance the cursor, because the LF that arrives next would then look like an empty size line.
            int next = (int)chunkEnd;
            if (data[next] == (byte)'\r')
            {
                if (next + 1 >= data.Length)
                {
                    return PduBoundaryResult.Incomplete;
                }

                next++;
            }

            if (data[next] == (byte)'\n')
            {
                next++;
            }

            cursor.ChunkOffset = next;
            cursor.LineScanned = 0;
        }

        return _SkipTrailers(data, ref cursor);
    }

    /// <summary>
    /// Skips the trailer section after the last chunk: zero or more header lines, then an empty line. Returns the end
    /// of the message, or Incomplete while the terminating empty line is not buffered. Complete trailer lines advance the
    /// cursor, so a flood of tiny trailer lines is walked once, not once per segment.
    /// </summary>
    private static PduBoundaryResult _SkipTrailers(ReadOnlySpan<byte> data, ref FramingCursor cursor)
    {
        // A loop instead of recursion: the buffer may hold millions of tiny trailer lines, and one stack frame per line
        // would overflow the stack (which cannot be caught) on crafted input.
        while (true)
        {
            int offset = cursor.ChunkOffset;
            if (offset >= data.Length)
            {
                return PduBoundaryResult.Incomplete;
            }

            if (data[offset] == (byte)'\r')
            {
                // A CR needs its LF before the empty line counts as complete; a CR cut off at the buffer end waits.
                if (offset + 1 >= data.Length)
                {
                    return PduBoundaryResult.Incomplete;
                }

                if (data[offset + 1] == (byte)'\n')
                {
                    return PduBoundaryResult.Complete(offset + 2);
                }
            }

            if (data[offset] == (byte)'\n')
            {
                return PduBoundaryResult.Complete(offset + 1);
            }

            if (!_TryFindLineEnd(data, offset, ref cursor.LineScanned, out int lineEnd))
            {
                return PduBoundaryResult.Incomplete;
            }

            cursor.ChunkOffset = _SkipLineEnding(data, lineEnd);
            cursor.LineScanned = 0;
        }
    }

    #endregion

    #region Lines

    /// <summary>
    /// Returns the index where the line's content ends (before CRLF, or before a bare LF), or -1 when no LF is
    /// buffered yet, i.e. the line is still incomplete. Used on small, bounded spans (the header block).
    /// </summary>
    private static int _FindLineEnd(ReadOnlySpan<byte> data)
    {
        int lf = data.IndexOf((byte)'\n');
        if (lf < 0)
        {
            return -1;
        }

        if (lf > 0 && data[lf - 1] == (byte)'\r')
        {
            return lf - 1;
        }

        return lf;
    }

    /// <summary>
    /// Incremental form of <see cref="_FindLineEnd"/> for a line that starts at <paramref name="lineStart"/> in a growing
    /// buffer: the LF search resumes where the previous call stopped, so a line that never ends costs one pass over its bytes
    /// in total and not one pass per segment.
    /// </summary>
    /// <param name="data">The buffered prefix.</param>
    /// <param name="lineStart">Index of the first byte of the line.</param>
    /// <param name="scanned">Bytes of the line already searched; updated while the line is incomplete, reset when found.</param>
    /// <param name="lineEnd">Absolute index where the line content ends (before CRLF or a bare LF), when found.</param>
    /// <returns><see langword="true"/> when the line's LF is buffered.</returns>
    private static bool _TryFindLineEnd(ReadOnlySpan<byte> data, int lineStart, ref int scanned, out int lineEnd)
    {
        // A scan position beyond the buffer can only mean the prefix shrank under the cursor (the owner resets the detector
        // when that happens); searching from the line start is the safe answer then.
        int searchFrom = lineStart + scanned;
        if (searchFrom > data.Length)
        {
            searchFrom = lineStart;
        }

        int lf = data[searchFrom..].IndexOf((byte)'\n');
        if (lf < 0)
        {
            scanned = data.Length - lineStart;
            lineEnd = -1;
            return false;
        }

        int lfIndex = searchFrom + lf;
        lineEnd = lfIndex > lineStart && data[lfIndex - 1] == (byte)'\r'
            ? lfIndex - 1
            : lfIndex;
        scanned = 0;
        return true;
    }

    /// <summary>
    /// Returns the index after the line ending at <paramref name="lineEnd"/>: skips an optional CR and an optional LF,
    /// so CRLF, bare LF and a CR cut off at the end of the buffer are all accepted.
    /// </summary>
    private static int _SkipLineEnding(ReadOnlySpan<byte> data, int lineEnd)
    {
        if (lineEnd < data.Length && data[lineEnd] == (byte)'\r')
        {
            lineEnd++;
        }

        if (lineEnd < data.Length && data[lineEnd] == (byte)'\n')
        {
            lineEnd++;
        }

        return lineEnd;
    }

    /// <summary>
    /// Parses a chunk size (hexadecimal, no sign, no "0x" prefix) without allocating. Fails on an empty value, a
    /// non-hex character, or a value above <see cref="int.MaxValue"/>.
    /// </summary>
    private static bool _TryParseHex(ReadOnlySpan<byte> hex, out int size)
    {
        size = 0;
        if (hex.IsEmpty)
        {
            return false;
        }

        foreach (byte b in hex)
        {
            int digit;
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                digit = b - (byte)'0';
            }
            else if (b >= (byte)'a' && b <= (byte)'f')
            {
                digit = b - (byte)'a' + 10;
            }
            else if (b >= (byte)'A' && b <= (byte)'F')
            {
                digit = b - (byte)'A' + 10;
            }
            else
            {
                return false;
            }

            // Overflow guard for size * 16 + digit, written without performing the overflowing operation.
            if (size > (int.MaxValue - digit) / 16)
            {
                return false;
            }

            size = (size * 16) + digit;
        }

        return true;
    }

    /// <summary>ASCII case-insensitive substring search over bytes (no string conversion, so no allocation).</summary>
    private static bool _ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
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

    #endregion

    #region Message start

    /// <summary>
    /// Tells whether a buffer without a header terminator can still grow into an HTTP/1 message: it is empty, or it
    /// starts (so far) like "HTTP/" or like one of the common request methods followed by a space. Matching is
    /// prefix-based in both directions, so a method cut off by a segment boundary ("PO") still counts. Only the
    /// listed methods are known; an extension method (for example a WebDAV one) is not recognized here.
    /// </summary>
    private static bool _CouldBeHttpPrefix(ReadOnlySpan<byte> data)
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

        foreach (byte[] method in _MethodPrefixes)
        {
            if (_IsPrefix(data, method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the shorter of the two spans is a prefix of the longer: either the buffer is a (so far) partial
    /// method, or it already starts with the full method.
    /// </summary>
    private static bool _IsPrefix(ReadOnlySpan<byte> data, ReadOnlySpan<byte> method)
    {
        int take = Math.Min(data.Length, method.Length);
        return data[..take].SequenceEqual(method[..take]);
    }

    /// <summary>
    /// Length of the junk at the start of a buffer that cannot begin an HTTP message: the index of the next line that
    /// could begin one, or the buffer length when there is none. A candidate may be cut off by the end of the buffer (a
    /// method that is only partly buffered), in which case the junk ends in front of it and the partial line waits for more
    /// bytes. Always at least 1, so the caller makes progress.
    /// </summary>
    private static int _JunkLength(ReadOnlySpan<byte> data)
    {
        // Line starts are the bytes after an LF. Position 0 is the junk itself (the caller checked it), so the search
        // starts behind it. One pass over the junk; the junk is consumed by the caller, so it is never scanned twice.
        int searchFrom = 1;
        while (searchFrom < data.Length)
        {
            int lf = data[searchFrom..].IndexOf((byte)'\n');
            if (lf < 0)
            {
                break;
            }

            int lineStart = searchFrom + lf + 1;
            if (lineStart >= data.Length)
            {
                break;
            }

            if (_CouldBeHttpPrefix(data[lineStart..]))
            {
                return lineStart;
            }

            searchFrom = lineStart;
        }

        return data.Length;
    }

    #endregion
}
