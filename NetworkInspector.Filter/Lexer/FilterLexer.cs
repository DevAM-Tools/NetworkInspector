// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Lexer;

/// <summary>
/// Tokenizes a v1 filter expression.
/// <para>
/// <b>Disambiguation order.</b> Several literal forms share a prefix, so the scanner probes
/// from most specific to least specific:
/// </para>
/// <list type="number">
///   <item><description>Radix-prefixed integers (<c>0x</c>, <c>0b</c>, <c>0o</c>).</description></item>
///   <item><description>Colon-separated hex pairs — six groups produce
///     <see cref="TokenKind.MacAddress"/>, any other count produces
///     <see cref="TokenKind.HexBytes"/>.</description></item>
///   <item><description>IPv6 (hex groups and colons, including the <c>::</c> shorthand).</description></item>
///   <item><description>Dotted decimal groups — four groups produce
///     <see cref="TokenKind.Ipv4Address"/>.</description></item>
///   <item><description>Decimal integer/float, optionally followed by a duration or
///     packet-count suffix. A leading minus immediately followed by a digit is a signed
///     integer (<c>-2</c>).</description></item>
///   <item><description>Dotted identifiers and the small keyword set.</description></item>
/// </list>
/// <para>
/// Removed v1 constructs (<c>seq</c>, <c>stream</c>, <c>window</c>, <c>nav</c>, <c>let</c>,
/// <c>where</c>, <c>step</c>) are intentionally <b>not</b> keywords here: they lex as plain
/// identifiers so that field paths such as <c>tcp.window_size</c> keep working. The parser
/// raises <see cref="FilterErrorKind.UnsupportedFeature"/> when one of them appears in its
/// removed syntactic position.
/// </para>
/// </summary>
internal sealed class FilterLexer
{
    #region Fields

