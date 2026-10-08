// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http;

/// <summary>
/// Finds one HTTP/1 message in a reassembled TCP byte stream.
/// A request or response without <c>Content-Length</c> and without chunked encoding completes at the
/// header terminator. A body that runs until the peer FIN is not framed here.
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
        int headerEnd = _FindHeaderEnd(data);
        if (headerEnd < 0)
        {
            if (!_CouldBeHttpPrefix(data))
            {
                return PduBoundaryResult.Complete(data.Length);
            }

            return PduBoundaryResult.Incomplete;
        }

        if (!_TryReadFraming(data[..headerEnd], out bool chunked, out long contentLength, out bool noBody, out bool switchingProtocols))
        {
            return PduBoundaryResult.Invalid;
        }

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
    }

    #endregion

    #region Headers

    private static int _FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        int crlf = data.IndexOf("\r\n\r\n"u8);
        if (crlf >= 0)
        {
            return crlf + 4;
        }

        int lf = data.IndexOf("\n\n"u8);
        if (lf >= 0)
        {
            return lf + 2;
        }

        return -1;
    }

    /// <summary>
    /// Reads Content-Length, chunked, and whether the message has no body.
    /// <paramref name="contentLength"/> is -1 when the header is absent.
    /// </summary>
    private static bool _TryReadFraming(
        ReadOnlySpan<byte> headers, out bool chunked, out long contentLength, out bool noBody, out bool switchingProtocols)
    {
        chunked = false;
        contentLength = -1;
        noBody = false;
        switchingProtocols = false;

        int lineEnd = _FindLineEnd(headers);
        if (lineEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> firstLine = headers[..lineEnd];
        noBody = _IsNoBodyMessage(firstLine, out switchingProtocols);

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
                ReadOnlySpan<byte> value = line[(colon + 1)..].Trim((byte)' ');
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

        if (noBody && !chunked)
        {
            contentLength = 0;
        }

        return true;
    }

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

        int firstSpace = firstLine.IndexOf((byte)' ');
        if (firstSpace < 0 || firstSpace + 3 > firstLine.Length)
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

    private static PduBoundaryResult _DetectChunked(ReadOnlySpan<byte> data, int offset)
    {
        while (offset < data.Length)
        {
            int lineEnd = _FindLineEnd(data[offset..]);
            if (lineEnd < 0)
            {
                return PduBoundaryResult.Incomplete;
            }

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

            int afterLine = _SkipLineEnding(data, offset + lineEnd);
            if (chunkSize == 0)
            {
                return _SkipTrailers(data, afterLine);
            }

            int chunkEnd = afterLine + chunkSize;
            if (chunkEnd > data.Length)
            {
                return PduBoundaryResult.Incomplete;
            }

            offset = _SkipLineEnding(data, chunkEnd);
            if (offset < 0)
            {
                return PduBoundaryResult.Incomplete;
            }
        }

        return PduBoundaryResult.Incomplete;
    }

    private static PduBoundaryResult _SkipTrailers(ReadOnlySpan<byte> data, int offset)
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

        return _SkipTrailers(data, _SkipLineEnding(data, offset + lineEnd));
    }

    #endregion

    #region Lines

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

            if (size > (int.MaxValue - digit) / 16)
            {
                return false;
            }

            size = (size * 16) + digit;
        }

        return true;
    }

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

    private static bool _IsPrefix(ReadOnlySpan<byte> data, ReadOnlySpan<byte> method)
    {
        int take = Math.Min(data.Length, method.Length);
        return data[..take].SequenceEqual(method[..take]);
    }

    #endregion
}
