// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Lightweight span-based tokenizer for whitespace-separated ASC line tokens.
/// Allocation-free: works directly on the input <see cref="ReadOnlySpan{T}"/>.
/// </summary>
internal ref struct AscTokenizer
{
    /// <summary>Remaining unparsed portion of the line.</summary>
    private ReadOnlySpan<char> _Remaining;

    /// <summary>
    /// Creates a tokenizer over the given line.
    /// </summary>
    /// <param name="line">The input line to tokenize.</param>
    internal AscTokenizer(ReadOnlySpan<char> line)
    {
        _Remaining = line;
    }

    /// <summary>
    /// Attempts to extract the next whitespace-delimited token.
    /// </summary>
    /// <param name="token">The next token, or empty if no more tokens are available.</param>
    /// <returns><c>true</c> if a token was found; <c>false</c> if the input is exhausted.</returns>
    internal bool TryNextToken(out ReadOnlySpan<char> token)
    {
        // Skip leading whitespace
        _Remaining = _Remaining.TrimStart();

        if (_Remaining.IsEmpty)
        {
            token = ReadOnlySpan<char>.Empty;
            return false;
        }

        int spaceIdx = _Remaining.IndexOfAny(' ', '\t');
        if (spaceIdx < 0)
        {
            // Last token
            token = _Remaining;
            _Remaining = ReadOnlySpan<char>.Empty;
        }
        else
        {
            token = _Remaining[..spaceIdx];
            _Remaining = _Remaining[(spaceIdx + 1)..];
        }

        return true;
    }

    /// <summary>
    /// Returns the remaining unparsed content (useful for grabbing trailing data).
    /// </summary>
    internal readonly ReadOnlySpan<char> Remaining => _Remaining.TrimStart();
}

/// <summary>
/// Lightweight span-based tokenizer for whitespace-separated ASC line tokens,
/// operating directly on raw ASCII bytes without conversion to <see cref="string"/>.
/// Allocation-free.
/// </summary>
internal ref struct AscTokenizerBytes
{
    /// <summary>Remaining unparsed portion of the line.</summary>
    private ReadOnlySpan<byte> _Remaining;

    /// <summary>
    /// Creates a tokenizer over the given raw ASCII byte line.
    /// </summary>
    /// <param name="line">The input byte span to tokenize.</param>
    internal AscTokenizerBytes(ReadOnlySpan<byte> line)
    {
        _Remaining = line;
    }

    /// <summary>
    /// Attempts to extract the next whitespace-delimited token.
    /// </summary>
    /// <param name="token">The next token, or empty if no more tokens are available.</param>
    /// <returns><c>true</c> if a token was found; <c>false</c> if the input is exhausted.</returns>
    internal bool TryNextToken(out ReadOnlySpan<byte> token)
    {
        // Skip leading space and tab
        _Remaining = TrimStartAscii(_Remaining);

        if (_Remaining.IsEmpty)
        {
            token = ReadOnlySpan<byte>.Empty;
            return false;
        }

        int spaceIdx = _IndexOfWhitespace(_Remaining);
        if (spaceIdx < 0)
        {
            token = _Remaining;
            _Remaining = ReadOnlySpan<byte>.Empty;
        }
        else
        {
            token = _Remaining[..spaceIdx];
            _Remaining = _Remaining[(spaceIdx + 1)..];
        }

        return true;
    }

    /// <summary>
    /// Returns the remaining unparsed content trimmed of leading whitespace.
    /// </summary>
    internal readonly ReadOnlySpan<byte> Remaining => TrimStartAscii(_Remaining);

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Trims leading ASCII space (0x20) and tab (0x09) bytes.</summary>
    internal static ReadOnlySpan<byte> TrimStartAscii(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == 0x20 || span[i] == 0x09))
        {
            i++;
        }

        return span[i..];
    }

    /// <summary>Trims trailing ASCII space (0x20) and tab (0x09) bytes.</summary>
    internal static ReadOnlySpan<byte> TrimEndAscii(ReadOnlySpan<byte> span)
    {
        int i = span.Length - 1;
        while (i >= 0 && (span[i] == 0x20 || span[i] == 0x09))
        {
            i--;
        }

        return span[..(i + 1)];
    }

    /// <summary>Trims leading and trailing ASCII whitespace bytes.</summary>
    internal static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span) =>
        TrimEndAscii(TrimStartAscii(span));

    /// <summary>Returns the index of the first space (0x20) or tab (0x09), or -1.</summary>
    private static int _IndexOfWhitespace(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == 0x20 || span[i] == 0x09)
            {
                return i;
            }
        }

        return -1;
    }
}