    /// <summary>Keyword lookup for undotted identifiers.</summary>
    private static readonly Dictionary<string, TokenKind> _Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["in"] = TokenKind.In,
        ["contains"] = TokenKind.Contains,
        ["matches"] = TokenKind.Matches,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["flank"] = TokenKind.Flank,
    };

    /// <summary>The source expression.</summary>
    private readonly string _Source;

    /// <summary>Accumulated tokens.</summary>
    private readonly List<Token> _Tokens;

    /// <summary>Current read offset into <see cref="_Source"/>.</summary>
    private int _Position;

    #endregion

    #region Construction

    /// <summary>Creates a lexer over <paramref name="source"/>.</summary>
    public FilterLexer(string source)
    {
        _Source = source;
        _Tokens = new List<Token>(16);
        _Position = 0;
    }

    #endregion

    #region Entry point

    /// <summary>Tokenizes the whole source, always terminating with <see cref="TokenKind.Eof"/>.</summary>
    public FilterResult<List<Token>> Tokenize()
    {
        while (true)
        {
            _SkipWhitespace();
            if (_Position >= _Source.Length)
            {
                break;
            }

            FilterResult<Token> next = _NextToken();
            if (!next.TryGetValue(out Token token))
            {
                return FilterResult.Fail<List<Token>>(next.Error);
            }

            _Tokens.Add(token);
        }

        _Tokens.Add(Token.Eof(_Position));
        return FilterResult.Ok<List<Token>>(_Tokens);
    }

    #endregion

    #region Token dispatch

    private FilterResult<Token> _NextToken()
    {
        char c = _Source[_Position];

        switch (c)
        {
            case '(':
                return _Make(TokenKind.LeftParen, 1);
            case ')':
                return _Make(TokenKind.RightParen, 1);
            case '[':
                return _Make(TokenKind.LeftBracket, 1);
            case ']':
                return _Make(TokenKind.RightBracket, 1);
            case '{':
                return _Make(TokenKind.LeftBrace, 1);
            case '}':
                return _Make(TokenKind.RightBrace, 1);
            case ',':
                return _Make(TokenKind.Comma, 1);
            case '$':
                return _Make(TokenKind.Dollar, 1);

            case ':':
                // "::" starts a compressed IPv6 literal such as ::1; a single ':' is punctuation.
                if (_Peek(1) == ':')
                {
                    return _LexIpv6From(_Position);
                }
                return _Make(TokenKind.Colon, 1);

            case '=':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.Equal, 2);
                }
                return FilterError.Lexer("Expected '==' — assignment is not part of the filter language", _Position, 1);

            case '!':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.NotEqual, 2);
                }
                return _Make(TokenKind.Not, 1);

            case '<':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.LessEqual, 2);
                }
                return _Make(TokenKind.LessThan, 1);

            case '>':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.GreaterEqual, 2);
                }
                return _Make(TokenKind.GreaterThan, 1);

            case '&':
                return _Make(TokenKind.And, _Peek(1) == '&' ? 2 : 1);

            case '|':
                return _Make(TokenKind.Or, _Peek(1) == '|' ? 2 : 1);

            case '.':
                if (_Peek(1) == '.')
                {
                    return _Make(TokenKind.Range, 2);
                }
                return FilterError.Lexer("Unexpected '.' — field paths must start with an identifier", _Position, 1);

            case '"':
                return _LexString();

            case '-':
                if (char.IsAsciiDigit(_Peek(1)))
                {
                    return _LexSignedInteger();
                }

                return FilterError.Lexer("Unexpected character '-'", _Position, 1);

            default:
                if (char.IsAsciiDigit(c))
                {
                    return _LexNumberOrAddress();
                }
                if (_IsIdentStart(c))
                {
                    return _LexIdentifierOrAddress();
                }
                return FilterError.Lexer($"Unexpected character '{c}'", _Position, 1);
        }
    }

    #endregion

    #region Strings

    private FilterResult<Token> _LexString()
    {
        int start = _Position;
        _Position++;

        StringBuilder content = new();
        while (_Position < _Source.Length)
        {
            char c = _Source[_Position];
            if (c == '\\')
            {
                _Position++;
                if (_Position >= _Source.Length)
                {
                    return FilterError.Lexer("Unterminated escape sequence", start, _Position - start);
                }
                char escaped = _Source[_Position];
                content.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    _ => escaped,
                });
                _Position++;
                continue;
            }

            if (c == '"')
            {
                _Position++;
                return new Token(TokenKind.StringLiteral, new FilterSpan(start, _Position - start), content.ToString());
            }

            content.Append(c);
            _Position++;
        }

        return FilterError.Lexer("Unterminated string literal", start, _Position - start);
    }

    #endregion

    #region Numbers and addresses

    private FilterResult<Token> _LexNumberOrAddress()
    {
        int start = _Position;

        if (_Source[_Position] == '0' && _Position + 1 < _Source.Length)
        {
            char prefix = _Source[_Position + 1];
            if (prefix is 'x' or 'X')
            {
                return _LexRadix(start, 2, static c => char.IsAsciiHexDigit(c) || c == '_');
            }
            if (prefix is 'b' or 'B')
            {
                return _LexRadix(start, 2, static c => c is '0' or '1' or '_');
            }
            if (prefix is 'o' or 'O')
            {
                return _LexRadix(start, 2, static c => c is >= '0' and <= '7' or '_');
            }
        }

        if (_TryLexHexGroups(start, out Token hexToken))
        {
            return hexToken;
        }

        if (_TryLexIpv6(start, out Token ipv6Token))
        {
            return ipv6Token;
        }

        return _LexDecimalOrIpv4(start);
    }

    /// <summary>
    /// Lexes a decimal integer with a leading minus (<c>-2</c>). Hex prefixes and duration
    /// suffixes are not part of the signed form; <c>-0x10</c> and <c>-2s</c> are not tokens.
    /// </summary>
    private FilterResult<Token> _LexSignedInteger()
    {
        int start = _Position;
        _Position++;
        _ConsumeDigits();
        return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
    }

    /// <summary>
    /// Speculatively lexes an IPv6 literal that begins with decimal digits, such as
    /// <c>2001:db8::1</c>. The probe only commits when the run holds at least two colons and
    /// parses as an address, so single-colon forms such as the slice bound in <c>[0:2]</c> stay
    /// numeric.
    /// </summary>
    private bool _TryLexIpv6(int start, out Token token)
    {
        token = default;

        int probe = start;
        int colons = 0;
        while (probe < _Source.Length && (_Source[probe] == ':' || char.IsAsciiHexDigit(_Source[probe])))
        {
            if (_Source[probe] == ':')
            {
                colons++;
            }
            probe++;
        }

        if (colons < 2)
        {
            return false;
        }

        string text = _Source[start..probe];
        if (!IPv6Address.TryParse(text, out _))
        {
            return false;
        }

        _Position = probe;
        token = new Token(TokenKind.Ipv6Address, new FilterSpan(start, text.Length), text);
        return true;
    }

    private FilterResult<Token> _LexRadix(int start, int prefixLength, Func<char, bool> isDigit)
    {
        _Position += prefixLength;
        while (_Position < _Source.Length && isDigit(_Source[_Position]))
        {
            _Position++;
        }
        return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
    }

    /// <summary>
    /// Probes for a colon-separated hex-pair sequence (<c>HH:HH:…</c>). Six groups yield a MAC
    /// address; two or more groups with a different count yield a raw byte sequence used by
    /// slice comparisons such as <c>eth.src[0:3] == 00:11:22</c>.
    /// </summary>
    private bool _TryLexHexGroups(int start, out Token token)
    {
        token = default;

        int probe = start;
        int groups = 0;
        while (true)
        {
            if (probe + 1 >= _Source.Length
                || !char.IsAsciiHexDigit(_Source[probe])
                || !char.IsAsciiHexDigit(_Source[probe + 1]))
            {
                break;
            }

            // Reject three-or-more digit groups: those belong to IPv6, not a byte sequence.
            if (probe + 2 < _Source.Length && char.IsAsciiHexDigit(_Source[probe + 2]))
            {
                break;
            }

            probe += 2;
            groups++;

            if (probe < _Source.Length && _Source[probe] == ':' && _Peek2(probe + 1) is char n && char.IsAsciiHexDigit(n))
            {
                probe++;
                continue;
            }
            break;
        }

        if (groups < 2)
        {
            return false;
        }

        // A trailing identifier character means this was not a standalone literal.
        if (probe < _Source.Length && (_IsIdentChar(_Source[probe]) || _Source[probe] == ':'))
        {
            return false;
        }

        _Position = probe;
        TokenKind kind = groups == 6 ? TokenKind.MacAddress : TokenKind.HexBytes;
        token = new Token(kind, new FilterSpan(start, _Position - start), _Slice(start));
        return true;
    }

    private FilterResult<Token> _LexIpv6From(int start)
    {
        int probe = start;
        while (probe < _Source.Length && (_Source[probe] == ':' || char.IsAsciiHexDigit(_Source[probe])))
        {
            probe++;
        }

        _Position = probe;
        string text = _Slice(start);
        if (!IPv6Address.TryParse(text, out _))
        {
            return FilterError.InvalidValue($"Invalid IPv6 address '{text}'", start, text.Length);
        }
        return new Token(TokenKind.Ipv6Address, new FilterSpan(start, text.Length), text);
    }

    private FilterResult<Token> _LexDecimalOrIpv4(int start)
    {
        _ConsumeDigits();

        if (_Position < _Source.Length && _Source[_Position] == '.')
        {
            // "1..10" is a range, not a float.
            if (_Peek(1) == '.')
            {
                return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
            }

            if (char.IsAsciiDigit(_Peek(1)))
            {
                int probe = _Position;
                int groups = 1;
                while (probe < _Source.Length && _Source[probe] == '.' && groups < 4)
                {
                    int afterDot = probe + 1;
                    if (afterDot >= _Source.Length || !char.IsAsciiDigit(_Source[afterDot]))
                    {
                        break;
                    }
                    probe = afterDot;
                    while (probe < _Source.Length && char.IsAsciiDigit(_Source[probe]))
                    {
                        probe++;
                    }
                    groups++;
                }

                if (groups == 4)
                {
                    _Position = probe;
                    string ipText = _Slice(start);
                    if (!IPv4Address.TryParse(ipText, out _))
                    {
                        return FilterError.InvalidValue($"Invalid IPv4 address '{ipText}'", start, ipText.Length);
                    }
                    return new Token(TokenKind.Ipv4Address, new FilterSpan(start, ipText.Length), ipText);
                }

                // Two dot-separated groups form a decimal fraction.
                _Position++;
                _ConsumeDigits();
                return _FinishNumber(start);
            }
        }

        return _FinishNumber(start);
    }

    private FilterResult<Token> _FinishNumber(int start)
    {
        if (_Position < _Source.Length && _IsIdentStart(_Source[_Position]))
        {
            int suffixStart = _Position;
            while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
            {
                _Position++;
            }

            string suffix = _Source[suffixStart.._Position];
            if (_IsDurationSuffix(suffix))
            {
                return new Token(TokenKind.Duration, new FilterSpan(start, _Position - start), _Slice(start));
            }

            _Position = suffixStart;
        }

        string text = _Slice(start);
        TokenKind kind = text.Contains('.', StringComparison.Ordinal) ? TokenKind.Float : TokenKind.Integer;
        return new Token(kind, new FilterSpan(start, text.Length), text);
    }

    private static bool _IsDurationSuffix(string suffix) =>
        suffix is "ns" or "us" or "ms" or "s" or "m" or "h" or "packet" or "packets";

    #endregion

    #region Identifiers

    private FilterResult<Token> _LexIdentifierOrAddress()
    {
        int start = _Position;
        while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
        {
            _Position++;
        }

        string firstSegment = _Source[start.._Position];

        // A hex-looking segment followed by ':' may begin a MAC or IPv6 literal.
        if (_Position < _Source.Length && _Source[_Position] == ':' && _IsAllHex(firstSegment))
        {
            int savedPosition = _Position;
            _Position = start;
            if (_TryLexHexGroups(start, out Token hexToken))
            {
                return hexToken;
            }

            _Position = savedPosition;
            if (char.IsAsciiHexDigit(_Peek(1)) || _Peek(1) == ':')
            {
                return _LexIpv6From(start);
            }
            _Position = savedPosition;
        }

        while (_Position < _Source.Length && _Source[_Position] == '.')
        {
            int dotPosition = _Position;
            _Position++;
            if (_Position >= _Source.Length || !_IsIdentStart(_Source[_Position]))
            {
                _Position = dotPosition;
                break;
            }
            while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
            {
                _Position++;
            }
        }

        string text = _Slice(start);
        if (!text.Contains('.', StringComparison.Ordinal) && _Keywords.TryGetValue(text, out TokenKind keyword))
        {
            return new Token(keyword, new FilterSpan(start, text.Length), text);
        }

        return new Token(TokenKind.Identifier, new FilterSpan(start, text.Length), text);
    }

    #endregion

    #region Helpers

    private void _SkipWhitespace()
    {
        while (_Position < _Source.Length && char.IsWhiteSpace(_Source[_Position]))
        {
            _Position++;
        }
    }

    private void _ConsumeDigits()
    {
        while (_Position < _Source.Length && char.IsAsciiDigit(_Source[_Position]))
        {
            _Position++;
        }
    }

    private string _Slice(int start) => _Source[start.._Position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char _Peek(int offset)
    {
        int index = _Position + offset;
        return index < _Source.Length
            ? _Source[index]
            : '\0';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char _Peek2(int absoluteIndex) =>
        absoluteIndex < _Source.Length
            ? _Source[absoluteIndex]
            : '\0';

    private FilterResult<Token> _Make(TokenKind kind, int length)
    {
        Token token = new(kind, new FilterSpan(_Position, length), _Source.Substring(_Position, length));
        _Position += length;
        return token;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Whether an identifier segment could also be read as hex digits. The only caller passes a
    /// segment that starts with an identifier character, so the segment is never empty.
    /// </summary>
    private static bool _IsAllHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    #endregion
}
