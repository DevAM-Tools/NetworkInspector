// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http;

/// <summary>
/// Finds one HTTP/1 message in a reassembled TCP byte stream.
/// A request or response without <c>Content-Length</c> and without chunked encoding completes at the
/// header terminator. A body that runs until the peer FIN is not framed here.
/// <para>
/// Design: the detector is stateless. <c>Detect</c> is called again with the whole buffered contiguous prefix every
/// time more bytes arrive, so it re-scans the header block (and, for chunked bodies, every chunk) from the start
/// instead of remembering a position; this keeps the detector free of per-stream state and trivially safe for
/// concurrent streams. The cost is quadratic in the number of segments of one very large message. Because it has no
/// memory of the request, a response to a <c>HEAD</c> request that carries <c>Content-Length</c> is framed as if the
/// body followed. The size of one message is bounded by the reassembly limits (<c>MaxPduSize</c>,
/// <c>MaxBufferSize</c>) that the registration gives the stream; this class enforces no limit of its own.
/// </para>
/// <para><b>Thread-safety:</b> stateless. <see cref="ResetStream"/> is a no-op.</para>
/// </summary>
internal sealed class Http1MessageDetector : IStreamPduBoundaryDetector
{
    #region Detect

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data) => Detect(data, default);

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data, in StreamDetectionContext context)
    {
        // Block 1: locate the end of the header block. Without a terminator, either more bytes are due (the buffer still
        // looks like the start of an HTTP message) or the bytes are not HTTP at all.
        int headerEnd = _FindHeaderEnd(data);
        if (headerEnd < 0)
        {
            // Not HTTP: hand the whole buffer over as one PDU instead of waiting for a terminator that may never come,
            // which would stall the stream until the buffer limit is hit. The protocol layer then flags the bytes.
            if (!_CouldBeHttpPrefix(data))
            {
                return PduBoundaryResult.Complete(data.Length);
            }

            return PduBoundaryResult.Incomplete;
        }

        // Block 2: read the framing headers. Invalid is the contract answer for a header block without a first line;
        // it cannot occur once a terminator was found, but keeps the result total.
        if (!_TryReadFraming(data[..headerEnd], out bool chunked, out long contentLength, out bool noBody, out bool switchingProtocols))
        {
            return PduBoundaryResult.Invalid;
        }

        // Block 3: pick the framing in the precedence of RFC 9112 section 6.3. Status 101, then "no body" messages,
        // then Transfer-Encoding: chunked (which wins over Content-Length), then Content-Length, then header block only.

        // 101 carries the upgraded protocol in the same segment. Keep those bytes in this PDU.
        if (switchingProtocols)
        {
            return PduBoundaryResult.Complete(data.Length);
        }

        if (noBody)
        {
            return PduBoundaryResult.Complete(headerEnd);
        }

        if (chunked)
        {
            return _DetectChunked(data, headerEnd);
        }

        if (contentLength < 0)
        {
            // No length and not chunked: the message is the header block (connection-close bodies wait for a later FIN signal).
            return PduBoundaryResult.Complete(headerEnd);
        }

        // The total must fit an int (spans are int-indexed). Written as a subtraction so the check itself cannot
        // overflow; a longer body is reported as Invalid rather than clamped, so the length is never silently cut.
        if (contentLength > int.MaxValue - headerEnd)
        {
            return PduBoundaryResult.Invalid;
        }

        int total = headerEnd + (int)contentLength;
        if (data.Length < total)
        {
            return PduBoundaryResult.Incomplete;
        }

        return PduBoundaryResult.Complete(total);
    }

    /// <inheritdoc />
    public void ResetStream(ulong streamId)
    {
        // Nothing to reset: the detector keeps no per-stream state (see the class remarks).
    }

    #endregion

    #region Headers

    /// <summary>
    /// Returns the length of the header block including its terminating empty line, or -1 when no terminator is
    /// buffered yet. Both the standard CRLF CRLF and a bare LF LF terminator are accepted (RFC 9112 section 2.2 lets a
    /// recipient treat a bare LF as a line end). The terminator that starts earliest wins.
    /// </summary>
    private static int _FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        // The u8 literals are static data and IndexOf is vectorized: no allocation and no per-byte loop here.
        int crlf = data.IndexOf("\r\n\r\n"u8);
        int lf = data.IndexOf("\n\n"u8);

        // Take the earliest terminator. Preferring CRLF CRLF unconditionally would mis-frame a header block that ends with
        // a bare LF LF when the body after it happens to contain CRLF CRLF: the body would be read as part of the headers.
        if (lf >= 0 && (crlf < 0 || lf < crlf))
        {
            return lf + 2;
        }

        if (crlf >= 0)
        {
            return crlf + 4;
        }

        return -1;
    }

    /// <summary>
    /// Reads Content-Length, chunked, and whether the message has no body.
    /// <paramref name="contentLength"/> is -1 when the header is absent.
    /// </summary>
    /// <remarks>
    /// Header names are matched ASCII case-insensitively without allocating. A repeated Content-Length keeps the last
    /// valid value (no conflict check: this is a passive analyzer, not a forwarding proxy, so it has no smuggling
    /// decision to protect, but a differing duplicate is framed by the last one). The Transfer-Encoding value is
    /// searched for the token substring "chunked", so a coding list such as "gzip, chunked" is recognized.
    /// </remarks>
    private static bool _TryReadFraming(
        ReadOnlySpan<byte> headers, out bool chunked, out long contentLength, out bool noBody, out bool switchingProtocols)
    {
        chunked = false;
        contentLength = -1;
        noBody = false;
        switchingProtocols = false;

        // The first line is the request line or status line; it decides the messages that never have a body.
        int lineEnd = _FindLineEnd(headers);
        if (lineEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> firstLine = headers[..lineEnd];
        noBody = _IsNoBodyMessage(firstLine, out switchingProtocols);

        // Walk the header lines with span slices only; the empty line that ends the block stops the walk.
        int offset = _SkipLineEnding(headers, lineEnd);
        while (offset < headers.Length)
        {
            if (offset < headers.Length && (headers[offset] == (byte)'\r' || headers[offset] == (byte)'\n'))
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
                    contentLength = parsed;
                }
                else if (System.Text.Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8)
                    && _ContainsIgnoreCase(value, "chunked"u8))
                {
                    chunked = true;
                }
            }

            offset = _SkipLineEnding(headers, offset + next);
        }

        // A no-body message ignores any Content-Length it carries (a 304 response may announce the length of the
        // representation although no body is sent).
        if (noBody && !chunked)
        {
            contentLength = 0;
        }

        return true;
    }

    /// <summary>
    /// Decides from the first line whether the message has no body. True for GET and HEAD requests (their bodies
    /// have no defined meaning, so a Content-Length on them is ignored) and for responses 1xx, 204 and 304 (RFC 9112
    /// section 6.3). Status 101 is reported through <paramref name="switchingProtocols"/> instead, because the bytes
    /// after it belong to the upgraded protocol.
    /// </summary>
    private static bool _IsNoBodyMessage(ReadOnlySpan<byte> firstLine, out bool switchingProtocols)
    {
        switchingProtocols = false;
        if (firstLine.StartsWith("GET "u8) || firstLine.StartsWith("HEAD "u8))
        {
            return true;
        }

        if (!firstLine.StartsWith("HTTP/"u8))
        {
            return false;
        }

        // Status line: "HTTP/1.1 200 OK". The three digits after the first space are the status code.
        int firstSpace = firstLine.IndexOf((byte)' ');
        // The space plus three code bytes must all be present (index firstSpace + 3 is the last one). A shorter status line
        // such as "HTTP/1.1 20" would otherwise make the slice below throw on wire input.
        if (firstSpace < 0 || firstSpace + 4 > firstLine.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> code = firstLine.Slice(firstSpace + 1, 3);
        if (code.SequenceEqual("101"u8))
        {
            switchingProtocols = true;
            return false;
        }

        if (code[0] == (byte)'1' || code.SequenceEqual("204"u8) || code.SequenceEqual("304"u8))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the chunks of a chunked body (RFC 9112 section 7.1) from <paramref name="offset"/> (the first byte after
    /// the header block) and returns the end of the final chunk plus its trailer section. Every call starts over from
    /// the beginning of the body, see the class remarks.
    /// </summary>
    private static PduBoundaryResult _DetectChunked(ReadOnlySpan<byte> data, int offset)
    {
        // One iteration per chunk: "size[;extension] CRLF data CRLF". Running out of bytes at any point means the
        // message continues in a later segment, so the answer is Incomplete; a size line that is not hex is Invalid.
        while (offset < data.Length)
        {
            int lineEnd = _FindLineEnd(data[offset..]);
            if (lineEnd < 0)
            {
                return PduBoundaryResult.Incomplete;
            }

            // Chunk extensions after ';' carry no framing information and are dropped.
            ReadOnlySpan<byte> sizeLine = data.Slice(offset, lineEnd);
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
            int afterLine = _SkipLineEnding(data, offset + lineEnd);
            if (chunkSize == 0)
            {
                return _SkipTrailers(data, afterLine);
            }

            // Skip the chunk data and the line ending that follows it. If the data is not fully buffered, wait. The sum is
            // formed in 64 bits: a chunk size near int.MaxValue added to the data offset would wrap to a negative index
            // and make the line-ending skip below throw on crafted input.
            long chunkEnd = (long)afterLine + chunkSize;
            if (chunkEnd > data.Length)
            {
                return PduBoundaryResult.Incomplete;
            }

            offset = _SkipLineEnding(data, (int)chunkEnd);
        }

        return PduBoundaryResult.Incomplete;
    }

    /// <summary>
    /// Skips the trailer section after the last chunk: zero or more header lines, then an empty line. Returns the end
    /// of the message, or Incomplete while the terminating empty line is not buffered.
    /// </summary>
    private static PduBoundaryResult _SkipTrailers(ReadOnlySpan<byte> data, int offset)
    {
        // A loop instead of recursion: the buffer may hold millions of tiny trailer lines, and one stack frame per line
        // would overflow the stack (which cannot be caught) on crafted input.
        while (true)
        {
            if (offset >= data.Length)
            {
                return PduBoundaryResult.Incomplete;
            }

            if (offset + 1 < data.Length && data[offset] == (byte)'\r' && data[offset + 1] == (byte)'\n')
            {
                return PduBoundaryResult.Complete(offset + 2);
            }

            if (data[offset] == (byte)'\n')
            {
                return PduBoundaryResult.Complete(offset + 1);
            }

            int lineEnd = _FindLineEnd(data[offset..]);
            if (lineEnd < 0)
            {
                return PduBoundaryResult.Incomplete;
            }

            offset = _SkipLineEnding(data, offset + lineEnd);
        }
    }

    #endregion

    #region Lines

    /// <summary>
    /// Returns the index where the line's content ends (before CRLF, or before a bare LF), or -1 when no LF is
    /// buffered yet, i.e. the line is still incomplete.
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

        if (_IsPrefix(data, "GET "u8)
            || _IsPrefix(data, "POST "u8)
            || _IsPrefix(data, "PUT "u8)
            || _IsPrefix(data, "HEAD "u8)
            || _IsPrefix(data, "DELETE "u8)
            || _IsPrefix(data, "OPTIONS "u8)
            || _IsPrefix(data, "PATCH "u8)
            || _IsPrefix(data, "CONNECT "u8)
            || _IsPrefix(data, "TRACE "u8))
        {
            return true;
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

    #endregion
}
